// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Reflection;
using ManagedCommon;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
public class LogRetentionTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(20)]
    public void CleanupPreservesCurrentPreviousAndRecentVersions(int previousAgeDays)
    {
        using var fixture = new LogFixture();
        string current = fixture.Add("3.5.4.0", 60);
        string previous = fixture.Add("3.5.3.0", previousAgeDays);
        string expired = fixture.Add("3.4.0.0", 40);
        fixture.Cleanup(current);

        Assert.IsTrue(File.Exists(Path.Combine(current, "Log.log")), "The current version is always protected, even after inactivity.");
        Assert.IsTrue(File.Exists(Path.Combine(previous, "Log.log")), "The previous version must remain available across the upgrade boundary.");
        Assert.IsFalse(Directory.Exists(expired));

        string recent = fixture.Add("3.5.2.0", 3);
        File.SetLastWriteTimeUtc(Path.Combine(previous, "Log.log"), DateTime.UtcNow);
        fixture.Cleanup(current);
        Assert.IsTrue(File.Exists(Path.Combine(recent, "Log.log")), "Another version used within fourteen days must not be pruned.");
    }

    [TestMethod]
    public void CleanupPreservesUnknownFoldersAndNonLogContent()
    {
        using var fixture = new LogFixture();
        string current = fixture.Add("3.5.4.0", 0);
        fixture.Add("3.5.3.0", 1);
        string unknown = fixture.Add("diagnostics", 90);
        string mixed = fixture.Add("3.0.0.0", 90);
        File.WriteAllText(Path.Combine(mixed, "report.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(mixed, "report.json"), DateTime.UtcNow.AddDays(-90));
        Directory.SetLastWriteTimeUtc(mixed, DateTime.UtcNow.AddDays(-90));
        string nested = fixture.Add("2.0.0.0", 90);
        Directory.CreateDirectory(Path.Combine(nested, "saved"));
        File.WriteAllText(Path.Combine(nested, "saved", "user-data.txt"), "preserve");
        Directory.SetLastWriteTimeUtc(nested, DateTime.UtcNow.AddDays(-90));

        fixture.Cleanup(current);

        Assert.IsTrue(File.Exists(Path.Combine(unknown, "Log.log")));
        Assert.IsTrue(File.Exists(Path.Combine(mixed, "Log.log")));
        Assert.IsTrue(File.Exists(Path.Combine(mixed, "report.json")));
        Assert.IsTrue(File.Exists(Path.Combine(nested, "saved", "user-data.txt")));
    }

    private sealed class LogFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "KitLogRetentionTests_" + Guid.NewGuid().ToString("N"));

        public string Add(string version, int ageDays)
        {
            string directory = Path.Combine(root, version);
            Directory.CreateDirectory(directory);
            string log = Path.Combine(directory, "Log.log");
            File.WriteAllText(log, "retained diagnostic evidence");
            File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddDays(-ageDays));
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-ageDays));
            return directory;
        }

        public void Cleanup(string current) => typeof(Logger).GetMethod("DeleteOldVersionLogFolders", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { root, current });

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
