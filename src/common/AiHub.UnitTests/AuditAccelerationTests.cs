namespace Kit.AiHub.UnitTests;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.Security;
using Kit.AiHub.Storage;
using Kit.AiHub.UnitTests.Fixtures;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AuditAccelerationTests
{
    private const string ValidReport = """
        {"findings":[{"itemId":"{{itemId}}","key":"health","severity":"low","titleEn":"Check health","titleZh":"Fixture title","descriptionEn":"A supplied fact","descriptionZh":"Fixture description","recommendationEn":"Review the service","recommendationZh":"Fixture recommendation"}],"actions":[]}
        """;

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task RouteTimeoutCleansTheMainTreeBeforeStartingFallback(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, "timeout-child", fallback: true, kernel: kernel);
        bool mainExitedBeforeFallback = false;
        int capturesAtFailover = 0;
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", Input(1), Schema(), new AiTaskOptions
        {
            RouteTimeoutSeconds = 1,
            TimeoutSeconds = 15,
            Progress = new ImmediateProgress(value =>
            {
                if (value.Stage == "Failover")
                {
                    using JsonDocument children = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "children.json")));
                    mainExitedBeforeFallback = children.RootElement.EnumerateArray().All(item => HasExited(item.GetInt32()));
                    capturesAtFailover = Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length;
                }
            }),
        });

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual("Fallback", result.UsedRoute);
        Assert.IsTrue(mainExitedBeforeFallback, "Fallback must not start while the timed-out main process tree survives.");
        Assert.AreEqual(1, capturesAtFailover);
        Assert.AreEqual(2, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
        fixture.AssertChildrenExited();
        Assert.AreEqual(0, Directory.GetDirectories(fixture.RequestRoot).Length);
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task CallerCancellationDoesNotStartFallback(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, "timeout-child", fallback: true, kernel: kernel);
        using var cancellation = new CancellationTokenSource();
        Task<AiTaskResult<AiTaskReport>> running = engine.ExecuteTaskAsync("Sample", "diagnostic", Input(1), Schema(),
            new AiTaskOptions { RouteTimeoutSeconds = 10, TimeoutSeconds = 15 }, cancellation.Token);
        await fixture.WaitForFileAsync("children.json");
        cancellation.Cancel();
        var result = await running;

        Assert.AreEqual(AiErrorCode.Cancelled, result.ErrorCode);
        Assert.AreEqual(1, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
        fixture.AssertChildrenExited();
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task OverallDeadlineDoesNotStartFallback(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, "timeout-child", fallback: true, kernel: kernel);
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", Input(1), Schema(),
            new AiTaskOptions { RouteTimeoutSeconds = 10, TimeoutSeconds = 2 });

        Assert.AreEqual(AiErrorCode.Timeout, result.ErrorCode);
        Assert.AreEqual(1, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
        fixture.AssertChildrenExited();
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task TimedOutBatchWithoutFallbackReleasesCapacityForTheNextBatch(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, "mixed-timeout", kernel: kernel, concurrency: 1);
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", Input(37), Schema(), new AiTaskOptions
        {
            AllowPartialResults = true,
            RouteTimeoutSeconds = 1,
            TimeoutSeconds = 15,
        });

        Assert.AreEqual(AiErrorCode.Timeout, result.ErrorCode);
        Assert.IsTrue(result.HasPartialResult);
        Assert.AreEqual(2, result.CompletedBatches);
        Assert.AreEqual(3, result.TotalBatches);
        CollectionAssert.AreEqual(new[] { "item-000001", "item-000031" }, result.Payload!.Findings.Select(item => item.ItemId).ToArray());
        Assert.AreEqual(3, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static TestInput[] Input(int count) => Enumerable.Range(0, count).Select(index => new TestInput($"record-{index}", string.Empty, string.Empty)).ToArray();

    private static AiTaskSchema<TestInput, AiTaskReport> Schema() => AiTaskReport.CreateSchema(EngineTestJsonContext.Default.TestInput);

    private static async Task<TaskAiEngine> CreateEngineAsync(KernelFixture fixture, string scenario = "success", bool fallback = false, string kernel = "codex", int concurrency = 2)
    {
        var store = new AiHubSettingsStore(fixture.Root);
        var config = AiHubConfig.CreateDefault();
        config.IsEnabled = true;
        config.SelectedKernel = kernel;
        config.MaxConcurrentAnalysis = concurrency;
        config.Targets[0] = KernelFixture.Target(scenario);
        config.Targets[0].Name = "Main";
        if (fallback)
        {
            config.Targets[1] = KernelFixture.Target();
            config.Targets[1].Name = "Fallback";
        }

        store.Save(config);
        string packages = Path.Combine(fixture.Root, "modules");
        string task = Path.Combine(packages, "Sample", "Chains", "diagnostic");
        Directory.CreateDirectory(task);
        await File.WriteAllTextAsync(Path.Combine(task, "AGENTS.md"), "Return a report conforming to the host schema. Do not execute actions.");
        await fixture.SetResponseAsync(ValidReport);
        return new TaskAiEngine(store, fixture.Manager, new SecurityPolicyService(fixture.Root, packages));
    }

    private sealed class ImmediateProgress(Action<AiTaskProgress> report) : IProgress<AiTaskProgress>
    {
        public void Report(AiTaskProgress value) => report(value);
    }
}
