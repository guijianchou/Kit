// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Matches and removes installer files for software that is already installed.
/// </summary>
public static partial class MonitorInstallerCleaner
{
    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".appx", ".msix", ".appxbundle", ".msixbundle",
    };

    private static readonly HashSet<string> InstallerNoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "install", "installer", "update", "updater", "x64", "x86", "win64", "win32", "win", "windows", "portable", "full",
        "stable", "overseas", "global", "latest", "x", "exe", "msi",
    };

    private static readonly HashSet<string> SoftwareNoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "x64", "x86", "user", "runtime", "additional", "minimum",
    };

    /// <summary>
    /// Finds installer files that match installed software names.
    /// </summary>
    /// <param name="scanRootPath">The folder to scan for installer files.</param>
    /// <param name="installedSoftwareNames">Installed software names.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative shutdown.</param>
    /// <returns>Installer matches sorted by confidence descending.</returns>
    public static IReadOnlyList<MonitorInstallerMatch> FindMatches(string scanRootPath, IEnumerable<string> installedSoftwareNames, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scanRootPath);
        ArgumentNullException.ThrowIfNull(installedSoftwareNames);

        DirectoryInfo scanRootDirectory = new(scanRootPath);
        if (!scanRootDirectory.Exists)
        {
            return Array.Empty<MonitorInstallerMatch>();
        }

        List<(FileInfo File, string CoreName)> installerTable = scanRootDirectory
            .SafeEnumerateFiles()
            .Where(file => InstallerExtensions.Contains(file.Extension))
            .Select(file => (file, ExtractCoreName(Path.GetFileNameWithoutExtension(file.Name))))
            .ToList();

        List<MonitorInstallerMatch> candidates = new();

        foreach (string softwareName in installedSoftwareNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedSoftwareName = NormalizeSoftwareName(softwareName);
            if (normalizedSoftwareName.Length < 3)
            {
                continue;
            }

            HashSet<string> keywords = ExtractKeywords(softwareName);
            foreach ((FileInfo File, string CoreName) installer in installerTable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double confidence = CalculateSimilarity(installer.CoreName, normalizedSoftwareName, keywords);
                if (confidence >= 0.5)
                {
                    candidates.Add(new MonitorInstallerMatch(installer.File.FullName, softwareName, confidence, installer.File.Length));
                }
            }
        }

        return SelectBestMatchPerInstaller(candidates);
    }

    /// <summary>
    /// Finds installer files that match installed software names and versions.
    /// </summary>
    /// <param name="scanRootPath">The folder to scan for installer files.</param>
    /// <param name="installedSoftwareIndex">Installed software metadata.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative shutdown.</param>
    /// <returns>Installer matches sorted by confidence descending.</returns>
    public static IReadOnlyList<MonitorInstallerMatch> FindMatches(string scanRootPath, MonitorInstalledSoftwareIndex installedSoftwareIndex, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scanRootPath);
        ArgumentNullException.ThrowIfNull(installedSoftwareIndex);

        DirectoryInfo scanRootDirectory = new(scanRootPath);
        if (!scanRootDirectory.Exists)
        {
            return Array.Empty<MonitorInstallerMatch>();
        }

        List<(FileInfo File, string CoreName, string? Version)> installerTable = scanRootDirectory
            .SafeEnumerateFiles()
            .Where(file => InstallerExtensions.Contains(file.Extension))
            .Select(file => (file, ExtractCoreName(Path.GetFileNameWithoutExtension(file.Name)), ExtractVersion(Path.GetFileNameWithoutExtension(file.Name))))
            .ToList();

        List<MonitorInstallerMatch> candidates = new();

        foreach (MonitorInstalledSoftwareEntry softwareEntry in installedSoftwareIndex.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedVersion = NormalizeVersion(softwareEntry.DisplayVersion);
            if (normalizedVersion.Length == 0)
            {
                continue;
            }

            string normalizedSoftwareName = NormalizeSoftwareName(softwareEntry.DisplayName);
            if (normalizedSoftwareName.Length < 3)
            {
                continue;
            }

            HashSet<string> keywords = ExtractKeywords(softwareEntry.DisplayName);
            foreach ((FileInfo File, string CoreName, string? Version) installer in installerTable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(NormalizeVersion(installer.Version), normalizedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double confidence = CalculateSimilarity(installer.CoreName, normalizedSoftwareName, keywords);
                if (confidence >= 0.5)
                {
                    candidates.Add(new MonitorInstallerMatch(installer.File.FullName, softwareEntry.DisplayName, Math.Min(0.99, confidence + 0.03), installer.File.Length));
                }
            }
        }

        return SelectBestMatchPerInstaller(candidates);
    }

    /// <summary>
    /// Deletes or previews deletion for matched installers.
    /// </summary>
    /// <param name="cleanupRootPath">The folder that bounds installer cleanup.</param>
    /// <param name="matches">The installer matches to clean.</param>
    /// <param name="minConfidence">The minimum confidence required to delete.</param>
    /// <param name="dryRun">True to report without deleting.</param>
    /// <param name="sendToRecycleBin">True to move deleted files to the Recycle Bin; false to delete permanently (used by tests).</param>
    /// <param name="cancellationToken">Cancellation token for cooperative shutdown.</param>
    /// <returns>A cleanup summary.</returns>
    public static MonitorInstallerCleanupResult Cleanup(
        string cleanupRootPath,
        IEnumerable<MonitorInstallerMatch> matches,
        double minConfidence,
        bool dryRun,
        bool sendToRecycleBin = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cleanupRootPath);
        ArgumentNullException.ThrowIfNull(matches);

        int deleted = 0;
        int skipped = 0;
        int errors = 0;
        long freedBytes = 0;

        foreach (MonitorInstallerMatch match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (match.Confidence < minConfidence || !MonitorFileOrganizer.IsPathInsideRoot(cleanupRootPath, match.FilePath) || !File.Exists(match.FilePath))
            {
                skipped++;
                continue;
            }

            try
            {
                long fileSize = new FileInfo(match.FilePath).Length;
                if (!dryRun)
                {
                    if (sendToRecycleBin)
                    {
                        RecycleFile(match.FilePath);
                    }
                    else
                    {
                        File.Delete(match.FilePath);
                    }
                }

                deleted++;
                freedBytes += fileSize;
            }
            catch (IOException)
            {
                errors++;
            }
            catch (UnauthorizedAccessException)
            {
                errors++;
            }
        }

        return new MonitorInstallerCleanupResult(deleted, skipped, errors, freedBytes);
    }

    private static IEnumerable<FileInfo> SafeEnumerateFiles(this DirectoryInfo directory)
    {
        FileInfo[] files;
        try
        {
            files = directory.EnumerateFiles().ToArray();
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

        foreach (FileInfo file in files)
        {
            yield return file;
        }
    }

    private static IReadOnlyList<MonitorInstallerMatch> SelectBestMatchPerInstaller(IEnumerable<MonitorInstallerMatch> candidates)
    {
        return candidates
            .GroupBy(match => match.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(match => match.Confidence)
                .ThenBy(match => match.SoftwareName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(match => match.Confidence)
            .ThenBy(match => match.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ExtractCoreName(string fileName)
    {
        IEnumerable<string> cleanParts = Regex
            .Split(fileName.ToLowerInvariant(), @"[-_.\s]+")
            .Where(part => part.Length >= 2)
            .Where(part => !InstallerNoiseWords.Contains(part))
            .Where(part => !Regex.IsMatch(part, @"^v?\d+[\d.]*((build|beta|alpha|rc)\d*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        return string.Concat(cleanParts);
    }

    private static string NormalizeSoftwareName(string softwareName)
    {
        string name = softwareName.ToLowerInvariant();
        name = Regex.Replace(name, @"\s*\(.*?\)\s*", string.Empty, RegexOptions.CultureInvariant);
        name = Regex.Replace(name, @"\s*-\s*.*$", string.Empty, RegexOptions.CultureInvariant);
        name = Regex.Replace(name, @"\s+v?\d+[\d.]*.*$", string.Empty, RegexOptions.CultureInvariant);
        return Regex.Replace(name, @"[\s\-_\.\(\)\[\]]+", string.Empty, RegexOptions.CultureInvariant).Trim();
    }

    private static HashSet<string> ExtractKeywords(string softwareName)
    {
        HashSet<string> keywords = Regex
            .Split(softwareName.ToLowerInvariant(), @"[\s\-_\.\(\)\[\]]+")
            .Where(word => word.Length >= 4)
            .Where(word => !SoftwareNoiseWords.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        MatchCollection capitalizedWords = Regex.Matches(softwareName, @"[A-Z][a-z]+", RegexOptions.CultureInvariant);
        if (capitalizedWords.Count >= 2)
        {
            string acronym = string.Concat(capitalizedWords.Select(match => char.ToLowerInvariant(match.Value[0])));
            if (acronym.Length >= 2)
            {
                keywords.Add(acronym);
            }
        }

        return keywords;
    }

    private static double CalculateSimilarity(string installerCoreName, string softwareName, HashSet<string> softwareKeywords)
    {
        if (string.IsNullOrEmpty(installerCoreName) || string.IsNullOrEmpty(softwareName))
        {
            return 0;
        }

        if (string.Equals(installerCoreName, softwareName, StringComparison.OrdinalIgnoreCase))
        {
            return 0.98;
        }

        if (installerCoreName.Length >= 4 && softwareName.Contains(installerCoreName, StringComparison.OrdinalIgnoreCase))
        {
            return 0.95;
        }

        if (softwareName.Length >= 4 && installerCoreName.Contains(softwareName, StringComparison.OrdinalIgnoreCase))
        {
            return 0.9;
        }

        foreach (string keyword in softwareKeywords)
        {
            if (keyword.Length <= 4 && (installerCoreName.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) || installerCoreName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                return 0.85;
            }

            if (keyword.Length >= 5 && (keyword.Contains(installerCoreName, StringComparison.OrdinalIgnoreCase) || installerCoreName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                return 0.85;
            }
        }

        int longestCommonSubstring = LongestCommonSubstringLength(installerCoreName, softwareName);
        int shorterLength = Math.Min(installerCoreName.Length, softwareName.Length);
        int longerLength = Math.Max(installerCoreName.Length, softwareName.Length);
        if (longestCommonSubstring >= 5 && shorterLength >= 5)
        {
            double ratio = (double)longestCommonSubstring / shorterLength;
            double coverage = (double)longestCommonSubstring / longerLength;
            if (ratio >= 0.8 && coverage >= 0.4)
            {
                return 0.5 + (ratio * 0.4);
            }
        }

        return 0;
    }

    private static string? ExtractVersion(string fileName)
    {
        Match match = Regex.Match(fileName, @"(?<![A-Za-z0-9])(\d+(?:[._-]\d+){1,4})(?!\d)", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        Match match = Regex.Match(version, @"(?<!\d)(\d+(?:[._-]\d+)*)(?!\d)", RegexOptions.CultureInvariant);
        string normalized = match.Success ? match.Groups[1].Value : version;
        return Regex.Replace(normalized, @"[._-]+", ".", RegexOptions.CultureInvariant).Trim('.');
    }

    private static int LongestCommonSubstringLength(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0)
        {
            return 0;
        }

        if (first.Length > second.Length)
        {
            (first, second) = (second, first);
        }

        int[] previous = new int[second.Length + 1];
        int[] current = new int[second.Length + 1];
        int maximumLength = 0;

        for (int firstIndex = 1; firstIndex <= first.Length; firstIndex++)
        {
            for (int secondIndex = 1; secondIndex <= second.Length; secondIndex++)
            {
                if (first[firstIndex - 1] == second[secondIndex - 1])
                {
                    current[secondIndex] = previous[secondIndex - 1] + 1;
                    maximumLength = Math.Max(maximumLength, current[secondIndex]);
                }
                else
                {
                    current[secondIndex] = 0;
                }
            }

            (previous, current) = (current, previous);
        }

        return maximumLength;
    }

    private const uint FileOperationDelete = 0x0003;
    private const ushort FileOperationFlagSilent = 0x0004;
    private const ushort FileOperationFlagNoConfirmation = 0x0010;
    private const ushort FileOperationFlagAllowUndo = 0x0040;
    private const ushort FileOperationFlagNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SHFileOperation(ref ShFileOpStruct fileOp);

    private static void RecycleFile(string filePath)
    {
        // SHFileOperation's pFrom must be double-null terminated. StringToHGlobalUni appends its own
        // terminating null, and the extra "\0\0" guarantees the required double termination so the
        // operation only ever targets this single path.
        IntPtr from = Marshal.StringToHGlobalUni(filePath + "\0\0");
        try
        {
            ShFileOpStruct fileOp = new()
            {
                Hwnd = IntPtr.Zero,
                Func = FileOperationDelete,
                From = from,
                To = IntPtr.Zero,
                Flags = unchecked((ushort)(FileOperationFlagAllowUndo | FileOperationFlagNoConfirmation | FileOperationFlagSilent | FileOperationFlagNoErrorUi)),
                AnyOperationsAborted = 0,
                NameMappings = IntPtr.Zero,
                ProgressTitle = IntPtr.Zero,
            };

            int result = SHFileOperation(ref fileOp);
            if (result != 0 || fileOp.AnyOperationsAborted != 0)
            {
                throw new IOException("Failed to move file to the Recycle Bin. SHFileOperation returned " + result.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }
}
