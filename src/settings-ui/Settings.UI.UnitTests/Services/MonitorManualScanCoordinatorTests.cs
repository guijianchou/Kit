// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

using Microsoft.PowerToys.Settings.UI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using MonitorCore = Microsoft.PowerToys.Monitor;

namespace ServicesTests
{
    [TestClass]
    public sealed class MonitorManualScanCoordinatorTests
    {
        [TestMethod]
        public void RestoreManualScanProgressIfRunningKeepsFreshLongRunningProgress()
        {
            using TemporaryDirectory tempDirectory = new();
            string progressPath = Path.Combine(tempDirectory.Path, "scan-progress.json");
            string statusDatabasePath = Path.Combine(tempDirectory.Path, "monitor-status.db");
            string scanId = "manual-fresh-progress";
            DateTimeOffset startedAt = DateTimeOffset.UtcNow.Subtract(MonitorCore.MonitorStatusStore.StaleRunningScanTimeout).AddMinutes(-5);

            _ = MonitorCore.MonitorStatusStore.BeginRun(statusDatabasePath, scanId, MonitorCore.MonitorScanTrigger.Manual, startedAt);
            WriteProgress(
                progressPath,
                new MonitorCore.MonitorScanProgressSnapshot(
                    MonitorCore.MonitorScanProgressPhase.Hashing,
                    filesProcessed: 2,
                    filesTotal: 10,
                    currentDirectory: tempDirectory.Path,
                    startedAt: startedAt,
                    completedAt: null,
                    recordCount: null,
                    scanId: scanId));

            MonitorManualScanCoordinator coordinator = CreateCoordinator(progressPath, statusDatabasePath);

            MonitorManualScanProgressUpdate progressUpdate = coordinator.RestoreManualScanProgressIfRunning();

            Assert.IsNotNull(progressUpdate);
            Assert.AreEqual(scanId, progressUpdate.ScanId);
            Assert.IsTrue(progressUpdate.IsRunning);
            Assert.IsFalse(progressUpdate.ShouldStopTimer);
            Assert.IsTrue(MonitorCore.MonitorStatusStore.TryGetLatestRun(statusDatabasePath, out MonitorCore.MonitorStatusRun latestRun));
            Assert.IsNotNull(latestRun);
            Assert.AreEqual(MonitorCore.MonitorScanStatus.Running, latestRun.Status);
        }

        [TestMethod]
        public void RestoreManualScanProgressIfRunningDoesNotFailFreshBackgroundProgress()
        {
            using TemporaryDirectory tempDirectory = new();
            string progressPath = Path.Combine(tempDirectory.Path, "scan-progress.json");
            string statusDatabasePath = Path.Combine(tempDirectory.Path, "monitor-status.db");
            string scanId = "background-fresh-progress";
            DateTimeOffset startedAt = DateTimeOffset.UtcNow.Subtract(MonitorCore.MonitorStatusStore.StaleRunningScanTimeout).AddMinutes(-5);

            _ = MonitorCore.MonitorStatusStore.BeginRun(statusDatabasePath, scanId, MonitorCore.MonitorScanTrigger.Background, startedAt);
            WriteProgress(
                progressPath,
                new MonitorCore.MonitorScanProgressSnapshot(
                    MonitorCore.MonitorScanProgressPhase.Hashing,
                    filesProcessed: 4,
                    filesTotal: 20,
                    currentDirectory: tempDirectory.Path,
                    startedAt: startedAt,
                    completedAt: null,
                    recordCount: null,
                    scanId: scanId));

            MonitorManualScanCoordinator coordinator = CreateCoordinator(progressPath, statusDatabasePath);

            MonitorManualScanProgressUpdate progressUpdate = coordinator.RestoreManualScanProgressIfRunning();

            Assert.IsNull(progressUpdate);
            Assert.IsTrue(MonitorCore.MonitorStatusStore.TryGetLatestRun(statusDatabasePath, out MonitorCore.MonitorStatusRun latestRun));
            Assert.IsNotNull(latestRun);
            Assert.AreEqual(MonitorCore.MonitorScanTrigger.Background, latestRun.Trigger);
            Assert.AreEqual(MonitorCore.MonitorScanStatus.Running, latestRun.Status);
        }

        [TestMethod]
        public void FailManualScanStartRecordsFailedManualStatus()
        {
            using TemporaryDirectory tempDirectory = new();
            string progressPath = Path.Combine(tempDirectory.Path, "scan-progress.json");
            string statusDatabasePath = Path.Combine(tempDirectory.Path, "monitor-status.db");
            MonitorManualScanCoordinator coordinator = CreateCoordinator(progressPath, statusDatabasePath);

            MonitorManualScanProgressUpdate started = coordinator.StartManualScanProgress();
            MonitorManualScanProgressUpdate failed = coordinator.FailManualScanStart();

            Assert.AreEqual(started.ScanId, failed.ScanId);
            Assert.IsFalse(failed.IsRunning);
            Assert.IsTrue(failed.ShouldStopTimer);
            Assert.IsTrue(failed.ShouldRefreshStatus);
            AssertFailedLatestRun(statusDatabasePath, started.ScanId, "Scan failed to start");
        }

        [TestMethod]
        public void FailedProgressSnapshotWithoutStatusRunRecordsFailedManualStatus()
        {
            using TemporaryDirectory tempDirectory = new();
            string progressPath = Path.Combine(tempDirectory.Path, "scan-progress.json");
            string statusDatabasePath = Path.Combine(tempDirectory.Path, "monitor-status.db");
            MonitorManualScanCoordinator coordinator = CreateCoordinator(progressPath, statusDatabasePath);

            MonitorManualScanProgressUpdate started = coordinator.StartManualScanProgress();
            WriteProgress(
                progressPath,
                new MonitorCore.MonitorScanProgressSnapshot(
                    MonitorCore.MonitorScanProgressPhase.Failed,
                    filesProcessed: 0,
                    filesTotal: 0,
                    currentDirectory: tempDirectory.Path,
                    startedAt: DateTimeOffset.UtcNow,
                    completedAt: DateTimeOffset.UtcNow,
                    recordCount: null,
                    scanId: started.ScanId,
                    message: "Scan failed to start"));

            MonitorManualScanProgressUpdate failed = coordinator.UpdateManualScanProgress();

            Assert.AreEqual(started.ScanId, failed.ScanId);
            Assert.IsFalse(failed.IsRunning);
            Assert.IsTrue(failed.ShouldStopTimer);
            Assert.IsTrue(failed.ShouldRefreshStatus);
            AssertFailedLatestRun(statusDatabasePath, started.ScanId, "Scan failed to start");
        }

        [TestMethod]
        public void SummaryStaleRefreshKeepsFreshProgressRunRunning()
        {
            using TemporaryDirectory tempDirectory = new();
            string progressPath = Path.Combine(tempDirectory.Path, "scan-progress.json");
            string statusDatabasePath = Path.Combine(tempDirectory.Path, "monitor-status.db");
            string scanId = "summary-fresh-progress";
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset startedAt = now.Subtract(MonitorCore.MonitorStatusStore.StaleRunningScanTimeout).AddMinutes(-5);

            _ = MonitorCore.MonitorStatusStore.BeginRun(statusDatabasePath, scanId, MonitorCore.MonitorScanTrigger.Manual, startedAt);
            WriteProgress(
                progressPath,
                new MonitorCore.MonitorScanProgressSnapshot(
                    MonitorCore.MonitorScanProgressPhase.Hashing,
                    filesProcessed: 1,
                    filesTotal: 10,
                    currentDirectory: tempDirectory.Path,
                    startedAt: startedAt,
                    completedAt: null,
                    recordCount: null,
                    scanId: scanId));

            MonitorStatusQueryService statusQueryService = new();

            MonitorCore.MonitorStatusSummary summary = statusQueryService.GetSummaryWithStaleRefresh(
                statusDatabasePath,
                progressPath,
                MonitorCore.MonitorStatusRange.SevenDays,
                now);

            Assert.AreEqual(MonitorCore.MonitorScanStatus.Running, summary.OverallStatus);
            Assert.AreEqual(1, summary.TotalRuns);
            Assert.AreEqual(0, summary.FailedRuns);
            Assert.IsTrue(MonitorCore.MonitorStatusStore.TryGetLatestRun(statusDatabasePath, out MonitorCore.MonitorStatusRun latestRun));
            Assert.AreEqual(MonitorCore.MonitorScanStatus.Running, latestRun.Status);
        }

        private static MonitorManualScanCoordinator CreateCoordinator(string progressPath, string statusDatabasePath)
        {
            return new MonitorManualScanCoordinator(
                new MonitorProgressSnapshotReader(),
                new MonitorStatusQueryService(),
                progressPath,
                statusDatabasePath);
        }

        private static void WriteProgress(string progressPath, MonitorCore.MonitorScanProgressSnapshot snapshot)
        {
            MonitorCore.MonitorScanProgressFileReporter reporter = new(progressPath, TimeSpan.Zero);
            reporter.Report(snapshot, force: true);
        }

        private static void AssertFailedLatestRun(string statusDatabasePath, string scanId, string expectedMessage)
        {
            Assert.IsTrue(MonitorCore.MonitorStatusStore.TryGetLatestRun(statusDatabasePath, out MonitorCore.MonitorStatusRun latestRun));
            Assert.IsNotNull(latestRun);
            Assert.AreEqual(scanId, latestRun.ScanId);
            Assert.AreEqual(MonitorCore.MonitorScanTrigger.Manual, latestRun.Trigger);
            Assert.AreEqual(MonitorCore.MonitorScanStatus.Failed, latestRun.Status);

            MonitorCore.MonitorStatusSummary summary = MonitorCore.MonitorStatusStore.GetSummary(statusDatabasePath, MonitorCore.MonitorStatusRange.SevenDays, DateTimeOffset.UtcNow);
            Assert.AreEqual(MonitorCore.MonitorScanStatus.Failed, summary.OverallStatus);
            Assert.AreEqual(1, summary.FailedRuns);
            Assert.AreEqual(expectedMessage, summary.LastMessage);
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
}
