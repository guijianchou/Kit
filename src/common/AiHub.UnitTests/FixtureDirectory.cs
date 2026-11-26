namespace Kit.AiHub.UnitTests;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal sealed class FixtureDirectory : IDisposable
{
    private readonly string _parentDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Kit.AiHub.Tests"));
    private readonly List<string> _directoryLinks = new();

    internal string RootPath { get; }

    internal FixtureDirectory()
    {
        RootPath = Path.Combine(_parentDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    internal string PathFor(params string[] components)
    {
        string candidate = Path.GetFullPath(Path.Combine(RootPath, Path.Combine(components)));
        if (!candidate.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The fixture path must stay inside its temporary directory.");
        }

        return candidate;
    }

    internal void CreateReparseDirectoryLink(string linkPath, string targetPath)
    {
        // Both ends stay in this fixture. Only the product's narrower package/storage
        // boundary is crossed, never the user's filesystem outside the temporary root.
        string link = PathFor(Path.GetRelativePath(RootPath, linkPath));
        string target = PathFor(Path.GetRelativePath(RootPath, targetPath));
        if ((int)new FileInfo(link).Attributes != -1 || !Directory.Exists(target))
        {
            throw new IOException("The fixture link must be absent and its target must exist.");
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && (exception is UnauthorizedAccessException or IOException))
        {
            // Directory junctions carry the same ReparsePoint attribute checked by
            // production and do not require the symbolic-link privilege/Developer Mode.
            CreateJunction(link, target);
        }

        _directoryLinks.Add(link);
        if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0 ||
            !string.Equals(Directory.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The fixture did not create a directory reparse point to its target.");
        }
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(RootPath);
        if (!string.Equals(Path.GetDirectoryName(resolved), _parentDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
        {
            throw new InvalidOperationException("Refusing to remove an unexpected fixture directory.");
        }

        if (Directory.Exists(resolved))
        {
            // Windows recursive deletion treats junctions as volume mount points and
            // may fail while trying to unmount them. Remove our links directly first.
            for (int index = _directoryLinks.Count - 1; index >= 0; index--)
            {
                string link = PathFor(Path.GetRelativePath(RootPath, _directoryLinks[index]));
                FileAttributes attributes = new FileInfo(link).Attributes;
                if ((int)attributes == -1)
                {
                    continue;
                }

                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
                    (FileAttributes.Directory | FileAttributes.ReparsePoint))
                {
                    throw new IOException("Refusing to remove a fixture link that is no longer a directory reparse point.");
                }

                Directory.Delete(link, recursive: false);
            }

            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        const uint reparseTagMountPoint = 0xA0000003;
        const uint setReparsePoint = 0x000900A4;
        const uint genericWrite = 0x40000000;
        const uint openExisting = 3;
        const uint openDirectoryReparsePoint = 0x02000000 | 0x00200000;

        // REPARSE_DATA_BUFFER: 8-byte common header, 8-byte mount-point header,
        // then two null-terminated UTF-16 paths. Offsets are relative to PathBuffer.
        byte[] substituteName = Encoding.Unicode.GetBytes(@"\??\" + target);
        byte[] printName = Encoding.Unicode.GetBytes(target);
        byte[] buffer = new byte[16 + substituteName.Length + 2 + printName.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, reparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substituteName.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substituteName.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)printName.Length));
        substituteName.CopyTo(buffer, 16);
        printName.CopyTo(buffer, 16 + substituteName.Length + 2);

        if ((int)new FileInfo(link).Attributes != -1)
        {
            throw new IOException("Refusing to replace an existing fixture link.");
        }

        Directory.CreateDirectory(link);
        try
        {
            using SafeFileHandle handle = NativeMethods.CreateFileW(link, genericWrite, FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero, openExisting, openDirectoryReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid || !NativeMethods.DeviceIoControl(handle, setReparsePoint, buffer, (uint)buffer.Length,
                IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new IOException("Could not create the directory junction fixture.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        catch
        {
            // Nonrecursive deletion removes the empty directory or link itself.
            Directory.Delete(link, recursive: false);
            throw;
        }
    }

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // Test-only interop keeps this fixture free of unsafe generated code.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, [In] byte[] inputBuffer,
            uint inputSize, IntPtr outputBuffer, uint outputSize, out uint bytesReturned, IntPtr overlapped);
#pragma warning restore SYSLIB1054
    }
}
