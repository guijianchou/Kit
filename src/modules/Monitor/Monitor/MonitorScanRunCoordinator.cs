// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Microsoft.PowerToys.Monitor;

internal static class MonitorScanRunCoordinator
{
    public static MonitorWorkerResult RunScanCycle(
        string downloadsPath,
        string csvPath,
        MonitorSettings settings,
        bool organize,
        bool cleanInstallers,
        string scanId,
        MonitorScanTrigger trigger,
        string statusDatabasePath,
        MonitorScanStatus canceledStatus,
        Func<string> canceledMessageFactory,
        CancellationToken cancellationToken,
        IMonitorScanProgressReporter? progressReporter = null)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        long? statusRunId = TryBeginStatusRun(statusDatabasePath, scanId, trigger, startedAt);
        progressReporter ??= new MonitorScanProgressFileReporter(MonitorRuntimePaths.ResolveProgressPath(), TimeSpan.FromMilliseconds(500));
        MonitorWorkerResult result;
        try
        {
            result = MonitorWorker.RunOnce(downloadsPath, csvPath, settings, organize, cleanInstallers, progressReporter: progressReporter, cancellationToken: cancellationToken, scanId: scanId);
        }
        catch (OperationCanceledException)
        {
            TryCompleteStatusRun(statusDatabasePath, statusRunId, canceledStatus, DateTimeOffset.UtcNow, null, 0, canceledMessageFactory());
            throw;
        }
        catch (Exception ex)
        {
            TryCompleteStatusRun(statusDatabasePath, statusRunId, MonitorScanStatus.Failed, DateTimeOffset.UtcNow, null, 0, string.IsNullOrWhiteSpace(ex.Message) ? "Scan failed" : ex.Message);
            throw;
        }

        WriteResult(result);
        int warningCount = CountWarnings(result);
        TryCompleteStatusRun(
            statusDatabasePath,
            statusRunId,
            warningCount > 0 ? MonitorScanStatus.Warning : MonitorScanStatus.Success,
            DateTimeOffset.UtcNow,
            result.RecordCount,
            warningCount,
            warningCount > 0 ? result.WarningMessage ?? "Completed with warnings" : null);

        return result;
    }

    public static void MarkStaleStatusRuns(string statusDatabasePath)
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            MonitorStatusStore.RefreshStaleRunningScans(statusDatabasePath, now, MonitorScanProgressFreshness.GetFreshProgressScanId(MonitorRuntimePaths.ResolveProgressPath(), now));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)
        {
        }
    }

    public static void ReportOneShotScanFailed(string scanId, string message)
    {
        DateTimeOffset failedAt = DateTimeOffset.UtcNow;
        MonitorScanProgressFileReporter progressReporter = new(MonitorRuntimePaths.ResolveProgressPath(), TimeSpan.Zero);
        try
        {
            progressReporter.Report(
                new MonitorScanProgressSnapshot(
                    MonitorScanProgressPhase.Failed,
                    filesProcessed: 0,
                    filesTotal: 0,
                    currentDirectory: string.Empty,
                    startedAt: failedAt,
                    completedAt: failedAt,
                    recordCount: null,
                    scanId: scanId,
                    message: message),
                force: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static long? TryBeginStatusRun(string statusDatabasePath, string scanId, MonitorScanTrigger trigger, DateTimeOffset startedAt)
    {
        try
        {
            return MonitorStatusStore.BeginRun(statusDatabasePath, scanId, trigger, startedAt);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)
        {
            return null;
        }
    }

    private static void TryCompleteStatusRun(string statusDatabasePath, long? runId, MonitorScanStatus status, DateTimeOffset completedAt, int? recordCount, int warningCount, string? message)
    {
        if (!runId.HasValue)
        {
            return;
        }

        try
        {
            MonitorStatusStore.CompleteRun(statusDatabasePath, runId.Value, status, completedAt, recordCount, warningCount, message);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)
        {
        }
    }

    private static int CountWarnings(MonitorWorkerResult result)
    {
        return result.WarningCount + (result.OrganizeResult?.Errors ?? 0) + (result.InstallerCleanupResult?.Errors ?? 0);
    }

    private static void WriteResult(MonitorWorkerResult result)
    {
        if (result.OrganizeResult is not null)
        {
            Console.WriteLine(
                "Organize: " +
                result.OrganizeResult.Organized.ToString(CultureInfo.InvariantCulture) +
                " organized, " +
                result.OrganizeResult.Skipped.ToString(CultureInfo.InvariantCulture) +
                " skipped, " +
                result.OrganizeResult.Errors.ToString(CultureInfo.InvariantCulture) +
                " errors.");
        }

        if (result.InstallerCleanupResult is not null)
        {
            Console.WriteLine(
                "Installer cleanup: " +
                result.InstallerCleanupResult.Deleted.ToString(CultureInfo.InvariantCulture) +
                " deleted, " +
                result.InstallerCleanupResult.Skipped.ToString(CultureInfo.InvariantCulture) +
                " skipped, " +
                result.InstallerCleanupResult.Errors.ToString(CultureInfo.InvariantCulture) +
                " errors.");
        }

        Console.WriteLine("Scan complete: " + result.RecordCount.ToString(CultureInfo.InvariantCulture) + " files.");
        Console.WriteLine("CSV: " + result.CsvPath);
    }
}
