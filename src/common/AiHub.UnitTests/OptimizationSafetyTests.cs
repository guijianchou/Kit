namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kit.AiHub.Security;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class OptimizationSafetyTests
{
    [TestMethod]
    public async Task DownloadsClassifyFiveGroupsAndLeaveUncertainFilesUntouched()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("Downloads");
        foreach (string name in new[] { "report.PDF", "archive.7z", "setup.msi", "song.flac", "film.mkv", "photo.png", "source.ts", "unknown.bin", "partial.pdf.crdownload", "active.pdf" })
        {
            WriteOld(Path.Combine(root, name));
        }

        File.WriteAllText(Path.Combine(root, "active.pdf.aria2"), "downloading");
        File.WriteAllText(Path.Combine(root, "recent.pdf"), "recent");
        WriteOld(Path.Combine(root, "nested", "inside.pdf"));
        string hidden = Path.Combine(root, "hidden.pdf");
        WriteOld(hidden);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        string locked = Path.Combine(root, "locked.pdf");
        WriteOld(locked);
        using var held = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var items = await new DownloadOrganizerService(root).ScanAsync();
        CollectionAssert.AreEquivalent(new[] { "Documents", "Compressed", "Programs", "Music", "Video" }, items.Select(i => i.Category).ToArray());
        Assert.IsTrue(items.All(i => i.Action == "move"));
        Assert.IsTrue(File.Exists(Path.Combine(root, "photo.png")));
    }

    [TestMethod]
    public async Task DownloadExecutionRevalidatesSnapshotScopeCategoryAndCollisions()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("Downloads");
        string source = Path.Combine(root, "report.pdf");
        WriteOld(source);
        WriteOld(Path.Combine(root, "Documents", "report.pdf"), "existing");
        var service = new DownloadOrganizerService(root);
        var item = (await service.ScanAsync()).Single();
        item.TargetRelativePath = "../escape";
        Assert.IsFalse(service.OrganizeItem(item));
        item.TargetRelativePath = "Music";
        Assert.IsFalse(service.OrganizeItem(item));
        item.TargetRelativePath = "Documents";
        File.AppendAllText(source, "changed");
        Assert.IsFalse(service.OrganizeItem(item));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-10));
        item = (await service.ScanAsync()).Single();
        item.FileName = "../untrusted.pdf";
        Assert.IsTrue(service.OrganizeItem(item));
        Assert.AreEqual("existing", File.ReadAllText(Path.Combine(root, "Documents", "report.pdf")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Documents", "report_1.pdf")));
        item.FilePath = Path.Combine(root, "Documents", "report_1.pdf");
        Assert.IsFalse(service.OrganizeItem(item));
        item.FilePath = fixture.PathFor("outside.pdf");
        WriteOld(item.FilePath);
        Assert.IsFalse(service.OrganizeItem(item));
    }

    [TestMethod]
    public async Task CacheScanOnlyOffersOldKnownLeafFilesAndDeduplicatesTemp()
    {
        using var fixture = new FixtureDirectory();
        string local = fixture.PathFor("Local");
        string temp = Path.Combine(local, "Temp");
        string explorer = Path.Combine(local, "Microsoft", "Windows", "Explorer");
        WriteOld(Path.Combine(temp, "old.tmp"));
        WriteOld(Path.Combine(temp, "nested", "old.temp"));
        WriteOld(Path.Combine(temp, "user.docx"));
        WriteOld(Path.Combine(temp, "partial.crdownload.tmp"));
        File.WriteAllText(Path.Combine(temp, "recent.tmp"), "new");
        WriteOld(Path.Combine(explorer, "thumbcache_32.db"));
        WriteOld(Path.Combine(explorer, "iconcache_32.db"));
        WriteOld(Path.Combine(explorer, "settings.json"));
        WriteOld(Path.Combine(local, "npm-cache", "_cacache", "content-v2", "old-cache"));
        WriteOld(Path.Combine(local, "npm-cache", "configuration.json"));
        var service = new CacheCleanupService(local, temp, _ => throw new AssertFailedException("Scanning must never recycle."));
        var items = await service.ScanAsync();
        CollectionAssert.AreEquivalent(new[] { "old.tmp", "old.temp", "thumbcache_32.db", "old-cache" }, items.Select(i => i.FileName).ToArray());
        Assert.IsTrue(items.All(i => File.Exists(i.FilePath)));
        Assert.AreEqual(items.Count, items.Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [TestMethod]
    public async Task CacheExecutionRejectsChangesLockedFilesFoldersAndOutsidePaths()
    {
        using var fixture = new FixtureDirectory();
        string local = fixture.PathFor("Local");
        string temp = Path.Combine(local, "Temp");
        string path = Path.Combine(temp, "old.tmp");
        WriteOld(path);
        var recycled = new List<string>();
        var service = new CacheCleanupService(local, temp, file =>
        {
            recycled.Add(file);
            return (true, null);
        });
        var item = (await service.ScanAsync()).Single();
        using (var held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsFalse(service.CleanupItem(item).Success);
        }

        File.AppendAllText(path, "changed");
        Assert.IsFalse(service.CleanupItem(item).Success);
        item.FilePath = temp;
        Assert.IsFalse(service.CleanupItem(item).Success);
        item.FilePath = fixture.PathFor("outside.tmp");
        WriteOld(item.FilePath);
        Assert.IsFalse(service.CleanupItem(item).Success);
        Assert.AreEqual(0, recycled.Count);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        item = (await service.ScanAsync()).Single();
        Assert.IsTrue(service.CleanupItem(item).Success);
        CollectionAssert.AreEqual(new[] { path }, recycled);
        Assert.IsTrue(File.Exists(path), "The fixture recycle callback must not delete files.");
    }

    [TestMethod]
    public async Task CandidateLimitAppliesAcrossCacheLocationsAndDownloads()
    {
        using var fixture = new FixtureDirectory();
        string downloads = fixture.PathFor("Downloads");
        string local = fixture.PathFor("Local");
        string temp = Path.Combine(local, "Temp");
        for (int index = 0; index < 505; index++)
        {
            WriteOld(Path.Combine(downloads, $"document-{index}.pdf"));
            WriteOld(Path.Combine(temp, $"old-{index}.tmp"));
        }

        WriteOld(Path.Combine(local, "npm-cache", "_cacache", "extra"));
        Assert.AreEqual(500, (await new DownloadOrganizerService(downloads).ScanAsync()).Count);
        var cleanup = new CacheCleanupService(local, temp, _ => throw new AssertFailedException("No cleanup expected."));
        Assert.AreEqual(506, (await cleanup.ScanAsync()).Count, "Cache categories must not be hidden behind the old 500-file cap.");
    }

    [TestMethod]
    public async Task OptimizationDefaultMigratesButCustomTaskPolicySurvives()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("data");
        var service = new SecurityPolicyService(root);
        await service.SaveTaskPolicyAsync("system-optimization", TaskPolicyDefaults.LegacySystemOptimizationInstructions.ReplaceLineEndings("\r\n") + "\r\n");
        service = new SecurityPolicyService(root);
        Assert.AreEqual(TaskPolicyDefaults.DefaultSystemOptimizationInstructions, await service.LoadTaskAgentsPolicyAsync("aihub", "system-optimization"));
        await service.SaveTaskPolicyAsync("system-optimization", TaskPolicyDefaults.PreviousSystemOptimizationInstructions);
        service = new SecurityPolicyService(root);
        Assert.AreEqual(TaskPolicyDefaults.DefaultSystemOptimizationInstructions, await service.LoadTaskAgentsPolicyAsync("aihub", "system-optimization"));
        await service.SaveTaskPolicyAsync("system-optimization", TaskPolicyDefaults.AiReviewedSystemOptimizationInstructions);
        service = new SecurityPolicyService(root);
        Assert.AreEqual(TaskPolicyDefaults.DefaultSystemOptimizationInstructions, await service.LoadTaskAgentsPolicyAsync("aihub", "system-optimization"));
        await service.SaveTaskPolicyAsync("system-optimization", TaskPolicyDefaults.GroupedSystemOptimizationInstructions);
        service = new SecurityPolicyService(root);
        Assert.AreEqual(TaskPolicyDefaults.DefaultSystemOptimizationInstructions, await service.LoadTaskAgentsPolicyAsync("aihub", "system-optimization"));
        await service.SaveTaskPolicyAsync("system-optimization", TaskPolicyDefaults.ProjectSystemOptimizationInstructions);
        service = new SecurityPolicyService(root);
        Assert.AreEqual(TaskPolicyDefaults.DefaultSystemOptimizationInstructions, await service.LoadTaskAgentsPolicyAsync("aihub", "system-optimization"));
        const string custom = "My custom optimization rules.";
        await service.SaveTaskPolicyAsync("system-optimization", custom);
        service = new SecurityPolicyService(root);
        Assert.AreEqual(custom, await service.LoadTaskAgentsPolicyAsync("aihub", "system-optimization"));
        service.ResetTaskPolicy("system-optimization");
        Assert.AreEqual(TaskPolicyDefaults.DefaultSystemOptimizationInstructions, await service.LoadTaskAgentsPolicyAsync("aihub", "system-optimization"));
    }

    private static void WriteOld(string path, string content = "fixture")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
    }
}
