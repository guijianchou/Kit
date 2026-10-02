using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

public sealed class CacheCleanupService
{
    private static readonly TimeSpan MinimumAge = TimeSpan.FromDays(7);
    private readonly CacheLocation[] _whitelist;
    private readonly Func<string, (bool Success, string? Error)> _recycle;

    private sealed record CacheLocation(string Name, string Path, RiskLevel Risk, bool TempOnly = false, bool ThumbnailsOnly = false);

    public CacheCleanupService()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath(), new RecycleBinHelper().MoveToRecycleBin)
    {
    }

    internal CacheCleanupService(string localAppData, string tempPath, Func<string, (bool Success, string? Error)> recycle)
    {
        _recycle = recycle;
        _whitelist =
        [
            new("Temporary files", Path.GetFullPath(tempPath), RiskLevel.Low, TempOnly: true),
            new("Temporary files", Path.Combine(localAppData, "Temp"), RiskLevel.Low, TempOnly: true),
            new("Chrome cache", Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Cache"), RiskLevel.Medium),
            new("Edge cache", Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default", "Cache"), RiskLevel.Medium),
            new("npm cache", Path.Combine(localAppData, "npm-cache", "_cacache"), RiskLevel.Low),
            new("pip cache", Path.Combine(localAppData, "pip", "cache", "http"), RiskLevel.Low),
            new("pip cache", Path.Combine(localAppData, "pip", "cache", "http-v2"), RiskLevel.Low),
            new("pip cache", Path.Combine(localAppData, "pip", "cache", "wheels"), RiskLevel.Low),
            new("pnpm cache", Path.Combine(localAppData, "pnpm", "cache"), RiskLevel.Low),
            new("Thumbnail cache", Path.Combine(localAppData, "Microsoft", "Windows", "Explorer"), RiskLevel.Medium, ThumbnailsOnly: true),
        ];
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
                        if (++examined > 10000 || results.Count >= 500) return results;
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
                                Risk = location.Risk,
                                ReasonEn = "Known temporary/cache file unchanged for at least 7 days; review before recycling",
                                ReasonZh = "已识别的临时/缓存文件，至少 7 天未修改；请审查后移入回收站",
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

        return results;
    }, cancellationToken);

    public (bool Success, string? Error) CleanupItem(TempFileInfo item)
    {
        try
        {
            var file = new FileInfo(item.FilePath);
            if (item.Action != "delete" || !Array.Exists(_whitelist, location => MatchesLocation(file, location)) ||
                !OptimizationFileSafety.IsEligible(file, MinimumAge, item))
            {
                return (false, "File is outside the cleanup rules, in use, or changed since scanning; scan again. / 文件不符合清理规则、正在使用或扫描后已变化，请重新扫描。");
            }

            return _recycle(file.FullName);
        }
        catch (IOException) { return (false, "File unavailable or in use. / 文件不可用或正在使用。"); }
        catch (UnauthorizedAccessException) { return (false, "File access denied. / 无法访问文件。"); }
        catch (ArgumentException) { return (false, "Invalid file path. / 文件路径无效。"); }
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
