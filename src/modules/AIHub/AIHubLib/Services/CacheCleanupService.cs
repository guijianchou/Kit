using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

public sealed class CacheCleanupService
{
    private readonly RecycleBinHelper _recycleBinHelper = new();

    private sealed record CacheLocation(string Name, Func<string> GetPath, RiskLevel Risk, string Reason, bool AsFolder = false);

    private static readonly List<CacheLocation> Whitelist = new()
    {
        new("Current user temp", () => Path.GetTempPath(), RiskLevel.Low, "Standard temp directory, safe to clean"),
        new("LocalAppData temp", () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"), RiskLevel.Low, "Windows temp cache"),
        new("Chrome cache", () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data", "Default", "Cache"), RiskLevel.Medium, "Browser cache"),
        new("Edge cache", () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data", "Default", "Cache"), RiskLevel.Medium, "Browser cache"),
        new("npm cache", () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm-cache"), RiskLevel.Low, "Package manager cache", true),
        new("pip cache", () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pip", "cache"), RiskLevel.Low, "Package manager cache", true),
        new("pnpm cache", () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "cache"), RiskLevel.Low, "Package manager cache", true),
        new("Thumbnail cache", () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer"), RiskLevel.Low, "Thumbnail cache"),
    };

    private static readonly HashSet<string> ForbiddenRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
    };

    public Task<List<TempFileInfo>> ScanAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var results = new List<TempFileInfo>();
            int index = 1;

            foreach (var loc in Whitelist)
            {
                if (cancellationToken.IsCancellationRequested) break;

                string path;
                try
                {
                    path = loc.GetPath();
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || IsForbidden(path))
                {
                    continue;
                }

                try
                {
                    if (loc.AsFolder)
                    {
                        var dirInfo = new DirectoryInfo(path);
                        long totalSize = 0;
                        try
                        {
                            foreach (var f in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                            {
                                totalSize += f.Length;
                            }
                        }
                        catch { }

                        if (totalSize > 0)
                        {
                            results.Add(new TempFileInfo
                            {
                                ItemId = $"item-{index++:D6}",
                                FilePath = path,
                                FileName = dirInfo.Name,
                                SizeInBytes = totalSize,
                                LastModified = dirInfo.LastWriteTimeUtc,
                                Category = loc.Name,
                                Risk = loc.Risk,
                                ReasonEn = loc.Reason,
                                ReasonZh = "临时缓存，移入回收站后可安全释放空间",
                                Action = "delete"
                            });
                        }
                    }
                    else
                    {
                        var dirInfo = new DirectoryInfo(path);
                        foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                        {
                            if (cancellationToken.IsCancellationRequested) break;
                            if (IsForbidden(file.FullName)) continue;

                            results.Add(new TempFileInfo
                            {
                                ItemId = $"item-{index++:D6}",
                                FilePath = file.FullName,
                                FileName = file.Name,
                                SizeInBytes = file.Length,
                                LastModified = file.LastWriteTimeUtc,
                                Category = loc.Name,
                                Risk = loc.Risk,
                                ReasonEn = loc.Reason,
                                ReasonZh = "临时文件，可安全清理",
                                Action = "delete"
                            });

                            if (results.Count >= 500) break;
                        }
                    }
                }
                catch
                {
                    // Ignore locked directories
                }
            }

            return results;
        }, cancellationToken);
    }

    public Task<(int Succeeded, int Failed)> CleanupSelectedAsync(IEnumerable<TempFileInfo> items, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            int succeeded = 0;
            int failed = 0;

            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (!item.IsSelected || IsForbidden(item.FilePath)) continue;

                var (success, _) = _recycleBinHelper.MoveToRecycleBin(item.FilePath);
                if (success)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                }
            }

            return (succeeded, failed);
        }, cancellationToken);
    }

    private static bool IsForbidden(string path)
    {
        string normalized = Path.GetFullPath(path).TrimEnd('\\', '/');
        foreach (var root in ForbiddenRoots)
        {
            if (string.Equals(normalized, Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
