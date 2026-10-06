namespace Kit.AiHub.UnitTests;

using System.Diagnostics;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class OptimizationExpandedScopeTests
{
    [TestMethod]
    public async Task ExpandedCachesDeleteOnlySnapshotsAndPreserveNewFilesAndSettings()
    {
        using var fixture = new FixtureDirectory();
        string local = fixture.PathFor("Local");
        string roaming = fixture.PathFor("Roaming");
        string user = fixture.PathFor("User");
        string cache = Path.Combine(roaming, "Code", "CachedData");
        Old(Path.Combine(cache, "old-cache"));
        Old(Path.Combine(local, "D3DSCache", "shader"));
        string settings = Path.Combine(roaming, "Code", "User", "settings.json");
        Old(settings);
        var service = new CacheCleanupService(local, fixture.PathFor("Temp"), CacheCleanupService.DeleteFile, user, roaming);
        var items = await service.ScanAsync();
        Assert.AreEqual(2, items.Count);
        string added = Path.Combine(cache, "new-cache");
        Old(added);
        foreach (var item in items)
        {
            Assert.IsTrue(service.CleanupItem(item).Success);
        }

        Assert.IsTrue(File.Exists(settings));
        Assert.IsTrue(File.Exists(added), "Files created after the scan are not targets.");
        Assert.IsTrue(items.All(item => !File.Exists(item.FilePath)));
        Assert.IsFalse(Directory.Exists(Path.Combine(local, "D3DSCache")), "An empty verified cache can be removed non-recursively.");
    }

    [TestMethod]
    public async Task NugetSkipsActiveVersionsAndInvalidatesRestoreMarkerBeforePayload()
    {
        using var fixture = new FixtureDirectory();
        string user = fixture.PathFor("User");
        string package = Path.Combine(user, ".nuget", "packages", "sample", "1.0");
        string active = Path.Combine(user, ".nuget", "packages", "sample", "2.0");
        string marker = Path.Combine(package, ".nupkg.metadata");
        string payload = Path.Combine(package, "lib", "sample.dll");
        Old(marker);
        Old(payload);
        Old(Path.Combine(active, ".nupkg.metadata"));
        Directory.CreateDirectory(Path.Combine(active, "lib"));
        File.WriteAllText(Path.Combine(active, "lib", "sample.dll"), "recent");
        var deleted = new List<string>();
        var service = new CacheCleanupService(fixture.PathFor("Local"), fixture.PathFor("Temp"), path =>
        {
            deleted.Add(path);
            return CacheCleanupService.DeleteFile(path);
        }, user);
        var items = await service.ScanAsync();
        Assert.AreEqual(2, items.Count);
        Assert.IsTrue(service.CleanupItem(items.Single(item => item.FilePath == payload)).Success);
        CollectionAssert.AreEqual(new[] { marker, payload }, deleted);
        Assert.IsTrue(File.Exists(Path.Combine(active, ".nupkg.metadata")));
        Assert.AreEqual(0, (await service.ScanAsync()).Count);
        Assert.IsTrue(service.UserDirectorySummaries.Single().Contains(".nuget", StringComparison.Ordinal), "Excluded packages must still appear in directory inventory.");
    }

    [TestMethod]
    public async Task DevelopmentScanRequiresIgnoredArtifactsAndRechecksGitBeforeDeletion()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("Codings");
        string repo = Path.Combine(root, "Example");
        Directory.CreateDirectory(repo);
        try
        {
            Git(repo, "init", "--quiet");
            Old(Path.Combine(repo, "Example.sln"));
            Old(Path.Combine(repo, ".gitignore"), "x64/\nobj/\n__pycache__/\n");
            string eligible = Path.Combine(repo, "x64", "Debug", "cache.pch");
            string tracked = Path.Combine(repo, "x64", "Debug", "tracked.obj");
            Old(eligible);
            Old(tracked);
            Old(Path.Combine(repo, "x64", "Debug", "program.exe"));
            Old(Path.Combine(repo, "source.obj"));
            Old(Path.Combine(repo, "Example.csproj"));
            Old(Path.Combine(repo, "obj", "project.assets.json"));
            Old(Path.Combine(repo, "__pycache__", "module.cpython-314.pyc"));
            Old(Path.Combine(repo, "module.py"));
            Old(Path.Combine(repo, "__pycache__", "orphan.cpython-314.pyc"));
            Git(repo, "add", "-f", "x64/Debug/tracked.obj");
            var service = new CacheCleanupService(fixture.PathFor("Local"), fixture.PathFor("Temp"), CacheCleanupService.DeleteFile, developmentRoot: root);
            var items = await service.ScanAsync();
            CollectionAssert.AreEquivalent(new[] { "cache.pch", "project.assets.json", "module.cpython-314.pyc" }, items.Select(item => item.FileName).ToArray());
            Assert.IsTrue(items.All(item => !string.IsNullOrWhiteSpace(item.ItemId) && !string.IsNullOrWhiteSpace(item.ReasonEn) && !string.IsNullOrWhiteSpace(item.ReasonZh)));
            Assert.AreEqual(items.Count, items.Select(item => item.ItemId).Distinct().Count());
            Git(repo, "add", "-f", "x64/Debug/cache.pch");
            Assert.IsFalse(service.CleanupItem(items.Single(item => item.FilePath == eligible)).Success);
            Assert.IsTrue(File.Exists(eligible));
            File.Delete(Path.Combine(repo, "module.py"));
            Assert.IsFalse(service.CleanupItem(items.Single(item => item.FileName.EndsWith(".pyc", StringComparison.Ordinal))).Success);
            Assert.IsTrue(service.CleanupItem(items.Single(item => item.FileName == "project.assets.json")).Success);
        }
        finally
        {
            string gitRoot = Path.Combine(repo, ".git");
            if (Directory.Exists(gitRoot))
            {
                foreach (string path in Directory.EnumerateFiles(gitRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                }
            }
        }
    }

    [TestMethod]
    public async Task DevelopmentBuildOutputsRequireManifestEvidenceAndOneHourInactivity()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("Codings");
        string repo = Path.Combine(root, "Example");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "--quiet");
        Old(Path.Combine(repo, "Example.sln"));
        string project = Path.Combine(repo, "Example.csproj");
        Old(project);
        Old(Path.Combine(repo, ".gitignore"), "x64/\nobj/\npublish/\n");
        string debug = Path.Combine(repo, "x64", "Debug");
        string release = Path.Combine(repo, "x64", "Release");
        string[] outputs = [Path.Combine(debug, "app.exe"), Path.Combine(release, "app.dll"), Path.Combine(debug, "app.pdb"), Path.Combine(debug, "app.deps.json")];
        string header = Path.Combine(debug, "cache.pchast");
        string recent = Path.Combine(debug, "recent.obj");
        string[] excluded = [Path.Combine(debug, "source.cs"), Path.Combine(debug, "settings.json"), Path.Combine(repo, "publish", "Debug", "app.dll"), fixture.PathFor("outside.dll")];
        foreach (string path in outputs.Concat(excluded).Append(header).Append(recent).Append(Path.Combine(debug, "unlisted.dll")))
        {
            Old(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
        }

        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddMinutes(-30));
        string manifest = Path.Combine(repo, "obj", "Example.csproj.FileListAbsolute.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, string.Join(Environment.NewLine, outputs.Concat(excluded)));
        var service = new CacheCleanupService(fixture.PathFor("Local"), fixture.PathFor("Temp"), CacheCleanupService.DeleteFile, developmentRoot: root);
        var items = await service.ScanAsync();
        CollectionAssert.AreEquivalent(outputs.Append(header).ToArray(), items.Select(item => item.FilePath).ToArray());
        Assert.AreEqual(debug, items.Single(item => item.FilePath == header).CacheRoot);
        Assert.AreEqual("MSBuild outputs", items.Single(item => item.FilePath == outputs[0]).Category);
        Assert.IsTrue(service.CleanupItem(items.Single(item => item.FilePath == header)).Success, "The cleanup age must match the scanner's one-hour rule.");
        Assert.IsTrue(service.CleanupItem(items.Single(item => item.FilePath == outputs[0])).Success);
        using (File.Open(outputs[1], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsFalse(service.CleanupItem(items.Single(item => item.FilePath == outputs[1])).Success);
        }

        File.AppendAllText(outputs[2], "changed");
        Assert.IsFalse(service.CleanupItem(items.Single(item => item.FilePath == outputs[2])).Success);
        File.WriteAllText(manifest, outputs[2]);
        Assert.IsFalse(service.CleanupItem(items.Single(item => item.FilePath == outputs[1])).Success, "Removed manifest membership must invalidate cleanup.");
        File.WriteAllText(manifest, string.Join(Environment.NewLine, outputs));
        File.Delete(project);
        Assert.IsFalse(service.CleanupItem(items.Single(item => item.FilePath == outputs[3])).Success, "Missing project evidence must invalidate cleanup.");
        Assert.IsTrue(excluded.Append(recent).Append(Path.Combine(debug, "unlisted.dll")).All(File.Exists));
    }

    [TestMethod]
    public async Task UserDirectoryInventoryPreservesStateAndOnlyCleansKnownCacheSubpaths()
    {
        using var fixture = new FixtureDirectory();
        string user = fixture.PathFor("AnotherUser");
        string[] caches = [Path.Combine(user, ".dotnet", "sdk-advertising", "10", "manifest.json"), Path.Combine(user, ".cache", "uv", "archive", "wheel")];
        string[] retained = [Path.Combine(user, ".dsh", ".credentials.yaml"), Path.Combine(user, ".pi", "agent", "auth.json"), Path.Combine(user, ".dotnet", "tools", "tool.exe"), Path.Combine(user, ".cache", "codex-runtimes", "runtime.exe"), Path.Combine(user, ".templateengine", "packages", "template.nupkg")];
        foreach (string path in caches.Concat(retained))
        {
            Old(path);
        }

        var service = new CacheCleanupService(fixture.PathFor("Local"), fixture.PathFor("Temp"), CacheCleanupService.DeleteFile, user);
        var items = await service.ScanAsync();
        CollectionAssert.AreEquivalent(caches, items.Select(item => item.FilePath).ToArray());
        Assert.AreEqual(5, service.UserDirectorySummaries.Count);
        Assert.IsTrue(service.UserDirectorySummaries.Any(summary => summary.Contains(".dsh", StringComparison.Ordinal)));
        Assert.IsTrue(service.UserDirectorySummaries.Any(summary => summary.Contains(".pi", StringComparison.Ordinal)));
        foreach (var item in items)
        {
            Assert.IsTrue(service.CleanupItem(item).Success);
        }

        Assert.IsTrue(retained.All(File.Exists));
        Assert.IsFalse(service.CleanupItem(new Kit.AIHubLib.Models.TempFileInfo { FilePath = retained[0], Action = "delete" }).Success);
        await service.ScanAsync();
        string runtimeSummary = service.UserDirectorySummaries.Single(summary => summary.Contains(".cache", StringComparison.Ordinal));
        Assert.IsTrue(runtimeSummary.Contains("No verified cache path", StringComparison.Ordinal) || runtimeSummary.Contains("未发现已验证缓存路径", StringComparison.Ordinal));
    }

    private static void Old(string path, string content = "fixture")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
    }

    private static void Git(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = repository, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(10000));
        Assert.AreEqual(0, process.ExitCode);
    }
}
