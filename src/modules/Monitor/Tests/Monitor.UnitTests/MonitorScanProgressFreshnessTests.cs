// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorScanProgressFreshnessTests
{
    [TestMethod]
    public void GetFreshProgressScanIdReturnsActiveNonTerminalScanId()
    {
        using TemporaryDirectory tempDirectory = new();
        string progressPath = Path.Combine(tempDirectory.Path, "scan-progress.json");
        DateTimeOffset now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
        MonitorScanProgressFileReporter reporter = new(progressPath, TimeSpan.Zero);
        reporter.Report(
            new MonitorScanProgressSnapshot(
                MonitorScanProgressPhase.Hashing,
                filesProcessed: 1,
                filesTotal: 10,
                currentDirectory: tempDirectory.Path,
                startedAt: now.AddMinutes(-1),
                completedAt: null,
                recordCount: null,
                scanId: "active-scan"),
            force: true);

        string scanId = MonitorScanProgressFreshness.GetFreshProgressScanId(progressPath, now);

        Assert.AreEqual("active-scan", scanId);
    }

    [TestMethod]
    public void GetFreshProgressScanIdRejectsTerminalStaleAndUnreadableProgress()
    {
        using TemporaryDirectory tempDirectory = new();
        string progressPath = Path.Combine(tempDirectory.Path, "scan-progress.json");
        DateTimeOffset now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
        MonitorScanProgressFileReporter reporter = new(progressPath, TimeSpan.Zero);
        reporter.Report(
            new MonitorScanProgressSnapshot(
                MonitorScanProgressPhase.Completed,
                filesProcessed: 10,
                filesTotal: 10,
                currentDirectory: tempDirectory.Path,
                startedAt: now.AddMinutes(-1),
                completedAt: now,
                recordCount: 10,
                scanId: "completed-scan"),
            force: true);
        Assert.AreEqual(string.Empty, MonitorScanProgressFreshness.GetFreshProgressScanId(progressPath, now));

        MonitorScanProgressSnapshot staleSnapshot = new(
            MonitorScanProgressPhase.Hashing,
            filesProcessed: 1,
            filesTotal: 10,
            currentDirectory: tempDirectory.Path,
            startedAt: now.Subtract(MonitorStatusStore.StaleRunningScanTimeout).AddSeconds(-1),
            completedAt: null,
            recordCount: null,
            scanId: "stale-scan")
        {
            UpdatedAt = now.Subtract(MonitorStatusStore.StaleRunningScanTimeout).AddSeconds(-1),
        };
        Assert.AreEqual(string.Empty, MonitorScanProgressFreshness.GetFreshProgressScanId(staleSnapshot, now));

        File.WriteAllText(progressPath, "{");
        Assert.AreEqual(string.Empty, MonitorScanProgressFreshness.GetFreshProgressScanId(progressPath, now));
        Assert.AreEqual(string.Empty, MonitorScanProgressFreshness.GetFreshProgressScanId(Path.Combine(tempDirectory.Path, "missing.json"), now));
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
