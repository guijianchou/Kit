namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kit.AiHub.Contract;
using Kit.AiHub.Models;
using Kit.AiHub.Serialization;
using Kit.AiHub.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AiUsageStoreTests
{
    [TestMethod]
    public void PersistsCodexUsageAcrossReopenAndIgnoresPi()
    {
        using var fixture = new FixtureDirectory();
        string dataDir = fixture.PathFor("data");
        var store = new AiUsageStore(dataDir);
        string sessionA = "session-a";

        Guid req1 = Guid.NewGuid();
        store.Record(CodexRoute(req1, "gpt-6-luna", "Main", input: 10_000, cached: 6_000, output: 1_200, elapsedMs: 12_000, retries: 1), sessionA);
        store.Record(CodexTask(req1, "gpt-6-luna", "Main", "succeeded", elapsedMs: 12_500), sessionA);

        // Pi records must be ignored completely (Codex-only scope).
        store.Record(CodexRoute(Guid.NewGuid(), "pi-model", "Main", input: 50_000, cached: 10_000, output: 5_000, elapsedMs: 5_000) with { Kernel = "pi" }, sessionA);
        store.Record(CodexTask(Guid.NewGuid(), "pi-model", "Main", "succeeded", elapsedMs: 5_000) with { Kernel = "pi" }, sessionA);

        // Simulate app restart by opening a fresh AiUsageStore instance on the same directory.
        var reopened = new AiUsageStore(dataDir);
        AiUsageOverview overview = reopened.GetOverview(AiUsageTimeRange.AllTime, "session-b");

        Assert.IsTrue(overview.PersistenceAvailable);
        Assert.IsTrue(File.Exists(reopened.DatabasePath));
        Assert.AreEqual(1L, overview.TaskCount);
        Assert.AreEqual(1L, overview.SucceededTasks);
        Assert.AreEqual(1L, overview.RouteCalls);
        Assert.AreEqual(1L, overview.MainCalls);
        Assert.AreEqual(0L, overview.FallbackCalls);
        Assert.AreEqual(1L, overview.NativeRetries);
        Assert.AreEqual(10_000L, overview.InputTokens);
        Assert.AreEqual(6_000L, overview.CachedInputTokens);
        Assert.AreEqual(4_000L, overview.UncachedInputTokens);
        Assert.AreEqual(1_200L, overview.OutputTokens);
        Assert.AreEqual(11_200L, overview.TotalTokens);
        Assert.IsNotNull(overview.CacheHitRate);
        Assert.AreEqual(0.6, overview.CacheHitRate.Value, 0.0001);
        Assert.AreEqual(1, overview.DistinctModelCount);
        Assert.AreEqual(1L, overview.SessionCount);
        Assert.AreEqual(1, overview.Models.Count);
        Assert.AreEqual("gpt-6-luna", overview.Models[0].Model);
    }

    [TestMethod]
    public void AggregatesPerModelBreakdownAndTokenShares()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiUsageStore(fixture.PathFor("data"));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        store.Record(CodexRoute(Guid.NewGuid(), "gpt-6-astra", "Main", input: 30_000, cached: 15_000, output: 10_000, elapsedMs: 20_000, timestampUtc: now.AddMinutes(-5)), "s1");
        store.Record(CodexRoute(Guid.NewGuid(), "gpt-6-luna", "Main", input: 8_000, cached: 4_000, output: 2_000, elapsedMs: 10_000, timestampUtc: now.AddMinutes(-2)), "s1");
        store.Record(CodexRoute(Guid.NewGuid(), "gpt-6-luna", "Fallback", input: 8_000, cached: 0, output: 2_000, elapsedMs: 14_000, retries: 2, outcome: "failed", timestampUtc: now.AddMinutes(-1)), "s1");

        AiUsageOverview overview = store.GetOverview(AiUsageTimeRange.AllTime, "s1");
        Assert.AreEqual(2, overview.Models.Count);
        Assert.AreEqual("gpt-6-astra", overview.Models[0].Model);
        Assert.AreEqual(40_000L, overview.Models[0].TotalTokens);
        Assert.AreEqual(66.6667, overview.Models[0].TokenSharePercent, 0.05);
        Assert.AreEqual(0.5, overview.Models[0].CacheHitRate!.Value, 0.0001);

        AiModelUsageSummary luna = overview.Models[1];
        Assert.AreEqual("gpt-6-luna", luna.Model);
        Assert.AreEqual(2L, luna.Calls);
        Assert.AreEqual(1L, luna.MainCalls);
        Assert.AreEqual(1L, luna.FallbackCalls);
        Assert.AreEqual(1L, luna.SucceededCalls);
        Assert.AreEqual(1L, luna.FailedCalls);
        Assert.AreEqual(2L, luna.NativeRetries);
        Assert.AreEqual(16_000L, luna.InputTokens);
        Assert.AreEqual(4_000L, luna.CachedInputTokens);
        Assert.AreEqual(12_000L, luna.UncachedInputTokens);
        Assert.AreEqual(4_000L, luna.OutputTokens);
        Assert.AreEqual(20_000L, luna.TotalTokens);
        Assert.AreEqual(0.25, luna.CacheHitRate!.Value, 0.0001);
        Assert.AreEqual(12_000.0, luna.AverageElapsedMilliseconds!.Value, 0.01);
        Assert.AreEqual(33.3333, luna.TokenSharePercent, 0.05);
    }

    [TestMethod]
    public void FiltersByTimeRangeAndSession()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiUsageStore(fixture.PathFor("data"));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        store.Record(CodexRoute(Guid.NewGuid(), "gpt-6-luna", "Main", input: 1_000, cached: 0, output: 100, elapsedMs: 1_000, timestampUtc: now.AddDays(-45)), "old-session");
        store.Record(CodexRoute(Guid.NewGuid(), "gpt-6-luna", "Main", input: 2_000, cached: 0, output: 200, elapsedMs: 1_000, timestampUtc: now.AddDays(-12)), "mid-session");
        store.Record(CodexRoute(Guid.NewGuid(), "gpt-6-luna", "Main", input: 3_000, cached: 0, output: 300, elapsedMs: 1_000, timestampUtc: now.AddDays(-2)), "week-session");
        store.Record(CodexRoute(Guid.NewGuid(), "gpt-6-astra", "Main", input: 4_000, cached: 1_000, output: 400, elapsedMs: 1_000, timestampUtc: now.AddMinutes(-1)), "current-session");

        Assert.AreEqual(4L, store.GetOverview(AiUsageTimeRange.AllTime, "current-session").RouteCalls);
        Assert.AreEqual(3L, store.GetOverview(AiUsageTimeRange.Last30Days, "current-session").RouteCalls);
        Assert.AreEqual(2L, store.GetOverview(AiUsageTimeRange.Last7Days, "current-session").RouteCalls);
        Assert.AreEqual(1L, store.GetOverview(AiUsageTimeRange.Session, "current-session").RouteCalls);
        Assert.AreEqual(4_400L, store.GetOverview(AiUsageTimeRange.Session, "current-session").TotalTokens);
        Assert.AreEqual(0L, store.GetOverview(AiUsageTimeRange.Session, "other-session").RouteCalls);
    }

    [TestMethod]
    public void SanitizesSecretsAndUrlsInModelAndMetadata()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiUsageStore(fixture.PathFor("data"));
        const string secretUrl = "https://private.example/sk-live-secret";

        store.Record(CodexRoute(Guid.NewGuid(), secretUrl, "Main", input: 500, cached: 0, output: 100, elapsedMs: 500) with
        {
            PluginId = secretUrl,
            TaskId = secretUrl,
            Effort = secretUrl,
            ApiMode = secretUrl,
        }, "s-secret");

        AiUsageOverview overview = store.GetOverview(AiUsageTimeRange.AllTime, "s-secret");
        Assert.AreEqual(1L, overview.RouteCalls);
        Assert.AreEqual(0, overview.Models.Count);
        Assert.IsNotNull(overview.LegacyUnattributedModel);
        Assert.AreEqual(1L, overview.LegacyUnattributedModel.Calls);
        Assert.AreEqual("other", overview.RecentCalls[0].PluginId);
        Assert.AreEqual("other", overview.RecentCalls[0].TaskId);
        Assert.AreEqual(string.Empty, overview.RecentCalls[0].Effort);
        Assert.AreEqual(0, overview.DistinctModelCount);

        // Ensure AiDiagnosticsStore also sanitizes Model/Effort/ApiMode.
        var diagStore = new AiDiagnosticsStore(fixture.PathFor("diag"));
        diagStore.Record(CodexRoute(Guid.NewGuid(), secretUrl, "Main", input: 100, cached: 0, output: 10, elapsedMs: 100) with
        {
            Effort = secretUrl,
            ApiMode = secretUrl,
        });
        AiDiagnosticRecord sanitized = diagStore.GetSnapshot().RecentRecords.Single();
        Assert.AreEqual(string.Empty, sanitized.Model);
        Assert.AreEqual(string.Empty, sanitized.Effort);
        Assert.AreEqual(string.Empty, sanitized.ApiMode);
    }

    [TestMethod]
    public void ImportsLegacyDiagnosticsOnceAndClearHistoryDoesNotReimport()
    {
        using var fixture = new FixtureDirectory();
        string dataDir = fixture.PathFor("data");
        string logsDir = Path.Combine(dataDir, "Logs");
        Directory.CreateDirectory(logsDir);

        Guid reqId = Guid.NewGuid();
        AiDiagnosticRecord[] legacy =
        [
            CodexRoute(reqId, string.Empty, "Main", input: 12_196, cached: 0, output: 7_294, elapsedMs: 20_000),
            CodexTask(reqId, string.Empty, "Main", "succeeded", elapsedMs: 20_100),
            CodexRoute(Guid.NewGuid(), "pi-model", "Main", input: 99_999, cached: 0, output: 1_000, elapsedMs: 1_000) with { Kernel = "pi" },
        ];
        File.WriteAllText(
            Path.Combine(logsDir, "diagnostics.json"),
            JsonSerializer.Serialize(legacy.ToList(), AiHubJsonContext.Default.ListAiDiagnosticRecord));

        var store = new AiUsageStore(dataDir);
        AiUsageOverview imported = store.GetOverview(AiUsageTimeRange.AllTime, "s-new");
        Assert.AreEqual(1L, imported.RouteCalls);
        Assert.AreEqual(1L, imported.TaskCount);
        Assert.AreEqual(19_490L, imported.TotalTokens);

        Assert.IsTrue(store.ClearHistory());

        // Reopening after ClearHistory must stay empty and NOT re-import legacy diagnostics.json.
        var reopened = new AiUsageStore(dataDir);
        AiUsageOverview afterClear = reopened.GetOverview(AiUsageTimeRange.AllTime, "s-new");
        Assert.AreEqual(0L, afterClear.RouteCalls);
        Assert.AreEqual(0L, afterClear.TaskCount);
    }

    [TestMethod]
    public void RecoversAutomaticallyFromCorruptDatabaseFile()
    {
        using var fixture = new FixtureDirectory();
        string dataDir = fixture.PathFor("data");
        var store = new AiUsageStore(dataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(store.DatabasePath)!);
        File.WriteAllText(store.DatabasePath, "definitely-not-a-valid-sqlite-database-header");

        store.Record(CodexRoute(Guid.NewGuid(), "gpt-6-luna", "Main", input: 1_234, cached: 234, output: 500, elapsedMs: 2_000), "s-recover");

        AiUsageOverview overview = store.GetOverview(AiUsageTimeRange.AllTime, "s-recover");
        Assert.IsTrue(overview.PersistenceAvailable);
        Assert.AreEqual(1L, overview.RouteCalls);
        Assert.AreEqual(1_734L, overview.TotalTokens);
    }

    [TestMethod]
    public void SessionActivityTrackerTracksLivePhasesAndDeduplicatesRepeatedDeltas()
    {
        var tracker = new AiSessionActivityTracker("sess-live");
        int changedNotifications = 0;
        tracker.Changed += (_, _) => changedNotifications++;

        Guid reqId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        tracker.TaskStarted(reqId, "aihub", "security-audit", "gpt-6-luna", "high", 24);
        tracker.TaskBatchPlanReady(reqId, 2);
        tracker.WorkerUpdated(reqId, workerId, "aihub", "security-audit", "gpt-6-luna", "Main", "high", 1, 2, 12, "queued");
        tracker.WorkerUpdated(reqId, workerId, "aihub", "security-audit", "gpt-6-luna", "Main", "high", 1, 2, 12, "reasoning");
        tracker.WorkerUpdated(reqId, workerId, "aihub", "security-audit", "gpt-6-luna", "Main", "high", 1, 2, 12, "reasoning"); // duplicate delta
        tracker.WorkerUpdated(reqId, workerId, "aihub", "security-audit", "gpt-6-luna", "Main", "high", 1, 2, 12, "retrying", retryCount: 1, retryLimit: 3, httpStatusCode: 429, reason: "RateLimited");

        AiSessionActivitySnapshot mid = tracker.GetSnapshot();
        Assert.AreEqual(1, mid.ActiveTaskCount);
        Assert.AreEqual(1, mid.ActiveWorkerCount);
        Assert.AreEqual(1, mid.ActiveExecutions.Count);
        Assert.AreEqual("retrying", mid.ActiveExecutions[0].Phase);
        Assert.AreEqual(1, mid.ActiveExecutions[0].RetryCount);

        // Should have 4 events (task_started, queued, reasoning once, retry) — NOT 2 reasoning events.
        Assert.AreEqual(4, mid.Events.Count);

        tracker.WorkerFinished(reqId, workerId, "aihub", "security-audit", "gpt-6-luna", "Main", "high", 1, 2, "succeeded", "None", null, 1, 5_000, 2_500, 600, 8_000);
        tracker.TaskFinished(reqId, "aihub", "security-audit", "gpt-6-luna", "Main", "succeeded", "None", 2, 2, 15_000);

        AiSessionActivitySnapshot done = tracker.GetSnapshot();
        Assert.AreEqual(0, done.ActiveTaskCount);
        Assert.AreEqual(0, done.ActiveWorkerCount);
        Assert.AreEqual(0, done.ActiveExecutions.Count);
        Assert.AreEqual(6, done.Events.Count);
        Assert.AreEqual("task_completed", done.Events[0].EventType);
        Assert.IsTrue(changedNotifications >= 7);
    }

    [TestMethod]
    public void SeparatesLegacyUnattributedHistoryFromAttributedModelsAndPersistsDetailedV2Metadata()
    {
        using var fixture = new FixtureDirectory();
        string dataDir = fixture.PathFor("data");
        string logsDir = Path.Combine(dataDir, "Logs");
        Directory.CreateDirectory(logsDir);

        // Seed legacy pre-SQLite diagnostics with missing model name.
        AiDiagnosticRecord[] legacy =
        [
            CodexRoute(Guid.NewGuid(), string.Empty, "Main", input: 100_000, cached: 20_000, output: 10_000, elapsedMs: 30_000),
        ];
        File.WriteAllText(
            Path.Combine(logsDir, "diagnostics.json"),
            JsonSerializer.Serialize(legacy.ToList(), AiHubJsonContext.Default.ListAiDiagnosticRecord));

        var store = new AiUsageStore(dataDir);
        Guid reqId = Guid.NewGuid();
        store.Record(CodexRoute(reqId, "gpt-6-luna", "Main", input: 8_000, cached: 4_000, output: 2_000, elapsedMs: 12_000) with
        {
            ConfiguredModel = "deepseek-v4.1-flash",
            ReportedModel = "gpt-6-luna",
            ModelSource = "codex",
            InputItemCount = 18,
            PromptBytes = 14_336,
            OutputCharacters = 2_048,
            QueueMilliseconds = 320,
            Diagnostics = new AiRouteDiagnostics
            {
                Phase = "complete",
                Reason = "None",
                NativeRetryCount = 1,
                NativeRetryLimit = 3,
                ExitCode = 0,
                ProcessFailure = "None",
                CancellationSource = "none",
                CodexThreadId = "thr_01abcXYZ-9",
                ReportedModel = "gpt-6-luna",
                InputTokens = 8_000,
                CachedInputTokens = 4_000,
                OutputTokens = 2_000,
                ContextWindowTokens = 256_000,
                ElapsedMilliseconds = 12_000,
            },
        }, "s-v2");

        AiUsageOverview overview = store.GetOverview(AiUsageTimeRange.AllTime, "s-v2");
        Assert.AreEqual(2L, overview.RouteCalls);
        Assert.AreEqual(120_000L, overview.TotalTokens);

        // Attributed models must exclude legacy empty-model rows and normalize shares cleanly to 100%.
        Assert.AreEqual(1, overview.DistinctModelCount);
        Assert.AreEqual(1, overview.Models.Count);
        AiModelUsageSummary realModel = overview.Models[0];
        Assert.AreEqual("gpt-6-luna", realModel.Model);
        Assert.IsFalse(realModel.IsLegacyUnattributed);
        Assert.AreEqual(100.0, realModel.TokenSharePercent, 0.01);
        Assert.AreEqual(1L, realModel.CodexReportedCalls);
        Assert.AreEqual(18L, realModel.TotalInputItems);
        Assert.AreEqual("high", realModel.DistinctEfforts);
        Assert.AreEqual("responses", realModel.DistinctApiModes);
        Assert.AreEqual(320.0, realModel.AverageQueueMilliseconds!.Value, 0.01);

        // Legacy unattributed rows are exposed separately.
        Assert.IsNotNull(overview.LegacyUnattributedModel);
        Assert.IsTrue(overview.LegacyUnattributedModel.IsLegacyUnattributed);
        Assert.AreEqual(1L, overview.LegacyUnattributedModel.Calls);
        Assert.AreEqual(110_000L, overview.LegacyUnattributedModel.TotalTokens);

        // Detailed v2 call columns round-trip accurately.
        AiUsageCallRecord latest = overview.RecentCalls[0];
        Assert.AreEqual("gpt-6-luna", latest.Model);
        Assert.AreEqual("deepseek-v4.1-flash", latest.ConfiguredModel);
        Assert.AreEqual("gpt-6-luna", latest.ReportedModel);
        Assert.AreEqual("codex", latest.ModelSource);
        Assert.AreEqual("thr_01abcXYZ-9", latest.CodexThreadId);
        Assert.AreEqual("responses", latest.ApiMode);
        Assert.AreEqual(18, latest.InputItemCount);
        Assert.AreEqual(14_336, latest.PromptBytes);
        Assert.AreEqual(2_048, latest.OutputCharacters);
        Assert.AreEqual(3, latest.NativeRetryLimit);
        Assert.AreEqual(0, latest.ExitCode);
        Assert.AreEqual("None", latest.ProcessFailure);
        Assert.AreEqual("none", latest.CancellationSource);
        Assert.AreEqual(4_000L, latest.UncachedInputTokens);
        Assert.AreEqual(10_000L, latest.TotalTokens);
    }

    [TestMethod]
    public void MigratesSchemaV1DatabaseToV2WithoutDataLoss()
    {
        using var fixture = new FixtureDirectory();
        string dataDir = fixture.PathFor("data-v1");
        string stateDir = Path.Combine(dataDir, "State");
        Directory.CreateDirectory(stateDir);
        string dbPath = Path.Combine(stateDir, AiUsageStore.DatabaseFileName);

        using (WinSqlite3.SqliteConnection conn = WinSqlite3.Open(dbPath, readOnly: false))
        {
            conn.ExecuteNonQuery("""
                CREATE TABLE usage_meta (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);
                INSERT INTO usage_meta(key, value) VALUES ('schema_version', '1'), ('diagnostics_backfill_v1', '1');
                CREATE TABLE codex_usage_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    dedup_key TEXT NOT NULL UNIQUE,
                    kind TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    timestamp_unix_ms INTEGER NOT NULL,
                    session_id TEXT NOT NULL,
                    request_id TEXT NOT NULL,
                    agent_id TEXT NOT NULL,
                    parent_agent_id TEXT NOT NULL,
                    attempt_id TEXT NOT NULL,
                    plugin_id TEXT NOT NULL,
                    task_id TEXT NOT NULL,
                    kernel TEXT NOT NULL,
                    model TEXT NOT NULL,
                    route TEXT NOT NULL,
                    effort TEXT NOT NULL,
                    api_mode TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    error_code TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    http_status INTEGER NULL,
                    phase TEXT NOT NULL,
                    batch_index INTEGER NOT NULL,
                    completed_batches INTEGER NOT NULL,
                    total_batches INTEGER NOT NULL,
                    native_retry_count INTEGER NOT NULL,
                    input_tokens INTEGER NULL,
                    cached_input_tokens INTEGER NULL,
                    output_tokens INTEGER NULL,
                    context_window_tokens INTEGER NOT NULL,
                    elapsed_ms INTEGER NOT NULL,
                    queue_ms INTEGER NOT NULL
                );
                INSERT INTO codex_usage_events (
                    dedup_key, kind, timestamp_utc, timestamp_unix_ms, session_id,
                    request_id, agent_id, parent_agent_id, attempt_id,
                    plugin_id, task_id, kernel, model, route, effort, api_mode,
                    outcome, error_code, reason, http_status, phase,
                    batch_index, completed_batches, total_batches, native_retry_count,
                    input_tokens, cached_input_tokens, output_tokens, context_window_tokens, elapsed_ms, queue_ms
                ) VALUES
                ('legacy-row', 'route', '2026-11-26T07:32:39.0000000+00:00', 1795678359000, 'legacy-import',
                 '', '', '', '', 'aihub', 'security-audit', 'codex', '', 'main', '', '',
                 'succeeded', 'None', 'None', NULL, 'complete', 1, 0, 1, 0,
                 12000, 2000, 3000, 256000, 50000, 0),
                ('named-row', 'route', '2026-11-26T13:00:20.0000000+00:00', 1795698020000, 'sess-1',
                 '', '', '', '', 'netmap', 'route-analysis', 'codex', 'deepseek-v4.1-flash', 'main', 'max', 'responses',
                 'succeeded', 'None', 'None', NULL, 'complete', 1, 0, 1, 0,
                 7869, 4096, 1018, 256000, 11570, 50);
                """);
        }

        var store = new AiUsageStore(dataDir);
        AiUsageOverview overview = store.GetOverview(AiUsageTimeRange.AllTime, "sess-2");

        Assert.IsTrue(overview.PersistenceAvailable);
        Assert.AreEqual(2L, overview.RouteCalls);
        Assert.AreEqual(1, overview.Models.Count);
        Assert.AreEqual("deepseek-v4.1-flash", overview.Models[0].Model);
        Assert.AreEqual(100.0, overview.Models[0].TokenSharePercent, 0.01);
        Assert.IsNotNull(overview.LegacyUnattributedModel);
        Assert.AreEqual(1L, overview.LegacyUnattributedModel.Calls);
        Assert.AreEqual("config", overview.RecentCalls[0].ModelSource);
        Assert.AreEqual("legacy", overview.RecentCalls[1].ModelSource);
        Assert.AreEqual(3773L, overview.RecentCalls[0].UncachedInputTokens);
        Assert.AreEqual(8887L, overview.RecentCalls[0].TotalTokens);
    }

    private static AiDiagnosticRecord CodexRoute(
        Guid requestId,
        string model,
        string route,
        long? input,
        long? cached,
        long? output,
        long elapsedMs,
        int retries = 0,
        string outcome = "succeeded",
        DateTimeOffset? timestampUtc = null) => new()
        {
            Kind = "route",
            RequestId = requestId,
            AgentId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            Role = "analysis-worker",
            TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow,
            PluginId = "aihub",
            TaskId = "security-audit",
            Kernel = AiKernelCatalog.Codex,
            Model = model,
            Route = route,
            Effort = "high",
            ApiMode = "responses",
            Outcome = outcome,
            ErrorCode = outcome == "succeeded" ? AiErrorCode.None : AiErrorCode.EndpointFailed,
            BatchIndex = 1,
            TotalBatches = 1,
            ElapsedMilliseconds = elapsedMs,
            Diagnostics = new AiRouteDiagnostics
            {
                Phase = "complete",
                Reason = outcome == "succeeded" ? "None" : "ServerError",
                NativeRetryCount = retries,
                InputTokens = input,
                CachedInputTokens = cached,
                OutputTokens = output,
                ContextWindowTokens = 256_000,
                ElapsedMilliseconds = elapsedMs,
            },
        };

    private static AiDiagnosticRecord CodexTask(
        Guid requestId,
        string model,
        string route,
        string outcome,
        long elapsedMs,
        DateTimeOffset? timestampUtc = null) => new()
        {
            Kind = "task",
            RequestId = requestId,
            AgentId = Guid.NewGuid(),
            Role = "coordinator",
            TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow,
            PluginId = "aihub",
            TaskId = "security-audit",
            Kernel = AiKernelCatalog.Codex,
            Model = model,
            Route = route,
            Outcome = outcome,
            ErrorCode = outcome == "succeeded" ? AiErrorCode.None : AiErrorCode.ExecutionFailed,
            CompletedBatches = outcome == "succeeded" ? 1 : 0,
            TotalBatches = 1,
            ElapsedMilliseconds = elapsedMs,
        };
}
