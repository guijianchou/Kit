// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorCsvStoreTests
{
    [TestMethod]
    public void SaveAndLoadRoundTripsPythonCompatibleColumns()
    {
        using TemporaryDirectory tempDirectory = new();
        string fullPath = Path.Combine(tempDirectory.Path, "Pictures", "image,one.png");
        MonitorFileRecord record = new(
            "~",
            "Pictures",
            "image,one.png",
            "Pictures/image,one.png",
            fullPath,
            "abc123",
            "2026-04-25T12:00:00",
            42,
            "Pictures");

        string csvPath = Path.Combine(tempDirectory.Path, "results.csv");

        MonitorCsvStore.Save(csvPath, new[] { record });
        IReadOnlyList<MonitorFileRecord> loaded = MonitorCsvStore.Load(csvPath, tempDirectory.Path, MonitorSettings.CreateDefault());

        string csvText = File.ReadAllText(csvPath);
        StringAssert.StartsWith(csvText, "path,rel_path,folder_name,filename,sha1sum,mtime_iso,file_size");
        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual("Pictures/image,one.png", loaded[0].RelativePath);
        Assert.AreEqual("abc123", loaded[0].Sha1);
        Assert.AreEqual(42, loaded[0].FileSize);
    }

    [TestMethod]
    public void LoadReturnsEmptyWhenCsvIsMalformed()
    {
        using TemporaryDirectory tempDirectory = new();
        string csvPath = Path.Combine(tempDirectory.Path, "results.csv");
        File.WriteAllText(csvPath, "path,rel_path,folder_name,filename,sha1sum,mtime_iso,file_size" + Environment.NewLine + "\"unterminated");

        IReadOnlyList<MonitorFileRecord> loaded = MonitorCsvStore.Load(csvPath, tempDirectory.Path, MonitorSettings.CreateDefault());

        Assert.AreEqual(0, loaded.Count);
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
