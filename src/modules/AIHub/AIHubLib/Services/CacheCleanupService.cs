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
    public List<string> UserDirectorySummaries { get; } = new();
    private readonly Dictionary<string, TempFileInfo> _packageMarkers = new(StringComparer.OrdinalIgnoreCase);
    public bool ScanTruncated { get; private set; }

    private sealed record CacheLocation(string Name, string Path, RiskLevel Risk, bool TempOnly = false, bool ThumbnailsOnly = false);

    public CacheCleanupService()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath(), DeleteFile,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"C:\Users\Zen\Repos\Codings")
    {
    }

    internal CacheCleanupService(string localAppData, string tempPath, Func<string, (bool Success, string? Error)> delete,
        string? userProfile = null, string? roamingAppData = null, string? developmentRoot = null)
    {
        _delete = delete;
        _developmentRoot = developmentRoot;
        _userProfile = userProfile;
        List<CacheLocation> locations =
        [
            new("Temporary files", Path.GetFullPath(tempPath), RiskLevel.Low, TempOnly: true),
            new("Temporary files", Path.Combine(localAppData, "Temp"), RiskLevel.Low, TempOnly: true),
            new("Chrome cache", Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Cache"), RiskLevel.Medium),
            new("Edge cache", Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default", "Cache"), RiskLevel.Medium),
            new("npm cache", Path.Combine(localAppData, "npm-cache", "_cacache"), RiskLevel.Low),
            new("pip HTTP cache", Path.Combine(localAppData, "pip", "cache", "http"), RiskLevel.Low),
            new("pip HTTP cache", Path.Combine(localAppData, "pip", "cache", "http-v2"), RiskLevel.Low),
            new("pip wheel cache", Path.Combine(localAppData, "pip", "cache", "wheels"), RiskLevel.Low),
            new("pnpm cache", Path.Combine(localAppData, "pnpm", "cache"), RiskLevel.Low),
            new("Thumbnail cache", Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"), RiskLevel.Medium, ThumbnailsOnly: true),
        ];
        foreach (string browser in new[] { Path.Combine("Microsoft", "Edge"), Path.Combine("Google", "Chrome") })
        {
            string name = browser.Contains("Edge", StringComparison.Ordinal) ? "Edge" : "Chrome";
            foreach (string cache in new[] { "Code Cache", "GPUCache" })
                locations.Add(new($"{name} {cache}", Path.Combine(localAppData, browser, "User Data", "Default", cache), RiskLevel.Medium));
        }
        foreach (string cache in new[] { "D3DSCache", @"AMD\DxCache", @"AMD\GLCache", @"NVIDIA\DXCache", @"NVIDIA\GLCache", @"NuGet\v3-cache" })
            locations.Add(new(cache.Replace('\\', ' '), Path.Combine(localAppData, cache), RiskLevel.Medium));
        if (!string.IsNullOrEmpty(userProfile))
        {
            locations.Add(new("NuGet packages", Path.Combine(userProfile, ".nuget", "packages"), RiskLevel.Medium));
            foreach (var entry in new[]
            {
                (".NET workload download metadata", @".dotnet\sdk-advertising"),
                (".NET telemetry cache", @".dotnet\TelemetryStorageService"),
                ("uv cache", @".cache\uv"),
                ("npm cache", @".npm\_cacache"),
                ("pip HTTP cache", @".cache\pip\http"),
                ("pip HTTP cache", @".cache\pip\http-v2"),
                ("pip wheel cache", @".cache\pip\wheels"),
            })
                locations.Add(new(entry.Item1, Path.Combine(userProfile, entry.Item2), RiskLevel.Medium));
        }
        if (!string.IsNullOrEmpty(roamingAppData))
        {
            foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache", "CachedData", "CachedExtensionVSIXs" })
                locations.Add(new($"VS Code {cache}", Path.Combine(roamingAppData, "Code", cache), RiskLevel.Medium));
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
        if (location.ThumbnailsOnly)
        {
            return string.Equals(file.DirectoryName, root, StringComparison.OrdinalIgnoreCase) &&
                file.Name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) &&
                file.Extension.Equals(".db", StringComparison.OrdinalIgnoreCase);
        }

        return !location.TempOnly || file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".temp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForbiddenRoot(string path)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows })
        {
            string root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root) && string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), StringComparison.OrdinalIgnoreCase);
    }

    public Task<List<TempFileInfo>> ScanAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var results = new List<TempFileInfo>();
        ScanTruncated = false;
        UserDirectorySummaries.Clear();
        _packageMarkers.Clear();
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int examined = 0;
        foreach (var location in _whitelist)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new Stack<string>();
            pending.Push(location.Path);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = Path.GetFullPath(pending.Pop());
                if (!visitedDirectories.Add(Path.TrimEndingDirectorySeparator(directory))) continue;
                try
                {
                    if (!Directory.Exists(directory) || IsForbiddenRoot(Path.TrimEndingDirectorySeparator(directory)) || !OptimizationFileSafety.HasNoReparsePoints(directory)) continue;
                    foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++examined > 1000000 || results.Count >= 200000)
                        {
                            ScanTruncated = true;
                            goto FinalizeScan;
                        }
                        try
                        {
                            if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                            if (entry is DirectoryInfo child)
                            {
                                if (!location.ThumbnailsOnly) pending.Push(child.FullName);
                                continue;
                            }

                            var file = (FileInfo)entry;
                            if (!MatchesLocation(file, location) || !OptimizationFileSafety.IsEligible(file, MinimumAge) || !seenFiles.Add(file.FullName)) continue;
                            results.Add(new TempFileInfo
                            {
                                ItemId = $"item-{results.Count + 1:D6}",
                                FilePath = file.FullName,
                                FileName = file.Name,
                                SizeInBytes = file.Length,
                                LastModified = file.LastWriteTimeUtc,
                                Category = location.Name,
                                CacheRoot = Path.GetFullPath(location.Path),
                                Risk = location.Risk,
                                ReasonEn = "Known regenerable cache unchanged for at least 7 days",
                                ReasonZh = "已识别的可重建缓存，至少 7 天未修改",
                                Action = "delete"
                            });
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        FinalizeScan:
        // NuGet considers a version restored while .nupkg.metadata exists. Only offer
        // complete inactive versions, and invalidate that marker before deleting payloads.
        foreach (var package in results.Where(item => item.Category == "NuGet packages")
            .GroupBy(item => PackageDirectory(item.FilePath, item.CacheRoot)).ToList())
        {
            if (package.Key == null || !CompletePackageSnapshot(package.Key, package.ToList(), cancellationToken))
            {
                var rejected = package.ToHashSet();
                results.RemoveAll(rejected.Contains);
                continue;
            }
            foreach (var marker in package.Where(item => item.FileName == ".nupkg.metadata"))
                _packageMarkers[marker.FilePath] = marker;
        }
        if (_developmentRoot != null)
            results.AddRange(DevelopmentCacheScanner.Scan(_developmentRoot, 200000 - results.Count, cancellationToken));
        if (results.Count >= 200000) ScanTruncated = true;
        for (int index = 0; index < results.Count; index++)
        {
            results[index].ItemId = $"item-{index + 1:D6}";
        }
        AnalyzeUserDirectories(results, cancellationToken);
        return results;
    }, cancellationToken);

    private void AnalyzeUserDirectories(List<TempFileInfo> candidates, CancellationToken token)
    {
        if (string.IsNullOrEmpty(_userProfile)) return;
        bool chinese = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        foreach (string name in new[] { ".nuget", ".dsh", ".pi", ".dotnet", ".cache", ".templateengine", ".npm" })
        {
            string root = Path.Combine(_userProfile, name);
            if (!Directory.Exists(root)) continue;
            long bytes = 0;
            int count = 0;
            int examined = 0;
            bool incomplete = false;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string current = pending.Pop();
                try
                {
                    if (!OptimizationFileSafety.HasNoReparsePoints(current)) { incomplete = true; continue; }
                    foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                    {
                        token.ThrowIfCancellationRequested();
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { incomplete = true; continue; }
                        if (entry is DirectoryInfo directory) pending.Push(directory.FullName);
                        else { bytes += ((FileInfo)entry).Length; count++; }
                        if (++examined >= 200000) { incomplete = true; pending.Clear(); break; }
                    }
                }
                catch (IOException) { incomplete = true; }
                catch (UnauthorizedAccessException) { incomplete = true; }
            }
            int eligible = candidates.Count(item => item.FilePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            string size = new TempFileInfo { SizeInBytes = bytes }.SizeFormatted;
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

    public (bool Success, string? Error) CleanupItem(TempFileInfo item)
    {
        try
        {
            var file = new FileInfo(item.FilePath);
            bool inScope = Array.Exists(_whitelist, location => MatchesLocation(file, location));
            bool development = false;
            if (!inScope && _developmentRoot != null)
            {
                development = DevelopmentCacheScanner.IsEligible(file, _developmentRoot, item.DevelopmentRepository, item.BuildManifestPath);
                inScope = development;
            }
            if (item.Action != "delete" || item.Risk == RiskLevel.High || !inScope ||
                !OptimizationFileSafety.IsEligible(file, development ? DevelopmentCacheScanner.MinimumAge : MinimumAge, item))
            {
                return (false, "File is outside the cleanup rules, in use, or changed since scanning; scan again. / 文件不符合清理规则、正在使用或扫描后已变化，请重新扫描。");
            }

            var packageLocation = Array.Find(_whitelist, location => location.Name == "NuGet packages" && MatchesLocation(file, location));
            if (packageLocation != null)
            {
                string? package = PackageDirectory(file.FullName, packageLocation.Path);
                if (package == null) return (false, "Invalid package scope.");
                string markerPath = Path.Combine(package, ".nupkg.metadata");
                if (!string.Equals(file.FullName, markerPath, StringComparison.OrdinalIgnoreCase) && File.Exists(markerPath))
                {
                    if (!_packageMarkers.TryGetValue(markerPath, out var marker) || !OptimizationFileSafety.IsEligible(new FileInfo(markerPath), MinimumAge, marker))
                        return (false, "Package changed since scanning; scan again.");
                    var invalidated = _delete(markerPath);
                    if (!invalidated.Success) return invalidated;
                }
            }
            var result = _delete(file.FullName);
            if (result.Success)
            {
                var location = Array.Find(_whitelist, entry => !entry.TempOnly && !entry.ThumbnailsOnly && MatchesLocation(file, entry));
                if (location != null) RemoveEmptyParents(file.DirectoryName!, location.Path);
            }
            return result;
        }
        catch (IOException) { return (false, "File unavailable or in use. / 文件不可用或正在使用。"); }
        catch (UnauthorizedAccessException) { return (false, "File access denied. / 无法访问文件。"); }
        catch (ArgumentException) { return (false, "Invalid file path. / 文件路径无效。"); }
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
        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!item.IsSelected) continue;
            if (CleanupItem(item).Success) succeeded++;
            else failed++;
        }

        return (succeeded, failed);
    }, cancellationToken);
}
