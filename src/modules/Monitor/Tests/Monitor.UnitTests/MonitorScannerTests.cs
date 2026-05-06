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
