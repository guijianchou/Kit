// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Audit history persistence: retention, the new count cap and the atomic write.
/// </summary>
[TestClass]
public sealed class AuditHistoryStorageTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void CreateIsolatedDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "KitAuditHistoryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void RemoveIsolatedDirectory()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    [TestMethod]
    public void EmptyStorageLoadsAsAnEmptyHistory()
    {
        var storage = new AuditHistoryStorage(_directory);

        Assert.AreEqual(0, storage.LoadHistory().Count);
        Assert.IsNull(storage.LoadLatestHistoryAsync().GetAwaiter().GetResult());
    }

    [TestMethod]
    public void SavedResultIsReturnedNewestFirst()
    {
        var storage = new AuditHistoryStorage(_directory);
        var older = new AuditResult { Timestamp = DateTime.UtcNow.AddHours(-1), HealthScore = 70 };
        var newer = new AuditResult { Timestamp = DateTime.UtcNow, HealthScore = 90 };

        storage.SaveResult(older);
        storage.SaveResult(newer);

        List<AuditResult> history = storage.LoadHistory();
        Assert.AreEqual(2, history.Count);
        Assert.AreEqual(90, history[0].HealthScore, "The newest audit must be first.");
    }

    [TestMethod]
    public void AiCompletionReplacesItsLocalSnapshotWithoutChangingOtherScanCounts()
    {
        var storage = new AuditHistoryStorage(_directory);
        DateTime scan = DateTime.UtcNow.AddMinutes(-1);
        storage.SaveResult(new AuditResult { Timestamp = scan, HealthScore = 90, AnalysisOutcome = "pending" });
        storage.SaveResult(new AuditResult { Timestamp = scan.AddSeconds(1), HealthScore = 80 });
        storage.SaveResult(new AuditResult { Timestamp = scan, HealthScore = 70, AnalysisOutcome = "partial" });
        storage.SaveResult(new AuditResult { Timestamp = scan, HealthScore = 60, AnalysisOutcome = "ok" });

        var history = storage.LoadHistory();
        Assert.AreEqual(2, history.Count);
        Assert.AreEqual(scan.AddSeconds(1), history[0].Timestamp, "Finishing old AI work must not make an older scan the newest.");
        Assert.AreEqual(60, history[1].HealthScore);
        Assert.AreEqual("ok", history[1].AnalysisOutcome);
        Assert.AreEqual(2, AuditHistoryStatistics.From(history).AuditCount);
    }

    [TestMethod]
    public void PeriodOverviewAndCollectionLimitsSurviveHistoryReload()
    {
        var storage = new AuditHistoryStorage(_directory);
        DateTime now = DateTime.UtcNow;
        storage.SaveResult(new AuditResult
        {
            Timestamp = now, ScanStart = now.AddDays(-30), ScanEnd = now, HealthScore = 68, EventsScanned = 5000,
            Findings = [new() { Severity = "High", Category = "Stability", Occurrences = 100 }],
            SkippedChannels = ["Security"], LimitedChannels = ["System"], AnalysisOutcome = "partial",
        });
        var restored = new AuditHistoryStorage(_directory).LoadHistory().Single();
        var summary = AuditSummary.From(restored);
        Assert.AreEqual((int?)30, summary.RangeDays);
        Assert.AreEqual(68, summary.HealthScore);
        Assert.AreEqual(5000, summary.EventsScanned);
        Assert.AreEqual(100L, summary.Occurrences);
        CollectionAssert.AreEqual(new[] { "Security" }, restored.SkippedChannels);
        CollectionAssert.AreEqual(new[] { "System" }, restored.LimitedChannels);
        Assert.AreEqual("partial", restored.AnalysisOutcome);
    }

    [TestMethod]
    public void RetentionDropsExpiredSnapshots()
    {
        var storage = new AuditHistoryStorage(_directory);
        storage.SaveResult(new AuditResult { Timestamp = DateTime.UtcNow.AddDays(-40), HealthScore = 50 });
        storage.SaveResult(new AuditResult { Timestamp = DateTime.UtcNow, HealthScore = 90 });

        List<AuditResult> history = storage.LoadHistory();

        Assert.AreEqual(1, history.Count, "Snapshots older than the retention window must be dropped.");
        Assert.AreEqual(90, history[0].HealthScore);
    }

    [TestMethod]
    public void CountCapKeepsTheHistoryBounded()
    {
        var storage = new AuditHistoryStorage(_directory);
        DateTime now = DateTime.UtcNow;

        for (int i = 0; i < AuditHistoryStorage.MaxRetainedAudits + 25; i++)
        {
            storage.SaveResult(new AuditResult { Timestamp = now.AddSeconds(-i), HealthScore = 100 });
        }

        List<AuditResult> history = storage.LoadHistory();

        Assert.AreEqual(
            AuditHistoryStorage.MaxRetainedAudits,
            history.Count,
            "History must stay capped so the full-file rewrite stays bounded.");
    }

    [TestMethod]
    public void SaveLeavesNoTemporaryFileBehind()
    {
        var storage = new AuditHistoryStorage(_directory);
        storage.SaveResult(new AuditResult { Timestamp = DateTime.UtcNow, HealthScore = 100 });

        Assert.AreEqual(
            0,
            Directory.GetFiles(_directory, "*.tmp").Length,
            "The atomic write must not leave a .tmp file behind.");
        Assert.AreEqual(1, Directory.GetFiles(_directory, "audit_history.json").Length);
    }

    [TestMethod]
    public void CorruptHistoryDegradesToEmptyInsteadOfThrowing()
    {
        var storage = new AuditHistoryStorage(_directory);
        File.WriteAllText(Path.Combine(_directory, "audit_history.json"), "{ this is not json");

        List<AuditResult> history = storage.LoadHistory();

        Assert.AreEqual(0, history.Count, "A corrupt file must not break the audit path.");
    }

    [TestMethod]
    public void SaveDoesNotOverwriteCorruptHistory()
    {
        const string damaged = "{ this is not json";
        string path = Path.Combine(_directory, "audit_history.json");
        File.WriteAllText(path, damaged);
        var storage = new AuditHistoryStorage(_directory);
        Assert.ThrowsExactly<JsonException>(() => storage.SaveResult(new AuditResult { Timestamp = DateTime.UtcNow }));
        Assert.AreEqual(damaged, File.ReadAllText(path));
    }

    [TestMethod]
    public async Task IndependentWritersPreserveEveryResult()
    {
        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            new AuditHistoryStorage(_directory).SaveResult(new AuditResult { Timestamp = DateTime.UtcNow, HealthScore = index }))));
        var history = new AuditHistoryStorage(_directory).LoadHistory();
        Assert.AreEqual(32, history.Count);
        Assert.AreEqual(32, history.Select(result => result.HealthScore).Distinct().Count());
    }
}
