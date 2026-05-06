// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorSettingsLoaderTests
{
    [TestMethod]
    public void LoadOrDefaultReadsPowerToysMonitorSettingsJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "kit-monitor-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "settings.json");
        string downloadsPath = Path.Combine(root, "Downloads");

        try
        {
            string settingsJson = $$"""
                {
                  "name": "Monitor",
                  "properties": {
                    "downloadsPath": { "value": "{{downloadsPath.Replace("\\", "\\\\")}}" },
                    "csvFileName": { "value": "inventory.csv" },
                    "scanIntervalSeconds": { "value": 300 },
                    "maxFileSizeMegabytes": { "value": 12 },
                    "hashAlgorithm": { "value": "SHA256" },
                    "useIncrementalHashing": { "value": false },
                    "runInBackground": { "value": true },
                    "organizeDownloads": { "value": false },
                    "cleanInstallers": { "value": false }
                  }
                }
                """;
            File.WriteAllText(settingsPath, settingsJson);

            MonitorSettings settings = MonitorSettingsLoader.LoadOrDefault(settingsPath);

            Assert.AreEqual(downloadsPath, settings.DownloadsPath);
            Assert.AreEqual("inventory.csv", settings.CsvPath);
            Assert.AreEqual(300, settings.IntervalSeconds);
            Assert.AreEqual(12, settings.MaxFileSizeForSha1Mb);
            Assert.AreEqual("SHA256", settings.HashAlgorithm);
            Assert.IsFalse(settings.IncrementalScan);
            Assert.IsTrue(settings.RunInBackground);
            Assert.IsFalse(settings.AutoOrganize);
            Assert.IsFalse(settings.AutoCleanInstallers);
            CollectionAssert.Contains(settings.ExcludedFiles.ToList(), "inventory.csv");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DefaultSettingsDoNotAutoCleanInstallers()
    {
        MonitorSettings settings = MonitorSettings.CreateDefault();

        Assert.IsFalse(settings.AutoCleanInstallers);
    }

    [TestMethod]
    public void DefaultSettingsDoNotRunInBackground()
    {
        MonitorSettings settings = MonitorSettings.CreateDefault();

        Assert.IsFalse(settings.RunInBackground);
    }
}
