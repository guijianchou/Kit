// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.WingetLib;

internal sealed class WingetDownloadCache
{
    internal const string OwnerFileName = ".kit-winget-owned";
    internal const string OwnerContent = "Kit.Winget.DownloadCache.v1";
    internal const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    private readonly string _root;

    internal WingetDownloadCache(string? root = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(Path.GetTempPath(), "Kit", "Winget"));
    }

    internal string CreateOperationDirectory()
    {
        VerifyAncestors(_root);
        Directory.CreateDirectory(_root);
        Cleanup(DateTimeOffset.UtcNow);
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, OwnerFileName), OwnerContent);
        return directory;
    }

    internal IReadOnlyList<string> GetDownloadedFiles(string directory)
    {
        ValidateOwnedDirectory(directory);
        var files = InspectFiles(directory).Where(file => !string.Equals(file.Name, OwnerFileName, StringComparison.Ordinal)).ToArray();
        if (files.Sum(file => file.Length) > MaximumBytes)
        {
            throw new WingetOperationException("The download exceeds the 4 GiB Kit cache limit. Use the manual WinGet command instead.");
        }

        Cleanup(DateTimeOffset.UtcNow, directory);
        return files.Select(file => file.FullName).ToArray();
    }

    internal void Cleanup(DateTimeOffset now, string? keepDirectory = null)
    {
        VerifyAncestors(_root);
        if (!Directory.Exists(_root))
        {
            return;
        }

        var entries = new List<CacheEntry>();
        foreach (var path in Directory.EnumerateDirectories(_root))
        {
            try
            {
                ValidateOwnedDirectory(path);
                var files = InspectFiles(path);
                entries.Add(new CacheEntry(path, File.GetLastWriteTimeUtc(Path.Combine(path, OwnerFileName)), files.Sum(file => file.Length)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Foreign directories and reparse points never become cleanup candidates.
            }
        }

        var totalBytes = entries.Sum(entry => entry.Bytes);
        foreach (var entry in entries.OrderBy(entry => entry.CreatedUtc))
        {
            if (string.Equals(entry.Path, keepDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (now - entry.CreatedUtc <= TimeSpan.FromDays(7) && totalBytes <= MaximumBytes)
            {
                continue;
            }

            if (TryRemoveOperationDirectory(entry.Path))
            {
                totalBytes -= entry.Bytes;
            }
        }
    }

    internal bool TryRemoveOperationDirectory(string directory)
    {
        try
        {
            ValidateOwnedDirectory(directory);
            _ = InspectFiles(directory);
            DeleteDirectoryWithoutLinks(directory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static List<FileInfo> InspectFiles(string directory)
    {
        VerifyNoLink(directory);
        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Kit does not traverse reparse points in its download cache.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                files.AddRange(InspectFiles(path));
            }
            else
            {
                files.Add(new FileInfo(path));
            }
        }

        return files;
    }

    private static void DeleteDirectoryWithoutLinks(string directory)
    {
        VerifyNoLink(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Kit does not delete reparse points in its download cache.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryWithoutLinks(path);
            }
            else
            {
                File.Delete(path);
            }
        }

        Directory.Delete(directory, recursive: false);
    }

    private static void VerifyNoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The Kit download cache must not contain reparse points.");
        }
    }

    private static void VerifyAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists)
            {
                VerifyNoLink(current.FullName);
            }
        }
    }

    private void ValidateOwnedDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(fullPath), _root, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _))
        {
            throw new ArgumentException("Only an immediate Kit operation directory can be removed.", nameof(directory));
        }

        VerifyAncestors(fullPath);
        var marker = Path.Combine(fullPath, OwnerFileName);
        VerifyNoLink(marker);
        if (!string.Equals(File.ReadAllText(marker), OwnerContent, StringComparison.Ordinal))
        {
            throw new IOException("This directory is not owned by the Kit WinGet download cache.");
        }
    }

    private sealed record CacheEntry(string Path, DateTimeOffset CreatedUtc, long Bytes);
}
