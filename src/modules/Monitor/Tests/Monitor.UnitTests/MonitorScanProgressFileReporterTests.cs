// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorScanProgressFileReporterTests
{
    [TestMethod]
    public void ReportWritesProgressJsonWithAtomicTempReplacement()
    {
        using TemporaryDirectory tempDirectory = new();
        string progressPath = Path.Combine(tempDirectory.Path, "scan-progress.json");
        DateTimeOffset startedAt = new(2026, 5, 5, 0, 0, 0, TimeSpan.Zero);
        MonitorScanProgressFileReporter reporter = new(progressPath, TimeSpan.Zero);

        reporter.Report(
            new MonitorScanProgressSnapshot(
                MonitorScanProgressPhase.Hashing,
                filesProcessed: 1,
                filesTotal: 3,
                currentDirectory: tempDirectory.Path,
                startedAt,
                completedAt: null,
                recordCount: null),
            force: true);

        string json = File.ReadAllText(progressPath);
        StringAssert.Contains(json, "\"phase\":\"hashing\"");
        StringAssert.Contains(json, "\"filesProcessed\":1");
        Assert.AreEqual(0, Directory.GetFiles(tempDirectory.Path, "*.tmp").Length, "Atomic progress writes should not leave temp files behind.");

        MonitorScanProgressSnapshot snapshot = MonitorScanProgressFileReporter.Read(progressPath);
        Assert.AreEqual(MonitorScanProgressPhase.Hashing, snapshot.Phase);
        Assert.AreEqual(1, snapshot.FilesProcessed);
        Assert.AreEqual(3, snapshot.FilesTotal);
        Assert.AreEqual(tempDirectory.Path, snapshot.CurrentDirectory);
        Assert.AreEqual(startedAt, snapshot.StartedAt);
    }

    [TestMethod]
    public void ReportHonorsThrottleUnlessForced()
    {
        using TemporaryDirectory tempDirectory = new();
        string progressPath = Path.Combine(tempDirectory.Path, "scan-progress.json");
        DateTimeOffset startedAt = new(2026, 5, 5, 0, 0, 0, TimeSpan.Zero);
        MonitorScanProgressFileReporter reporter = new(progressPath, TimeSpan.FromDays(1));

        reporter.Report(new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Hashing, 1, 3, tempDirectory.Path, startedAt, null, null));
        reporter.Report(new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Hashing, 2, 3, tempDirectory.Path, startedAt, null, null));

        MonitorScanProgressSnapshot throttledSnapshot = MonitorScanProgressFileReporter.Read(progressPath);
        Assert.AreEqual(1, throttledSnapshot.FilesProcessed);

        reporter.Report(new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Completed, 3, 3, tempDirectory.Path, startedAt, startedAt.AddSeconds(1), 3), force: true);

        MonitorScanProgressSnapshot forcedSnapshot = MonitorScanProgressFileReporter.Read(progressPath);
        Assert.AreEqual(MonitorScanProgressPhase.Completed, forcedSnapshot.Phase);
        Assert.AreEqual(3, forcedSnapshot.FilesProcessed);
        Assert.AreEqual(3, forcedSnapshot.RecordCount);
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
