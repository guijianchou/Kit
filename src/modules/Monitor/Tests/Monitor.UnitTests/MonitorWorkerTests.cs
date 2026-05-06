// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorWorkerTests
{
    private static readonly string[] AppSoftwareNames = { "App" };

    [TestMethod]
    public void RunOnceScansWithoutMovingRootFilesWhenOrganizeIsFalse()
    {
        using TemporaryDirectory tempDirectory = new();
        string sourcePath = Path.Combine(tempDirectory.Path, "setup.exe");
        string csvPath = Path.Combine(tempDirectory.Path, "results.csv");
        File.WriteAllText(sourcePath, "installer");

        MonitorWorkerResult result = MonitorWorker.RunOnce(
            tempDirectory.Path,
            csvPath,
            MonitorSettings.CreateDefault(),
            organize: false,
            cleanInstallers: false);

        Assert.AreEqual(1, result.RecordCount);
        Assert.IsTrue(File.Exists(sourcePath));
        Assert.IsTrue(File.Exists(csvPath));
        Assert.IsNull(result.OrganizeResult);
        Assert.IsNull(result.InstallerCleanupResult);
    }

    [TestMethod]
    public void RunOnceCreatesCategoryFoldersBeforeScanningWithoutMovingFiles()
    {
        using TemporaryDirectory tempDirectory = new();
        MonitorSettings settings = MonitorSettings.CreateDefault();
        string sourcePath = Path.Combine(tempDirectory.Path, "notes.pdf");
        string csvPath = Path.Combine(tempDirectory.Path, "results.csv");
        File.WriteAllText(sourcePath, "document");

        MonitorWorkerResult result = MonitorWorker.RunOnce(
            tempDirectory.Path,
            csvPath,
            settings,
            organize: false,
            cleanInstallers: false);

        Assert.AreEqual(1, result.RecordCount);
        Assert.IsTrue(File.Exists(sourcePath));
        foreach (string categoryName in settings.Categories.Keys)
        {
            Assert.IsTrue(Directory.Exists(Path.Combine(tempDirectory.Path, categoryName)), categoryName + " folder should be created before CSV write.");
        }

        IReadOnlyList<MonitorFileRecord> records = MonitorCsvStore.Load(csvPath, tempDirectory.Path, settings);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("Documents", records[0].Category);
        Assert.AreEqual("notes.pdf", records[0].RelativePath);
    }

    [TestMethod]
    public void RunOnceOrganizesOnlyWhenRequested()
    {
        using TemporaryDirectory tempDirectory = new();
        string sourcePath = Path.Combine(tempDirectory.Path, "notes.pdf");
        string csvPath = Path.Combine(tempDirectory.Path, "results.csv");
        File.WriteAllText(sourcePath, "document");

        MonitorWorkerResult result = MonitorWorker.RunOnce(
            tempDirectory.Path,
            csvPath,
            MonitorSettings.CreateDefault(),
            organize: true,
            cleanInstallers: false);

        Assert.AreEqual(1, result.OrganizeResult?.Organized);
        Assert.IsFalse(File.Exists(sourcePath));
        Assert.IsTrue(File.Exists(Path.Combine(tempDirectory.Path, "Documents", "notes.pdf")));

        IReadOnlyList<MonitorFileRecord> records = MonitorCsvStore.Load(csvPath, tempDirectory.Path, MonitorSettings.CreateDefault());
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("Documents", records[0].FolderName);
        Assert.AreEqual("Documents", records[0].Category);
        Assert.AreEqual("Documents/notes.pdf", records[0].RelativePath);
    }

    [TestMethod]
    public void RunOnceCleansInstallersWhenRequested()
    {
        using TemporaryDirectory tempDirectory = new();
        string programsPath = Path.Combine(tempDirectory.Path, "Programs");
        string installerPath = Path.Combine(programsPath, "app-setup.exe");
        string csvPath = Path.Combine(tempDirectory.Path, "results.csv");
        Directory.CreateDirectory(programsPath);
        File.WriteAllText(installerPath, "installer");

        MonitorWorkerResult result = MonitorWorker.RunOnce(
            tempDirectory.Path,
            csvPath,
            MonitorSettings.CreateDefault(),
            organize: false,
            cleanInstallers: true,
            installedSoftwareNames: AppSoftwareNames);

        Assert.AreEqual(1, result.InstallerCleanupResult?.Deleted);
        Assert.IsFalse(File.Exists(installerPath));
    }

    [TestMethod]
    public void RunOnceReportsScanProgressThroughCsvWriteAndCompletion()
    {
        using TemporaryDirectory tempDirectory = new();
        string sourcePath = Path.Combine(tempDirectory.Path, "notes.pdf");
        string csvPath = Path.Combine(tempDirectory.Path, "results.csv");
        File.WriteAllText(sourcePath, "document");
        RecordingProgressReporter progressReporter = new();

        MonitorWorkerResult result = MonitorWorker.RunOnce(
            tempDirectory.Path,
            csvPath,
            MonitorSettings.CreateDefault(),
            organize: false,
            cleanInstallers: false,
            progressReporter: progressReporter);

        Assert.AreEqual(1, result.RecordCount);
        CollectionAssert.Contains(progressReporter.Phases, MonitorScanProgressPhase.Hashing);
        CollectionAssert.Contains(progressReporter.Phases, MonitorScanProgressPhase.Writing);
        CollectionAssert.Contains(progressReporter.Phases, MonitorScanProgressPhase.Completed);

        MonitorScanProgressSnapshot completed = progressReporter.Snapshots.Last();
        Assert.AreEqual(MonitorScanProgressPhase.Completed, completed.Phase);
        Assert.AreEqual(1, completed.FilesProcessed);
        Assert.AreEqual(1, completed.FilesTotal);
        Assert.AreEqual(1, completed.RecordCount);
        Assert.IsNotNull(completed.CompletedAt);
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

    private sealed class RecordingProgressReporter : IMonitorScanProgressReporter
    {
        public List<MonitorScanProgressSnapshot> Snapshots { get; } = new();

        public List<string> Phases => Snapshots.Select(snapshot => snapshot.Phase).ToList();

        public void Report(MonitorScanProgressSnapshot snapshot, bool force = false)
        {
            Snapshots.Add(snapshot);
        }
    }
}
