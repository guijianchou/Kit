namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Concurrent;
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
        Exception? observationFailure = null;
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", Input(1), Schema(), new AiTaskOptions
        {
            // Include Codex catalog/startup time before the fixture publishes its child IDs.
            RouteTimeoutSeconds = 3,
            TimeoutSeconds = 15,
            Progress = new ImmediateProgress(value =>
            {
                if (value.Stage == "Failover")
                {
                    try
                    {
                        using JsonDocument children = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "children.json")));
                        mainExitedBeforeFallback = children.RootElement.EnumerateArray().All(item => HasExited(item.GetInt32()));
                        capturesAtFailover = Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length;
                    }
                    catch (Exception exception)
                    {
                        observationFailure = exception;
                    }
                }
            }),
        });

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual("Fallback", result.UsedRoute);
        Assert.IsNull(observationFailure, observationFailure?.ToString());
        Assert.IsTrue(mainExitedBeforeFallback, "Fallback must not start while the timed-out main process tree survives.");
        Assert.AreEqual(1, capturesAtFailover);
        Assert.AreEqual(2, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
        AiAgentResult agent = result.Agents.Single();
        Assert.AreEqual(2, agent.AttemptCount);
        Assert.IsTrue(agent.IsValidated);
        var attempts = engine.Diagnostics.GetSnapshot().RecentRecords.Where(record => record.Kind == "route").ToArray();
        Assert.AreEqual(2, attempts.Select(record => record.AttemptId).Distinct().Count());
        Assert.IsTrue(attempts.All(record => record.AgentId == agent.AgentId && record.ParentAgentId == result.CoordinatorId));
        Assert.AreEqual(attempts.Single(record => record.Route == "fallback").AttemptId, agent.AttemptId);
        fixture.AssertChildrenExited();
        Assert.AreEqual(0, Directory.GetDirectories(fixture.RequestRoot).Length);
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task ValidatedWorkerObserversCannotChangeAcceptedResultsOrSeeRejectedBatches(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, "mixed-batches", kernel: kernel);
        var acceptedIds = new ConcurrentQueue<string>();
        AiTaskSchema<TestInput, AiTaskReport> baseSchema = Schema();
        var schema = new AiTaskSchema<TestInput, AiTaskReport>
        {
            InputTypeInfo = baseSchema.InputTypeInfo,
            OutputTypeInfo = baseSchema.OutputTypeInfo,
            ValidateOutput = baseSchema.ValidateOutput,
            MergeBatches = baseSchema.MergeBatches,
            AllowedActions = baseSchema.AllowedActions,
            ReportValidatedBatch = report =>
            {
                acceptedIds.Enqueue(report.Findings.Single().ItemId);
                report.Findings.Clear();
                throw new InvalidOperationException("Synthetic observer failure after modifying its copy.");
            },
        };

        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", Input(37), schema,
            new AiTaskOptions { AllowPartialResults = true });
        Assert.IsTrue(result.HasPartialResult);
        Assert.AreEqual(AiErrorCode.PolicyViolation, result.ErrorCode);
        CollectionAssert.AreEquivalent(new[] { "item-000001", "item-000031" }, acceptedIds.ToArray());
        CollectionAssert.AreEqual(new[] { "item-000001", "item-000031" }, result.Payload!.Findings.Select(item => item.ItemId).ToArray());
        Assert.AreEqual(2, result.Agents.Count(agent => agent.IsValidated));
        Assert.AreEqual(AiErrorCode.PolicyViolation, result.Agents.Single(agent => agent.BatchIndex == 2).ErrorCode);
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task PlanningFailureClosesEveryWorkerWithoutLeavingQueuedTerminalMetadata(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel: kernel);
        AiTaskSchema<TestInput, AiTaskReport> shared = Schema();
        var schema = new AiTaskSchema<TestInput, AiTaskReport>
        {
            InputTypeInfo = shared.InputTypeInfo,
            OutputTypeInfo = shared.OutputTypeInfo,
            ValidateOutput = shared.ValidateOutput,
        };
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", Input(2), schema, new AiTaskOptions { BatchSize = 1 });
        Assert.AreEqual(AiErrorCode.InvalidPayload, result.ErrorCode);
        Assert.AreEqual(2, result.Agents.Count);
        Assert.IsTrue(result.Agents.All(agent => agent.State == "not-started" && agent.AttemptCount == 0
            && !agent.IsValidated && agent.ErrorCode == AiErrorCode.InvalidPayload));
        Assert.AreNotEqual(Guid.Empty, result.RunId);
        Assert.AreNotEqual(Guid.Empty, result.CoordinatorId);
        Assert.IsFalse(Directory.Exists(fixture.RequestRoot) && Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length != 0);
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task CancellationFromValidatedProgressDoesNotPublishAWorkerPayload(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel: kernel);
        using var cancellation = new CancellationTokenSource();
        int published = 0;
        AiTaskSchema<TestInput, AiTaskReport> shared = Schema();
        var schema = new AiTaskSchema<TestInput, AiTaskReport>
        {
            InputTypeInfo = shared.InputTypeInfo,
            OutputTypeInfo = shared.OutputTypeInfo,
            ValidateOutput = shared.ValidateOutput,
            ReportValidatedBatch = _ => Interlocked.Increment(ref published),
        };
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", Input(1), schema, new AiTaskOptions
        {
            Progress = new ImmediateProgress(value =>
            {
                if (value.Stage == "Validate")
                {
                    cancellation.Cancel();
                }
            }),
        }, cancellation.Token);
        Assert.AreEqual(AiErrorCode.Cancelled, result.ErrorCode);
        Assert.IsFalse(result.HasPartialResult);
        Assert.IsNull(result.Payload);
        Assert.AreEqual(0, published);
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
            new AiTaskOptions { RouteTimeoutSeconds = 10, TimeoutSeconds = 4 });

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
        var progress = new ConcurrentQueue<AiTaskProgress>();
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", Input(37), Schema(), new AiTaskOptions
        {
            AllowPartialResults = true,
            RouteTimeoutSeconds = 3,
            TimeoutSeconds = 15,
            Progress = new ImmediateProgress(progress.Enqueue),
        });

        Assert.AreEqual(AiErrorCode.Timeout, result.ErrorCode);
        Assert.IsTrue(result.HasPartialResult);
        Assert.AreEqual(2, result.CompletedBatches);
        Assert.AreEqual(3, result.TotalBatches);
        CollectionAssert.AreEqual(new[] { "item-000001", "item-000031" }, result.Payload!.Findings.Select(item => item.ItemId).ToArray());
        Assert.AreEqual(3, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
        AiTaskProgress failed = progress.First(value => value.Stage == "Failed");
        Assert.AreEqual(1, failed.FailedBatches);
        Assert.AreEqual(1, failed.CompletedBatches, "A failed batch must not be counted as successfully validated.");
        Assert.AreEqual(1, failed.QueuedBatches);
        Assert.AreEqual(0, failed.ActiveBatches);
        Assert.AreEqual(2, failed.BatchIndex);
        AiTaskProgress final = progress.Last(value => value.Stage == "Validate");
        Assert.AreEqual(1, final.FailedBatches);
        Assert.AreEqual(2, final.CompletedBatches);
        Assert.AreEqual(0, final.QueuedBatches);
        Assert.IsTrue(final.ElapsedMilliseconds >= failed.ElapsedMilliseconds);
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task NativeRetriesProduceTypedProgressWithoutLeakingProviderText(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, "native-usage", kernel: kernel);
        var progress = new ConcurrentQueue<AiTaskProgress>();
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", Input(1), Schema(), new AiTaskOptions
        {
            Progress = new ImmediateProgress(progress.Enqueue),
        });

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        AiTaskProgress retry = progress.Last(value => value.Activity == "retrying");
        Assert.AreEqual(kernel == "codex" ? 2 : 1, retry.RetryCount);
        Assert.AreEqual("Main", retry.Route);
        Assert.AreEqual(1, retry.BatchIndex);
        Assert.AreEqual(1, retry.ActiveBatches);
        Assert.AreEqual(0, retry.CompletedBatches);
        Assert.IsFalse(progress.Any(value => value.StatusMessage.Contains("synthetic-fixture-secret", StringComparison.Ordinal)));
        Assert.IsFalse(progress.Any(value => value.StatusMessage.Contains("https://synthetic.invalid", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task ProgressSubscriberFailuresDoNotChangeTaskResults(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        foreach (string stage in new[] { "Prepare", "Queue", "Execute", "Route", "Failover", "Validate" })
        {
            using TaskAiEngine engine = await CreateEngineAsync(fixture,
                stage == "Failover" ? "transport" : "success", fallback: true, kernel: kernel);
            int callbacks = 0;
            var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", Input(1), Schema(), new AiTaskOptions
            {
                Progress = new ImmediateProgress(value =>
                {
                    if (value.Stage == stage)
                    {
                        Interlocked.Increment(ref callbacks);
                        throw new InvalidOperationException("Synthetic progress subscriber failure.");
                    }
                }),
            });
            Assert.IsTrue(callbacks > 0, stage);
            Assert.IsTrue(result.IsSuccess, $"{stage}: {result.ErrorCode}");
            Assert.AreEqual(stage == "Failover" ? "Fallback" : "Main", result.UsedRoute);
        }
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
