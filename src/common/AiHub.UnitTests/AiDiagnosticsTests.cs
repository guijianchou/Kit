namespace Kit.AiHub.UnitTests;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Models;
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
        Assert.AreEqual(0, safe.CompletedBatches);
        Assert.IsNull(safe.Diagnostics!.HttpStatusCode);
        Assert.IsNull(safe.Diagnostics.InputTokens);
        Assert.IsNull(safe.Diagnostics.NativeRetryLimit);
        Assert.AreEqual(0, safe.Diagnostics.NativeRetryCount);
        Assert.AreEqual(0L, store.GetSnapshot().KnownUsageRoutes);

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
