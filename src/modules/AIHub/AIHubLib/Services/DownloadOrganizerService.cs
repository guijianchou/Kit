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
    private static readonly Dictionary<string, string> ExtensionToCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".pdf", "Documents" }, { ".docx", "Documents" }, { ".doc", "Documents" },
        { ".xlsx", "Documents" }, { ".xls", "Documents" }, { ".pptx", "Documents" },
        { ".txt", "Documents" }, { ".md", "Documents" }, { ".epub", "Documents" },
        { ".zip", "Archives" }, { ".rar", "Archives" }, { ".7z", "Archives" },
        { ".tar", "Archives" }, { ".gz", "Archives" },
        { ".png", "Images" }, { ".jpg", "Images" }, { ".jpeg", "Images" },
        { ".webp", "Images" }, { ".svg", "Images" }, { ".gif", "Images" },
        { ".exe", "Installers" }, { ".msi", "Installers" }, { ".iso", "Installers" },
        { ".mp4", "Videos" }, { ".mkv", "Videos" }, { ".avi", "Videos" }, { ".mov", "Videos" },
        { ".mp3", "Audio" }, { ".wav", "Audio" }, { ".flac", "Audio" },
        { ".cs", "Code" }, { ".cpp", "Code" }, { ".h", "Code" }, { ".py", "Code" },
        { ".js", "Code" }, { ".ts", "Code" }, { ".json", "Code" }, { ".html", "Code" },
    };

    public string? GetDownloadsPath()
    {
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

    public Task<List<TempFileInfo>> ScanDownloadsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var results = new List<TempFileInfo>();
            string? downloadsRoot = GetDownloadsPath();
            if (downloadsRoot == null || !Directory.Exists(downloadsRoot))
            {
                return results;
            }

            var dir = new DirectoryInfo(downloadsRoot);
            int index = 1;

            foreach (var file in dir.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (cancellationToken.IsCancellationRequested) break;
                if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

                string ext = file.Extension;
                string category = ExtensionToCategory.TryGetValue(ext, out var cat) ? cat : "Other";
                string targetSubfolder = Path.Combine(downloadsRoot, category);

                results.Add(new TempFileInfo
                {
                    ItemId = $"item-{index++:D6}",
                    FilePath = file.FullName,
                    FileName = file.Name,
                    SizeInBytes = file.Length,
                    LastModified = file.LastWriteTimeUtc,
                    Category = category,
                    Risk = RiskLevel.Low,
                    Action = "move",
                    TargetRelativePath = category,
                    ReasonEn = $"Organize into {category} subfolder based on {ext} extension",
                    ReasonZh = $"根据文件类型 {ext} 整理至 {category} 文件夹"
                });

                if (results.Count >= 500) break;
            }

            return results;
        }, cancellationToken);
    }

    public Task<(int Succeeded, int Failed)> OrganizeSelectedAsync(IEnumerable<TempFileInfo> items, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            int succeeded = 0;
            int failed = 0;
            string? downloadsRoot = GetDownloadsPath();
            if (downloadsRoot == null) return (0, 0);

            string canonicalRoot = Path.GetFullPath(downloadsRoot).TrimEnd('\\') + Path.DirectorySeparatorChar;

            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (!item.IsSelected || string.IsNullOrEmpty(item.TargetRelativePath)) continue;

                try
                {
                    string sourcePath = Path.GetFullPath(item.FilePath);
                    if (!sourcePath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        // Forbidden: file is outside downloads root!
                        failed++;
                        continue;
                    }

                    string targetDir = Path.GetFullPath(Path.Combine(downloadsRoot, item.TargetRelativePath));
                    if (!targetDir.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        // Target directory escapes downloads root!
                        failed++;
                        continue;
                    }

                    Directory.CreateDirectory(targetDir);

                    string targetFile = Path.Combine(targetDir, item.FileName);
                    if (File.Exists(targetFile))
                    {
                        string stem = Path.GetFileNameWithoutExtension(item.FileName);
                        string ext = Path.GetExtension(item.FileName);
                        int counter = 1;
                        while (File.Exists(targetFile))
                        {
                            targetFile = Path.Combine(targetDir, $"{stem}_{counter++}{ext}");
                        }
                    }

                    File.Move(sourcePath, targetFile);
                    succeeded++;
                }
                catch
                {
                    failed++;
                }
            }

            return (succeeded, failed);
        }, cancellationToken);
    }

    public Task<List<TempFileInfo>> ScanAsync(CancellationToken cancellationToken = default) => ScanDownloadsAsync(cancellationToken);

    public bool OrganizeItem(string sourceFilePath, string targetRelativePath)
    {
        string? downloadsRoot = GetDownloadsPath();
        if (downloadsRoot == null) return false;

        string canonicalRoot = Path.GetFullPath(downloadsRoot).TrimEnd('\\') + Path.DirectorySeparatorChar;
        string sourcePath = Path.GetFullPath(sourceFilePath);
        if (!sourcePath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)) return false;

        string targetDir = Path.GetFullPath(Path.Combine(downloadsRoot, targetRelativePath));
        if (!targetDir.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)) return false;

        Directory.CreateDirectory(targetDir);
        string fileName = Path.GetFileName(sourcePath);
        string targetFile = Path.Combine(targetDir, fileName);
        if (File.Exists(targetFile))
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int counter = 1;
            while (File.Exists(targetFile))
            {
                targetFile = Path.Combine(targetDir, $"{stem}_{counter++}{ext}");
            }
        }

        File.Move(sourcePath, targetFile);
        return true;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
