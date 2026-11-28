using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

public sealed class CacheCleanupService
{
    private static readonly TimeSpan MinimumAge = TimeSpan.FromDays(7);
    private readonly CacheLocation[] _whitelist;
    private readonly Func<string, (bool Success, string? Error)> _delete;
    private readonly string? _developmentRoot;
    private readonly string? _userProfile;
    private readonly OptimizationInventoryService _inventory;
    public List<string> UserDirectorySummaries { get; } = new();
    public OptimizationInventoryReport InventoryReport { get; private set; } = new();
    private readonly Dictionary<string, TempFileInfo> _packageMarkers = new(StringComparer.OrdinalIgnoreCase);
    public bool ScanTruncated { get; private set; }

    private sealed record CacheLocation(
        string Name,
        string Path,
        RiskLevel Risk,
        bool TempOnly = false,
        bool ThumbnailsOnly = false,
        bool TempResidual = false,
        string EvidenceKind = "cache-whitelist",
        bool TemporaryBuildOnly = false);

    public CacheCleanupService()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath(), DeleteFile,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetEnvironmentVariable("KIT_DEV_ROOT"))
    {
        _inventory = new OptimizationInventoryService();
    }

    internal CacheCleanupService(string localAppData, string tempPath, Func<string, (bool Success, string? Error)> delete,
        string? userProfile = null, string? roamingAppData = null, string? developmentRoot = null)
    {
        _delete = delete;
        _developmentRoot = developmentRoot;
        _userProfile = userProfile;
        _inventory = new OptimizationInventoryService(localAppData, tempPath, userProfile, roamingAppData);
        List<CacheLocation> locations =
        [
            new("Temporary build files", Path.GetFullPath(tempPath), RiskLevel.Medium, EvidenceKind: "temp-residual", TemporaryBuildOnly: true),
            new("Temporary build files", Path.Combine(localAppData, "Temp"), RiskLevel.Medium, EvidenceKind: "temp-residual", TemporaryBuildOnly: true),
            new("Temporary files", Path.GetFullPath(tempPath), RiskLevel.Low, TempOnly: true),
            new("Temporary files", Path.Combine(localAppData, "Temp"), RiskLevel.Low, TempOnly: true),
            new("Temporary residuals", Path.GetFullPath(tempPath), RiskLevel.Medium, TempResidual: true, EvidenceKind: "temp-residual"),
            new("Temporary residuals", Path.Combine(localAppData, "Temp"), RiskLevel.Medium, TempResidual: true, EvidenceKind: "temp-residual"),
            new("Chrome cache", Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Cache"), RiskLevel.Medium),
            new("Edge cache", Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default", "Cache"), RiskLevel.Medium),
            new("npm cache", Path.Combine(localAppData, "npm-cache", "_cacache"), RiskLevel.Low),
            new("pip HTTP cache", Path.Combine(localAppData, "pip", "cache", "http"), RiskLevel.Low),
            new("pip HTTP cache", Path.Combine(localAppData, "pip", "cache", "http-v2"), RiskLevel.Low),
            new("pip wheel cache", Path.Combine(localAppData, "pip", "cache", "wheels"), RiskLevel.Low),
            new("pnpm cache", Path.Combine(localAppData, "pnpm", "cache"), RiskLevel.Low),
            new("pnpm cache", Path.Combine(localAppData, "pnpm-cache"), RiskLevel.Low, EvidenceKind: "cache-residual"),
            new("Go build cache", Path.Combine(localAppData, "go-build"), RiskLevel.Medium, EvidenceKind: "cache-residual"),
            new("ccache compiler cache", Path.Combine(localAppData, "ccache"), RiskLevel.Medium, EvidenceKind: "cache-residual"),
            new("sccache compiler cache", Path.Combine(localAppData, "Mozilla", "sccache"), RiskLevel.Medium, EvidenceKind: "cache-residual"),
            new("Temporary Internet files", Path.Combine(localAppData, "Microsoft", "Windows", "INetCache", "IE"), RiskLevel.Medium, EvidenceKind: "cache-residual"),
            new("Yarn cache", Path.Combine(localAppData, "Yarn", "Cache"), RiskLevel.Low, EvidenceKind: "cache-residual"),
            new("Thumbnail cache", Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"), RiskLevel.Medium, ThumbnailsOnly: true),
            new("Crash dumps", Path.Combine(localAppData, "CrashDumps"), RiskLevel.Medium, EvidenceKind: "cache-residual"),
            new("Windows error reports", Path.Combine(localAppData, "Microsoft", "Windows", "WER"), RiskLevel.Medium, EvidenceKind: "cache-residual"),
            new("uv cache", Path.Combine(localAppData, "uv", "cache"), RiskLevel.Medium, EvidenceKind: "cache-residual"),
        ];
        foreach (string browser in new[] { Path.Combine("Microsoft", "Edge"), Path.Combine("Google", "Chrome") })
        {
            string name = browser.Contains("Edge", StringComparison.Ordinal) ? "Edge" : "Chrome";
            foreach (string cache in new[] { "Code Cache", "GPUCache" })
                locations.Add(new($"{name} {cache}", Path.Combine(localAppData, browser, "User Data", "Default", cache), RiskLevel.Medium));
            locations.Add(new($"{name} GrShaderCache", Path.Combine(localAppData, browser, "User Data", "Default", "GrShaderCache"), RiskLevel.Medium, EvidenceKind: "cache-residual"));
            locations.Add(new($"{name} Service Worker cache", Path.Combine(localAppData, browser, "User Data", "Default", "Service Worker", "CacheStorage"), RiskLevel.Medium, EvidenceKind: "cache-residual"));
        }
        foreach (var browser in new[]
        {
            ("Chrome", Path.Combine(localAppData, "Google", "Chrome", "User Data"), false),
            ("Edge", Path.Combine(localAppData, "Microsoft", "Edge", "User Data"), false),
            ("Brave", Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data"), true),
        })
        {
            foreach (string profile in DiscoverBrowserProfiles(browser.Item2, chromium: true, includeDefault: browser.Item3))
            {
                string profileName = Path.GetFileName(profile);
                foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache", "GrShaderCache" })
                    locations.Add(new($"{browser.Item1} {profileName} {cache}", Path.Combine(profile, cache), RiskLevel.Medium, EvidenceKind: "cache-residual"));
                locations.Add(new($"{browser.Item1} {profileName} Service Worker cache", Path.Combine(profile, "Service Worker", "CacheStorage"), RiskLevel.Medium, EvidenceKind: "cache-residual"));
            }
        }
        foreach (string profile in DiscoverBrowserProfiles(Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles"), chromium: false, includeDefault: true))
            locations.Add(new($"Firefox {Path.GetFileName(profile)} cache", Path.Combine(profile, "cache2"), RiskLevel.Medium, EvidenceKind: "cache-residual"));
        locations.Add(new("DirectX Shader Cache", Path.Combine(localAppData, "D3DSCache"), RiskLevel.Medium));
        foreach (string cache in new[] { @"AMD\DxCache", @"AMD\GLCache", @"NVIDIA\DXCache", @"NVIDIA\GLCache", @"NuGet\v3-cache" })
            locations.Add(new(cache.Replace('\\', ' '), Path.Combine(localAppData, cache), RiskLevel.Medium));
        if (!string.IsNullOrEmpty(userProfile))
        {
            locations.Add(new("NuGet packages", Path.Combine(userProfile, ".nuget", "packages"), RiskLevel.Medium));
            locations.Add(new("NVIDIA DXCache", Path.Combine(userProfile, "AppData", "LocalLow", "NVIDIA", "PerDriverVersion", "DXCache"), RiskLevel.Medium, EvidenceKind: "cache-residual"));
            locations.Add(new("NVIDIA DXCache", Path.Combine(userProfile, "AppData", "LocalLow", "NVIDIA", "DXCache"), RiskLevel.Medium, EvidenceKind: "cache-residual"));
            locations.Add(new("NVIDIA GLCache", Path.Combine(userProfile, "AppData", "LocalLow", "NVIDIA", "GLCache"), RiskLevel.Medium, EvidenceKind: "cache-residual"));
            foreach (var entry in new[]
            {
                (".NET workload download metadata", @".dotnet\sdk-advertising", "cache-whitelist"),
                (".NET telemetry cache", @".dotnet\TelemetryStorageService", "cache-whitelist"),
                ("uv cache", @".cache\uv", "cache-whitelist"),
                ("npm cache", @".npm\_cacache", "cache-whitelist"),
                ("pip HTTP cache", @".cache\pip\http", "cache-whitelist"),
                ("pip HTTP cache", @".cache\pip\http-v2", "cache-whitelist"),
                ("pip wheel cache", @".cache\pip\wheels", "cache-whitelist"),
                ("Gradle caches", @".gradle\caches", "cache-residual"),
                ("Cargo registry cache", @".cargo\registry\cache", "cache-residual"),
            })
                locations.Add(new(entry.Item1, Path.Combine(userProfile, entry.Item2), RiskLevel.Medium, EvidenceKind: entry.Item3));
        }
        if (!string.IsNullOrEmpty(roamingAppData))
        {
            foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache", "CachedData", "CachedExtensionVSIXs" })
                locations.Add(new($"VS Code {cache}", Path.Combine(roamingAppData, "Code", cache), RiskLevel.Medium));
            locations.Add(new("VS Code GrShaderCache", Path.Combine(roamingAppData, "Code", "GrShaderCache"), RiskLevel.Medium, EvidenceKind: "cache-residual"));
            foreach (var app in new[] { ("Discord", Path.Combine(roamingAppData, "discord")), ("Microsoft Teams", Path.Combine(roamingAppData, "Microsoft", "Teams")) })
            {
                foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache" })
                    locations.Add(new($"{app.Item1} {cache}", Path.Combine(app.Item2, cache), RiskLevel.Medium, EvidenceKind: "cache-residual"));
            }
        }
        _whitelist = locations.ToArray();
    }

    internal static (bool Success, string? Error) DeleteFile(string path)
    {
        File.Delete(path);
        return (true, null);
    }

    private static bool MatchesLocation(FileInfo file, CacheLocation location)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Path));
        if (IsForbiddenRoot(root) || !file.FullName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        return MatchesLocationFilter(file, location, root);
    }

    private static bool MatchesLocationFilter(FileInfo file, CacheLocation location, string root)
    {
        if (OptimizationProtectionPolicy.IsProtectedFromCleanup(file.FullName)) return false;

        if (IsTemporaryLocation(location) && !TemporaryCleanupPolicy.IsRecognizedFile(file.FullName)) return false;

        if (location.TemporaryBuildOnly) return TemporaryCleanupPolicy.IsBuildArtifact(file.FullName);

        if (location.ThumbnailsOnly)
        {
            return string.Equals(file.DirectoryName, root, StringComparison.OrdinalIgnoreCase) &&
                file.Name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) &&
                file.Extension.Equals(".db", StringComparison.OrdinalIgnoreCase);
        }

        if (location.TempOnly)
        {
            return file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".temp", StringComparison.OrdinalIgnoreCase);
        }

        if (location.TempResidual)
        {
            return !TemporaryCleanupPolicy.IsBuildArtifact(file.FullName)
                && !file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) && !file.Extension.Equals(".temp", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool IsTemporaryLocation(CacheLocation location) => location.TempOnly || location.TempResidual || location.TemporaryBuildOnly;

    private static IEnumerable<string> DiscoverBrowserProfiles(string root, bool chromium, bool includeDefault)
    {
        var profiles = new List<string>();
        try
        {
            if (!Directory.Exists(root) || !OptimizationFileSafety.HasNoReparsePoints(root)) return profiles;
            int examined = 0;
            foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                if (++examined > 256 || profiles.Count >= 12) break;
                if (entry is not DirectoryInfo directory || (directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                bool matched = !chromium
                    || (includeDefault && directory.Name == "Default")
                    || directory.Name.StartsWith("Profile ", StringComparison.Ordinal);
                if (matched) profiles.Add(directory.FullName);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return profiles;
    }

    private static bool IsForbiddenRoot(string path)
    {
        if (IsProtectedCorePath(path)) return true;
        foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows })
        {
            string root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root) && string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsProtectedCorePath(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string value = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(value)) continue;
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }

        string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonData))
        {
            string installerCache = Path.GetFullPath(Path.Combine(commonData, "Package Cache"));
            if (full.Equals(installerCache, StringComparison.OrdinalIgnoreCase) || full.StartsWith(installerCache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static readonly HashSet<string> EmptyFileNames = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsSidecarExtension(string extension) =>
        extension.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".partial", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".crdownload", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".download", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".aria2", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".idm", StringComparison.OrdinalIgnoreCase);

    public Task<List<TempFileInfo>> ScanAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var results = new List<TempFileInfo>();
        ScanTruncated = false;
        UserDirectorySummaries.Clear();
        _packageMarkers.Clear();
        var inventoryTask = _inventory.ScanAsync(cancellationToken);
        try
        {
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incompleteNuGetPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime cutoffUtc = DateTime.UtcNow - MinimumAge;
        int examined = 0;

        // Group rules sharing the same canonical root (e.g. %TEMP% == LocalAppData\Temp for
        // Temporary files + Temporary residuals) so each directory tree is traversed once.
        var rootGroups = _whitelist
            .GroupBy(location => Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Path)), StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                Root: group.Key,
                Locations: group
                    .DistinctBy(l => (l.Name, l.TempOnly, l.TempResidual, l.ThumbnailsOnly, l.TemporaryBuildOnly, l.EvidenceKind, l.Risk))
                    .ToArray()))
            .ToArray();

        foreach (var group in rootGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rootPath = group.Root;
            if (IsForbiddenRoot(rootPath) || !Directory.Exists(rootPath) || !OptimizationFileSafety.HasNoReparsePoints(rootPath))
            {
                continue;
            }

            bool thumbnailsOnlyRoot = group.Locations.All(location => location.ThumbnailsOnly);
            bool temporaryRoot = group.Locations.Any(IsTemporaryLocation);
            bool isNuGetPackagesRoot = group.Locations.Any(location => location.Name == "NuGet packages");
            var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(rootPath);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                if (!visitedDirectories.Add(directory)) continue;
                try
                {
                    var entries = new List<FileSystemInfo>();
                    HashSet<string>? sidecarNames = null;
                    foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++examined > 1000000 || results.Count >= 200000)
                        {
                            ScanTruncated = true;
                            goto FinalizeScan;
                        }

                        entries.Add(entry);
                        if (entry is FileInfo candidateFile && IsSidecarExtension(candidateFile.Extension))
                        {
                            sidecarNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            sidecarNames.Add(candidateFile.Name);
                        }
                    }

                    IReadOnlySet<string> siblingSidecars = sidecarNames ?? EmptyFileNames;
                    // TEMP can contain a checkout or an unpacked project. Its artifacts need
                    // the explicit development scanner's Git/project evidence, not a Temp rule.
                    if (temporaryRoot && entries.Any(entry => TemporaryCleanupPolicy.IsProjectMarker(entry.Name))) continue;
                    foreach (var entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            FileAttributes attrs = entry.Attributes;
                            if ((attrs & FileAttributes.ReparsePoint) != 0)
                            {
                                if (isNuGetPackagesRoot) MarkIncompletePackage(entry.FullName, rootPath, incompleteNuGetPackages);
                                continue;
                            }

                            if (entry is DirectoryInfo child)
                            {
                                if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                                {
                                    if (isNuGetPackagesRoot) MarkIncompletePackage(child.FullName, rootPath, incompleteNuGetPackages);
                                    continue;
                                }

                                if (!thumbnailsOnlyRoot)
                                {
                                    pending.Push(Path.TrimEndingDirectorySeparator(child.FullName));
                                }
                                continue;
                            }

                            var file = (FileInfo)entry;
                            if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                            {
                                if (isNuGetPackagesRoot) MarkIncompletePackage(file.FullName, rootPath, incompleteNuGetPackages);
                                continue;
                            }

                            CacheLocation? matched = null;
                            foreach (var location in group.Locations)
                            {
                                if (MatchesLocationFilter(file, location, rootPath))
                                {
                                    matched = location;
                                    break;
                                }
                            }

                            if (matched == null)
                            {
                                if (isNuGetPackagesRoot) MarkIncompletePackage(file.FullName, rootPath, incompleteNuGetPackages);
                                continue;
                            }

                            if (OptimizationFileSafety.CheckScanEligibility(file, cutoffUtc, parentVerifiedNoReparse: true, siblingSidecars) != FileEligibilityStatus.Eligible)
                            {
                                if (isNuGetPackagesRoot) MarkIncompletePackage(file.FullName, rootPath, incompleteNuGetPackages);
                                continue;
                            }

                            if (!seenFiles.Add(file.FullName)) continue;
                            bool residual = matched.EvidenceKind is "temp-residual" or "cache-residual";
                            results.Add(new TempFileInfo
                            {
                                ItemId = $"item-{results.Count + 1:D6}",
                                FilePath = file.FullName,
                                FileName = file.Name,
                                SizeInBytes = file.Length,
                                LastModified = file.LastWriteTimeUtc,
                                Category = matched.Name,
                                CacheRoot = Path.GetFullPath(matched.Path),
                                EvidenceKind = matched.EvidenceKind,
                                Risk = matched.Risk,
                                ReasonEn = residual ? "Residual file in temp/cache unchanged for at least 7 days; pending AI review" : "Known regenerable cache unchanged for at least 7 days",
                                ReasonZh = residual ? "临时或缓存目录中至少 7 天未修改的残留文件，待 AI 建议决定是否清理" : "已识别的可重建缓存，至少 7 天未修改",
                                Action = "delete",
                            });
                            if (results.Count >= 200000)
                            {
                                ScanTruncated = true;
                                goto FinalizeScan;
                            }
                        }
                        catch (IOException)
                        {
                            if (isNuGetPackagesRoot) MarkIncompletePackage(entry.FullName, rootPath, incompleteNuGetPackages);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            if (isNuGetPackagesRoot) MarkIncompletePackage(entry.FullName, rootPath, incompleteNuGetPackages);
                        }
                    }
                }
                catch (IOException)
                {
                    if (isNuGetPackagesRoot) MarkIncompletePackage(directory, rootPath, incompleteNuGetPackages);
                }
                catch (UnauthorizedAccessException)
                {
                    if (isNuGetPackagesRoot) MarkIncompletePackage(directory, rootPath, incompleteNuGetPackages);
                }
            }
        }

        FinalizeScan:
        // NuGet considers a version restored while .nupkg.metadata exists. Only offer
        // complete inactive versions, and invalidate that marker before deleting payloads.
        foreach (var package in results.Where(item => item.Category == "NuGet packages")
            .GroupBy(item => PackageDirectory(item.FilePath, item.CacheRoot)).ToList())
        {
            bool complete = package.Key != null
                && !incompleteNuGetPackages.Contains(package.Key)
                && (!ScanTruncated || CompletePackageSnapshot(package.Key, package.ToList(), cancellationToken));
            if (!complete)
            {
                var rejected = package.ToHashSet();
                results.RemoveAll(rejected.Contains);
                continue;
            }
            foreach (var marker in package.Where(item => item.FileName == ".nupkg.metadata"))
                _packageMarkers[marker.FilePath] = marker;
        }
        if (_developmentRoot != null && !IsProtectedCorePath(_developmentRoot))
        {
            results.AddRange(DevelopmentCacheScanner.Scan(_developmentRoot, 200000 - results.Count, cancellationToken)
                .Where(item => !IsProtectedCorePath(item.FilePath) && !OptimizationProtectionPolicy.IsProtectedFromCleanup(item.FilePath)));
        }
        if (results.Count >= 200000) ScanTruncated = true;
        for (int index = 0; index < results.Count; index++)
        {
            results[index].ItemId = $"item-{index + 1:D6}";
        }
        InventoryReport = await inventoryTask.ConfigureAwait(false);
        DescribeUserDirectories(results);
        cancellationToken.ThrowIfCancellationRequested();
        return results;
        }
        catch
        {
            if (!inventoryTask.IsCompleted)
            {
                _ = inventoryTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            else if (inventoryTask.IsFaulted)
            {
                _ = inventoryTask.Exception;
            }
            throw;
        }
    }, cancellationToken);

    private static void MarkIncompletePackage(string path, string root, HashSet<string> incompletePackages)
    {
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        if (parts.Length >= 2 && parts[0] != "..")
        {
            incompletePackages.Add(Path.Combine(root, parts[0], parts[1]));
        }
    }

    private void DescribeUserDirectories(List<TempFileInfo> candidates)
    {
        if (string.IsNullOrEmpty(_userProfile)) return;
        bool chinese = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        foreach (string name in new[] { ".nuget", ".dsh", ".pi", ".dotnet", ".cache", ".templateengine", ".npm" })
        {
            string root = Path.Combine(_userProfile, name);
            if (!Directory.Exists(root)) continue;
            var scopes = InventoryReport.Scopes.Where(scope => scope.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                scope.RootPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
            long bytes = scopes.Where(scope => scope.ObservedBytes.HasValue).Sum(scope => scope.ObservedBytes!.Value);
            int count = scopes.Sum(scope => scope.FileCount);
            bool incomplete = scopes.Length == 0 || scopes.Any(scope => scope.State != "complete");
            int eligible = candidates.Count(item => item.FilePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            string size = scopes.Any(scope => scope.ObservedBytes.HasValue) ? new TempFileInfo { SizeInBytes = bytes }.SizeFormatted : chinese ? "大小未统计" : "size not measured";
            bool knownCache = _whitelist.Any(location => location.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(location.Path));
            string status = !knownCache
                ? (chinese ? "未发现已验证缓存路径；配置、凭据、会话及安装内容保留" : "No verified cache path; configuration, credentials, sessions and installed content retained")
                : eligible == 0
                ? (chinese ? "未满足缓存清理条件（7 天未修改、完整包快照及可访问性）" : "No eligible cache snapshot: requires 7-day inactivity, complete package snapshots and accessible files")
                : (chinese ? $"{eligible:N0} 个文件符合缓存审核条件；其余内容保留" : $"{eligible:N0} files eligible for cache review; other content retained");
            UserDirectorySummaries.Add($"{root} · {size} · {count:N0} " + (chinese ? "个文件" : "files") +
                (incomplete ? (chinese ? " · 部分统计" : " · partial inventory") : string.Empty) + "\n" + status);
        }
    }

    public (bool Success, string? Error) CleanupItem(TempFileInfo item, CancellationToken cancellationToken = default)
    {
        var result = CleanupItemDetailed(item, ignoredPaths: null, allowImplicitMarkerDeletion: true, cancellationToken);
        return (result.Success, result.Error);
    }

    internal IReadOnlySet<string> PrepareDevelopmentBatch(IReadOnlyList<TempFileInfo> items, CancellationToken cancellationToken) =>
        _developmentRoot == null || IsProtectedCorePath(_developmentRoot) ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) :
        DevelopmentCacheScanner.CollectIgnoredPaths(items.Where(item => !IsProtectedCorePath(item.FilePath)).ToArray(), _developmentRoot, cancellationToken);

    internal (bool Success, string? Error) CleanupItem(TempFileInfo item, IReadOnlySet<string>? ignoredPaths, bool allowImplicitMarkerDeletion, CancellationToken cancellationToken)
    {
        var result = CleanupItemDetailed(item, ignoredPaths, allowImplicitMarkerDeletion, cancellationToken);
        return (result.Success, result.Error);
    }

    internal (bool Success, bool Skipped, string? Error) CleanupItemDetailed(TempFileInfo item, IReadOnlySet<string>? ignoredPaths, bool allowImplicitMarkerDeletion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var file = new FileInfo(item.FilePath);
            if (OptimizationProtectionPolicy.IsProtectedFromCleanup(file.FullName))
                return (false, false, "User documents, credentials, configuration and backups are protected. / 用户文档、凭据、配置和备份受保护。");
            if (IsProtectedCorePath(file.FullName))
                return (false, false, "Core system and installed application files are protected. / 核心系统及已安装应用文件受保护。");
            var matchedLocation = Array.Find(_whitelist, location => MatchesLocation(file, location));
            bool inScope = matchedLocation != null
                && (string.IsNullOrEmpty(item.Category) || string.Equals(item.Category, matchedLocation.Name, StringComparison.Ordinal))
                && item.Risk >= matchedLocation.Risk
                && (!IsTemporaryLocation(matchedLocation) || TemporaryCleanupPolicy.HasNoProjectAncestor(file.FullName, matchedLocation.Path, cancellationToken));
            bool development = false;
            if (!inScope && _developmentRoot != null && !string.IsNullOrEmpty(item.DevelopmentRepository)
                && (matchedLocation == null || IsTemporaryLocation(matchedLocation)))
            {
                development = DevelopmentCacheScanner.IsEligible(file, _developmentRoot, item.DevelopmentRepository, item.BuildManifestPath, ignoredPaths, cancellationToken);
                inScope = development;
            }
            if (item.Action != "delete" || item.Risk == RiskLevel.High || !inScope)
            {
                return (false, false, "File is outside the cleanup rules, in use, or changed since scanning; scan again. / 文件不符合清理规则、正在使用或扫描后已变化，请重新扫描。");
            }

            var eligibility = OptimizationFileSafety.CheckEligibility(file, development ? DevelopmentCacheScanner.MinimumAge : MinimumAge, item);
            if (eligibility == FileEligibilityStatus.InUse)
            {
                return (false, true, "Skipped because the file is currently in use. / 文件正在被占用，已跳过。");
            }
            if (eligibility != FileEligibilityStatus.Eligible)
            {
                return (false, false, "File is outside the cleanup rules, in use, or changed since scanning; scan again. / 文件不符合清理规则、正在使用或扫描后已变化，请重新扫描。");
            }

            var packageLocation = Array.Find(_whitelist, location => location.Name == "NuGet packages" && MatchesLocation(file, location));
            if (packageLocation != null)
            {
                string? package = PackageDirectory(file.FullName, packageLocation.Path);
                if (package == null) return (false, false, "Invalid package scope.");
                string markerPath = Path.Combine(package, ".nupkg.metadata");
                if (!string.Equals(file.FullName, markerPath, StringComparison.OrdinalIgnoreCase) && File.Exists(markerPath))
                {
                    if (!allowImplicitMarkerDeletion)
                        return (false, false, "Include the package restore marker in the reviewed selection before deleting its payload; scan again.");
                    if (!_packageMarkers.TryGetValue(markerPath, out var marker))
                        return (false, false, "Package changed since scanning; scan again.");
                    var markerEligibility = OptimizationFileSafety.CheckEligibility(new FileInfo(markerPath), MinimumAge, marker);
                    if (markerEligibility == FileEligibilityStatus.InUse)
                        return (false, true, "Skipped because the file is currently in use. / 文件正在被占用，已跳过。");
                    if (markerEligibility != FileEligibilityStatus.Eligible)
                        return (false, false, "Package changed since scanning; scan again.");
                    cancellationToken.ThrowIfCancellationRequested();
                    var invalidated = _delete(markerPath);
                    if (!invalidated.Success) return (false, false, invalidated.Error);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = _delete(file.FullName);
            if (result.Success)
            {
                var location = Array.Find(_whitelist, entry => !IsTemporaryLocation(entry) && !entry.ThumbnailsOnly && MatchesLocation(file, entry));
                if (location != null) RemoveEmptyParents(file.DirectoryName!, location.Path);
            }
            return (result.Success, false, result.Error);
        }
        catch (IOException exception) when (OptimizationFileSafety.IsFileInUse(exception))
        {
            return (false, true, "Skipped because the file is currently in use. / 文件正在被占用，已跳过。");
        }
        catch (IOException) { return (false, false, "File unavailable or in use. / 文件不可用或正在使用。"); }
        catch (UnauthorizedAccessException) { return (false, false, "File access denied. / 无法访问文件。"); }
        catch (ArgumentException) { return (false, false, "Invalid file path. / 文件路径无效。"); }
    }

    private static string? PackageDirectory(string file, string root)
    {
        var parts = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
        return parts.Length >= 3 && parts[0] != ".." ? Path.Combine(root, parts[0], parts[1]) : null;
    }

    private static bool CompletePackageSnapshot(string directory, List<TempFileInfo> items, CancellationToken token)
    {
        var paths = items.Select(item => item.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(directory);
        try
        {
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string current = pending.Pop();
                if (!OptimizationFileSafety.HasNoReparsePoints(current)) return false;
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if (entry is DirectoryInfo child) pending.Push(child.FullName);
                    else if (!paths.Remove(entry.FullName)) return false;
                }
            }
            return paths.Count == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void RemoveEmptyParents(string directory, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (string? current = Path.GetFullPath(directory); current != null &&
            (current.Equals(root, StringComparison.OrdinalIgnoreCase) || current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)); current = Path.GetDirectoryName(current))
        {
            try
            {
                if (!OptimizationFileSafety.HasNoReparsePoints(current) || IsForbiddenRoot(current)) break;
                Directory.Delete(current, recursive: false);
            }
            catch (IOException) { break; }
            catch (UnauthorizedAccessException) { break; }
        }
    }

    public Task<(int Succeeded, int Failed)> CleanupSelectedAsync(IEnumerable<TempFileInfo> items, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        int succeeded = 0;
        int failed = 0;
        foreach (var batch in items.Where(item => item.IsSelected).Chunk(128))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ignoredPaths = PrepareDevelopmentBatch(batch, cancellationToken);
            foreach (var item in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (CleanupItem(item, ignoredPaths, allowImplicitMarkerDeletion: true, cancellationToken).Success) succeeded++;
                else failed++;
            }
        }

        return (succeeded, failed);
    }, cancellationToken);
}
