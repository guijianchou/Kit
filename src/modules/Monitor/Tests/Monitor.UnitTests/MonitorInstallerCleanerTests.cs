// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorInstallerCleanerTests
{
    private static readonly string[] InstalledAppNames = { "App" };

    [TestMethod]
    public void FindMatchesMatchesInstallerNamesAgainstInstalledSoftware()
    {
        using TemporaryDirectory tempDirectory = new();
        string installerPath = Path.Combine(tempDirectory.Path, "app-setup.exe");
        File.WriteAllText(installerPath, "installer");

        IReadOnlyList<MonitorInstallerMatch> matches = MonitorInstallerCleaner.FindMatches(tempDirectory.Path, InstalledAppNames);

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual(installerPath, matches[0].FilePath);
        Assert.IsTrue(matches[0].Confidence >= 0.9);
    }

    [TestMethod]
    public void FindMatchesRequiresNameAndVersionWhenUsingInstalledSoftwareIndex()
    {
        using TemporaryDirectory tempDirectory = new();
        string matchingInstallerPath = Path.Combine(tempDirectory.Path, "app-1.2.3-setup.exe");
        string wrongVersionInstallerPath = Path.Combine(tempDirectory.Path, "app-2.0.0-setup.exe");
        File.WriteAllText(matchingInstallerPath, "installer");
        File.WriteAllText(wrongVersionInstallerPath, "installer");
        MonitorInstalledSoftwareIndex softwareIndex = new(new[]
        {
            new MonitorInstalledSoftwareEntry("App", "1.2.3", installDate: null, installLocation: null),
        });

        IReadOnlyList<MonitorInstallerMatch> matches = MonitorInstallerCleaner.FindMatches(tempDirectory.Path, softwareIndex);

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual(matchingInstallerPath, matches[0].FilePath);
        Assert.IsTrue(matches[0].Confidence >= 0.9);
    }

    [TestMethod]
    public void FindMatchesIgnoresArchitectureNumbersWhenMatchingInstalledSoftwareVersion()
    {
        using TemporaryDirectory tempDirectory = new();
        string installerPath = Path.Combine(tempDirectory.Path, "app-x64-1.2.3-setup.exe");
        File.WriteAllText(installerPath, "installer");
        MonitorInstalledSoftwareIndex softwareIndex = new(new[]
        {
            new MonitorInstalledSoftwareEntry("App", "1.2.3", installDate: null, installLocation: null),
        });

        IReadOnlyList<MonitorInstallerMatch> matches = MonitorInstallerCleaner.FindMatches(tempDirectory.Path, softwareIndex);

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual(installerPath, matches[0].FilePath);
    }

    [TestMethod]
    public void FindMatchesSkipsVersionlessInstalledSoftwareIndexEntries()
    {
        using TemporaryDirectory tempDirectory = new();
        string installerPath = Path.Combine(tempDirectory.Path, "app-setup.exe");
        File.WriteAllText(installerPath, "installer");
        MonitorInstalledSoftwareIndex softwareIndex = new(new[]
        {
            new MonitorInstalledSoftwareEntry("App", displayVersion: null, installDate: null, installLocation: null),
        });

        IReadOnlyList<MonitorInstallerMatch> matches = MonitorInstallerCleaner.FindMatches(tempDirectory.Path, softwareIndex);

        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public void CleanupDryRunReportsWithoutDeleting()
    {
        using TemporaryDirectory tempDirectory = new();
        string installerPath = Path.Combine(tempDirectory.Path, "app-setup.exe");
        File.WriteAllText(installerPath, "12345");
        MonitorInstallerMatch match = new(installerPath, "App", 0.95, 5);

        MonitorInstallerCleanupResult result = MonitorInstallerCleaner.Cleanup(tempDirectory.Path, new[] { match }, minConfidence: 0.7, dryRun: true);

        Assert.AreEqual(1, result.Deleted);
        Assert.AreEqual(5, result.FreedBytes);
        Assert.IsTrue(File.Exists(installerPath));
    }

    [TestMethod]
    public void CleanupDeleteModeRemovesOnlyFilesUnderProgramsPath()
    {
        using TemporaryDirectory programsDirectory = new();
        using TemporaryDirectory outsideDirectory = new();
        string installerPath = Path.Combine(programsDirectory.Path, "app-setup.exe");
        string outsidePath = Path.Combine(outsideDirectory.Path, "outside-setup.exe");
        File.WriteAllText(installerPath, "12345");
        File.WriteAllText(outsidePath, "outside");

        MonitorInstallerMatch insideMatch = new(installerPath, "App", 0.95, 5);
        MonitorInstallerMatch outsideMatch = new(outsidePath, "Outside", 0.95, 7);

        MonitorInstallerCleanupResult result = MonitorInstallerCleaner.Cleanup(programsDirectory.Path, new[] { insideMatch, outsideMatch }, minConfidence: 0.7, dryRun: false);

        Assert.AreEqual(1, result.Deleted);
        Assert.AreEqual(1, result.Skipped);
        Assert.IsFalse(File.Exists(installerPath));
        Assert.IsTrue(File.Exists(outsidePath));
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
