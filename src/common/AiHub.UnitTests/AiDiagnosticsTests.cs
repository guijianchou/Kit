namespace Kit.AiHub.UnitTests;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Models;
using Kit.AiHub.Serialization;
using Kit.AiHub.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AiDiagnosticsTests
{
    [TestMethod]
    public void LoggingOffKeepsSessionStatisticsWithoutCreatingHistory()
    {
        using var fixture = new FixtureDirectory();
        string preference = SetLogLevel(fixture, "off");
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), preference);
        int notifications = 0;
        store.Changed += (_, _) => throw new InvalidOperationException("Subscriber failure");
        store.Changed += (_, _) => notifications++;

        store.Record(TaskRecord("succeeded"));

        AiDiagnosticsSnapshot snapshot = store.GetSnapshot();
        Assert.AreEqual(1L, snapshot.RequestCount);
        Assert.AreEqual(1L, snapshot.Succeeded);
        Assert.AreEqual(1, notifications);
        Assert.IsFalse(snapshot.PersistenceEnabled);
        Assert.IsFalse(Directory.Exists(store.LogDirectory));
        Assert.AreEqual(0L, snapshot.PersistenceFailures);
    }

    [TestMethod]
    public void TaskOutcomesAndRouteUsageHaveSeparateCounters()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), SetLogLevel(fixture, "off"));
        AiDiagnosticRecord route = RouteRecord() with
        {
            Diagnostics = new AiRouteDiagnostics
            {
                InputTokens = 100,
                CachedInputTokens = 80,
                CacheWriteTokens = 10,
                OutputTokens = 20,
                NativeRetryCount = 2,
            },
        };
        store.Record(route);
        store.Record(RouteRecord() with { Route = "Fallback", Diagnostics = null });
        store.Record(RouteRecord() with { Diagnostics = new AiRouteDiagnostics { InputTokens = 0 } });
        foreach (string outcome in new[] { "succeeded", "failed", "cancelled", "timeout", "partial" })
        {
            // A task may repeat its last-route metadata; it must not count that usage twice.
            store.Record(TaskRecord(outcome) with { Diagnostics = route.Diagnostics, ElapsedMilliseconds = 10 });
        }

        AiDiagnosticsSnapshot snapshot = store.GetSnapshot();
        Assert.AreEqual(5L, snapshot.RequestCount);
        Assert.AreEqual(1L, snapshot.Succeeded);
        Assert.AreEqual(1L, snapshot.Failed);
        Assert.AreEqual(1L, snapshot.Cancelled);
        Assert.AreEqual(1L, snapshot.Timeout);
        Assert.AreEqual(1L, snapshot.Partial);
        Assert.AreEqual(3L, snapshot.RouteAttempts);
        Assert.AreEqual(2L, snapshot.MainAttempts);
        Assert.AreEqual(1L, snapshot.FallbackAttempts);
        Assert.AreEqual(2L, snapshot.KnownUsageRoutes);
        Assert.AreEqual(100L, snapshot.InputTokens);
        Assert.AreEqual(80L, snapshot.CachedInputTokens);
        Assert.AreEqual(10L, snapshot.CacheWriteTokens);
        Assert.AreEqual(20L, snapshot.OutputTokens);
        Assert.AreEqual(2L, snapshot.NativeRetryCount);
        Assert.AreEqual(50L, snapshot.ElapsedMilliseconds);
    }

    [TestMethod]
    public void PersistenceNormalizesAllTextAndReopeningDoesNotRecountPreviousSession()
    {
        using var fixture = new FixtureDirectory();
        string preference = SetLogLevel(fixture, "info");
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), preference);
        const string secret = "https://private.example/sk-fixture-secret";
        store.Record(TaskRecord("failed") with
        {
            PluginId = secret,
            TaskId = secret,
            Kernel = secret,
            Route = secret,
            ErrorCode = (AiErrorCode)int.MaxValue,
            Diagnostics = new AiRouteDiagnostics
            {
                Phase = secret,
                Reason = secret,
                ProcessFailure = secret,
                CancellationSource = secret,
            },
        });

        string json = File.ReadAllText(Path.Combine(store.LogDirectory, "diagnostics.json"));
        Assert.IsFalse(json.Contains(secret, StringComparison.Ordinal));
        var reopened = new AiDiagnosticsStore(fixture.PathFor("data"), preference);
        AiDiagnosticRecord record = reopened.LoadHistory().Single();
        Assert.AreEqual("other", record.PluginId);
        Assert.AreEqual("other", record.TaskId);
        Assert.AreEqual("unknown", record.Kernel);
        Assert.AreEqual(string.Empty, record.Route);
        Assert.AreEqual("Unknown", record.Diagnostics!.Reason);
        Assert.AreEqual(AiErrorCode.ExecutionFailed, record.ErrorCode);
        Assert.AreEqual(0L, reopened.GetSnapshot().RequestCount);
    }

    [TestMethod]
    public void LoggingLevelChangesApplyToSubsequentRecords()
    {
        using var fixture = new FixtureDirectory();
        string preference = SetLogLevel(fixture, "warning");
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), preference);
        store.Record(TaskRecord("succeeded"));
        store.Record(TaskRecord("cancelled"));
        store.Record(TaskRecord("failed"));
        Assert.AreEqual(1, store.LoadHistory().Count);

        SetLogLevel(fixture, "off");
        store.Record(TaskRecord("failed"));
        Assert.AreEqual(1, store.LoadHistory().Count);
        Assert.IsFalse(store.GetSnapshot().PersistenceEnabled);

        SetLogLevel(fixture, "info");
        store.Record(TaskRecord("succeeded"));
        Assert.AreEqual(2, store.LoadHistory().Count);
        Assert.AreEqual(5L, store.GetSnapshot().RequestCount);
    }

    [TestMethod]
    [DataRow("warn")]
    [DataRow("warning")]
    [DataRow("err")]
    [DataRow("error")]
    [DataRow("critical")]
    [DataRow("WARN")]
    [DataRow("ERR")]
    public void ProductionLogLevelAliasesPersistFailuresWithoutSuccessNoise(string level)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), SetLogLevel(fixture, level));

        store.Record(TaskRecord("succeeded"));
        store.Record(TaskRecord("failed"));
        store.Record(TaskRecord("timeout"));
        store.Record(TaskRecord("partial"));

        AiDiagnosticsSnapshot snapshot = store.GetSnapshot();
        Assert.IsTrue(snapshot.PersistenceEnabled);
        Assert.AreEqual(4L, snapshot.RequestCount);
        Assert.AreEqual(0L, snapshot.PersistenceFailures);
        CollectionAssert.AreEquivalent(new[] { "failed", "timeout", "partial" },
            store.LoadHistory().Select(record => record.Outcome).ToArray());
    }

    [TestMethod]
    [DataRow("warning")]
    [DataRow("error")]
    public void FailureHistoryRetainsFallbackRecoveryAndTaskOutcomeAcrossInstances(string level)
    {
        using var fixture = new FixtureDirectory();
        string preference = SetLogLevel(fixture, level);
        var first = new AiDiagnosticsStore(fixture.PathFor("data"), preference);
        Guid requestId = Guid.NewGuid();
        first.Record(RouteRecord() with
        {
            RequestId = requestId,
            Outcome = "failed",
            ErrorCode = AiErrorCode.EndpointFailed,
            TotalBatches = 2,
            BatchIndex = 1,
        });

        var reopened = new AiDiagnosticsStore(fixture.PathFor("data"), preference);
        reopened.Record(RouteRecord() with { RequestId = requestId, Route = "Fallback", TotalBatches = 2, BatchIndex = 1 });
        reopened.Record(TaskRecord("succeeded") with { RequestId = requestId, Route = "Fallback", CompletedBatches = 2, TotalBatches = 2 });
        reopened.Record(TaskRecord("succeeded"));

        AiDiagnosticRecord[] history = reopened.LoadHistory().Reverse().ToArray();
        Assert.AreEqual(3, history.Length);
        Assert.IsTrue(history.All(record => record.RequestId == requestId));
        Assert.AreEqual("main", history[0].Route);
        Assert.AreEqual("failed", history[0].Outcome);
        Assert.AreEqual(1, history[0].BatchIndex);
        Assert.AreEqual("fallback", history[1].Route);
        Assert.AreEqual("succeeded", history[1].Outcome);
        Assert.AreEqual(1, history[1].BatchIndex);
        Assert.AreEqual("task", history[2].Kind);
        Assert.AreEqual("succeeded", history[2].Outcome);
        Assert.AreEqual(0, history[2].BatchIndex);
    }

    [TestMethod]
    public void WarningHistoryBackfillsRecentBatchContextWithoutDuplicatingRecords()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), SetLogLevel(fixture, "warning"));
        Guid requestId = Guid.NewGuid();
        store.Record(RouteRecord() with { RequestId = requestId, TotalBatches = 2, BatchIndex = 1 });
        Assert.AreEqual(0, store.LoadHistory().Count);
        store.Record(RouteRecord() with
        {
            RequestId = requestId,
            TotalBatches = 2,
            BatchIndex = 2,
            Outcome = "failed",
            ErrorCode = AiErrorCode.EndpointFailed,
        });
        store.Record(RouteRecord() with { RequestId = requestId, TotalBatches = 2, BatchIndex = 2, Route = "Fallback" });
        store.Record(TaskRecord("succeeded") with { RequestId = requestId, TotalBatches = 2, CompletedBatches = 2 });

        AiDiagnosticRecord[] history = store.LoadHistory().Reverse().ToArray();
        Assert.AreEqual(4, history.Length);
        Assert.AreEqual(1, history[0].BatchIndex);
        Assert.AreEqual("succeeded", history[0].Outcome);
        Assert.AreEqual(2, history[1].BatchIndex);
        Assert.AreEqual("failed", history[1].Outcome);
        Assert.AreEqual("fallback", history[2].Route);
        Assert.AreEqual("task", history[3].Kind);
    }

    [TestMethod]
    public void BackfilledContextKeepsHistoryInTimestampOrderAcrossInterleavedRequests()
    {
        using var fixture = new FixtureDirectory();
        string preference = SetLogLevel(fixture, "warning");
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), preference);
        Guid firstRequest = Guid.NewGuid();
        Guid secondRequest = Guid.NewGuid();
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.Record(RouteRecord() with { RequestId = firstRequest, TimestampUtc = start, BatchIndex = 1, TotalBatches = 2 });
        store.Record(RouteRecord() with { RequestId = secondRequest, TimestampUtc = start.AddSeconds(1), Outcome = "failed" });
        store.Record(RouteRecord() with { RequestId = firstRequest, TimestampUtc = start.AddSeconds(2), BatchIndex = 2, TotalBatches = 2, Outcome = "failed" });

        var reopened = new AiDiagnosticsStore(fixture.PathFor("data"), preference);
        AiDiagnosticRecord[] history = reopened.LoadHistory().ToArray();
        Assert.AreEqual(3, history.Length);
        Assert.AreEqual(firstRequest, history[0].RequestId);
        Assert.AreEqual(2, history[0].BatchIndex);
        Assert.AreEqual(secondRequest, history[1].RequestId);
        Assert.AreEqual(firstRequest, history[2].RequestId);
        Assert.AreEqual(1, history[2].BatchIndex);
        Assert.IsTrue(history.Zip(history.Skip(1), (newer, older) => newer.TimestampUtc >= older.TimestampUtc).All(ordered => ordered));
    }

    [TestMethod]
    public void RecordsWithoutRequestIdentityCannotPullUnrelatedSuccessIntoWarningHistory()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), SetLogLevel(fixture, "warning"));
        store.Record(TaskRecord("failed") with { RequestId = Guid.Empty });
        store.Record(TaskRecord("succeeded") with { RequestId = Guid.Empty });
        Assert.AreEqual(1, store.LoadHistory().Count);
        Assert.AreEqual("failed", store.LoadHistory()[0].Outcome);
    }

    [TestMethod]
    public void EnablingWarningLogsDoesNotBackfillObservationsMadeWhileLoggingWasOff()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), SetLogLevel(fixture, "off"));
        Guid requestId = Guid.NewGuid();
        store.Record(RouteRecord() with { RequestId = requestId, TotalBatches = 2, BatchIndex = 1 });
        SetLogLevel(fixture, "warning");
        store.Record(RouteRecord() with { RequestId = requestId, TotalBatches = 2, BatchIndex = 2, Outcome = "failed" });

        AiDiagnosticRecord only = store.LoadHistory().Single();
        Assert.AreEqual(2, only.BatchIndex);
        Assert.AreEqual("failed", only.Outcome);
        Assert.AreEqual(2, store.GetSnapshot().RecentRecords.Count);
    }

    [TestMethod]
    public void HistoryLimitEvictsWholeOtherRequestsAndKeepsNewestFailure()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), SetLogLevel(fixture, "info"));
        Guid activeRequest = Guid.NewGuid();
        store.Record(RouteRecord() with { RequestId = activeRequest, TotalBatches = 2, BatchIndex = 1 });
        for (int index = 0; index < (AiDiagnosticsStore.MaximumHistoryRecords / 2) - 1; index++)
        {
            Guid requestId = Guid.NewGuid();
            store.Record(RouteRecord() with { RequestId = requestId });
            store.Record(TaskRecord("succeeded") with { RequestId = requestId });
        }

        store.Record(RouteRecord() with { RequestId = activeRequest, TotalBatches = 2, BatchIndex = 2, Outcome = "failed", ErrorCode = AiErrorCode.EndpointFailed });
        store.Record(RouteRecord() with { RequestId = activeRequest, TotalBatches = 2, BatchIndex = 2, Route = "Fallback", Outcome = "failed", ErrorCode = AiErrorCode.EndpointFailed });
        var history = store.LoadHistory();
        Assert.IsTrue(history.Count <= AiDiagnosticsStore.MaximumHistoryRecords);
        Assert.AreEqual(activeRequest, history[0].RequestId);
        Assert.AreEqual("failed", history[0].Outcome);
        Assert.AreEqual("fallback", history[0].Route);
        Assert.AreEqual(3, history.Count(record => record.RequestId == activeRequest));
        Assert.IsTrue(history.Where(record => record.RequestId != activeRequest).GroupBy(record => record.RequestId)
            .All(group => group.Count() == 2), "Eviction must not leave an unrelated route without its terminal task.");
    }

    [TestMethod]
    public void NewRequestDoesNotEvictRecentlyFailedLongRunningRequest()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), SetLogLevel(fixture, "info"));
        DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-1);
        Guid activeRequest = Guid.NewGuid();
        Guid oldestCompletedRequest = Guid.Empty;
        store.Record(RouteRecord() with { RequestId = activeRequest, TimestampUtc = start, BatchIndex = 1, TotalBatches = 2 });
        for (int index = 0; index < (AiDiagnosticsStore.MaximumHistoryRecords / 2) - 1; index++)
        {
            Guid requestId = Guid.NewGuid();
            if (index == 0)
            {
                oldestCompletedRequest = requestId;
            }

            store.Record(RouteRecord() with { RequestId = requestId, TimestampUtc = start.AddSeconds(index * 2 + 1) });
            store.Record(TaskRecord("succeeded") with { RequestId = requestId, TimestampUtc = start.AddSeconds(index * 2 + 2) });
        }

        store.Record(RouteRecord() with
        {
            RequestId = activeRequest,
            TimestampUtc = start.AddMinutes(10),
            BatchIndex = 2,
            TotalBatches = 2,
            Route = "Fallback",
            Outcome = "failed",
        });
        Guid newestRequest = Guid.NewGuid();
        store.Record(TaskRecord("failed") with { RequestId = newestRequest, TimestampUtc = start.AddMinutes(11) });

        var history = store.LoadHistory();
        Assert.AreEqual(newestRequest, history[0].RequestId);
        Assert.AreEqual(2, history.Count(record => record.RequestId == activeRequest));
        Assert.AreEqual("failed", history.First(record => record.RequestId == activeRequest).Outcome);
        Assert.IsFalse(history.Any(record => record.RequestId == oldestCompletedRequest));
        Assert.IsTrue(history.Count <= AiDiagnosticsStore.MaximumHistoryRecords);
    }

    [TestMethod]
    public void ExpiredHistoryIsHiddenAndReclaimedByEnabledWritesOnly()
    {
        using var fixture = new FixtureDirectory();
        string preference = SetLogLevel(fixture, "off");
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), preference);
        Directory.CreateDirectory(store.LogDirectory);
        string historyPath = Path.Combine(store.LogDirectory, "diagnostics.json");
        Guid oldRequest = Guid.NewGuid();
        Guid recentRequest = Guid.NewGuid();
        AiDiagnosticRecord[] records =
        [
            TaskRecord("failed") with { RequestId = oldRequest, TimestampUtc = DateTimeOffset.UtcNow.AddDays(-AiDiagnosticsStore.HistoryRetentionDays - 1) },
            TaskRecord("failed") with { RequestId = recentRequest, TimestampUtc = DateTimeOffset.UtcNow.AddDays(-1) },
        ];
        File.WriteAllText(historyPath, JsonSerializer.Serialize(records.ToList(), AiHubJsonContext.Default.ListAiDiagnosticRecord));
        byte[] before = File.ReadAllBytes(historyPath);

        Assert.AreEqual(recentRequest, store.LoadHistory().Single().RequestId);
        store.Record(TaskRecord("failed"));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(historyPath), "Logging off must not rewrite existing history.");

        SetLogLevel(fixture, "info");
        store.Record(TaskRecord("succeeded"));
        Assert.AreEqual(2, store.LoadHistory().Count);
        Assert.IsFalse(File.ReadAllText(historyPath).Contains(oldRequest.ToString(), StringComparison.Ordinal));
        Assert.AreEqual(0L, store.GetSnapshot().PersistenceFailures);
    }

    [TestMethod]
    public void HistoryAndSessionRecentEventsStayBounded()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), SetLogLevel(fixture, "info"));
        Guid finalId = Guid.Empty;
        for (int index = 0; index < AiDiagnosticsStore.MaximumHistoryRecords + 4; index++)
        {
            finalId = Guid.NewGuid();
            store.Record(RouteRecord() with
            {
                RequestId = finalId,
                Diagnostics = new AiRouteDiagnostics
                {
                    Phase = "execute",
                    Reason = "UnsupportedNativeConfiguration",
                    CancellationSource = "processDeadline",
                    ProcessFailure = "CleanupFailed",
                    NativeRetryCount = int.MaxValue,
                    NativeRetryLimit = int.MaxValue,
                    InputTokens = long.MaxValue,
                    CachedInputTokens = long.MaxValue,
                    CacheWriteTokens = long.MaxValue,
                    OutputTokens = long.MaxValue,
                    ContextWindowTokens = int.MaxValue,
                    ElapsedMilliseconds = long.MaxValue,
                    HttpStatusCode = 503,
                    ExitCode = int.MinValue,
                },
            });
        }

        AiDiagnosticsSnapshot snapshot = store.GetSnapshot();
        Assert.AreEqual(AiDiagnosticsStore.MaximumRecentRecords, snapshot.RecentRecords.Count);
        Assert.AreEqual(finalId, snapshot.RecentRecords[0].RequestId);
        var history = store.LoadHistory();
        Assert.IsTrue(history.Count > 0 && history.Count <= AiDiagnosticsStore.MaximumHistoryRecords);
        Assert.AreEqual(finalId, history[0].RequestId);
        Assert.IsTrue(new FileInfo(Path.Combine(store.LogDirectory, "diagnostics.json")).Length <= AiDiagnosticsStore.MaximumHistoryBytes);
        Assert.AreEqual(1, Directory.GetFiles(store.LogDirectory).Length);
        Assert.AreEqual(long.MaxValue, snapshot.InputTokens);
        Assert.AreEqual(0L, snapshot.PersistenceFailures);
    }

    [TestMethod]
    public async Task MultipleInstancesMergeHistoryOrReportContentionDrops()
    {
        using var fixture = new FixtureDirectory();
        string preference = SetLogLevel(fixture, "info");
        var first = new AiDiagnosticsStore(fixture.PathFor("data"), preference);
        var second = new AiDiagnosticsStore(fixture.PathFor("data"), preference);
        await Task.WhenAll(
            Task.Run(() =>
            {
                for (int index = 0; index < 12; index++)
                {
                    first.Record(TaskRecord("succeeded"));
                }
            }),
            Task.Run(() =>
            {
                for (int index = 0; index < 12; index++)
                {
                    second.Record(TaskRecord("succeeded"));
                }
            }));

        var history = first.LoadHistory();
        long failures = first.GetSnapshot().PersistenceFailures + second.GetSnapshot().PersistenceFailures;
        Assert.AreEqual(24L, history.Count + failures);
        Assert.AreEqual(history.Count, history.Select(record => record.RequestId).Distinct().Count());
        Assert.AreEqual(12L, first.GetSnapshot().RequestCount);
        Assert.AreEqual(12L, second.GetSnapshot().RequestCount);
    }

    [TestMethod]
    public void InvalidMetadataAndOverflowCannotCorruptSessionTotals()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), SetLogLevel(fixture, "off"));
        store.Record(RouteRecord() with
        {
            TotalBatches = -1,
            BatchIndex = int.MaxValue,
            CompletedBatches = int.MaxValue,
            ElapsedMilliseconds = -1,
            Diagnostics = new AiRouteDiagnostics
            {
                HttpStatusCode = 999,
                InputTokens = -1,
                OutputTokens = -3,
                NativeRetryCount = -4,
                NativeRetryLimit = -1,
                ContextWindowTokens = -8,
            },
        });
        AiDiagnosticRecord safe = store.GetSnapshot().RecentRecords.Single();
        Assert.AreEqual(0, safe.TotalBatches);
        Assert.AreEqual(0, safe.BatchIndex);
        Assert.AreEqual(0, safe.CompletedBatches);
        Assert.IsNull(safe.Diagnostics!.HttpStatusCode);
        Assert.IsNull(safe.Diagnostics.InputTokens);
        Assert.IsNull(safe.Diagnostics.NativeRetryLimit);
        Assert.AreEqual(0, safe.Diagnostics.NativeRetryCount);
        Assert.AreEqual(0L, store.GetSnapshot().KnownUsageRoutes);

        store.Record(RouteRecord() with { TotalBatches = int.MaxValue, BatchIndex = int.MaxValue, CompletedBatches = int.MaxValue });
        AiDiagnosticRecord boundedBatch = store.GetSnapshot().RecentRecords[0];
        Assert.AreEqual(2_000, boundedBatch.TotalBatches);
        Assert.AreEqual(2_000, boundedBatch.BatchIndex);
        Assert.AreEqual(2_000, boundedBatch.CompletedBatches);

        store.Record(TaskRecord("succeeded") with { ElapsedMilliseconds = long.MaxValue });
        store.Record(TaskRecord("succeeded") with { ElapsedMilliseconds = 1 });
        Assert.AreEqual(long.MaxValue, store.GetSnapshot().ElapsedMilliseconds);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MalformedOrOversizedHistoryFailsSafelyAndCanBeReplaced(bool oversized)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), SetLogLevel(fixture, "info"));
        Directory.CreateDirectory(store.LogDirectory);
        string historyPath = Path.Combine(store.LogDirectory, "diagnostics.json");
        File.WriteAllText(historyPath, oversized ? new string('x', AiDiagnosticsStore.MaximumHistoryBytes + 1) : "{broken-json");

        Assert.AreEqual(0, store.LoadHistory().Count);
        Assert.AreEqual(1L, store.GetSnapshot().PersistenceFailures);
        store.Record(TaskRecord("failed"));
        Assert.AreEqual(1, store.LoadHistory().Count);
        Assert.AreEqual(2L, store.GetSnapshot().PersistenceFailures);
    }

    [TestMethod]
    public void DiagnosticTextFormatsOnlyAllowlistedMetadata()
    {
        const string secret = "https://private.example/sk-private-key";
        string text = AiDiagnosticText.Format(new AiRouteDiagnostics
        {
            Phase = secret,
            Reason = secret,
            CancellationSource = secret,
            ProcessFailure = secret,
            HttpStatusCode = 999,
            NativeRetryCount = -1,
        });
        Assert.IsFalse(text.Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(text, "phase=unknown, reason=Unknown, httpStatus=unknown");
        StringAssert.Contains(text, "nativeRetries=0/unknown");
        Assert.AreEqual(string.Empty, AiDiagnosticText.Format(null));
    }

    [TestMethod]
    public async Task HeldHistoryLockDoesNotBlockTaskDiagnostics()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.PathFor("data"), SetLogLevel(fixture, "info"));
        string mutexName = @"Global\Kit.AiHub.Diagnostics." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(store.LogDirectory.ToUpperInvariant())));
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, mutexName);
            mutex.WaitOne();
            try
            {
                acquired.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        });

        try
        {
            Assert.IsTrue(acquired.Wait(TimeSpan.FromSeconds(3)));
            var stopwatch = Stopwatch.StartNew();
            store.Record(TaskRecord("succeeded"));
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.AreEqual(1L, store.GetSnapshot().RequestCount);
            Assert.AreEqual(1L, store.GetSnapshot().PersistenceFailures);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    private static string SetLogLevel(FixtureDirectory fixture, string level)
    {
        string path = fixture.PathFor("log_settings.json");
        File.WriteAllText(path, "{\"logLevel\":\"" + level + "\"}");
        return path;
    }

    private static AiDiagnosticRecord TaskRecord(string outcome) => new()
    {
        Kind = "task",
        RequestId = Guid.NewGuid(),
        PluginId = "aihub",
        TaskId = "security-audit",
        Kernel = "codex",
        Route = "Main",
        Outcome = outcome,
        ErrorCode = outcome == "succeeded" ? AiErrorCode.None : AiErrorCode.ExecutionFailed,
    };

    private static AiDiagnosticRecord RouteRecord() => TaskRecord("succeeded") with { Kind = "route" };
}
