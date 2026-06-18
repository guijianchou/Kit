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
            organizeResult = MonitorFileOrganizer.Organize(downloadsPath, settings, dryRun: false, cancellationToken);
        }

        MonitorInstallerCleanupResult? installerCleanupResult = null;
        if (cleanInstallers)
        {
            string programsPath = Path.Combine(downloadsPath, "Programs");
            IReadOnlyList<MonitorInstallerMatch> matches = FindInstallerMatches(downloadsPath, programsPath, installedSoftwareNames, installedSoftwareIndex, cancellationToken);
            installerCleanupResult = MonitorInstallerCleaner.Cleanup(downloadsPath, matches, settings.InstallerMinConfidence, dryRun: false, cancellationToken: cancellationToken);
        }

        IReadOnlyList<MonitorFileRecord> existingRecords = MonitorCsvStore.Load(csvPath, downloadsPath, settings);
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
            IReadOnlyList<MonitorInstallerMatch> rootMatches = resolvedInstalledSoftwareIndex is not null
                ? MonitorInstallerCleaner.FindMatches(scanRoot, resolvedInstalledSoftwareIndex, cancellationToken)
                : MonitorInstallerCleaner.FindMatches(scanRoot, installedSoftwareNames!, cancellationToken);
            matches.AddRange(rootMatches);
        }

        return matches
            .GroupBy(match => match.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(match => match.Confidence).ThenBy(match => match.SoftwareName, StringComparer.OrdinalIgnoreCase).First())
            .ToList();
    }
}
