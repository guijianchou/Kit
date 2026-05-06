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
    /// <returns>A summary of the run.</returns>
    public static MonitorWorkerResult RunOnce(
        string downloadsPath,
        string csvPath,
        MonitorSettings settings,
        bool organize,
        bool cleanInstallers,
        IEnumerable<string>? installedSoftwareNames = null,
        MonitorInstalledSoftwareIndex? installedSoftwareIndex = null,
        IMonitorScanProgressReporter? progressReporter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        ArgumentNullException.ThrowIfNull(settings);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        _ = MonitorFileOrganizer.EnsureCategoryFolders(downloadsPath, settings);

        MonitorFileOrganizerResult? organizeResult = null;
        if (organize)
        {
            organizeResult = MonitorFileOrganizer.Organize(downloadsPath, settings, dryRun: false);
        }

        MonitorInstallerCleanupResult? installerCleanupResult = null;
        if (cleanInstallers)
        {
            string programsPath = Path.Combine(downloadsPath, "Programs");
            IReadOnlyList<MonitorInstallerMatch> matches = installedSoftwareIndex is not null
                ? MonitorInstallerCleaner.FindMatches(programsPath, installedSoftwareIndex)
                : installedSoftwareNames is not null
                    ? MonitorInstallerCleaner.FindMatches(programsPath, installedSoftwareNames)
                    : MonitorInstallerCleaner.FindMatches(programsPath, MonitorInstalledSoftwareProvider.GetInstalledSoftwareIndex());
            installerCleanupResult = MonitorInstallerCleaner.Cleanup(programsPath, matches, settings.InstallerMinConfidence, dryRun: false);
        }

        IReadOnlyList<MonitorFileRecord> existingRecords = MonitorCsvStore.Load(csvPath, downloadsPath, settings);
        IReadOnlyList<MonitorFileRecord> records = MonitorScanner.Scan(downloadsPath, settings, existingRecords, progressReporter, startedAt);
        progressReporter?.Report(new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Writing, records.Count, records.Count, downloadsPath, startedAt, null, null), force: true);
        MonitorCsvStore.Save(csvPath, records);
        progressReporter?.Report(new MonitorScanProgressSnapshot(MonitorScanProgressPhase.Completed, records.Count, records.Count, downloadsPath, startedAt, DateTimeOffset.UtcNow, records.Count), force: true);

        return new MonitorWorkerResult(records.Count, csvPath, organizeResult, installerCleanupResult);
    }
}
