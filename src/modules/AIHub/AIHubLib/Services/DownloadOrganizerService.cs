using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

public sealed class DownloadOrganizerService
{
    public const string OthersCategory = "Others";
    internal static readonly HashSet<string> KnownCategories = new(StringComparer.Ordinal)
    {
        "Documents", "Compressed", "Programs", "Music", "Video", "Pictures",
    };
    internal static readonly HashSet<string> AllowedCategories = new(StringComparer.Ordinal)
    {
        "Documents", "Compressed", "Programs", "Music", "Video", "Pictures", OthersCategory,
    };

    private static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(10);
    private readonly string? _downloadsRoot;
    public bool ScanTruncated { get; private set; }

    private static readonly Dictionary<string, string> ExtensionToCategory = new(StringComparer.OrdinalIgnoreCase);

    static DownloadOrganizerService()
    {
        // IDM-style categories with Pictures; unrecognized loose files fall back to Others (or AI category).
        AddCategory("Documents", ".doc .docx .xls .xlsx .ppt .pptx .pdf .txt .rtf .odt .ods .odp .csv .md .epub .mobi .azw .azw3 .chm .djvu");
        AddCategory("Compressed", ".zip .rar .7z .tar .gz .gzip .bz2 .xz .tgz .tbz .tbz2 .txz .z .lz .lzma .zst .cab .arj .ace");
        AddCategory("Programs", ".exe .msi .msix .msixbundle .appx .appxbundle .msu .msp .iso .img .com");
        AddCategory("Music", ".mp3 .mp2 .mpa .wav .wma .aac .m4a .flac .ogg .oga .opus .mid .midi .aif .aiff .ape .alac");
        AddCategory("Video", ".mp4 .m4v .mkv .avi .mov .wmv .mpg .mpeg .mpe .webm .flv .f4v .3gp .3g2 .vob .asf .rm .rmvb .m2ts .mts");
        AddCategory("Pictures", ".jpg .jpeg .jpe .jfif .png .apng .gif .bmp .dib .tif .tiff .webp .avif .heic .heif .svg .ico");
    }

    internal static bool TryGetKnownCategory(string extension, out string? category) =>
        ExtensionToCategory.TryGetValue(extension, out category);

    public DownloadOrganizerService() { }

    internal DownloadOrganizerService(string downloadsRoot) => _downloadsRoot = Path.GetFullPath(downloadsRoot);

    private static void AddCategory(string category, string extensions)
    {
        foreach (string extension in extensions.Split(' '))
        {
            ExtensionToCategory.Add(extension, category);
        }
    }

    public string? GetDownloadsPath()
    {
        if (_downloadsRoot != null)
        {
            return Directory.Exists(_downloadsRoot) ? _downloadsRoot : null;
        }

        try
        {
            var guid = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
            int hr = SHGetKnownFolderPath(guid, 0, IntPtr.Zero, out var pathPtr);
            if (hr == 0 && pathPtr != IntPtr.Zero)
            {
                try
                {
                    string? path = Marshal.PtrToStringUni(pathPtr);
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    {
                        return path;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }
            }
        }
        catch { }

        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(fallback) ? fallback : null;
    }

    public Task<List<TempFileInfo>> ScanDownloadsAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ScanTruncated = false;
        var results = new List<TempFileInfo>();
        string? root = GetDownloadsPath();
        if (root == null || !OptimizationFileSafety.HasNoReparsePoints(root)) return results;

        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (OptimizationProtectionPolicy.IsSensitiveState(file.FullName) || !OptimizationFileSafety.IsEligible(file, MinimumAge)) continue;

                bool known = ExtensionToCategory.TryGetValue(file.Extension, out string? category);
                string resolvedCategory = known ? category! : OthersCategory;
                string extensionLabel = string.IsNullOrEmpty(file.Extension) ? "extensionless" : file.Extension;

                results.Add(new TempFileInfo
                {
                    ItemId = $"item-{results.Count + 1:D6}",
                    FilePath = file.FullName,
                    FileName = file.Name,
                    SizeInBytes = file.Length,
                    LastModified = file.LastWriteTimeUtc,
                    Category = resolvedCategory,
                    EvidenceKind = known ? "download-extension" : "download-fallback",
                    Risk = RiskLevel.Low,
                    Action = "move",
                    TargetRelativePath = resolvedCategory,
                    ReasonEn = known
                        ? $"Unchanged for at least 10 minutes; organize by {file.Extension} into {resolvedCategory}"
                        : $"Unchanged for at least 10 minutes; unrecognized {extensionLabel} file routed to Others fallback or AI category",
                    ReasonZh = known
                        ? $"至少 10 分钟未修改；按 {file.Extension} 类型整理至 {resolvedCategory}"
                        : $"至少 10 分钟未修改；未识别类型（{extensionLabel}），默认兜底整理至 Others 或由 AI 归类",
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (results.Count >= 500)
            {
                ScanTruncated = true;
                break;
            }
        }

        return results;
    }, cancellationToken);

    public Task<List<TempFileInfo>> ScanAsync(CancellationToken cancellationToken = default) => ScanDownloadsAsync(cancellationToken);

    public Task<(int Succeeded, int Failed)> OrganizeSelectedAsync(IEnumerable<TempFileInfo> items, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        int succeeded = 0;
        int failed = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.IsSelected) continue;
            if (OrganizeItem(item, cancellationToken)) succeeded++;
            else failed++;
        }

        return (succeeded, failed);
    }, cancellationToken);

    public bool OrganizeItem(TempFileInfo item, CancellationToken cancellationToken = default) =>
        OrganizeItemDetailed(item, cancellationToken).Success;

    public (bool Success, string? DestinationPath, string? Error) OrganizeItemDetailed(TempFileInfo item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string? root = GetDownloadsPath();
            if (root == null || item.Action != "move") return (false, null, "Downloads location or requested action is invalid.");

            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var file = new FileInfo(item.FilePath);
            if (OptimizationProtectionPolicy.IsSensitiveState(file.FullName))
                return (false, null, "Credentials and configuration are protected. / 凭据和配置受保护。");
            if (!string.Equals(file.DirectoryName, root, StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, "File is outside the download rules, in use, or changed since scanning; scan again.");
            }

            string? category;
            if (ExtensionToCategory.TryGetValue(file.Extension, out string? knownCategory))
            {
                if (!string.Equals(item.TargetRelativePath, knownCategory, StringComparison.Ordinal))
                {
                    return (false, null, "File is outside the download rules, in use, or changed since scanning; scan again.");
                }
                category = knownCategory;
            }
            else if (!string.IsNullOrEmpty(item.TargetRelativePath) && AllowedCategories.Contains(item.TargetRelativePath))
            {
                category = item.TargetRelativePath;
            }
            else
            {
                return (false, null, "File is outside the download rules, in use, or changed since scanning; scan again.");
            }

            if (!OptimizationFileSafety.IsEligible(file, MinimumAge, item))
            {
                return (false, null, "File is outside the download rules, in use, or changed since scanning; scan again.");
            }

            string targetDir = Path.Combine(root, category);
            if (!OptimizationFileSafety.HasNoReparsePoints(targetDir)) return (false, null, "The destination contains a reparse point.");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(targetDir);
            if (!OptimizationFileSafety.HasNoReparsePoints(targetDir)) return (false, null, "The destination contains a reparse point.");

            // Derive the name from the validated source, never from recommendation metadata.
            string targetFile = Path.Combine(targetDir, file.Name);
            int counter = 1;
            while (File.Exists(targetFile) || Directory.Exists(targetFile))
            {
                cancellationToken.ThrowIfCancellationRequested();
                targetFile = Path.Combine(targetDir, $"{Path.GetFileNameWithoutExtension(file.Name)}_{counter++}{file.Extension}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(file.FullName, targetFile); // Never overwrite an existing download.
            return (true, targetFile, null);
        }
        catch (IOException) { return (false, null, "File unavailable or in use."); }
        catch (UnauthorizedAccessException) { return (false, null, "File access denied."); }
        catch (ArgumentException) { return (false, null, "Invalid file path."); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
