using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

internal enum FileEligibilityStatus
{
    Eligible,
    InUse,
    NotEligible,
}

internal static class OptimizationFileSafety
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static readonly string[] SidecarSuffixes = [".part", ".partial", ".crdownload", ".download", ".tmp", ".aria2", ".idm"];

    internal static bool IsFileInUse(IOException exception)
    {
        int code = exception.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation;
    }

    internal static bool HasNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsEligible(FileInfo file, TimeSpan minimumAge, TempFileInfo? snapshot = null) =>
        CheckEligibility(file, minimumAge, snapshot) == FileEligibilityStatus.Eligible;

    internal static FileEligibilityStatus CheckEligibility(FileInfo file, TimeSpan minimumAge, TempFileInfo? snapshot = null)
    {
        file.Refresh();
        if (!file.Exists ||
            (file.Attributes & (FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0 ||
            file.LastWriteTimeUtc > DateTime.UtcNow - minimumAge ||
            !HasNoReparsePoints(file.FullName))
        {
            return FileEligibilityStatus.NotEligible;
        }

        // Sidecars may remain open while the final download name already exists.
        foreach (string suffix in SidecarSuffixes)
        {
            if (file.Name.Contains(suffix + ".", StringComparison.OrdinalIgnoreCase) ||
                (file.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && suffix != ".tmp") ||
                File.Exists(file.FullName + suffix))
            {
                return FileEligibilityStatus.NotEligible;
            }
        }

        if (snapshot != null && (file.Length != snapshot.SizeInBytes || file.LastWriteTimeUtc != snapshot.LastModified))
        {
            return FileEligibilityStatus.NotEligible;
        }

        return CheckExclusiveReadAccess(file.FullName);
    }

    /// <summary>
    /// Fast scan-time eligibility check using NTFS metadata already populated by directory
    /// enumeration, pre-verified reparse-free ancestors, and an in-memory sibling filename set.
    /// Execution paths continue to call the full <see cref="CheckEligibility"/> re-verification.
    /// </summary>
    internal static FileEligibilityStatus CheckScanEligibility(
        FileInfo file,
        DateTime cutoffUtc,
        bool parentVerifiedNoReparse,
        IReadOnlySet<string>? siblingFileNames)
    {
        FileAttributes attributes = file.Attributes;
        if ((attributes & (FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0 ||
            file.LastWriteTimeUtc > cutoffUtc)
        {
            return FileEligibilityStatus.NotEligible;
        }

        if (!parentVerifiedNoReparse && !HasNoReparsePoints(file.FullName))
        {
            return FileEligibilityStatus.NotEligible;
        }

        string name = file.Name;
        foreach (string suffix in SidecarSuffixes)
        {
            if (name.Contains(suffix + ".", StringComparison.OrdinalIgnoreCase) ||
                (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && suffix != ".tmp"))
            {
                return FileEligibilityStatus.NotEligible;
            }

            if (siblingFileNames != null)
            {
                if (siblingFileNames.Contains(name + suffix))
                {
                    return FileEligibilityStatus.NotEligible;
                }
            }
            else if (File.Exists(file.FullName + suffix))
            {
                return FileEligibilityStatus.NotEligible;
            }
        }

        return CheckExclusiveReadAccess(file.FullName);
    }

    private static FileEligibilityStatus CheckExclusiveReadAccess(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            string nativePath = fullPath.Length >= 248 && !fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? (fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath)
                : fullPath;
            // Requesting DELETE with exclusive share mode (0) verifies both delete permission
            // and absence of open handles without requesting FILE_READ_DATA, which avoids
            // triggering synchronous AV content scans across tens of thousands of cached files.
            IntPtr handle = CreateFileW(nativePath, DeleteAccess, 0, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle != InvalidHandleValue)
            {
                CloseHandle(handle);
                return FileEligibilityStatus.Eligible;
            }

            int error = Marshal.GetLastWin32Error();
            return error is ErrorSharingViolation or ErrorLockViolation
                ? FileEligibilityStatus.InUse
                : FileEligibilityStatus.NotEligible;
        }

        try
        {
            using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return FileEligibilityStatus.Eligible;
        }
        catch (IOException exception) when (IsFileInUse(exception))
        {
            return FileEligibilityStatus.InUse;
        }
        catch (IOException)
        {
            return FileEligibilityStatus.NotEligible;
        }
        catch (UnauthorizedAccessException)
        {
            return FileEligibilityStatus.NotEligible;
        }
    }
}
