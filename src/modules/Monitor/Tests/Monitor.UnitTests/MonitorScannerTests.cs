// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorScannerTests
{
    [TestMethod]
    public void ScanReusesSha1ForUnchangedFilesAndSkipsExcludedFiles()
    {
        using TemporaryDirectory tempDirectory = new();
        string notesPath = Path.Combine(tempDirectory.Path, "notes.txt");
        File.WriteAllText(notesPath, "hello");
        File.WriteAllText(Path.Combine(tempDirectory.Path, "results.csv"), "ignored");

        DateTime timestamp = new(2026, 4, 25, 12, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(notesPath, timestamp);

        MonitorSettings settings = MonitorSettings.CreateDefault();
        string expectedTimestamp = MonitorScanner.FormatTimestamp(File.GetLastWriteTime(notesPath));
        MonitorFileRecord previousRecord = new(
            "~",
            "~",
            "notes.txt",
            "notes.txt",
            notesPath,
            "cached-sha1",
            expectedTimestamp,
            5,
            "Documents");

        IReadOnlyList<MonitorFileRecord> records = MonitorScanner.Scan(tempDirectory.Path, settings, new[] { previousRecord });

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("Documents", records[0].Category);
        Assert.AreEqual("cached-sha1", records[0].Sha1);
    }

    [TestMethod]
    public void ScanSkipsFilesDeletedAfterEnumerationBegins()
    {
        using TemporaryDirectory tempDirectory = new();
        string notesPath = Path.Combine(tempDirectory.Path, "notes.txt");
        File.WriteAllText(notesPath, "hello");

        IReadOnlyList<MonitorFileRecord> records = MonitorScanner.Scan(
            tempDirectory.Path,
            MonitorSettings.CreateDefault(),
            progressReporter: new DeletingProgressReporter(notesPath));

        Assert.AreEqual(0, records.Count);
    }

    [TestMethod]
    public void ScanRecalculatesHashWhenFileSizeChangedWithinSameTimestampSecond()
    {
        using TemporaryDirectory tempDirectory = new();
        string notesPath = Path.Combine(tempDirectory.Path, "notes.txt");
        File.WriteAllText(notesPath, "hello world");

        DateTime timestamp = new(2026, 4, 25, 12, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(notesPath, timestamp);
        string expectedTimestamp = MonitorScanner.FormatTimestamp(File.GetLastWriteTime(notesPath));
        MonitorFileRecord previousRecord = new(
            "~",
            "~",
            "notes.txt",
            "notes.txt",
            notesPath,
            "cached-sha1",
            expectedTimestamp,
            5,
            "Documents");

        IReadOnlyList<MonitorFileRecord> records = MonitorScanner.Scan(tempDirectory.Path, MonitorSettings.CreateDefault(), new[] { previousRecord });

        Assert.AreEqual(1, records.Count);
        Assert.AreNotEqual("cached-sha1", records[0].Sha1);
    }

    private sealed class DeletingProgressReporter : IMonitorScanProgressReporter
    {
        private readonly string _filePath;
        private bool _deleted;

        public DeletingProgressReporter(string filePath)
        {
            _filePath = filePath;
        }

        public void Report(MonitorScanProgressSnapshot snapshot, bool force = false)
        {
            if (!_deleted)
            {
                File.Delete(_filePath);
                _deleted = true;
            }
        }
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
