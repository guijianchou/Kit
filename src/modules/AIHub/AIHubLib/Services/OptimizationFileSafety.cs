using System;
using System.IO;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

internal static class OptimizationFileSafety
{
    internal static bool HasNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsEligible(FileInfo file, TimeSpan minimumAge, TempFileInfo? snapshot = null)
    {
        file.Refresh();
        if (!file.Exists ||
            (file.Attributes & (FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0 ||
            file.LastWriteTimeUtc > DateTime.UtcNow - minimumAge ||
            !HasNoReparsePoints(file.FullName))
        {
            return false;
        }

        // Sidecars may remain open while the final download name already exists.
        foreach (string suffix in new[] { ".part", ".partial", ".crdownload", ".download", ".tmp", ".aria2", ".idm" })
        {
            if (file.Name.Contains(suffix + ".", StringComparison.OrdinalIgnoreCase) ||
                (file.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && suffix != ".tmp") ||
                File.Exists(file.FullName + suffix))
            {
                return false;
            }
        }

        if (snapshot != null && (file.Length != snapshot.SizeInBytes || file.LastWriteTimeUtc != snapshot.LastModified))
        {
            return false;
        }

        // No contents are read. Exclusive access rejects files currently held by a writer.
        using var stream = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
        return true;
    }
}
