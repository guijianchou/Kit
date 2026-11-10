// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Compression;
using Kit.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
[DoNotParallelize]
public class BackupArchiveCacheTests
{
    [TestMethod]
    public void SameNamedArchivesInDifferentBackupLocationsKeepTheirOwnContents()
    {
        using var fixture = new ArchiveFixture();
        string first = fixture.BackupDirectory("first");
        string second = fixture.BackupDirectory("second");
        const string fileName = "Kit_settings_20261109_120000.ptb";
        fixture.WriteArchive(first, fileName, "A");
        fixture.WriteArchive(second, fileName, "B");
        var utility = SettingsBackupAndRestoreUtils.Instance;

        Assert.AreEqual("A", utility.GetLatestSettingsBackupManifest(first)["BackupSource"].ToString());
        Assert.AreEqual("B", utility.GetLatestSettingsBackupManifest(second)["BackupSource"].ToString());
        Assert.AreEqual("A", utility.GetLatestSettingsBackupManifest(first)["BackupSource"].ToString());
        Assert.AreEqual(fileName, utility.GetLatestBackupFileName(second));
    }

    [TestMethod]
    public void ReplacedArchiveDoesNotReuseExtractedContentsEvenWhenLengthAndTimestampMatch()
    {
        using var fixture = new ArchiveFixture();
        string directory = fixture.BackupDirectory("replace");
        const string fileName = "Kit_settings_20261109_120000.ptb";
        string path = fixture.WriteArchive(directory, fileName, "A");
        var utility = SettingsBackupAndRestoreUtils.Instance;
        Assert.AreEqual("A", utility.GetLatestSettingsBackupManifest(directory)["BackupSource"].ToString());
        var originalWriteTime = File.GetLastWriteTimeUtc(path);
        long originalLength = new FileInfo(path).Length;

        fixture.WriteArchive(directory, fileName, "B");
        File.SetLastWriteTimeUtc(path, originalWriteTime);
        Assert.AreEqual(originalLength, new FileInfo(path).Length, "The fixture must defeat a length-and-mtime-only cache key.");

        Assert.AreEqual("B", utility.GetLatestSettingsBackupManifest(directory)["BackupSource"].ToString());
    }

    [TestMethod]
    public void CorruptReplacementFallsBackToThePreviousArchiveInsteadOfItsOldExtraction()
    {
        using var fixture = new ArchiveFixture();
        string directory = fixture.BackupDirectory("fallback");
        const string olderName = "Kit_settings_20261109_110000.ptb";
        fixture.WriteArchive(directory, olderName, "older");
        string newest = fixture.WriteArchive(directory, "Kit_settings_20261109_120000.ptb", "newest");
        var utility = SettingsBackupAndRestoreUtils.Instance;
        Assert.AreEqual("newest", utility.GetLatestSettingsBackupManifest(directory)["BackupSource"].ToString());

        File.WriteAllText(newest, "Synthetic corrupt replacement");

        Assert.AreEqual("older", utility.GetLatestSettingsBackupManifest(directory)["BackupSource"].ToString());
        Assert.AreEqual(olderName, utility.GetLatestBackupFileName(directory));
    }

    [TestMethod]
    public void ArchiveEntryCannotEscapeTheExtractionDirectory()
    {
        using var fixture = new ArchiveFixture();
        string directory = fixture.BackupDirectory("traversal");
        using (var archive = ZipFile.Open(Path.Combine(directory, "Kit_settings_20261109_120000.ptb"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open());
            writer.Write("Synthetic escape attempt");
        }

        Assert.IsNull(SettingsBackupAndRestoreUtils.Instance.GetLatestSettingsBackupManifest(directory));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.TempDirectory, "escaped.txt")));
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly string originalTemp = Environment.GetEnvironmentVariable("TEMP");
        private readonly string originalTmp = Environment.GetEnvironmentVariable("TMP");
        private readonly string root = Path.Combine(Path.GetTempPath(), "KitBackupCacheTests_" + Guid.NewGuid().ToString("N"));

        public ArchiveFixture()
        {
            TempDirectory = Path.Combine(root, "Temp");
            Directory.CreateDirectory(TempDirectory);
            Environment.SetEnvironmentVariable("TEMP", TempDirectory);
            Environment.SetEnvironmentVariable("TMP", TempDirectory);
            Assert.AreEqual(Path.TrimEndingDirectorySeparator(TempDirectory), Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
        }

        public string TempDirectory { get; }

        public string BackupDirectory(string name)
        {
            string directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            return directory;
        }

        public string WriteArchive(string directory, string fileName, string source)
        {
            string path = Path.Combine(directory, fileName);
            using var file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            var entry = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("{\"BackupSource\":\"" + source + "\"}");
            return path;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TEMP", originalTemp);
            Environment.SetEnvironmentVariable("TMP", originalTmp);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
