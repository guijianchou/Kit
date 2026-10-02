namespace Kit.AiHub.UnitTests;

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
public sealed class ConcurrentTaskTests
{
    private const string ValidReport = """
        {"findings":[{"itemId":"{{itemId}}","key":"health","severity":"low","titleEn":"Check health","titleZh":"Fixture title","descriptionEn":"A supplied fact","descriptionZh":"Fixture description","recommendationEn":"Review the service","recommendationZh":"Fixture recommendation"}],"actions":[]}
        """;

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task IndependentBusinessTasksRunNativeProcessesAtTheSameTime(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel);
        using EventWaitHandle auditGate = CreateGate(fixture, "audit");
        using EventWaitHandle optimizationGate = CreateGate(fixture, "optimization");
        Task<AiTaskResult<AiTaskReport>> audit = Run(engine, "audit", "audit");
        Task<AiTaskResult<AiTaskReport>> optimization = Run(engine, "optimization", "optimization");

        await Task.WhenAll(fixture.WaitForFileAsync("kit-gate-audit.ready"), fixture.WaitForFileAsync("kit-gate-optimization.ready"));
        Assert.IsFalse(audit.IsCompleted);
        Assert.IsFalse(optimization.IsCompleted);
        Assert.IsTrue(IsGateProcessRunning(fixture, "audit"));
        Assert.IsTrue(IsGateProcessRunning(fixture, "optimization"));
        auditGate.Set();
        optimizationGate.Set();
        AiTaskResult<AiTaskReport>[] results = await Task.WhenAll(audit, optimization).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.IsTrue(results.All(result => result.IsSuccess));
        Assert.IsTrue(results.All(result => result.UsedRoute == "Main"));
        Assert.AreEqual(2, MaximumConcurrentProcesses(fixture));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task CancellingOneBusinessDoesNotCancelTheOther(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel);
        using EventWaitHandle auditGate = CreateGate(fixture, "audit");
        using EventWaitHandle optimizationGate = CreateGate(fixture, "optimization");
        using var auditCancellation = new CancellationTokenSource();
        Task<AiTaskResult<AiTaskReport>> audit = Run(engine, "audit", "audit", cancellationToken: auditCancellation.Token);
        Task<AiTaskResult<AiTaskReport>> optimization = Run(engine, "optimization", "optimization");

        await Task.WhenAll(fixture.WaitForFileAsync("kit-gate-audit.ready"), fixture.WaitForFileAsync("kit-gate-optimization.ready"));
        auditCancellation.Cancel();
        AiTaskResult<AiTaskReport> cancelled = await audit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(AiErrorCode.Cancelled, cancelled.ErrorCode);
        Assert.IsFalse(IsGateProcessRunning(fixture, "audit"));
        Assert.IsFalse(optimization.IsCompleted);
        Assert.IsTrue(IsGateProcessRunning(fixture, "optimization"));
        optimizationGate.Set();
        AiTaskResult<AiTaskReport> completed = await optimization.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(completed.IsSuccess, completed.ErrorMessage);
        Assert.AreEqual("Main", completed.UsedRoute);
        Assert.AreEqual(2, MaximumConcurrentProcesses(fixture));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task OneBusinessCanTimeOutAndFailOverWhileTheOtherKeepsRunning(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel, fallback: true);
        using EventWaitHandle auditGate = CreateGate(fixture, "audit");
        using EventWaitHandle optimizationGate = CreateGate(fixture, "optimization");
        Task<AiTaskResult<AiTaskReport>> audit = Run(engine, "audit", "audit", routeTimeoutSeconds: 2);
        Task<AiTaskResult<AiTaskReport>> optimization = Run(engine, "optimization", "optimization");

        await Task.WhenAll(fixture.WaitForFileAsync("kit-gate-audit.ready"), fixture.WaitForFileAsync("kit-gate-optimization.ready"));
        AiTaskResult<AiTaskReport> recovered = await audit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(recovered.IsSuccess, recovered.ErrorMessage);
        Assert.AreEqual("Fallback", recovered.UsedRoute);
        Assert.IsFalse(IsGateProcessRunning(fixture, "audit"));
        Assert.IsFalse(optimization.IsCompleted);
        Assert.IsTrue(IsGateProcessRunning(fixture, "optimization"));
        optimizationGate.Set();
        AiTaskResult<AiTaskReport> completed = await optimization.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(completed.IsSuccess, completed.ErrorMessage);
        Assert.AreEqual("Main", completed.UsedRoute);
        Assert.AreEqual(3, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
        Assert.AreEqual(2, MaximumConcurrentProcesses(fixture));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task MultipleBatchesAcrossBusinessesShareTheTwoProcessLimit(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel);
        using EventWaitHandle sharedGate = CreateGate(fixture, "shared");
        Task<AiTaskResult<AiTaskReport>> audit = Run(engine, "audit", "shared", records: 3);
        Task<AiTaskResult<AiTaskReport>> optimization = Run(engine, "optimization", "shared", records: 3);

        await fixture.WaitForFileAsync("two-active.ready");
        sharedGate.Set();
        AiTaskResult<AiTaskReport>[] results = await Task.WhenAll(audit, optimization).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.IsTrue(results.All(result => result.IsSuccess));
        Assert.IsTrue(results.All(result => result.CompletedBatches == 3));
        Assert.AreEqual(6, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
        Assert.AreEqual(2, MaximumConcurrentProcesses(fixture));
    }

    private static Task<AiTaskResult<AiTaskReport>> Run(TaskAiEngine engine, string taskId, string gate, int routeTimeoutSeconds = 15,
        int records = 1, CancellationToken cancellationToken = default) => engine.ExecuteTaskAsync("Sample", taskId,
            Enumerable.Range(0, records).Select(_ => new TestInput($"kit-gate-{gate}", string.Empty, string.Empty)).ToArray(),
            AiTaskReport.CreateSchema(EngineTestJsonContext.Default.TestInput),
            new AiTaskOptions { BatchSize = 1, TimeoutSeconds = 25, RouteTimeoutSeconds = routeTimeoutSeconds }, cancellationToken);

    private static EventWaitHandle CreateGate(KernelFixture fixture, string gate) => new(false, EventResetMode.ManualReset,
        $"Local\\KitAiHubFixture-{Path.GetFileName(fixture.Root)}-kit-gate-{gate}");

    private static int MaximumConcurrentProcesses(KernelFixture fixture) =>
        int.Parse(File.ReadAllText(Path.Combine(fixture.Root, "maximum-active.txt")), CultureInfo.InvariantCulture);

    private static bool IsGateProcessRunning(KernelFixture fixture, string gate)
    {
        int id = int.Parse(File.ReadAllText(Path.Combine(fixture.Root, $"kit-gate-{gate}.ready")), CultureInfo.InvariantCulture);
        try
        {
            using Process process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<TaskAiEngine> CreateEngineAsync(KernelFixture fixture, string kernel, bool fallback = false)
    {
        var store = new AiHubSettingsStore(fixture.Root);
        var config = AiHubConfig.CreateDefault();
        config.IsEnabled = true;
        config.SelectedKernel = kernel;
        config.MaxConcurrentAnalysis = 2;
        config.Targets[0] = KernelFixture.Target("handshake");
        config.Targets[0].Name = "Main";
        if (fallback)
        {
            config.Targets[1] = KernelFixture.Target("tracked-success");
            config.Targets[1].Name = "Fallback";
        }

        store.Save(config);
        string packages = Path.Combine(fixture.Root, "modules");
        foreach (string taskId in new[] { "audit", "optimization" })
        {
            string task = Path.Combine(packages, "Sample", "Chains", taskId);
            Directory.CreateDirectory(task);
            await File.WriteAllTextAsync(Path.Combine(task, "AGENTS.md"), "Return a report conforming to the host schema. Do not execute actions.");
        }

        await fixture.SetResponseAsync(ValidReport);
        return new TaskAiEngine(store, fixture.Manager, new SecurityPolicyService(fixture.Root, packages));
    }
}
