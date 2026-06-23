// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Coordinates one Monitor scan pass.
/// </summary>
public static class MonitorWorker
{
    /// <summary>
    /// Runs one Monitor pass.
    /// </summary>
    /// <param name="downloadsPath">The Downloads root folder.</param>
    /// <param name="csvPath">The CSV state path.</param>
    /// <param name="settings">Monitor settings.</param>
    /// <param name="organize">Whether root files should be organized first.</param>
    /// <param name="cleanInstallers">Whether matched installer files should be cleaned before scanning.</param>
    /// <param name="installedSoftwareNames">Optional installed software names, primarily for tests.</param>
    /// <param name="installedSoftwareIndex">Optional installed software metadata, primarily for tests.</param>
    /// <param name="progressReporter">Optional scan progress reporter.</param>
    /// <param name="scanId">Unique scan identifier used for progress snapshots.</param>
    /// <param name="cancellationToken">Cancellation token for cooperative shutdown.</param>
    /// <returns>A summary of the run.</returns>
    public static MonitorWorkerResult RunOnce(
        string downloadsPath,
        string csvPath,
        MonitorSettings settings,
        bool organize,
        bool cleanInstallers,
        IEnumerable<string>? installedSoftwareNames = null,
        MonitorInstalledSoftwareIndex? installedSoftwareIndex = null,
        IMonitorScanProgressReporter? progressReporter = null,
        string? scanId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        ArgumentNullException.ThrowIfNull(settings);

        using MonitorScanLock scanLock = MonitorScanLock.Acquire(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Action reportCategorizingProgress = () => ReportProgress(progressReporter, MonitorScanProgressPhase.Categorizing, downloadsPath, startedAt, scanId);
        reportCategorizingProgress();
        if (!Directory.Exists(downloadsPath))
        {
            string warningMessage = "Downloads folder does not exist: " + downloadsPath;
            MonitorProgressReporter.TryReport(
                progressReporter,
                new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Completed, 0, 0, downloadsPath, startedAt, DateTimeOffset.UtcNow, 0, scanId, warningMessage),
                force: true);

            return new MonitorWorkerResult(0, csvPath, null, null, WarningCount: 1, WarningMessage: warningMessage);
        }

        MonitorFileOrganizerResult? organizeResult = null;
        if (organize)
        {
            reportCategorizingProgress();
            organizeResult = MonitorFileOrganizer.Organize(downloadsPath, settings, dryRun: false, progressCallback: reportCategorizingProgress, cancellationToken: cancellationToken);
        }

        MonitorInstallerCleanupResult? installerCleanupResult = null;
        if (cleanInstallers)
        {
            reportCategorizingProgress();
            string programsPath = Path.Combine(downloadsPath, "Programs");
            IReadOnlyList<MonitorInstallerMatch> matches = FindInstallerMatches(downloadsPath, programsPath, installedSoftwareNames, installedSoftwareIndex, reportCategorizingProgress, cancellationToken);
            installerCleanupResult = MonitorInstallerCleaner.Cleanup(downloadsPath, matches, settings.InstallerMinConfidence, dryRun: false, cancellationToken: cancellationToken, progressCallback: reportCategorizingProgress);
        }

        reportCategorizingProgress();
        IReadOnlyList<MonitorFileRecord> existingRecords = MonitorCsvStore.Load(csvPath, downloadsPath, settings, reportCategorizingProgress);
        IReadOnlyList<MonitorFileRecord> records = MonitorScanner.Scan(downloadsPath, settings, existingRecords, progressReporter, startedAt, scanId, cancellationToken);
        MonitorProgressReporter.TryReport(progressReporter, new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Writing, records.Count, records.Count, downloadsPath, startedAt, null, null, scanId), force: true);
        cancellationToken.ThrowIfCancellationRequested();
        MonitorCsvStore.Save(csvPath, records);
        MonitorProgressReporter.TryReport(progressReporter, new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Completed, records.Count, records.Count, downloadsPath, startedAt, DateTimeOffset.UtcNow, records.Count, scanId), force: true);

        return new MonitorWorkerResult(records.Count, csvPath, organizeResult, installerCleanupResult);
    }

    private static IReadOnlyList<MonitorInstallerMatch> FindInstallerMatches(
        string downloadsPath,
        string programsPath,
        IEnumerable<string>? installedSoftwareNames,
        MonitorInstalledSoftwareIndex? installedSoftwareIndex,
        Action? progressCallback,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> scanRoots = new[] { downloadsPath, programsPath }.Distinct(StringComparer.OrdinalIgnoreCase);
        List<MonitorInstallerMatch> matches = new();
        MonitorInstalledSoftwareIndex? resolvedInstalledSoftwareIndex = installedSoftwareIndex;
        if (installedSoftwareNames is null)
        {
            resolvedInstalledSoftwareIndex ??= MonitorInstalledSoftwareProvider.GetInstalledSoftwareIndex();
        }

        foreach (string scanRoot in scanRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progressCallback?.Invoke();
            IReadOnlyList<MonitorInstallerMatch> rootMatches = resolvedInstalledSoftwareIndex is not null
                ? MonitorInstallerCleaner.FindMatches(scanRoot, resolvedInstalledSoftwareIndex, progressCallback, cancellationToken)
                : MonitorInstallerCleaner.FindMatches(scanRoot, installedSoftwareNames!, progressCallback, cancellationToken);
            matches.AddRange(rootMatches);
        }

        return matches
            .GroupBy(match => match.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(match => match.Confidence).ThenBy(match => match.SoftwareName, StringComparer.OrdinalIgnoreCase).First())
            .ToList();
    }

    private static void ReportProgress(IMonitorScanProgressReporter? progressReporter, string phase, string currentDirectory, DateTimeOffset startedAt, string? scanId)
    {
        MonitorProgressReporter.TryReport(
            progressReporter,
            new MonitorScanProgressSnapshot(phase, 0, 0, currentDirectory, startedAt, null, null, scanId),
            force: true);
    }
}
