// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.RegularExpressions;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Matches and removes installer files for software that is already installed.
/// </summary>
public static class MonitorInstallerCleaner
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
    /// <param name="programsPath">The Programs category folder to scan.</param>
    /// <param name="installedSoftwareNames">Installed software names.</param>
    /// <returns>Installer matches sorted by confidence descending.</returns>
    public static IReadOnlyList<MonitorInstallerMatch> FindMatches(string programsPath, IEnumerable<string> installedSoftwareNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programsPath);
        ArgumentNullException.ThrowIfNull(installedSoftwareNames);

        DirectoryInfo programsDirectory = new(programsPath);
        if (!programsDirectory.Exists)
        {
            return Array.Empty<MonitorInstallerMatch>();
        }

        List<(FileInfo File, string CoreName)> installerTable = programsDirectory
            .EnumerateFiles()
            .Where(file => InstallerExtensions.Contains(file.Extension))
            .Select(file => (file, ExtractCoreName(Path.GetFileNameWithoutExtension(file.Name))))
            .ToList();

        List<MonitorInstallerMatch> matches = new();
        HashSet<string> matchedPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (string softwareName in installedSoftwareNames)
        {
            string normalizedSoftwareName = NormalizeSoftwareName(softwareName);
            if (normalizedSoftwareName.Length < 3)
            {
                continue;
            }

            HashSet<string> keywords = ExtractKeywords(softwareName);
            foreach ((FileInfo File, string CoreName) installer in installerTable)
            {
                if (matchedPaths.Contains(installer.File.FullName))
                {
                    continue;
                }

                double confidence = CalculateSimilarity(installer.CoreName, normalizedSoftwareName, keywords);
                if (confidence >= 0.5)
                {
                    matches.Add(new MonitorInstallerMatch(installer.File.FullName, softwareName, confidence, installer.File.Length));
                    matchedPaths.Add(installer.File.FullName);
                }
            }
        }

        return matches.OrderByDescending(match => match.Confidence).ToList();
    }

    /// <summary>
    /// Finds installer files that match installed software names and versions.
    /// </summary>
    /// <param name="programsPath">The Programs category folder to scan.</param>
    /// <param name="installedSoftwareIndex">Installed software metadata.</param>
    /// <returns>Installer matches sorted by confidence descending.</returns>
    public static IReadOnlyList<MonitorInstallerMatch> FindMatches(string programsPath, MonitorInstalledSoftwareIndex installedSoftwareIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programsPath);
        ArgumentNullException.ThrowIfNull(installedSoftwareIndex);

        DirectoryInfo programsDirectory = new(programsPath);
        if (!programsDirectory.Exists)
        {
            return Array.Empty<MonitorInstallerMatch>();
        }

        List<(FileInfo File, string CoreName, string? Version)> installerTable = programsDirectory
            .EnumerateFiles()
            .Where(file => InstallerExtensions.Contains(file.Extension))
            .Select(file => (file, ExtractCoreName(Path.GetFileNameWithoutExtension(file.Name)), ExtractVersion(Path.GetFileNameWithoutExtension(file.Name))))
            .ToList();

        List<MonitorInstallerMatch> matches = new();
        HashSet<string> matchedPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (MonitorInstalledSoftwareEntry softwareEntry in installedSoftwareIndex.Entries)
        {
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
                if (matchedPaths.Contains(installer.File.FullName) || !string.Equals(NormalizeVersion(installer.Version), normalizedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double confidence = CalculateSimilarity(installer.CoreName, normalizedSoftwareName, keywords);
                if (confidence >= 0.5)
                {
                    matches.Add(new MonitorInstallerMatch(installer.File.FullName, softwareEntry.DisplayName, Math.Min(0.99, confidence + 0.03), installer.File.Length));
                    matchedPaths.Add(installer.File.FullName);
                }
            }
        }

        return matches.OrderByDescending(match => match.Confidence).ToList();
    }

    /// <summary>
    /// Deletes or previews deletion for matched installers.
    /// </summary>
    /// <param name="programsPath">The Programs category folder that bounds deletions.</param>
    /// <param name="matches">The installer matches to clean.</param>
    /// <param name="minConfidence">The minimum confidence required to delete.</param>
    /// <param name="dryRun">True to report without deleting.</param>
    /// <returns>A cleanup summary.</returns>
    public static MonitorInstallerCleanupResult Cleanup(
        string programsPath,
        IEnumerable<MonitorInstallerMatch> matches,
        double minConfidence,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programsPath);
        ArgumentNullException.ThrowIfNull(matches);

        int deleted = 0;
        int skipped = 0;
        int errors = 0;
        long freedBytes = 0;

        foreach (MonitorInstallerMatch match in matches)
        {
            if (match.Confidence < minConfidence || !MonitorFileOrganizer.IsPathInsideRoot(programsPath, match.FilePath) || !File.Exists(match.FilePath))
            {
                skipped++;
                continue;
            }

            try
            {
                long fileSize = new FileInfo(match.FilePath).Length;
                if (!dryRun)
                {
                    File.Delete(match.FilePath);
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
}
