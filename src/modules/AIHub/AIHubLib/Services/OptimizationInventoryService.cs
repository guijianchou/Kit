using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

internal sealed record OptimizationInventoryLocation(string Id, string FamilyId, string NameEn, string NameZh, string Path, string Disposition = "analysis-only");

/// <summary>Reads bounded filesystem metadata. No location in this inventory grants cleanup permission.</summary>
public sealed class OptimizationInventoryService
{
    private readonly Func<ScanBudget, IEnumerable<OptimizationInventoryLocation>> _locations;
    private readonly Func<string, IEnumerable<FileSystemInfo>> _enumerate;
    private readonly int _maximumEntries;
    private readonly int _maximumEntriesPerScope;
    private readonly int _maximumScopes;
    private readonly int _maximumTopFiles;
    private readonly TimeSpan _maximumDuration;

    public OptimizationInventoryService()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), includeSystemLocations: true)
    {
    }

    internal OptimizationInventoryService(string localAppData, string tempPath, string? userProfile, string? roamingAppData, bool includeSystemLocations = false)
        : this(Array.Empty<OptimizationInventoryLocation>())
    {
        _locations = budget => DefaultLocations(localAppData, tempPath, userProfile, roamingAppData, includeSystemLocations, budget);
    }

    internal OptimizationInventoryService(IEnumerable<OptimizationInventoryLocation> locations,
        int maximumEntries = 250000, int maximumEntriesPerScope = 25000, int maximumScopes = 96,
        int maximumTopFiles = 5, TimeSpan? maximumDuration = null,
        Func<string, IEnumerable<FileSystemInfo>>? enumerate = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        OptimizationInventoryLocation[] snapshot = locations.ToArray();
        _locations = _ => snapshot;
        _maximumEntries = Math.Clamp(maximumEntries, 1, 250000);
        _maximumEntriesPerScope = Math.Clamp(maximumEntriesPerScope, 1, 25000);
        _maximumScopes = Math.Clamp(maximumScopes, 1, 96);
        _maximumTopFiles = Math.Clamp(maximumTopFiles, 0, 5);
        _maximumDuration = maximumDuration ?? TimeSpan.FromSeconds(20);
        if (_maximumDuration < TimeSpan.Zero || _maximumDuration > TimeSpan.FromSeconds(20)) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        _enumerate = enumerate ?? (path => new DirectoryInfo(path).EnumerateFileSystemInfos());
    }

    public Task<OptimizationInventoryReport> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    internal OptimizationInventoryReport Scan(CancellationToken cancellationToken)
    {
        var budget = new ScanBudget(_maximumEntries, _maximumDuration, cancellationToken);
        var locations = new List<OptimizationInventoryLocation>();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool scopeLimit = false;
        foreach (OptimizationInventoryLocation location in _locations(budget))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = NormalizePath(location.Path);
            string disposition = MustProtect(root) ? "protected" : location.Disposition;
            if (!roots.Add(root))
            {
                // Protection wins even when a configurable TEMP path appeared before
                // an explicitly protected location with the same canonical path.
                if (disposition == "protected")
                {
                    int existing = locations.FindIndex(item => item.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
                    if (existing >= 0) locations[existing] = locations[existing] with { Disposition = "protected" };
                }

                continue;
            }
            if (locations.Count == _maximumScopes)
            {
                scopeLimit = true;
                break;
            }

            locations.Add(location with { Path = root, Disposition = disposition });
        }

        // A nested inventory location cannot reopen an explicitly protected subtree.
        string[] protectedRoots = locations.Where(item => item.Disposition == "protected").Select(item => item.Path).ToArray();
        for (int index = 0; index < locations.Count; index++)
        {
            if (protectedRoots.Any(root => IsWithin(locations[index].Path, root)))
                locations[index] = locations[index] with { Disposition = "protected" };
        }

        var scopes = new List<OptimizationInventoryScope>(locations.Count);
        foreach (OptimizationInventoryLocation location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scopes.Add(Inspect(location, locations, budget));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new OptimizationInventoryReport
        {
            CompletedUtc = DateTimeOffset.UtcNow,
            ElapsedMilliseconds = budget.Clock.ElapsedMilliseconds,
            ExaminedEntries = budget.ExaminedEntries,
            IsPartial = scopeLimit || budget.DiscoveryIncomplete || scopes.Any(scope => scope.State is "partial" or "unavailable"),
            Scopes = scopes.AsReadOnly(),
        };
    }

    private OptimizationInventoryScope Inspect(OptimizationInventoryLocation location, IReadOnlyList<OptimizationInventoryLocation> locations, ScanBudget budget)
    {
        var scope = Describe(location);
        if (location.Disposition == "protected") return scope with { State = "protected" };
        if (budget.Expired) return scope with { State = "partial" };

        try
        {
            FileAttributes rootAttributes = File.GetAttributes(location.Path);
            if ((rootAttributes & FileAttributes.Directory) == 0) return scope with { State = "unavailable", AccessFailures = 1 };
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0 || !OptimizationFileSafety.HasNoReparsePoints(location.Path))
                return scope with { State = "unavailable", SkippedEntries = 1 };
        }
        catch (DirectoryNotFoundException) { return scope with { State = "missing" }; }
        catch (FileNotFoundException) { return scope with { State = "missing" }; }
        catch (IOException) { return scope with { State = "unavailable", AccessFailures = 1 }; }
        catch (UnauthorizedAccessException) { return scope with { State = "unavailable", AccessFailures = 1 }; }

        long bytes = 0;
        long recent = 0;
        long older = 0;
        int files = 0;
        int examined = 0;
        int skipped = 0;
        int failures = 0;
        bool partial = false;
        bool enumeratedRoot = false;
        DateTime now = DateTime.UtcNow;
        var largest = new List<OptimizationInventoryFile>(_maximumTopFiles + 1);
        var pending = new Stack<string>();
        pending.Push(location.Path);
        while (pending.Count > 0)
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            if (budget.Expired || examined >= _maximumEntriesPerScope)
            {
                partial = true;
                break;
            }

            string directory = pending.Pop();
            try
            {
                if (!OptimizationFileSafety.HasNoReparsePoints(directory))
                {
                    skipped++;
                    partial = true;
                    continue;
                }

                foreach (FileSystemInfo entry in _enumerate(directory))
                {
                    budget.CancellationToken.ThrowIfCancellationRequested();
                    enumeratedRoot = true;
                    if (examined >= _maximumEntriesPerScope || !budget.Examine())
                    {
                        partial = true;
                        pending.Clear();
                        break;
                    }

                    examined++;
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            skipped++;
                            partial = true;
                            continue;
                        }

                        // Each nested location owns its subtree. Aggregate totals therefore never
                        // count a cache twice when a parent inventory location also exists.
                        string path = NormalizePath(entry.FullName);
                        if (!IsWithin(path, location.Path) || path.Equals(location.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            skipped++;
                            partial = true;
                            continue;
                        }

                        if (entry is DirectoryInfo && locations.Any(other => !ReferenceEquals(other, location)
                            && string.Equals(path, other.Path, StringComparison.OrdinalIgnoreCase)))
                        {
                            skipped++;
                            continue;
                        }

                        if (entry is DirectoryInfo)
                        {
                            pending.Push(path);
                            continue;
                        }

                        var file = (FileInfo)entry;
                        long length = Math.Max(0, file.Length);
                        DateTime modified = file.LastWriteTimeUtc;
                        bytes = Add(bytes, length);
                        files++;
                        if (modified > now.AddDays(-7)) recent = Add(recent, length);
                        if (modified <= now.AddDays(-30)) older = Add(older, length);
                        if (_maximumTopFiles > 0)
                        {
                            largest.Add(new OptimizationInventoryFile { Name = file.Name, Path = file.FullName, SizeInBytes = length, LastModifiedUtc = new DateTimeOffset(modified, TimeSpan.Zero) });
                            largest.Sort((left, right) => right.SizeInBytes.CompareTo(left.SizeInBytes));
                            if (largest.Count > _maximumTopFiles) largest.RemoveAt(largest.Count - 1);
                        }
                    }
                    catch (IOException)
                    {
                        failures++;
                        partial = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        failures++;
                        partial = true;
                    }
                }

                // Empty accessible directories have a known zero size, unlike unavailable roots.
                enumeratedRoot = true;
            }
            catch (DirectoryNotFoundException)
            {
                failures++;
                partial = true;
            }
            catch (IOException)
            {
                failures++;
                partial = true;
            }
            catch (UnauthorizedAccessException)
            {
                failures++;
                partial = true;
            }
        }

        budget.CancellationToken.ThrowIfCancellationRequested();

        return scope with
        {
            State = !enumeratedRoot && failures > 0 ? "unavailable" : partial ? "partial" : "complete",
            ObservedBytes = enumeratedRoot ? bytes : null,
            FileCount = files,
            RecentBytes = recent,
            OlderBytes = older,
            SkippedEntries = skipped,
            AccessFailures = failures,
            TopFiles = largest.AsReadOnly(),
        };
    }

    private static OptimizationInventoryScope Describe(OptimizationInventoryLocation location) => new()
    {
        Id = location.Id,
        FamilyId = location.FamilyId,
        NameEn = location.NameEn,
        NameZh = location.NameZh,
        RootPath = location.Path,
        Disposition = location.Disposition,
        ReasonEn = location.Disposition switch
        {
            "protected" => "Core installation or user state is protected and was not traversed.",
            "system-managed" => "Windows manages this location; observed space is not a cleanup allowance.",
            "cache" => "Known cache location. Occupancy includes recent or active files that may need to be retained.",
            _ => "Metadata-only analysis. Age and size do not establish that a file is disposable.",
        },
        ReasonZh = location.Disposition switch
        {
            "protected" => "核心安装内容或用户状态受保护，不遍历其内容。",
            "system-managed" => "此位置由 Windows 管理；统计占用不代表允许清理。",
            "cache" => "已知缓存位置；占用包含可能需要保留的近期或正在使用的文件。",
            _ => "仅分析文件元数据；文件大小与年龄不能证明可以删除。",
        },
        GuidanceEn = location.Disposition switch
        {
            "protected" => "Use the owning application's maintenance or uninstall interface if space must be reclaimed.",
            "system-managed" => "Review Windows Settings > System > Storage > Temporary files or the owning system maintenance tool.",
            "cache" => "Only separately reviewed cleanup candidates can be selected below. This inventory itself is read-only.",
            _ => "Review the owning application's storage or diagnostic settings. No delete action is provided for this location.",
        },
        GuidanceZh = location.Disposition switch
        {
            "protected" => "需要释放空间时，请使用所属应用的维护或卸载入口。",
            "system-managed" => "请通过 Windows 设置 > 系统 > 存储 > 临时文件，或所属系统维护工具处理。",
            "cache" => "只有另行审核通过的清理候选可被选择；本占用分析本身只读。",
            _ => "请查看所属应用的存储或诊断设置；此位置不提供删除操作。",
        },
    };

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool MustProtect(string root)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData) && IsWithin(root, NormalizePath(Path.Combine(programData, "Package Cache")))) return true;
        if (CacheCleanupService.IsProtectedCorePath(root))
        {
            // Only these exact Windows-managed analysis roots are traversable. A
            // configurable TEMP under WinSxS, Installer or any other core tree is not.
            string[] managed =
            [
                Path.Combine(windows, "Temp"),
                Path.Combine(windows, "SoftwareDistribution", "Download"),
                Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
            ];
            return !managed.Any(path => root.Equals(NormalizePath(path), StringComparison.OrdinalIgnoreCase));
        }

        if (root.Equals(NormalizePath(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.MyDocuments })
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && root.Equals(NormalizePath(path), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static long Add(long total, long amount) => amount > long.MaxValue - total ? long.MaxValue : total + amount;

    private static IEnumerable<OptimizationInventoryLocation> DefaultLocations(string local, string temp, string? user, string? roaming, bool includeSystemLocations, ScanBudget budget)
    {
        static OptimizationInventoryLocation At(string id, string family, string en, string zh, string path, string disposition = "cache") => new(id, family, en, zh, path, disposition);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var protectedRoot in new[]
        {
            ("windows-components", "Windows component store", "Windows 组件存储", Path.Combine(windows, "WinSxS")),
            ("windows-installer", "Windows Installer", "Windows 安装程序缓存", Path.Combine(windows, "Installer")),
            ("program-files", "Program Files", "程序安装目录", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("program-files-x86", "Program Files (x86)", "32 位程序安装目录", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            ("package-installer", "Installer package cache", "安装程序包缓存", Path.Combine(programData, "Package Cache")),
        })
        {
            if (includeSystemLocations && !string.IsNullOrWhiteSpace(protectedRoot.Item4)) yield return At(protectedRoot.Item1, "system", protectedRoot.Item2, protectedRoot.Item3, protectedRoot.Item4, "protected");
        }

        yield return At("temporary", "system", "Temporary files", "临时文件", temp, "analysis-only");
        yield return At("local-temporary", "system", "Local temporary files", "用户临时文件", Path.Combine(local, "Temp"), "analysis-only");
        foreach (var entry in new[]
        {
            ("crash-dumps", "Crash dumps", "应用崩溃转储", "CrashDumps"),
            ("wer-local", "Windows error reports", "Windows 错误报告", @"Microsoft\Windows\WER"),
        }) yield return At(entry.Item1, "diagnostics", entry.Item2, entry.Item3, Path.Combine(local, entry.Item4), "analysis-only");
        foreach (var entry in new[]
        {
            ("shader-directx", "DirectX shader cache", "DirectX 着色器缓存", "D3DSCache"),
            ("shader-amd-dx", "AMD DirectX cache", "AMD DirectX 缓存", @"AMD\DxCache"),
            ("shader-amd-gl", "AMD OpenGL cache", "AMD OpenGL 缓存", @"AMD\GLCache"),
            ("shader-nvidia-dx", "NVIDIA DirectX cache", "NVIDIA DirectX 缓存", @"NVIDIA\DXCache"),
            ("shader-nvidia-gl", "NVIDIA OpenGL cache", "NVIDIA OpenGL 缓存", @"NVIDIA\GLCache"),
            ("explorer-cache", "Explorer image caches", "资源管理器图像缓存", @"Microsoft\Windows\Explorer"),
        }) yield return At(entry.Item1, "system", entry.Item2, entry.Item3, Path.Combine(local, entry.Item4));
        foreach (var entry in new[]
        {
            ("npm-local", "npm content cache", "npm 内容缓存", @"npm-cache\_cacache"),
            ("pip-local", "pip cache", "pip 缓存", @"pip\cache"),
            ("pnpm-local", "pnpm cache", "pnpm 缓存", @"pnpm\cache"),
            ("nuget-http", "NuGet HTTP cache", "NuGet HTTP 缓存", @"NuGet\v3-cache"),
            ("uv-local", "uv cache", "uv 缓存", @"uv\cache"),
        }) yield return At(entry.Item1, "packages", entry.Item2, entry.Item3, Path.Combine(local, entry.Item4));

        if (!string.IsNullOrEmpty(user))
        {
            foreach (var entry in new[]
            {
                ("nuget-packages", "NuGet packages", "NuGet 包缓存", @".nuget\packages"),
                ("dotnet-advertising", ".NET workload metadata", ".NET 工作负载元数据", @".dotnet\sdk-advertising"),
                ("dotnet-telemetry", ".NET telemetry cache", ".NET 遥测缓存", @".dotnet\TelemetryStorageService"),
                ("uv-user", "uv user cache", "uv 用户缓存", @".cache\uv"),
                ("npm-user", "npm user cache", "npm 用户缓存", @".npm\_cacache"),
                ("pip-user", "pip user cache", "pip 用户缓存", @".cache\pip"),
                ("gradle-cache", "Gradle caches", "Gradle 缓存", @".gradle\caches"),
                ("maven-cache", "Maven local repository", "Maven 本地包仓库", @".m2\repository"),
            }) yield return At(entry.Item1, "packages", entry.Item2, entry.Item3, Path.Combine(user, entry.Item4));
            foreach (var entry in new[]
            {
                ("pi-state", "Pi configuration and sessions", "Pi 配置与会话", ".pi"),
                ("dsh-state", "Shell assistant state", "Shell 助手数据", ".dsh"),
                ("dotnet-tools", ".NET installed tools", ".NET 已安装工具", @".dotnet\tools"),
                ("templates", "Installed templates", "已安装模板", ".templateengine"),
                ("codex-runtimes", "Codex runtimes", "Codex 运行时", @".cache\codex-runtimes"),
            }) yield return At(entry.Item1, "applications", entry.Item2, entry.Item3, Path.Combine(user, entry.Item4), "protected");
        }

        if (!string.IsNullOrEmpty(roaming))
        {
            foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache", "CachedData", "CachedExtensionVSIXs" })
                yield return At("vscode-" + cache.Replace(' ', '-').ToLowerInvariant(), "editors", "VS Code " + cache, "VS Code " + cache, Path.Combine(roaming, "Code", cache));
            foreach (string app in new[] { "discord", "Microsoft Teams" })
            {
                string root = app == "discord" ? Path.Combine(roaming, app) : Path.Combine(roaming, "Microsoft", "Teams");
                foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache" })
                    yield return At(app.Replace(' ', '-').ToLowerInvariant() + "-" + cache.Replace(' ', '-').ToLowerInvariant(), "applications", app + " " + cache, app + " " + cache, Path.Combine(root, cache));
            }
        }

        foreach (var entry in new[]
        {
            ("windows-temp", "Windows temporary files", "Windows 临时文件", Path.Combine(windows, "Temp")),
            ("windows-update", "Windows Update downloads", "Windows 更新下载", Path.Combine(windows, "SoftwareDistribution", "Download")),
            ("windows-do", "Delivery Optimization cache", "传递优化缓存", Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")),
            ("wer-system", "System error reports", "系统错误报告", Path.Combine(programData, "Microsoft", "Windows", "WER")),
        })
        {
            if (includeSystemLocations) yield return At(entry.Item1, "system", entry.Item2, entry.Item3, entry.Item4, "system-managed");
        }

        foreach (var browser in new[]
        {
            ("chrome", "Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("edge", "Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("brave", "Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
        })
        {
            foreach (string profile in DiscoverProfiles(browser.Item3, chromium: true, budget))
            {
                foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache" })
                {
                    yield return At(browser.Item1 + "-" + Path.GetFileName(profile) + "-" + cache, "browsers", browser.Item2 + " " + Path.GetFileName(profile) + " " + cache,
                        browser.Item2 + " " + Path.GetFileName(profile) + " " + cache, Path.Combine(profile, cache));
                }
            }
        }

        foreach (string profile in DiscoverProfiles(Path.Combine(local, "Mozilla", "Firefox", "Profiles"), chromium: false, budget))
            yield return At("firefox-" + Path.GetFileName(profile), "browsers", "Firefox " + Path.GetFileName(profile) + " cache", "Firefox " + Path.GetFileName(profile) + " 缓存", Path.Combine(profile, "cache2"));
    }

    private static IEnumerable<string> DiscoverProfiles(string root, bool chromium, ScanBudget budget)
    {
        var profiles = new List<string>();
        if (budget.Expired)
        {
            budget.DiscoveryIncomplete = true;
            return profiles;
        }
        try
        {
            if (!Directory.Exists(root) || !OptimizationFileSafety.HasNoReparsePoints(root)) return profiles;
            int examined = 0;
            foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                budget.CancellationToken.ThrowIfCancellationRequested();
                if (++examined > 256 || !budget.Examine())
                {
                    budget.DiscoveryIncomplete = true;
                    break;
                }
                if (entry is not DirectoryInfo directory || (directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (!chromium || directory.Name == "Default" || directory.Name.StartsWith("Profile ", StringComparison.Ordinal)) profiles.Add(directory.FullName);
                if (profiles.Count >= 12)
                {
                    budget.DiscoveryIncomplete = true;
                    break;
                }
            }
        }
        catch (IOException) { budget.DiscoveryIncomplete = true; }
        catch (UnauthorizedAccessException) { budget.DiscoveryIncomplete = true; }
        return profiles;
    }

    private sealed class ScanBudget(int maximumEntries, TimeSpan maximumDuration, CancellationToken cancellationToken)
    {
        internal Stopwatch Clock { get; } = Stopwatch.StartNew();
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal int ExaminedEntries { get; private set; }
        internal bool DiscoveryIncomplete { get; set; }
        internal bool Expired => ExaminedEntries >= maximumEntries || Clock.Elapsed >= maximumDuration;

        internal bool Examine()
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (Expired) return false;
            ExaminedEntries++;
            return true;
        }
    }
}
