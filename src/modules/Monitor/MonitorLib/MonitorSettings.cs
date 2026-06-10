// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.ObjectModel;
using System.Linq;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Configuration values used by the Monitor module core library.
/// </summary>
public sealed class MonitorSettings
{
    private static readonly string[] ProgramsExtensions =
    {
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".torrent",
    };

    private static readonly string[] DocumentsExtensions =
    {
        ".pdf", ".doc", ".docx", ".txt", ".rtf", ".md", ".csv", ".xls", ".xlsx", ".ppt", ".pptx", ".epub", ".mobi", ".azw", ".azw3",
        ".py", ".js", ".html", ".css", ".json", ".xml", ".yaml", ".yml", ".sql", ".sh", ".php", ".java", ".cpp", ".c", ".h",
    };

    private static readonly string[] PicturesExtensions =
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg", ".webp", ".ico", ".tiff", ".tif", ".ttf", ".otf", ".woff", ".woff2", ".eot",
    };

    private static readonly string[] MediaExtensions =
    {
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a",
    };

    private MonitorSettings(
        string? downloadsPath = null,
        string? csvPath = null,
        int? intervalSeconds = null,
        int? maxFileSizeForHashMb = null,
        string? hashAlgorithm = null,
        bool? incrementalScan = null,
        bool? runInBackground = null,
        bool? autoOrganize = null,
        bool? autoCleanInstallers = null)
    {
        Categories = new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Programs"] = ProgramsExtensions,
                ["Documents"] = DocumentsExtensions,
                ["Pictures"] = PicturesExtensions,
                ["Media"] = MediaExtensions,
                ["Others"] = Array.Empty<string>(),
            });

        SmartRules = new[]
        {
            new MonitorSmartRule("screenshot*", "Pictures"),
            new MonitorSmartRule("Screen Shot*", "Pictures"),
            new MonitorSmartRule("IMG_*", "Pictures"),
            new MonitorSmartRule("DSC_*", "Pictures"),
            new MonitorSmartRule("wallpaper*", "Pictures"),
            new MonitorSmartRule("*setup*", "Programs"),
            new MonitorSmartRule("*installer*", "Programs"),
            new MonitorSmartRule("*portable*", "Programs"),
        };

        DownloadsPath = string.IsNullOrWhiteSpace(downloadsPath) ? null : downloadsPath;
        CsvPath = string.IsNullOrWhiteSpace(csvPath) ? "results.csv" : csvPath;
        ExcludedFiles = new[] { CsvPath, "results.csv", "desktop.ini", "Thumbs.db", ".DS_Store" }
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IntervalSeconds = intervalSeconds.GetValueOrDefault(7200);
        HashAlgorithm = NormalizeHashAlgorithm(hashAlgorithm);
        CalculateSha1 = !string.IsNullOrWhiteSpace(HashAlgorithm);
        IncrementalScan = incrementalScan.GetValueOrDefault(true);
        RunInBackground = runInBackground.GetValueOrDefault(false);
        AutoOrganize = autoOrganize.GetValueOrDefault(true);
        AutoCleanInstallers = autoCleanInstallers.GetValueOrDefault(false);
        MaxFileSizeForSha1Mb = Math.Max(1, maxFileSizeForHashMb.GetValueOrDefault(500));
    }

    /// <summary>
    /// Gets the optional Downloads root path. A null value means the Windows Downloads folder.
    /// </summary>
    public string? DownloadsPath { get; private init; }

    /// <summary>
    /// Gets the CSV file path used for scan state.
    /// </summary>
    public string CsvPath { get; private init; } = "results.csv";

    /// <summary>
    /// Gets the scan interval in seconds.
    /// </summary>
    public int IntervalSeconds { get; private init; } = 7200;

    /// <summary>
    /// Gets a value indicating whether extension analytics are enabled.
    /// </summary>
    public bool EnableExtensions { get; private init; } = true;

    /// <summary>
    /// Gets a value indicating whether hash values should be calculated.
    /// </summary>
    public bool CalculateSha1 { get; private init; } = true;

    /// <summary>
    /// Gets the selected hash algorithm.
    /// </summary>
    public string HashAlgorithm { get; private init; } = "SHA1";

    /// <summary>
    /// Gets a value indicating whether unchanged files should reuse prior scan data.
    /// </summary>
    public bool IncrementalScan { get; private init; } = true;

    /// <summary>
    /// Gets a value indicating whether Monitor should run continuous background scan cycles.
    /// </summary>
    public bool RunInBackground { get; private init; }

    /// <summary>
    /// Gets a value indicating whether Monitor should organize files into category folders.
    /// </summary>
    public bool AutoOrganize { get; private init; } = true;

    /// <summary>
    /// Gets a value indicating whether Monitor should remove matched installer files.
    /// </summary>
    public bool AutoCleanInstallers { get; private init; }

    /// <summary>
    /// Gets the minimum installer match confidence required before cleanup.
    /// </summary>
    public double InstallerMinConfidence { get; private init; } = 0.7;

    /// <summary>
    /// Gets category names and their file extensions.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Categories { get; private init; }

    /// <summary>
    /// Gets filenames that should be ignored during scans.
    /// </summary>
    public IReadOnlyList<string> ExcludedFiles { get; private init; }

    /// <summary>
    /// Gets filename wildcard rules evaluated before extension matching.
    /// </summary>
    public IReadOnlyList<MonitorSmartRule> SmartRules { get; private init; }

    /// <summary>
    /// Gets the largest file size, in megabytes, eligible for SHA1 calculation.
    /// </summary>
    public int MaxFileSizeForSha1Mb { get; private init; } = 500;

    /// <summary>
    /// Gets the read chunk size used while hashing files.
    /// </summary>
    public int ChunkSizeBytes { get; private init; } = 32768;

    /// <summary>
    /// Creates Monitor settings with defaults translated from the Python Monitor configuration.
    /// </summary>
    /// <returns>The default Monitor settings.</returns>
    public static MonitorSettings CreateDefault()
    {
        return new MonitorSettings();
    }

    /// <summary>
    /// Creates Monitor settings from user-facing module values.
    /// </summary>
    /// <param name="downloadsPath">The Downloads root path, or null to use the default Downloads folder.</param>
    /// <param name="csvPath">The CSV file path or file name.</param>
    /// <param name="intervalSeconds">The scan interval in seconds.</param>
    /// <param name="maxFileSizeForHashMb">The largest file size, in megabytes, eligible for hashing.</param>
    /// <param name="hashAlgorithm">The selected hash algorithm.</param>
    /// <param name="incrementalScan">Whether unchanged files reuse prior scan data.</param>
    /// <param name="runInBackground">Whether continuous background scan cycles are enabled.</param>
    /// <param name="autoOrganize">Whether organization is enabled.</param>
    /// <param name="autoCleanInstallers">Whether installer cleanup is enabled.</param>
    /// <returns>The configured Monitor settings.</returns>
    public static MonitorSettings Create(
        string? downloadsPath = null,
        string? csvPath = null,
        int? intervalSeconds = null,
        int? maxFileSizeForHashMb = null,
        string? hashAlgorithm = null,
        bool? incrementalScan = null,
        bool? runInBackground = null,
        bool? autoOrganize = null,
        bool? autoCleanInstallers = null)
    {
        return new MonitorSettings(
            downloadsPath,
            csvPath,
            intervalSeconds,
            maxFileSizeForHashMb,
            hashAlgorithm,
            incrementalScan,
            runInBackground,
            autoOrganize,
            autoCleanInstallers);
    }

    private static string NormalizeHashAlgorithm(string? algorithm)
    {
        return algorithm?.ToUpperInvariant() switch
        {
            "MD5" => "MD5",
            "SHA256" => "SHA256",
            "SHA512" => "SHA512",
            _ => "SHA1",
        };
    }
}
