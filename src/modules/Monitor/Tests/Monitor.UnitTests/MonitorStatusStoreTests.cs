// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorStatusStoreTests
{
    [TestMethod]
    public void GetSummaryReturnsStatusCountsAndRangeCells()
    {
        using TemporaryDirectory tempDirectory = new();
        string databasePath = Path.Combine(tempDirectory.Path, "monitor-status.db");
        DateTimeOffset now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

        long oldRun = MonitorStatusStore.BeginRun(databasePath, "old", MonitorScanTrigger.Background, now.AddDays(-40));
        MonitorStatusStore.CompleteRun(databasePath, oldRun, MonitorScanStatus.Success, now.AddDays(-40).AddMinutes(1), 10, 0, null);

        long successRun = MonitorStatusStore.BeginRun(databasePath, "success", MonitorScanTrigger.Manual, now.AddDays(-2));
        MonitorStatusStore.CompleteRun(databasePath, successRun, MonitorScanStatus.Success, now.AddDays(-2).AddMinutes(1), 12, 0, null);

        long warningRun = MonitorStatusStore.BeginRun(databasePath, "warning", MonitorScanTrigger.Background, now.AddDays(-1));
        MonitorStatusStore.CompleteRun(databasePath, warningRun, MonitorScanStatus.Warning, now.AddDays(-1).AddMinutes(1), 9, 2, "Completed with warnings");

        long failedRun = MonitorStatusStore.BeginRun(databasePath, "failed", MonitorScanTrigger.Manual, now);
        MonitorStatusStore.CompleteRun(databasePath, failedRun, MonitorScanStatus.Failed, now.AddMinutes(1), null, 0, "Scan failed");

        MonitorStatusSummary sevenDaySummary = MonitorStatusStore.GetSummary(databasePath, MonitorStatusRange.SevenDays, now);
        Assert.AreEqual(MonitorScanStatus.Failed, sevenDaySummary.OverallStatus);
        Assert.AreEqual(3, sevenDaySummary.TotalRuns);
        Assert.AreEqual(1, sevenDaySummary.SuccessRuns);
        Assert.AreEqual(1, sevenDaySummary.WarningRuns);
        Assert.AreEqual(1, sevenDaySummary.FailedRuns);
        Assert.AreEqual(7, sevenDaySummary.Days.Count);

        MonitorStatusSummary allSummary = MonitorStatusStore.GetSummary(databasePath, MonitorStatusRange.All, now);
        Assert.AreEqual(4, allSummary.TotalRuns);
        Assert.IsTrue(allSummary.Days.Count >= 41, "All range should include the whole recorded history through today.");
    }

    [TestMethod]
    public void MarkStaleRunningScansAsFailedMarksUnexpectedExit()
    {
        using TemporaryDirectory tempDirectory = new();
        string databasePath = Path.Combine(tempDirectory.Path, "monitor-status.db");
        DateTimeOffset now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

        _ = MonitorStatusStore.BeginRun(databasePath, "stale", MonitorScanTrigger.Background, now.AddHours(-2));

        int marked = MonitorStatusStore.MarkStaleRunningScansAsFailed(databasePath, now.AddMinutes(-30), now);
        MonitorStatusSummary summary = MonitorStatusStore.GetSummary(databasePath, MonitorStatusRange.SevenDays, now);

        Assert.AreEqual(1, marked);
        Assert.AreEqual(MonitorScanStatus.Failed, summary.OverallStatus);
        Assert.AreEqual(1, summary.FailedRuns);
        Assert.AreEqual("Unexpected exit", summary.LastMessage);
    }

    [TestMethod]
    public void RefreshStaleRunningScansMarksOnlyRunsPastSharedTimeout()
    {
        using TemporaryDirectory tempDirectory = new();
        string databasePath = Path.Combine(tempDirectory.Path, "monitor-status.db");
        DateTimeOffset now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

        _ = MonitorStatusStore.BeginRun(databasePath, "fresh", MonitorScanTrigger.Background, now.AddMinutes(-5));
        _ = MonitorStatusStore.BeginRun(databasePath, "stale", MonitorScanTrigger.Background, now.Subtract(MonitorStatusStore.StaleRunningScanTimeout).AddSeconds(-1));

        int marked = MonitorStatusStore.RefreshStaleRunningScans(databasePath, now);
        MonitorStatusSummary summary = MonitorStatusStore.GetSummary(databasePath, MonitorStatusRange.SevenDays, now);

        Assert.AreEqual(1, marked);
        Assert.AreEqual(MonitorScanStatus.Running, summary.OverallStatus);
        Assert.AreEqual(1, summary.FailedRuns);
        Assert.AreEqual(2, summary.TotalRuns);
    }

    [TestMethod]
    public void TryGetSummaryDoesNotCreateMissingDatabase()
    {
        using TemporaryDirectory tempDirectory = new();
        string databasePath = Path.Combine(tempDirectory.Path, "monitor-status.db");
        DateTimeOffset now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

        bool exists = MonitorStatusStore.TryGetSummary(databasePath, MonitorStatusRange.SevenDays, now, out MonitorStatusSummary summary);

        Assert.IsFalse(exists);
        Assert.IsFalse(File.Exists(databasePath));
        Assert.AreEqual(0, summary.TotalRuns);
        Assert.AreEqual(7, summary.Days.Count);
    }

    [TestMethod]
    public void TryGetLatestRunReturnsMostRecentRunIdentityTriggerAndStatus()
    {
        using TemporaryDirectory tempDirectory = new();
        string databasePath = Path.Combine(tempDirectory.Path, "monitor-status.db");
        DateTimeOffset now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

        long oldRun = MonitorStatusStore.BeginRun(databasePath, "old-background", MonitorScanTrigger.Background, now.AddMinutes(-10));
        MonitorStatusStore.CompleteRun(databasePath, oldRun, MonitorScanStatus.Success, now.AddMinutes(-9), 10, 0, null);
        _ = MonitorStatusStore.BeginRun(databasePath, "manual-running", MonitorScanTrigger.Manual, now);

        bool exists = MonitorStatusStore.TryGetLatestRun(databasePath, out MonitorStatusRun? latestRun);

        Assert.IsTrue(exists);
        Assert.IsNotNull(latestRun);
        Assert.AreEqual("manual-running", latestRun.ScanId);
        Assert.AreEqual(MonitorScanTrigger.Manual, latestRun.Trigger);
        Assert.AreEqual(MonitorScanStatus.Running, latestRun.Status);
        Assert.AreEqual(now, latestRun.StartedAt);
    }

    [TestMethod]
    public void TryGetLatestRunDoesNotCreateMissingDatabase()
    {
        using TemporaryDirectory tempDirectory = new();
        string databasePath = Path.Combine(tempDirectory.Path, "monitor-status.db");

        bool exists = MonitorStatusStore.TryGetLatestRun(databasePath, out MonitorStatusRun? latestRun);

        Assert.IsFalse(exists);
        Assert.IsNull(latestRun);
        Assert.IsFalse(File.Exists(databasePath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KitMonitorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
