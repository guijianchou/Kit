// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorFileOrganizerTests
{
    [TestMethod]
    public void OrganizeMovesRootFilesToCategoryFoldersAndRenamesConflicts()
    {
        using TemporaryDirectory tempDirectory = new();
        string sourcePath = Path.Combine(tempDirectory.Path, "setup.exe");
        string programsPath = Path.Combine(tempDirectory.Path, "Programs");
        Directory.CreateDirectory(programsPath);
        File.WriteAllText(sourcePath, "new");
        File.WriteAllText(Path.Combine(programsPath, "setup.exe"), "existing");

        MonitorFileOrganizerResult result = MonitorFileOrganizer.Organize(tempDirectory.Path, MonitorSettings.CreateDefault(), dryRun: false);

        Assert.AreEqual(1, result.Organized);
        Assert.IsFalse(File.Exists(sourcePath));
        Assert.IsTrue(File.Exists(Path.Combine(programsPath, "setup_1.exe")));
    }

    [TestMethod]
    public void OrganizeDryRunReportsMoveWithoutMovingFile()
    {
        using TemporaryDirectory tempDirectory = new();
        string sourcePath = Path.Combine(tempDirectory.Path, "notes.pdf");
        File.WriteAllText(sourcePath, "document");

        MonitorFileOrganizerResult result = MonitorFileOrganizer.Organize(tempDirectory.Path, MonitorSettings.CreateDefault(), dryRun: true);

        Assert.AreEqual(1, result.Organized);
        Assert.IsTrue(File.Exists(sourcePath));
        Assert.IsFalse(Directory.Exists(Path.Combine(tempDirectory.Path, "Documents")));
    }

    [TestMethod]
    public void OrganizeCreatesMissingCategoryFoldersEvenWhenThereAreNoRootFiles()
    {
        using TemporaryDirectory tempDirectory = new();
        MonitorSettings settings = MonitorSettings.CreateDefault();

        MonitorFileOrganizerResult result = MonitorFileOrganizer.Organize(tempDirectory.Path, settings, dryRun: false);

        Assert.AreEqual(0, result.TotalFiles);
        Assert.AreEqual(0, result.Errors);
        foreach (string categoryName in settings.Categories.Keys)
        {
            Assert.IsTrue(Directory.Exists(Path.Combine(tempDirectory.Path, categoryName)), categoryName + " folder should be created.");
        }
    }

    [TestMethod]
    public void IsPathInsideRootRejectsOutsideTargets()
    {
        using TemporaryDirectory tempDirectory = new();
        string outsidePath = Path.GetFullPath(Path.Combine(tempDirectory.Path, "..", "outside"));

        Assert.IsFalse(MonitorFileOrganizer.IsPathInsideRoot(tempDirectory.Path, outsidePath));
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
