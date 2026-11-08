namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class OptimizationInventoryTests
{
    [TestMethod]
    public async Task MetadataInventoryCountsRecentOldAndLockedFilesWithoutChangingThem()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("cache");
        string recent = Write(root, "recent.cache", 17, 1);
        string middle = Write(root, "middle.cache", 23, 14);
        string old = Write(root, "old.cache", 31, 40);
        string locked = Write(root, "locked.cache", 41, 60);
        DateTime modified = File.GetLastWriteTimeUtc(locked);
        using (var held = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = await new OptimizationInventoryService([Location("cache", root)], maximumTopFiles: 2).ScanAsync();
            var scope = report.Scopes.Single();
            Assert.AreEqual("complete", scope.State);
            Assert.AreEqual(112L, scope.ObservedBytes);
            Assert.AreEqual(4, scope.FileCount);
            Assert.AreEqual(17L, scope.RecentBytes);
            Assert.AreEqual(72L, scope.OlderBytes);
            CollectionAssert.AreEqual(new[] { "locked.cache", "old.cache" }, scope.TopFiles.Select(file => file.Name).ToArray());
            Assert.IsFalse(report.IsPartial);
        }

        Assert.AreEqual(modified, File.GetLastWriteTimeUtc(locked));
        foreach (var entry in new[] { (recent, 17), (middle, 23), (old, 31), (locked, 41) })
        {
            CollectionAssert.AreEqual(Enumerable.Repeat((byte)0x5a, entry.Item2).ToArray(), File.ReadAllBytes(entry.Item1));
        }
    }

    [TestMethod]
    public async Task DuplicateAndNestedLocationsOwnTheirBytesOnlyOnce()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("parent");
        string child = Path.Combine(root, "nested");
        Write(root, "parent.cache", 11, 14);
        Write(child, "child.cache", 29, 14);
        var service = new OptimizationInventoryService([
            Location("parent", root), Location("child", child), Location("duplicate", child + Path.DirectorySeparatorChar)]);

        var report = await service.ScanAsync();

        Assert.AreEqual(2, report.Scopes.Count);
        Assert.AreEqual(40L, report.Scopes.Sum(scope => scope.ObservedBytes ?? 0));
        Assert.AreEqual(11L, report.Scopes.Single(scope => scope.Id == "parent").ObservedBytes);
        Assert.AreEqual(29L, report.Scopes.Single(scope => scope.Id == "child").ObservedBytes);
        Assert.AreEqual(2, report.Scopes.Sum(scope => scope.FileCount));
        Assert.IsFalse(report.IsPartial);
    }

    [TestMethod]
    public async Task ProtectedSubtreeNeverReachesTheEnumeratorOrParentTotals()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("parent");
        string protectedRoot = Path.Combine(root, "installation");
        string secret = Write(protectedRoot, "keep.dll", 100, 90);
        Write(root, "cache.bin", 7, 14);
        int protectedReads = 0;
        var service = new OptimizationInventoryService([
            Location("parent", root), Location("protected", protectedRoot, "protected")],
            enumerate: path =>
            {
                if (path.Equals(protectedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    protectedReads++;
                    throw new AssertFailedException("Protected contents must not be enumerated.");
                }

                return new DirectoryInfo(path).EnumerateFileSystemInfos();
            });

        var report = await service.ScanAsync();
        var protectedScope = report.Scopes.Single(scope => scope.Id == "protected");
        Assert.AreEqual(0, protectedReads);
        Assert.AreEqual("protected", protectedScope.State);
        Assert.IsNull(protectedScope.ObservedBytes);
        Assert.AreEqual(0, protectedScope.TopFiles.Count);
        Assert.AreEqual(7L, report.Scopes.Sum(scope => scope.ObservedBytes ?? 0));
        Assert.AreEqual(100L, new FileInfo(secret).Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DuplicateRootProtectionWinsRegardlessOfDiscoveryOrder(bool protectedFirst)
    {
        using var fixture = new FixtureDirectory();
        Write(fixture.RootPath, "keep.bin", 20, 40);
        var ordinary = Location("ordinary", fixture.RootPath, "cache");
        var protectedLocation = Location("protected", fixture.RootPath + Path.DirectorySeparatorChar, "protected");
        var locations = protectedFirst ? new[] { protectedLocation, ordinary } : new[] { ordinary, protectedLocation };
        var report = await new OptimizationInventoryService(locations,
            enumerate: _ => throw new AssertFailedException("A duplicate cannot reopen a protected root.")).ScanAsync();

        Assert.AreEqual(1, report.Scopes.Count);
        Assert.AreEqual("protected", report.Scopes[0].State);
        Assert.IsNull(report.Scopes[0].ObservedBytes);
        Assert.AreEqual(0, report.ExaminedEntries);
    }

    [TestMethod]
    public async Task ProtectedParentPreventsSeparatelyListedCacheFromReopeningItsSubtree()
    {
        using var fixture = new FixtureDirectory();
        string child = fixture.PathFor("installation", "cache");
        Write(child, "keep.bin", 20, 40);
        var report = await new OptimizationInventoryService([
            Location("child", child, "cache"), Location("parent", fixture.PathFor("installation"), "protected")],
            enumerate: _ => throw new AssertFailedException("Protected descendants must not be enumerated.")).ScanAsync();

        Assert.AreEqual(2, report.Scopes.Count);
        Assert.IsTrue(report.Scopes.All(scope => scope.State == "protected" && !scope.ObservedBytes.HasValue));
        Assert.AreEqual(0, report.ExaminedEntries);
    }

    [TestMethod]
    public async Task EnumeratorCannotCountFilesOrFollowDirectoriesOutsideItsScope()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("scope");
        string inside = Write(root, "inside.cache", 7, 40);
        string siblingRoot = fixture.PathFor("scope-sibling");
        string outside = Write(siblingRoot, "outside.cache", 100, 40);
        var service = new OptimizationInventoryService([Location("scope", root)], enumerate: path =>
        {
            Assert.AreEqual(root, path, "An out-of-scope directory must never be queued for traversal.");
            return new FileSystemInfo[] { new FileInfo(inside), new FileInfo(outside), new DirectoryInfo(siblingRoot), new DirectoryInfo(root) };
        });

        var scope = (await service.ScanAsync()).Scopes.Single();
        Assert.AreEqual("partial", scope.State);
        Assert.AreEqual(7L, scope.ObservedBytes);
        Assert.AreEqual(1, scope.FileCount);
        Assert.AreEqual(3, scope.SkippedEntries);
        Assert.AreEqual(inside, scope.TopFiles.Single().Path);
    }

    [TestMethod]
    public async Task CoreAndBroadRootsAreProtectedBeforeAnyEnumeration()
    {
        string[] roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.GetPathRoot(Path.GetTempPath()),
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();
        var locations = roots.Select((root, index) => Location("root-" + index, root, "cache")).ToList();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            locations.Add(Location("core-child", Path.Combine(windows, "WinSxS", "cache"), "cache"));
            locations.Add(Location("managed-child", Path.Combine(windows, "Temp", "arbitrary-subfolder"), "cache"));
        }

        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            locations.Add(Location("installer-cache", Path.Combine(programData, "Package Cache"), "cache"));
        }

        Assert.IsTrue(locations.Count > 0);
        foreach (var location in locations)
        {
            // Isolate every root so a protected drive/parent cannot mask a missing
            // automatic guard on another location in this test.
            var report = await new OptimizationInventoryService([location],
                enumerate: _ => throw new AssertFailedException("System and broad roots must not be traversed.")).ScanAsync();
            Assert.AreEqual("protected", report.Scopes.Single().State, location.Path);
            Assert.IsNull(report.Scopes.Single().ObservedBytes, location.Path);
            Assert.AreEqual(0, report.ExaminedEntries, location.Path);
        }
    }

    [TestMethod]
    public async Task EmptyMissingUnavailableAndPartialLocationsKeepDistinctStates()
    {
        using var fixture = new FixtureDirectory();
        string empty = fixture.PathFor("empty");
        string denied = fixture.PathFor("denied");
        string partial = fixture.PathFor("partial");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(denied);
        Write(partial, "readable.cache", 21, 10);
        var service = new OptimizationInventoryService([
            Location("empty", empty), Location("missing", fixture.PathFor("missing")),
            Location("denied", denied), Location("partial", partial)],
            enumerate: path => path == denied ? throw new UnauthorizedAccessException("fixture")
                : path == partial ? PartiallyReadable(path) : new DirectoryInfo(path).EnumerateFileSystemInfos());

        var report = await service.ScanAsync();
        Assert.AreEqual("complete", report.Scopes[0].State);
        Assert.AreEqual(0L, report.Scopes[0].ObservedBytes);
        Assert.AreEqual("missing", report.Scopes[1].State);
        Assert.IsNull(report.Scopes[1].ObservedBytes);
        Assert.AreEqual("unavailable", report.Scopes[2].State);
        Assert.IsNull(report.Scopes[2].ObservedBytes);
        Assert.AreEqual(1, report.Scopes[2].AccessFailures);
        Assert.AreEqual("partial", report.Scopes[3].State);
        Assert.AreEqual(21L, report.Scopes[3].ObservedBytes);
        Assert.AreEqual(1, report.Scopes[3].AccessFailures);
        Assert.IsTrue(report.IsPartial);

        static IEnumerable<FileSystemInfo> PartiallyReadable(string path)
        {
            yield return new FileInfo(Path.Combine(path, "readable.cache"));
            throw new IOException("Fixture stopped enumeration.");
        }
    }

    [TestMethod]
    public async Task GlobalEntryBudgetLeavesUnvisitedLocationsUnknown()
    {
        using var fixture = new FixtureDirectory();
        string first = fixture.PathFor("first");
        string second = fixture.PathFor("second");
        for (int index = 0; index < 5; index++)
        {
            Write(first, index + ".cache", 10, 10);
        }

        Write(second, "keep.cache", 100, 10);
        var report = await new OptimizationInventoryService([Location("first", first), Location("second", second)], maximumEntries: 2).ScanAsync();
        Assert.AreEqual(2, report.ExaminedEntries);
        Assert.AreEqual(2, report.Scopes[0].FileCount);
        Assert.AreEqual(20L, report.Scopes[0].ObservedBytes);
        Assert.AreEqual("partial", report.Scopes[0].State);
        Assert.AreEqual("partial", report.Scopes[1].State);
        Assert.IsNull(report.Scopes[1].ObservedBytes);
        Assert.IsTrue(report.IsPartial);
    }

    [TestMethod]
    public async Task PerLocationAndScopeCountBudgetsRemainBounded()
    {
        using var fixture = new FixtureDirectory();
        string first = fixture.PathFor("first");
        string second = fixture.PathFor("second");
        for (int index = 0; index < 4; index++)
        {
            Write(first, index + ".cache", 10, 10);
        }

        Write(second, "one.cache", 20, 10);
        var locations = new[] { Location("first", first), Location("second", second), Location("missing", fixture.PathFor("missing")) };
        var report = await new OptimizationInventoryService(locations, maximumEntriesPerScope: 2, maximumScopes: 2).ScanAsync();
        Assert.AreEqual(2, report.Scopes.Count);
        Assert.AreEqual(2, report.Scopes[0].FileCount);
        Assert.AreEqual("partial", report.Scopes[0].State);
        Assert.AreEqual(1, report.Scopes[1].FileCount);
        Assert.AreEqual("complete", report.Scopes[1].State);
        Assert.IsTrue(report.IsPartial);
    }

    [TestMethod]
    public async Task ExhaustedTimeBudgetDoesNotEnumerateOrPretendZeroBytes()
    {
        using var fixture = new FixtureDirectory();
        var report = await new OptimizationInventoryService([Location("expired", fixture.RootPath)], maximumDuration: TimeSpan.Zero,
            enumerate: _ => throw new AssertFailedException("Expired work must not enumerate.")).ScanAsync();
        Assert.IsTrue(report.IsPartial);
        Assert.AreEqual(0, report.ExaminedEntries);
        Assert.AreEqual("partial", report.Scopes.Single().State);
        Assert.IsNull(report.Scopes.Single().ObservedBytes);
    }

    [TestMethod]
    public async Task CancellationDuringEnumerationDoesNotPublishAPartialSuccess()
    {
        using var fixture = new FixtureDirectory();
        Write(fixture.RootPath, "first.cache", 10, 10);
        using var cancellation = new CancellationTokenSource();
        var service = new OptimizationInventoryService([Location("cancel", fixture.RootPath)], enumerate: path => CancelAfterFirst(path));
        try
        {
            await service.ScanAsync(cancellation.Token);
            Assert.Fail("A cancelled inventory must not return a completed report.");
        }
        catch (OperationCanceledException)
        {
            Assert.IsTrue(cancellation.IsCancellationRequested);
        }

        IEnumerable<FileSystemInfo> CancelAfterFirst(string path)
        {
            yield return new FileInfo(Path.Combine(path, "first.cache"));
            cancellation.Cancel();
            yield return new FileInfo(Path.Combine(path, "first.cache"));
        }
    }

    [TestMethod]
    public async Task CancellationAtTheEndOfEmptyEnumerationStillPreventsPublication()
    {
        using var fixture = new FixtureDirectory();
        using var cancellation = new CancellationTokenSource();
        var service = new OptimizationInventoryService([Location("cancel", fixture.RootPath)], enumerate: _ => CancelAtEnd());
        try
        {
            await service.ScanAsync(cancellation.Token);
            Assert.Fail("Cancellation after the last iterator move must not publish a report.");
        }
        catch (OperationCanceledException)
        {
            Assert.IsTrue(cancellation.IsCancellationRequested);
        }

        IEnumerable<FileSystemInfo> CancelAtEnd()
        {
            cancellation.Cancel();
            yield break;
        }
    }

    [TestMethod]
    public async Task DefaultDiscoveryIncludesOnlyBrowserCacheLeavesAndProtectsUserState()
    {
        using var fixture = new FixtureDirectory();
        string local = fixture.PathFor("Local");
        string user = fixture.PathFor("User");
        string roaming = fixture.PathFor("Roaming");
        string chrome = Path.Combine(local, "Google", "Chrome", "User Data");
        Write(Path.Combine(chrome, "Profile 2", "Cache"), "payload", 10, 40);
        Write(Path.Combine(chrome, "Profile 2"), "Cookies", 200, 40);
        Write(Path.Combine(chrome, "Guest Profile", "Cache"), "guest", 300, 40);
        Write(Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "GPUCache"), "payload", 20, 40);
        Write(Path.Combine(local, "Mozilla", "Firefox", "Profiles", "fixture.default", "cache2"), "payload", 30, 40);
        Write(Path.Combine(user, ".pi"), "credentials.json", 400, 40);
        Write(Path.Combine(local, "CrashDumps"), "crash.dmp", 40, 40);
        var report = await new OptimizationInventoryService(local, Path.Combine(local, "Temp"), user, roaming).ScanAsync();

        Assert.AreEqual(60L, report.Scopes.Where(scope => scope.FamilyId == "browsers").Sum(scope => scope.ObservedBytes ?? 0));
        Assert.IsFalse(report.Scopes.Any(scope => scope.RootPath.Contains("Guest Profile", StringComparison.Ordinal)));
        Assert.AreEqual("protected", report.Scopes.Single(scope => scope.Id == "pi-state").State);
        Assert.IsNull(report.Scopes.Single(scope => scope.Id == "pi-state").ObservedBytes);
        Assert.AreEqual("analysis-only", report.Scopes.Single(scope => scope.Id == "crash-dumps").Disposition);
        Assert.AreEqual(40L, report.Scopes.Single(scope => scope.Id == "crash-dumps").ObservedBytes);
    }

    [TestMethod]
    public async Task NewInventoryLocationsDoNotAuthorizeCleanupEvenWithForgedApproval()
    {
        using var fixture = new FixtureDirectory();
        string local = fixture.PathFor("Local");
        string crash = Write(Path.Combine(local, "CrashDumps"), "crash.dmp", 20, 40);
        string brave = Write(Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Cache"), "payload", 30, 40);
        int deletions = 0;
        var service = new CacheCleanupService(local, Path.Combine(local, "Temp"), _ =>
        {
            deletions++;
            return (true, null);
        });
        var candidates = await service.ScanAsync();
        Assert.AreEqual(0, candidates.Count);
        Assert.IsTrue(service.InventoryReport.Scopes.Any(scope => scope.ObservedBytes == 20));
        foreach (string path in new[] { crash, brave })
        {
            var file = new FileInfo(path);
            var forged = new TempFileInfo
            {
                FilePath = path, FileName = file.Name, CacheRoot = file.DirectoryName!, Category = "Temporary files",
                Action = "delete", ReviewState = "approved", Risk = RiskLevel.Low, SizeInBytes = file.Length, LastModified = file.LastWriteTimeUtc,
            };
            Assert.IsFalse(service.CleanupItem(forged).Success);
            Assert.IsTrue(File.Exists(path));
        }

        Assert.AreEqual(0, deletions);
    }

    [TestMethod]
    public void CorePathProtectionUsesDirectoryBoundariesAndNormalizesTraversal()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string root = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            Assert.IsTrue(CacheCleanupService.IsProtectedCorePath(root));
            Assert.IsTrue(CacheCleanupService.IsProtectedCorePath(Path.Combine(root, "Temp", "old.tmp")));
            Assert.IsTrue(CacheCleanupService.IsProtectedCorePath(Path.Combine(root, "other", "..", "Temp")));
            Assert.IsFalse(CacheCleanupService.IsProtectedCorePath(Path.TrimEndingDirectorySeparator(root) + "-fixture"));
        }

        using var fixture = new FixtureDirectory();
        Assert.IsFalse(CacheCleanupService.IsProtectedCorePath(fixture.PathFor("User", "cache")));
    }

    private static OptimizationInventoryLocation Location(string id, string path, string disposition = "analysis-only") =>
        new(id, "system", id, id, path, disposition);

    private static string Write(string root, string name, int length, int ageDays)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5a, length).ToArray());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }
}
