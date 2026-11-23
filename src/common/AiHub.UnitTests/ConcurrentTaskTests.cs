namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Concurrent;
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
        var auditProgress = new ConcurrentQueue<AiTaskProgress>();
        Task<AiTaskResult<AiTaskReport>> audit = Run(engine, "audit", "audit", cancellationToken: auditCancellation.Token,
            progress: new ImmediateProgress(auditProgress.Enqueue));
        Task<AiTaskResult<AiTaskReport>> optimization = Run(engine, "optimization", "optimization");

        await Task.WhenAll(fixture.WaitForFileAsync("kit-gate-audit.ready"), fixture.WaitForFileAsync("kit-gate-optimization.ready"));
        auditCancellation.Cancel();
        AiTaskResult<AiTaskReport> cancelled = await audit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(AiErrorCode.Cancelled, cancelled.ErrorCode);
        Assert.AreEqual("cancelled", cancelled.Agents.Single().State);
        Assert.IsFalse(cancelled.Agents.Single().IsValidated);
        Assert.IsTrue(auditProgress.Any(value => value.Stage == "Cancelled" && value.Activity == "cancelled"));
        Assert.IsTrue(auditProgress.All(value => value.FailedBatches == 0), "Caller cancellation is not a failed analysis batch.");
        Assert.IsFalse(IsGateProcessRunning(fixture, "audit"));
        Assert.IsFalse(optimization.IsCompleted);
        Assert.IsTrue(IsGateProcessRunning(fixture, "optimization"));
        optimizationGate.Set();
        AiTaskResult<AiTaskReport> completed = await optimization.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(completed.IsSuccess, completed.ErrorMessage);
        Assert.AreEqual("Main", completed.UsedRoute);
        Assert.AreNotEqual(cancelled.RunId, completed.RunId);
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
        Task<AiTaskResult<AiTaskReport>> audit = Run(engine, "audit", "audit", routeTimeoutSeconds: 3);
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

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task SmallTaskReceivesTheNextReleasedSlotFromABulkRequest(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel);
        using EventWaitHandle auditGate = CreateGate(fixture, "audit");
        using EventWaitHandle optimizationGate = CreateGate(fixture, "optimization");
        using var cancellation = new CancellationTokenSource();
        Task<AiTaskResult<AiTaskReport>> audit = Run(engine, "audit", "audit", records: 12, cancellationToken: cancellation.Token);
        await fixture.WaitForFileAsync("two-active.ready");
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AiTaskResult<AiTaskReport>> optimization = Run(engine, "optimization", "optimization", cancellationToken: cancellation.Token,
            progress: new ImmediateProgress(value =>
            {
                if (value.Stage == "Queue")
                {
                    queued.TrySetResult();
                }
            }));
        try
        {
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "kit-gate-optimization.ready")),
                "An admitted bulk worker keeps its slot until its attempt finishes.");
            auditGate.Set();
            await fixture.WaitForFileAsync("kit-gate-optimization.ready");
            Assert.IsFalse(audit.IsCompleted, "The waiting task must start before the bulk request drains.");
            Assert.IsTrue(Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length < 13);
            optimizationGate.Set();
            Assert.IsTrue((await optimization.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
            Assert.IsTrue((await audit.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
            Assert.AreEqual(2, MaximumConcurrentProcesses(fixture));
        }
        finally
        {
            cancellation.Cancel();
            auditGate.Set();
            optimizationGate.Set();
            await Task.WhenAll(audit, optimization);
        }
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task OneTaskBorrowsIdleSlotsAndKeepsWorkerIdentityAcrossProgressAndResults(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel);
        using EventWaitHandle gate = CreateGate(fixture, "audit");
        using var cancellation = new CancellationTokenSource();
        var progress = new ConcurrentQueue<AiTaskProgress>();
        Task<AiTaskResult<AiTaskReport>> running = Run(engine, "audit", "audit", records: 4,
            progress: new ImmediateProgress(progress.Enqueue), cancellationToken: cancellation.Token);
        try
        {
            await fixture.WaitForFileAsync("two-active.ready");
            Assert.AreEqual(2, MaximumConcurrentProcesses(fixture), "An idle slot must not remain reserved.");
            gate.Set();
            AiTaskResult<AiTaskReport> result = await running.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
            Assert.AreNotEqual(Guid.Empty, result.RunId);
            Assert.AreNotEqual(Guid.Empty, result.CoordinatorId);
            Assert.AreEqual(4, result.Agents.Count);
            Assert.AreEqual(4, result.Agents.Select(agent => agent.AgentId).Distinct().Count());
            Assert.IsTrue(progress.All(value => value.RunId == result.RunId));
            foreach (AiAgentResult agent in result.Agents)
            {
                Assert.IsTrue(agent.IsValidated);
                Assert.AreEqual("validated", agent.State);
                Assert.AreEqual(result.RunId, agent.RunId);
                Assert.AreEqual(result.CoordinatorId, agent.ParentAgentId);
                Assert.AreEqual(1, agent.AttemptCount);
                Assert.AreNotEqual(Guid.Empty, agent.AttemptId);
                Assert.IsTrue(progress.Any(value => value.Stage == "Validate" && value.AgentId == agent.AgentId
                    && value.AttemptId == agent.AttemptId && value.ParentAgentId == result.CoordinatorId
                    && value.Role == "analysis-worker"));
            }

            CollectionAssert.AreEqual(Enumerable.Range(1, 4).Select(index => $"item-{index:D6}").ToArray(),
                result.Payload!.Findings.Select(finding => finding.ItemId).ToArray());
            Assert.IsTrue(engine.Diagnostics.GetSnapshot().RecentRecords.Where(record => record.Kind == "route")
                .All(record => record.RequestId == result.RunId && record.ParentAgentId == result.CoordinatorId
                    && result.Agents.Any(agent => agent.AgentId == record.AgentId && agent.AttemptId == record.AttemptId)));
        }
        finally
        {
            cancellation.Cancel();
            gate.Set();
            await running;
        }
    }

    [TestMethod]
    [DataRow("codex", true, false, 3)]
    [DataRow("pi", true, false, 3)]
    [DataRow("codex", false, true, 3)]
    [DataRow("pi", false, true, 3)]
    [DataRow("codex", true, true, 1)]
    [DataRow("pi", true, true, 1)]
    public async Task WorkerConcurrencyHonorsServicePolicyTaskOverrideAndTaskLimit(string kernel, bool enabled, bool requested, int limit)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel);
        new AiHubSettingsStore(fixture.Root).Update(config =>
        {
            config.EnableSubagents = enabled;
            config.MaxAgentsPerTask = limit;
        });
        using EventWaitHandle gate = CreateGate(fixture, "audit");
        gate.Set();
        var result = await Run(engine, "audit", "audit", records: 4, enableSubagents: requested);
        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual(1, MaximumConcurrentProcesses(fixture));
        Assert.AreEqual(4, result.Agents.Count(agent => agent.IsValidated));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task TaskWorkerLimitDoesNotBorrowBeyondItsOwnBudget(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel);
        new AiHubSettingsStore(fixture.Root).Update(config =>
        {
            config.MaxConcurrentAnalysis = 4;
            config.MaxAgentsPerTask = 2;
        });
        using EventWaitHandle gate = CreateGate(fixture, "audit");
        using var cancellation = new CancellationTokenSource();
        Task<AiTaskResult<AiTaskReport>> running = Run(engine, "audit", "audit", records: 6, cancellationToken: cancellation.Token);
        try
        {
            await fixture.WaitForFileAsync("two-active.ready");
            gate.Set();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
            Assert.AreEqual(2, MaximumConcurrentProcesses(fixture));
            Assert.AreEqual(6, result.Agents.Count(agent => agent.IsValidated));
        }
        finally
        {
            cancellation.Cancel();
            gate.Set();
            await running;
        }
    }

    [TestMethod]
    [DataRow("codex", "global")]
    [DataRow("pi", "global")]
    [DataRow("codex", "task")]
    [DataRow("pi", "task")]
    [DataRow("codex", "disabled")]
    [DataRow("pi", "disabled")]
    public async Task LoweringWorkerBudgetLetsCurrentAttemptsFinishAndRestrictsNewAdmissions(string kernel, string budget)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel);
        using EventWaitHandle gate = CreateGate(fixture, "audit");
        using var cancellation = new CancellationTokenSource();
        int lowered = 0;
        var admittedAfterChange = new ConcurrentQueue<int>();
        Task<AiTaskResult<AiTaskReport>> running = Run(engine, "audit", "audit", records: 8,
            cancellationToken: cancellation.Token, progress: new ImmediateProgress(value =>
            {
                if (value.Stage == "Execute" && Volatile.Read(ref lowered) != 0)
                {
                    admittedAfterChange.Enqueue(value.ActiveBatches);
                }
            }));
        try
        {
            await fixture.WaitForFileAsync("two-active.ready");
            new AiHubSettingsStore(fixture.Root).Update(config =>
            {
                if (budget == "global")
                {
                    config.MaxConcurrentAnalysis = 1;
                }
                else if (budget == "task")
                {
                    config.MaxAgentsPerTask = 1;
                }
                else
                {
                    config.EnableSubagents = false;
                }
            });
            engine.NotifyStateChanged();
            Volatile.Write(ref lowered, 1);
            Assert.IsFalse(running.IsCompleted, "Changing admission budgets must not cancel running work.");
            Assert.IsTrue(IsGateProcessRunning(fixture, "audit"));
            gate.Set();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
            Assert.AreEqual(8, result.CompletedBatches);
            Assert.IsTrue(admittedAfterChange.Count >= 6);
            Assert.IsTrue(admittedAfterChange.All(active => active == 1), "New workers must honor the reduced budget.");
        }
        finally
        {
            cancellation.Cancel();
            gate.Set();
            await running;
        }
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task APreparedRequestCannotOverwriteAChangedGlobalConcurrencyBudget(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel);
        using EventWaitHandle gate = CreateGate(fixture, "audit");
        gate.Set();
        var result = await Run(engine, "audit", "audit", records: 4, progress: new ImmediateProgress(value =>
        {
            if (value.Stage == "Prepare")
            {
                new AiHubSettingsStore(fixture.Root).Update(config => config.MaxConcurrentAnalysis = 1);
                engine.NotifyStateChanged();
            }
        }));
        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual(1, MaximumConcurrentProcesses(fixture));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task FallbackQueuesBehindAnAlreadyWaitingIndependentTask(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel, fallback: true);
        new AiHubSettingsStore(fixture.Root).Update(config => config.MaxConcurrentAnalysis = 1);
        using EventWaitHandle auditGate = CreateGate(fixture, "audit");
        using EventWaitHandle optimizationGate = CreateGate(fixture, "optimization");
        using var cancellation = new CancellationTokenSource();
        Task<AiTaskResult<AiTaskReport>> audit = Run(engine, "audit", "audit", routeTimeoutSeconds: 3, cancellationToken: cancellation.Token);
        await fixture.WaitForFileAsync("kit-gate-audit.ready");
        Task<AiTaskResult<AiTaskReport>> optimization = Run(engine, "optimization", "optimization", cancellationToken: cancellation.Token);
        try
        {
            await fixture.WaitForFileAsync("kit-gate-optimization.ready");
            Assert.IsFalse(audit.IsCompleted, "Fallback must queue behind the task already waiting for capacity.");
            Assert.AreEqual(2, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
            optimizationGate.Set();
            var results = await Task.WhenAll(audit, optimization).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsTrue(results.All(result => result.IsSuccess));
            Assert.AreEqual("Fallback", results[0].UsedRoute);
            Assert.AreEqual("Main", results[1].UsedRoute);
            Assert.AreEqual(1, MaximumConcurrentProcesses(fixture));
        }
        finally
        {
            cancellation.Cancel();
            auditGate.Set();
            optimizationGate.Set();
            await Task.WhenAll(audit, optimization);
        }
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task SmallTaskStartsBeforeAnEarlierLargeTaskDrainsItsBatches(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using TaskAiEngine engine = await CreateEngineAsync(fixture, kernel);
        new AiHubSettingsStore(fixture.Root).Update(config => config.MaxConcurrentAnalysis = 1);
        using EventWaitHandle auditGate = CreateGate(fixture, "audit");
        using EventWaitHandle optimizationGate = CreateGate(fixture, "optimization");
        using var cancellation = new CancellationTokenSource();
        Task<AiTaskResult<AiTaskReport>> audit = Run(engine, "audit", "audit", records: 12, cancellationToken: cancellation.Token);
        await fixture.WaitForFileAsync("kit-gate-audit.ready");
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AiTaskResult<AiTaskReport>> optimization = Run(engine, "optimization", "optimization", cancellationToken: cancellation.Token,
            progress: new ImmediateProgress(value =>
            {
                if (value.Stage == "Queue")
                {
                    queued.TrySetResult();
                }
            }));
        try
        {
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            auditGate.Set();
            await fixture.WaitForFileAsync("kit-gate-optimization.ready");
            Assert.IsFalse(audit.IsCompleted, "The small task must run before all twelve earlier batches finish.");
            Assert.IsTrue(Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length < 13);
            optimizationGate.Set();
            var results = await Task.WhenAll(audit, optimization).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsTrue(results.All(result => result.IsSuccess));
            CollectionAssert.AreEqual(Enumerable.Range(1, 12).Select(index => $"item-{index:D6}").ToArray(),
                results[0].Payload!.Findings.Select(finding => finding.ItemId).ToArray());
            Assert.AreEqual(1, MaximumConcurrentProcesses(fixture));
        }
        finally
        {
            cancellation.Cancel();
            auditGate.Set();
            optimizationGate.Set();
            await Task.WhenAll(audit, optimization);
        }
    }

    private sealed class ImmediateProgress(Action<AiTaskProgress> report) : IProgress<AiTaskProgress>
    {
        public void Report(AiTaskProgress value) => report(value);
    }

    private static Task<AiTaskResult<AiTaskReport>> Run(TaskAiEngine engine, string taskId, string gate, int routeTimeoutSeconds = 15,
        int records = 1, IProgress<AiTaskProgress>? progress = null, bool? enableSubagents = null,
        CancellationToken cancellationToken = default) => engine.ExecuteTaskAsync("Sample", taskId,
            Enumerable.Range(0, records).Select(_ => new TestInput($"kit-gate-{gate}", string.Empty, string.Empty)).ToArray(),
            AiTaskReport.CreateSchema(EngineTestJsonContext.Default.TestInput),
            new AiTaskOptions { BatchSize = 1, TimeoutSeconds = 25, RouteTimeoutSeconds = routeTimeoutSeconds, Progress = progress, EnableSubagents = enableSubagents }, cancellationToken);

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
