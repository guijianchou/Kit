// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Shared, fault-tolerant file-system enumeration helpers for Monitor scans.
/// Enumeration that fails mid-pass (deleted directory, locked file, denied access) yields what it can and stops,
/// so a single unreadable entry never aborts a scan.
/// </summary>
internal static class MonitorFileSystem
{
    /// <summary>
    /// Enumerates files in a directory, yielding nothing when the directory cannot be read.
    /// </summary>
    /// <param name="directory">The directory to enumerate.</param>
    /// <returns>The files that could be read.</returns>
    public static IEnumerable<FileInfo> SafeEnumerateFiles(this DirectoryInfo directory)
    {
        IEnumerator<FileInfo> enumerator;
        try
        {
            enumerator = directory.EnumerateFiles().GetEnumerator();
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                FileInfo file;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    file = enumerator.Current;
                }
                catch (DirectoryNotFoundException)
                {
                    yield break;
                }
                catch (IOException)
                {
                    yield break;
                }
                catch (UnauthorizedAccessException)
                {
                    yield break;
                }

                yield return file;
            }
        }
    }

    /// <summary>
    /// Enumerates subdirectories of a directory, yielding nothing when the directory cannot be read.
    /// </summary>
    /// <param name="directory">The directory to enumerate.</param>
    /// <returns>The subdirectories that could be read.</returns>
    public static IEnumerable<DirectoryInfo> SafeEnumerateDirectories(this DirectoryInfo directory)
    {
        IEnumerator<DirectoryInfo> enumerator;
        try
        {
            enumerator = directory.EnumerateDirectories().GetEnumerator();
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                DirectoryInfo childDirectory;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    childDirectory = enumerator.Current;
                }
                catch (DirectoryNotFoundException)
                {
                    yield break;
                }
                catch (IOException)
                {
                    yield break;
                }
                catch (UnauthorizedAccessException)
                {
                    yield break;
                }

                yield return childDirectory;
            }
        }
    }

    /// <summary>
    /// Returns whether a file-system entry is a reparse point. Read failures are treated as reparse points so the
    /// caller skips entries it cannot safely inspect (matching the previous per-call behavior).
    /// </summary>
    /// <param name="fileSystemInfo">The entry to inspect.</param>
    /// <returns>True when the entry is a reparse point or cannot be inspected.</returns>
    public static bool IsReparsePoint(this FileSystemInfo fileSystemInfo)
    {
        try
        {
            return fileSystemInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
