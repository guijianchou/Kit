namespace Kit.AiHub.UnitTests;

using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WindowsCleanupStandardsTests
{
    [TestMethod]
    public async Task WindowsUserCacheLeavesAndCompilerCachesAreOfferedWithoutNeighboringState()
    {
        using var fixture = new FixtureDirectory();
        string local = fixture.PathFor("Local");
        string temp = Path.Combine(local, "Temp");
        string[] offered =
        [
            Write(local, @"Temp\scratch.tmp"),
            Write(local, @"Temp\compiler\build.obj"),
            Write(local, @"Microsoft\Windows\INetCache\IE\A123\page.htm"),
            Write(local, @"Microsoft\Windows\Explorer\thumbcache_32.db"),
            Write(local, @"D3DSCache\shader.bin"),
            Write(local, @"ccache\a\cache-entry"),
            Write(local, @"Mozilla\sccache\b\cache-entry"),
        ];
        string[] retained =
        [
            Write(local, @"Microsoft\Windows\INetCache\Content.MSO\recovery.bin"),
            Write(local, @"Microsoft\Windows\INetCache\Content.Outlook\draft.msg"),
            Write(local, @"Microsoft\Windows\WebCache\WebCacheV01.dat"),
            Write(local, @"Microsoft\Windows\INetCache\IE\A123\credentials.json"),
            Write(local, @"Microsoft\Windows\Explorer\iconcache_32.db"),
            Write(local, @"ccache\ccache.conf"),
            Write(local, @"Mozilla\sccache\config.toml"),
            Write(local, @"Temp\recent.tmp", daysOld: 1),
            Write(local, @"Microsoft\Windows Defender\Scans\history.tmp"),
            Write(local, @"Microsoft\Windows\DeliveryOptimization\Cache\download.tmp"),
        ];
        var service = new CacheCleanupService(local, temp, _ => throw new AssertFailedException("Scan must not write."));
        var candidates = await service.ScanAsync();

        CollectionAssert.AreEquivalent(offered, candidates.Select(item => item.FilePath).ToArray());
        Assert.AreEqual("Temporary build files", candidates.Single(item => item.FileName == "build.obj").Category);
        Assert.AreEqual("DirectX Shader Cache", candidates.Single(item => item.FileName == "shader.bin").Category);
        Assert.IsTrue(candidates.All(item => item.ReviewState == "pending"));
        Assert.IsTrue(retained.All(File.Exists));
        Assert.IsTrue(service.InventoryReport.Scopes.Any(scope => scope.Id == "temporary-internet" && scope.ObservedBytes > 0));
        Assert.IsTrue(service.InventoryReport.Scopes.Any(scope => scope.Id == "ccache-compiler" && scope.FamilyId == "development"));
    }

    [TestMethod]
    [DataRow("program.cs")]
    [DataRow("program.cpp")]
    [DataRow("program.py")]
    [DataRow("program.js")]
    [DataRow("unknown.xyz")]
    [DataRow("notes.txt")]
    [DataRow("program.cs.tmp")]
    [DataRow("program.py.temp.tmp")]
    [DataRow("private.docx.tmp.tmp")]
    public async Task SourceAndUnknownTempFilesCannotBeScannedOrDeletedByForgedApproval(string name)
    {
        using var fixture = new FixtureDirectory();
        string local = fixture.PathFor("Local");
        string temp = Path.Combine(local, "Temp");
        string path = Write(temp, name);
        int deletes = 0;
        var service = new CacheCleanupService(local, temp, _ =>
        {
            deletes++;
            return (true, null);
        });
        Assert.AreEqual(0, (await service.ScanAsync()).Count);
        var file = new FileInfo(path);
        var forged = new TempFileInfo
        {
            FilePath = path, FileName = name, CacheRoot = temp, Action = "delete", IsSelected = true,
            Risk = RiskLevel.Medium, ReviewState = "approved", SizeInBytes = file.Length, LastModified = file.LastWriteTimeUtc,
        };
        Assert.IsFalse(service.CleanupItem(forged).Success);
        Assert.AreEqual(0, deletes);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task TempProjectArtifactsNeedExplicitDevelopmentEvidenceAndAreRecheckedAtExecution()
    {
        using var fixture = new FixtureDirectory();
        string local = fixture.PathFor("Local");
        string temp = Path.Combine(local, "Temp");
        Write(temp, @"project\sample.csproj");
        Write(temp, @"project\obj\sample.obj");
        Write(temp, @"checkout\.git\HEAD");
        Write(temp, @"checkout\obj\sample.obj");
        string loose = Write(temp, @"compiler\sample.obj");
        int deletes = 0;
        var service = new CacheCleanupService(local, temp, _ =>
        {
            deletes++;
            return (true, null);
        });
        var candidates = await service.ScanAsync();
        Assert.AreEqual(loose, candidates.Single().FilePath);

        // A project introduced after scanning must also protect its old artifacts.
        Write(temp, @"compiler\sample.csproj");
        candidates[0].ReviewState = "approved";
        Assert.IsFalse(service.CleanupItem(candidates[0]).Success);
        Assert.AreEqual(0, deletes);
        Assert.IsTrue(File.Exists(loose));
    }

    [TestMethod]
    public async Task NewCacheLeavesPreserveLockAgeSnapshotAndReparseChecks()
    {
        using var fixture = new FixtureDirectory();
        string local = fixture.PathFor("Local");
        string root = Path.Combine(local, "Mozilla", "sccache");
        string changed = Write(root, "changed");
        string locked = Write(root, "locked");
        string outside = fixture.PathFor("outside");
        Write(outside, "outside-cache");
        fixture.CreateReparseDirectoryLink(Path.Combine(root, "linked"), outside);
        int deletes = 0;
        var service = new CacheCleanupService(local, Path.Combine(local, "Temp"), _ =>
        {
            deletes++;
            return (true, null);
        });
        using (var handle = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var candidates = await service.ScanAsync();
            Assert.AreEqual(changed, candidates.Single().FilePath);
            File.AppendAllText(changed, "changed after scan");
            Assert.IsFalse(service.CleanupItem(candidates[0]).Success);
        }

        Assert.AreEqual(0, deletes);
        Assert.IsTrue(File.Exists(Path.Combine(outside, "outside-cache")));
    }

    [TestMethod]
    public async Task ReviewedCompilerAndInternetCacheSnapshotsCanUseTheExistingExecutionPath()
    {
        using var fixture = new FixtureDirectory();
        string local = fixture.PathFor("Local");
        string[] paths =
        [
            Write(local, @"Temp\build.obj"),
            Write(local, @"ccache\payload"),
            Write(local, @"Microsoft\Windows\INetCache\IE\A123\page.htm"),
        ];
        var deleted = new List<string>();
        var service = new CacheCleanupService(local, Path.Combine(local, "Temp"), path =>
        {
            deleted.Add(path);
            return (true, null);
        });
        foreach (var candidate in await service.ScanAsync())
        {
            candidate.ReviewState = "approved";
            Assert.IsTrue(service.CleanupItem(candidate).Success);
        }

        CollectionAssert.AreEquivalent(paths, deleted.ToArray());
        Assert.IsTrue(paths.All(File.Exists), "The test delegate records actions without deleting files.");
    }

    private static string Write(string root, string relative, int daysOld = 10)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        return path;
    }
}
