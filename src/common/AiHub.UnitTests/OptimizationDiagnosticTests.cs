namespace Kit.AiHub.UnitTests;

using System.Text.Json;
using Kit.AiHub.Contract;
using Kit.AiHub.Models;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class OptimizationDiagnosticTests
{
    [TestMethod]
    public async Task RuntimeCollectionReusesSuppliedInventoryAndKeepsSampleCoverageExplicit()
    {
        using var engine = new DiagnosticEngine();
        var inventory = new OptimizationInventoryReport
        {
            CompletedUtc = DateTimeOffset.UtcNow, ExaminedEntries = 123, IsPartial = true,
            Scopes = [new() { FamilyId = "system", State = "partial", NameEn = "Fixture storage", ObservedBytes = 99 }],
        };
        var result = await new OptimizationDiagnosticService(engine).CollectAsync(inventory);
        Assert.IsTrue(result.Evidence.Count <= 40);
        StringAssert.Contains(result.Evidence[0].Observation, "123 examined entries");
        Assert.AreEqual("partial", result.Evidence[0].Coverage);
        Assert.IsTrue(result.Evidence.Any(item => item.Domain == "performance"));
        Assert.IsTrue(result.Evidence.Any(item => item.Domain == "startup" && item.Coverage == "partial"));
        Assert.AreEqual(0, engine.Calls, "Read-only collection must never call AI or execute its advice.");
        string json = JsonSerializer.Serialize(result, OptimizationDiagnosticJsonContext.Default.OptimizationEvidenceSnapshot);
        Assert.IsFalse(json.Contains("Fixture storage", StringComparison.Ordinal));
        Assert.IsTrue(result.CollectionElapsedMilliseconds >= 0);
    }

    [TestMethod]
    public void InventorySummariesCoverEveryFamilyWithoutTransmittingPathsNamesOrUnknownFamilies()
    {
        var inventory = new OptimizationInventoryReport
        {
            CompletedUtc = DateTimeOffset.UtcNow, IsPartial = true,
            Scopes = Enumerable.Range(0, 30).Select(index => new OptimizationInventoryScope
            {
                Id = "private-id", RootPath = @"C:\Users\PrivateUser\" + index, NameEn = "PrivateApp" + index,
                FamilyId = index % 2 == 0 ? "packages" : "private-family", State = index == 0 ? "partial" : "complete",
                ObservedBytes = index + 1, OlderBytes = index, FileCount = 1, Disposition = "cache",
            }).ToArray(),
        };
        var snapshot = OptimizationEvidenceCollector.Combine(inventory,
            [new("performance", "sampled", "Anonymous process CPU 1 percent.", "PrivateProcessName")], null, 42);
        string json = JsonSerializer.Serialize(snapshot, OptimizationDiagnosticJsonContext.Default.OptimizationEvidenceSnapshot);
        Assert.IsFalse(json.Contains("Private", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("private-family", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("private-id", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("RootPath", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(17, snapshot.Evidence.Count, "Global storage + 2 complete family aggregations + top 12 scopes + process + unavailable stability.");
        Assert.IsTrue(snapshot.Evidence.Any(item => item.Observation.Contains("Family packages", StringComparison.Ordinal)));
        Assert.IsTrue(snapshot.Evidence.Any(item => item.Coverage == "unavailable" && item.Domain == "stability"));
        Assert.IsTrue(snapshot.LocalLabels.Values.Any(label => label == "PrivateProcessName"));
    }

    [TestMethod]
    public async Task DiagnosisUsesIndependentAdvisoryContractAndLocalEvidenceNamesRemainLocal()
    {
        using var engine = new DiagnosticEngine();
        var snapshot = Snapshot();
        var result = await new OptimizationDiagnosticService(engine).AnalyzeAsync(snapshot, "en-US");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("optimization-diagnosis", engine.TaskId);
        Assert.IsNotNull(engine.Input);
        Assert.IsFalse(engine.InputJson.Contains("PrivateProcessName", StringComparison.Ordinal));
        Assert.IsNotNull(engine.Options);
        Assert.IsFalse(engine.Options.AllowPartialResults);
        Assert.AreEqual(false, engine.Options.EnableSubagents);
        Assert.AreEqual(40, engine.Options.BatchSize);
        StringAssert.Contains(result.Format(false), "PrivateProcessName");
        StringAssert.Contains(result.Format(false), "brief sample");
    }

    [TestMethod]
    [DataRow("unknown-evidence")]
    [DataRow("duplicate-evidence")]
    [DataRow("no-limitations")]
    [DataRow("control-character")]
    [DataRow("invalid-priority")]
    public async Task InvalidDiagnosisIsRejectedWithoutLosingCollectedEvidence(string scenario)
    {
        using var engine = new DiagnosticEngine();
        engine.Edit = report =>
        {
            switch (scenario)
            {
                case "unknown-evidence": report.Findings[0].EvidenceIds = ["invented"]; break;
                case "duplicate-evidence": report.Findings[0].EvidenceIds = ["item-000001", "item-000001"]; break;
                case "no-limitations": report.Limitations.Clear(); break;
                case "control-character": report.Findings[0].Recommendation = "line\ncommand"; break;
                case "invalid-priority": report.Findings[0].Priority = "execute-now"; break;
            }
        };
        var snapshot = Snapshot();
        var result = await new OptimizationDiagnosticService(engine).AnalyzeAsync(snapshot, "en-US");
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(AiErrorCode.InvalidPayload, result.ErrorCode);
        Assert.IsNull(result.Report);
        Assert.AreSame(snapshot, result.Snapshot);
        StringAssert.Contains(result.Format(false), "Coverage and collected evidence");
    }

    [TestMethod]
    public async Task FailedAiAndCancellationNeverBecomeACompletedDiagnosis()
    {
        using var engine = new DiagnosticEngine { Failure = AiErrorCode.HubDisabled };
        var snapshot = Snapshot();
        var result = await new OptimizationDiagnosticService(engine).AnalyzeAsync(snapshot, "en-US");
        Assert.AreEqual(AiErrorCode.HubDisabled, result.ErrorCode);
        Assert.AreSame(snapshot, result.Snapshot);
        Assert.IsNull(result.Report);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new OptimizationDiagnosticService(engine).AnalyzeAsync(snapshot, "en-US", token: cancelled.Token));
        Assert.AreEqual(1, engine.Calls);
    }

    [TestMethod]
    public void DiagnosticJsonRejectsExecutionFieldsEvenWhenTheyAreNotUsedByTheUi()
    {
        const string json = """{"summary":"Fixture","findings":[],"limitations":["brief sample"],"action":"delete"}""";
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(json, OptimizationDiagnosticJsonContext.Default.OptimizationDiagnosticReport));
    }

    [TestMethod]
    public async Task InvalidCoverageCannotReachTheEngine()
    {
        using var engine = new DiagnosticEngine();
        var snapshot = new OptimizationEvidenceSnapshot
        {
            Evidence = [new() { Id = "item-000001", Domain = "performance", Coverage = "healthy", Observation = "No facts." }],
        };
        var result = await new OptimizationDiagnosticService(engine).AnalyzeAsync(snapshot, "en-US");
        Assert.AreEqual(AiErrorCode.InvalidPayload, result.ErrorCode);
        Assert.AreEqual(0, engine.Calls);
    }

    private static OptimizationEvidenceSnapshot Snapshot() => new()
    {
        CollectedUtc = DateTimeOffset.UtcNow,
        Evidence = [new() { Id = "item-000001", Domain = "performance", Coverage = "sampled", Observation = "Anonymous process CPU 1 percent, brief sample." }],
        LocalLabels = new(StringComparer.Ordinal) { ["item-000001"] = "PrivateProcessName" },
    };

    private sealed class DiagnosticEngine : IAiTaskEngine
    {
        public bool IsEnabled => true;
        public string ActiveKernel => "fixture";
        public event EventHandler<AiHubStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public string TaskId { get; private set; } = string.Empty;
        public OptimizationDiagnosticEvidence? Input { get; private set; }
        public string InputJson { get; private set; } = string.Empty;
        public AiTaskOptions? Options { get; private set; }
        public Action<OptimizationDiagnosticReport>? Edit { get; set; }
        public AiErrorCode Failure { get; set; }
        public int Calls { get; private set; }
        public AiReadiness GetReadiness() => throw new NotSupportedException();
        public Task<AiReadiness> ProbeReadinessAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose()
        {
        }

        public Task<AiTaskResult<TOutput>> ExecuteTaskAsync<TInput, TOutput>(string pluginId, string taskId, IReadOnlyList<TInput> items,
            AiTaskSchema<TInput, TOutput> schema, AiTaskOptions? options = null, CancellationToken cancellationToken = default)
            where TOutput : class
        {
            Calls++;
            TaskId = taskId;
            Input = (OptimizationDiagnosticEvidence)(object)items.Single()!;
            InputJson = JsonSerializer.Serialize(Input, OptimizationDiagnosticJsonContext.Default.OptimizationDiagnosticEvidence);
            Options = options;
            var report = new OptimizationDiagnosticReport
            {
                Summary = "No sustained pressure established.", Limitations = ["Only a brief sample was collected."],
                Findings =
                [
                    new()
                    {
                        Title = "Monitor load", Priority = "monitor", Diagnosis = "One sample is insufficient.",
                        Recommendation = "Observe Task Manager while reproducing the slowdown.", ExpectedBenefit = "Identify sustained resource consumers.",
                        Tradeoff = "Observation requires time; no settings changed.", Verification = "Compare repeated samples during the same workload.", EvidenceIds = ["item-000001"],
                    },
                ],
            };
            Edit?.Invoke(report);
            return Task.FromResult(new AiTaskResult<TOutput>
            {
                IsSuccess = Failure == AiErrorCode.None, ErrorCode = Failure, Payload = (TOutput)(object)report,
            });
        }
    }
}
