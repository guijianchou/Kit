// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Scans a configured Downloads directory and produces Monitor file records.
/// </summary>
public static class MonitorScanner
{
    /// <summary>
    /// Scans a directory using Monitor settings.
    /// </summary>
    /// <param name="downloadsPath">The root folder to scan.</param>
    /// <param name="settings">The Monitor settings.</param>
    /// <param name="existingRecords">Optional previous records used for incremental SHA1 reuse.</param>
    /// <param name="progressReporter">Optional progress reporter.</param>
    /// <param name="startedAt">The scan start time.</param>
    /// <param name="scanId">Unique scan identifier used for progress snapshots.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative shutdown.</param>
    /// <returns>The records discovered in the scan.</returns>
    public static IReadOnlyList<MonitorFileRecord> Scan(
        string downloadsPath,
        MonitorSettings settings,
        IReadOnlyList<MonitorFileRecord>? existingRecords = null,
        IMonitorScanProgressReporter? progressReporter = null,
        DateTimeOffset? startedAt = null,
        string? scanId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadsPath);
        ArgumentNullException.ThrowIfNull(settings);

        DirectoryInfo rootDirectory = new(downloadsPath);
        if (!rootDirectory.Exists)
        {
            return Array.Empty<MonitorFileRecord>();
        }

        Dictionary<string, MonitorFileRecord> existingIndex = BuildExistingIndex(settings, existingRecords);
        HashSet<string> excludedFiles = new(settings.ExcludedFiles, StringComparer.OrdinalIgnoreCase);
        HashSet<string> categoryFolders = new(settings.Categories.Keys, StringComparer.OrdinalIgnoreCase);
        List<MonitorFileRecord> records = new();
        DateTimeOffset scanStartedAt = startedAt ?? DateTimeOffset.UtcNow;
        int filesProcessed = 0;

        MonitorProgressReporter.TryReport(progressReporter, new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Hashing, filesProcessed, 0, rootDirectory.FullName, scanStartedAt, null, null, scanId), force: true);

        Action<DirectoryInfo> reportEnumerationProgress = directory => MonitorProgressReporter.TryReport(
            progressReporter,
            new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Hashing, filesProcessed, 0, directory.FullName, scanStartedAt, null, null, scanId));
        foreach (FileInfo fileInfo in EnumerateFiles(rootDirectory, reportEnumerationProgress, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludedFiles.Contains(fileInfo.Name) || fileInfo.IsReparsePoint())
            {
                continue;
            }

            try
            {
                fileInfo.Refresh();
                if (!fileInfo.Exists || fileInfo.IsReparsePoint())
                {
                    filesProcessed++;
                    MonitorProgressReporter.TryReport(progressReporter, new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Hashing, filesProcessed, 0, fileInfo.DirectoryName ?? rootDirectory.FullName, scanStartedAt, null, null, scanId));
                    continue;
                }

                string relativePath = NormalizeRelativePath(Path.GetRelativePath(rootDirectory.FullName, fileInfo.FullName));
                string folderName = ResolveFolderName(relativePath, categoryFolders);
                string timestamp = FormatTimestamp(fileInfo.LastWriteTime);
                Action hashProgressCallback = () => MonitorProgressReporter.TryReport(
                    progressReporter,
                    new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Hashing, filesProcessed, 0, fileInfo.DirectoryName ?? rootDirectory.FullName, scanStartedAt, null, null, scanId));
                string? sha1 = ResolveSha1(fileInfo, relativePath, timestamp, settings, existingIndex, hashProgressCallback, cancellationToken);
                string category = categoryFolders.Contains(folderName) ? folderName : MonitorCategoryResolver.ResolveCategory(fileInfo.Name, settings);

                records.Add(new MonitorFileRecord(
                    "~",
                    folderName,
                    fileInfo.Name,
                    relativePath,
                    fileInfo.FullName,
                    sha1,
                    timestamp,
                    fileInfo.Length,
                    category));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            filesProcessed++;
            MonitorProgressReporter.TryReport(progressReporter, new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Hashing, filesProcessed, 0, fileInfo.DirectoryName ?? rootDirectory.FullName, scanStartedAt, null, null, scanId));
        }

        return records;
    }

    /// <summary>
    /// Formats a timestamp in the Python Monitor CSV-compatible format.
    /// </summary>
    /// <param name="timestamp">The timestamp to format.</param>
    /// <returns>The formatted timestamp.</returns>
    public static string FormatTimestamp(DateTime timestamp)
    {
        return timestamp.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, MonitorFileRecord> BuildExistingIndex(MonitorSettings settings, IReadOnlyList<MonitorFileRecord>? existingRecords)
    {
        if (!settings.IncrementalScan || existingRecords is null)
        {
            return new Dictionary<string, MonitorFileRecord>(StringComparer.OrdinalIgnoreCase);
        }

        return existingRecords
            .GroupBy(record => record.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo rootDirectory, Action<DirectoryInfo>? progressCallback, CancellationToken cancellationToken)
    {
        Queue<DirectoryInfo> pendingDirectories = new();
        pendingDirectories.Enqueue(rootDirectory);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo directory = pendingDirectories.Dequeue();
            progressCallback?.Invoke(directory);
            foreach (FileInfo file in directory.SafeEnumerateFiles())
            {
                yield return file;
            }

            foreach (DirectoryInfo childDirectory in directory.SafeEnumerateDirectories())
            {
                if (!childDirectory.IsReparsePoint())
                {
                    pendingDirectories.Enqueue(childDirectory);
                }
            }
        }
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string ResolveFolderName(string relativePath, HashSet<string> categoryFolders)
    {
        int slashIndex = relativePath.IndexOf('/');
        if (slashIndex <= 0)
        {
            return "~";
        }

        string firstSegment = relativePath[..slashIndex];
        return categoryFolders.Contains(firstSegment) ? firstSegment : "~";
    }

    private static string? ResolveSha1(
        FileInfo fileInfo,
        string relativePath,
        string timestamp,
        MonitorSettings settings,
        IReadOnlyDictionary<string, MonitorFileRecord> existingIndex,
        Action? progressCallback,
        CancellationToken cancellationToken)
    {
        if (!settings.CalculateSha1)
        {
            return null;
        }

        if (settings.IncrementalScan &&
            existingIndex.TryGetValue(relativePath, out MonitorFileRecord? existingRecord) &&
            string.Equals(existingRecord.Timestamp, timestamp, StringComparison.Ordinal) &&
            existingRecord.FileSize == fileInfo.Length &&
            !string.IsNullOrEmpty(existingRecord.Sha1))
        {
            return existingRecord.Sha1;
        }

        return MonitorHasher.CalculateHash(fileInfo.FullName, settings.HashAlgorithm, settings.ChunkSizeBytes, settings.MaxFileSizeForSha1Mb, progressCallback, cancellationToken);
    }
}
