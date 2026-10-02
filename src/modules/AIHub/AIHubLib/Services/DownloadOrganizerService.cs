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
    private static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(10);
    private readonly string? _downloadsRoot;

    private static readonly Dictionary<string, string> ExtensionToCategory = new(StringComparer.OrdinalIgnoreCase);

    static DownloadOrganizerService()
    {
        // IDM-style categories; images and unrecognized extensions stay where they are.
        AddCategory("Documents", ".doc .docx .xls .xlsx .ppt .pptx .pdf .txt .rtf .odt .ods .odp .csv .md .epub .mobi .azw .azw3 .chm .djvu");
        AddCategory("Compressed", ".zip .rar .7z .tar .gz .gzip .bz2 .xz .tgz .tbz .tbz2 .txz .z .lz .lzma .zst .cab .arj .ace");
        AddCategory("Programs", ".exe .msi .msix .msixbundle .appx .appxbundle .msu .msp .iso .img .com");
        AddCategory("Music", ".mp3 .mp2 .mpa .wav .wma .aac .m4a .flac .ogg .oga .opus .mid .midi .aif .aiff .ape .alac");
        AddCategory("Video", ".mp4 .m4v .mkv .avi .mov .wmv .mpg .mpeg .mpe .webm .flv .f4v .3gp .3g2 .vob .asf .rm .rmvb .m2ts .mts");
    }

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
        var results = new List<TempFileInfo>();
        string? root = GetDownloadsPath();
        if (root == null || !OptimizationFileSafety.HasNoReparsePoints(root)) return results;

        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!ExtensionToCategory.TryGetValue(file.Extension, out string? category) ||
                    !OptimizationFileSafety.IsEligible(file, MinimumAge)) continue;

                results.Add(new TempFileInfo
                {
                    ItemId = $"item-{results.Count + 1:D6}",
                    FilePath = file.FullName,
                    FileName = file.Name,
                    SizeInBytes = file.Length,
                    LastModified = file.LastWriteTimeUtc,
                    Category = category,
                    Risk = RiskLevel.Low,
                    Action = "move",
                    TargetRelativePath = category,
                    ReasonEn = $"Unchanged for at least 10 minutes; organize by {file.Extension} into {category}",
                    ReasonZh = $"至少 10 分钟未修改；按 {file.Extension} 类型整理至 {category}"
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (results.Count >= 500) break;
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
            if (cancellationToken.IsCancellationRequested) break;
            if (!item.IsSelected) continue;
            if (OrganizeItem(item)) succeeded++;
            else failed++;
        }

        return (succeeded, failed);
    }, cancellationToken);

    public bool OrganizeItem(TempFileInfo item)
    {
        try
        {
            string? root = GetDownloadsPath();
            if (root == null || item.Action != "move") return false;

            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var file = new FileInfo(item.FilePath);
            if (!string.Equals(file.DirectoryName, root, StringComparison.OrdinalIgnoreCase) ||
                !ExtensionToCategory.TryGetValue(file.Extension, out string? category) ||
                !string.Equals(item.TargetRelativePath, category, StringComparison.Ordinal) ||
                !OptimizationFileSafety.IsEligible(file, MinimumAge, item)) return false;

            string targetDir = Path.Combine(root, category);
            if (!OptimizationFileSafety.HasNoReparsePoints(targetDir)) return false;
            Directory.CreateDirectory(targetDir);
            if (!OptimizationFileSafety.HasNoReparsePoints(targetDir)) return false;

            // Derive the name from the validated source, never from recommendation metadata.
            string targetFile = Path.Combine(targetDir, file.Name);
            int counter = 1;
            while (File.Exists(targetFile) || Directory.Exists(targetFile))
            {
                targetFile = Path.Combine(targetDir, $"{Path.GetFileNameWithoutExtension(file.Name)}_{counter++}{file.Extension}");
            }

            File.Move(file.FullName, targetFile); // Never overwrite an existing download.
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
