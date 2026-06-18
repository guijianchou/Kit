// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Entry point for the Monitor worker process.
/// </summary>
public static class Program
{
    private const string MonitorExitEvent = @"Local\KitMonitorExitEvent-0b94f553-2821-4690-a940-76d04c3ef7e8";
    private const string MonitorBackgroundExitEvent = @"Local\KitMonitorBackgroundExitEvent-1f418ca1-9e3f-48f4-a37e-e1b747aa41aa";
    private const string MonitorScanCompletedEvent = @"Local\KitMonitorScanCompletedEvent-b7fb014b-c1fd-46c4-9d33-b517ef54824c";
    private const string MonitorProgressFileName = "scan-progress.json";
    private const string MonitorStatusDatabaseFileName = "monitor-status.db";
    private static readonly TimeSpan OneShotScanTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runs the Monitor worker.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Zero on success; non-zero on failure.</returns>
    public static int Main(string[] args)
    {
        try
        {
            MonitorCommandLine commandLine = MonitorCommandLine.Parse(args);
            if (commandLine.ShowHelp)
            {
                Console.WriteLine(GetHelpText());
                return 0;
            }

            MonitorSettings settings = MonitorSettingsLoader.LoadOrDefault(ResolveSettingsPath(commandLine.SettingsPath));
            string downloadsPath = ResolveDownloadsPath(commandLine.DownloadsPath ?? settings.DownloadsPath);
            string csvPath = ResolveCsvPath(downloadsPath, commandLine.CsvPath ?? settings.CsvPath);
            string statusDatabasePath = ResolveStatusDatabasePath();
            MarkStaleStatusRuns(statusDatabasePath);

            if (commandLine.ScanOnce)
            {
                bool organize = commandLine.UseConfiguredActions ? settings.AutoOrganize : commandLine.Organize;
                bool cleanInstallers = commandLine.UseConfiguredActions ? settings.AutoCleanInstallers : commandLine.CleanInstallers;
                string scanId = ResolveScanId(commandLine.ScanId);
                using EventWaitHandle exitEvent = new(false, EventResetMode.ManualReset, MonitorExitEvent);
                using LifetimeCancellation lifetimeCancellation = StartLifetimeCancellation(commandLine.ParentProcessId, exitEvent);
                using CancellationTokenSource oneShotTimeoutCancellation = new(OneShotScanTimeout);
                using CancellationTokenSource oneShotCancellation = CancellationTokenSource.CreateLinkedTokenSource(oneShotTimeoutCancellation.Token, lifetimeCancellation.Token);
                try
                {
                    RunScanCycle(
                        downloadsPath,
                        csvPath,
                        settings,
                        organize,
                        cleanInstallers,
                        scanId,
                        MonitorScanTrigger.Manual,
                        statusDatabasePath,
                        MonitorScanStatus.Failed,
                        () => lifetimeCancellation.ExitRequested ? "Scan interrupted" : "Scan timed out",
                        signalScanCompleted: true,
                        oneShotCancellation.Token);
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    string message = lifetimeCancellation.ExitRequested ? "Scan interrupted" : "Scan timed out";
                    ReportOneShotScanFailed(scanId, message);
                    SignalScanCompleted();
                    Console.Error.WriteLine(message + ".");
                    return lifetimeCancellation.ExitRequested ? 0 : 1;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    ReportOneShotScanFailed(scanId, "Scan failed");
                    SignalScanCompleted();
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
            }

            return RunContinuous(commandLine, downloadsPath, csvPath, settings, statusDatabasePath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunContinuous(MonitorCommandLine commandLine, string downloadsPath, string csvPath, MonitorSettings settings, string statusDatabasePath)
    {
        using EventWaitHandle exitEvent = new(false, EventResetMode.ManualReset, MonitorExitEvent);
        using EventWaitHandle backgroundExitEvent = new(false, EventResetMode.ManualReset, MonitorBackgroundExitEvent);
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(60, commandLine.IntervalSeconds ?? settings.IntervalSeconds));

        while (true)
        {
            // Scope the watcher to the active scan, then let the idle wait own both exit events.
            // This keeps background restarts responsive without sharing that signal with manual scans.
            LifetimeCancellation lifetimeCancellation = StartLifetimeCancellation(commandLine.ParentProcessId, exitEvent, backgroundExitEvent);
            try
            {
                RunScanCycle(downloadsPath, csvPath, settings, settings.AutoOrganize, settings.AutoCleanInstallers, CreateScanId(), MonitorScanTrigger.Background, statusDatabasePath, MonitorScanStatus.Warning, () => "Scan interrupted", signalScanCompleted: false, lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (lifetimeCancellation.ExitRequested)
            {
                return 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)
            {
                Console.Error.WriteLine("Monitor background scan failed; waiting for next cycle.");
                Console.Error.WriteLine(ex.Message);
            }
            finally
            {
                lifetimeCancellation.Dispose();
            }

            if (lifetimeCancellation.ExitRequested)
            {
                return 0;
            }

            if (WaitForNextCycleOrExit(commandLine.ParentProcessId, interval, exitEvent, backgroundExitEvent))
            {
                return 0;
            }
        }
    }

    private static MonitorWorkerResult RunScanCycle(string downloadsPath, string csvPath, MonitorSettings settings, bool organize, bool cleanInstallers, string scanId, MonitorScanTrigger trigger, string statusDatabasePath, MonitorScanStatus canceledStatus, Func<string> canceledMessageFactory, bool signalScanCompleted, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        long? statusRunId = TryBeginStatusRun(statusDatabasePath, scanId, trigger, startedAt);
        MonitorScanProgressFileReporter progressReporter = new(ResolveProgressPath(), TimeSpan.FromMilliseconds(500));
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
        int warningCount = CountWarnings(result);
        TryCompleteStatusRun(
            statusDatabasePath,
            statusRunId,
            warningCount > 0 ? MonitorScanStatus.Warning : MonitorScanStatus.Success,
            DateTimeOffset.UtcNow,
            result.RecordCount,
            warningCount,
            warningCount > 0 ? result.WarningMessage ?? "Completed with warnings" : null);

        if (signalScanCompleted)
        {
            SignalScanCompleted();
        }

        return result;
    }

    private static int CountWarnings(MonitorWorkerResult result)
    {
        return result.WarningCount + (result.OrganizeResult?.Errors ?? 0) + (result.InstallerCleanupResult?.Errors ?? 0);
    }

    private static void MarkStaleStatusRuns(string statusDatabasePath)
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            MonitorStatusStore.RefreshStaleRunningScans(statusDatabasePath, now);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)
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

    private static void ReportOneShotScanFailed(string scanId, string message)
    {
        DateTimeOffset failedAt = DateTimeOffset.UtcNow;
        MonitorScanProgressFileReporter progressReporter = new(ResolveProgressPath(), TimeSpan.Zero);
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

    private static string GetHelpText()
    {
        return string.Join(
            Environment.NewLine,
            "Kit Monitor",
            "  --scan-once                 Scan once and write CSV state.",
            "  --organize                  Organize root files before scanning.",
            "  --clean-installers          Clean installers that match installed software.",
            "  --use-configured-actions    Apply organize and cleanup settings during a one-shot scan.",
            "  --downloads-path <path>     Override the Downloads folder.",
            "  --csv-path <path>           Override the CSV output path.",
            "  --settings-path <path>      Reserved for module settings integration.",
            "  --scan-id <value>           Internal scan progress identifier.",
            "  --interval-seconds <value>  Reserved for continuous monitoring.",
            "  --pid <value>               Internal runner parent process ID.",
            "  --help                      Show help.");
    }

    private static LifetimeCancellation StartLifetimeCancellation(int? parentProcessId, params EventWaitHandle[] exitEvents)
    {
        return new LifetimeCancellation(parentProcessId, exitEvents);
    }

    private static bool WaitForNextCycleOrExit(int? parentProcessId, TimeSpan interval, params EventWaitHandle[] exitEvents)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + interval;

        while (DateTimeOffset.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            TimeSpan wait = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            if (wait <= TimeSpan.Zero)
            {
                break;
            }

            if (WaitHandle.WaitAny(exitEvents, wait) != WaitHandle.WaitTimeout)
            {
                return true;
            }

            if (parentProcessId.HasValue && !IsProcessRunning(parentProcessId.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string ResolveScanId(string? scanId)
    {
        return string.IsNullOrWhiteSpace(scanId) ? CreateScanId() : scanId;
    }

    private static string CreateScanId()
    {
        return Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    private static string ResolveDownloadsPath(string? downloadsPath)
    {
        if (!string.IsNullOrWhiteSpace(downloadsPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(downloadsPath));
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Downloads");
    }

    private static string ResolveSettingsPath(string? settingsPath)
    {
        if (!string.IsNullOrWhiteSpace(settingsPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(settingsPath));
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Kit", "Monitor", "settings.json");
    }

    private static string ResolveProgressPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Kit", "Monitor", MonitorProgressFileName);
    }

    private static string ResolveStatusDatabasePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Kit", "Monitor", MonitorStatusDatabaseFileName);
    }

    private static string ResolveCsvPath(string downloadsPath, string csvPath)
    {
        string expandedCsvPath = Environment.ExpandEnvironmentVariables(csvPath);
        return Path.IsPathRooted(expandedCsvPath) ? Path.GetFullPath(expandedCsvPath) : Path.Combine(downloadsPath, expandedCsvPath);
    }

    private static void SignalScanCompleted()
    {
        using EventWaitHandle scanCompletedEvent = new(false, EventResetMode.AutoReset, MonitorScanCompletedEvent);
        scanCompletedEvent.Set();
    }

    private sealed class LifetimeCancellation : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

        private readonly WaitHandle[] _exitEvents;
        private readonly int? _parentProcessId;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly Thread _watcherThread;
        private volatile bool _disposed;
        private volatile bool _exitRequested;

        public LifetimeCancellation(int? parentProcessId, params EventWaitHandle[] exitEvents)
        {
            if (exitEvents.Length == 0)
            {
                throw new ArgumentException("At least one exit event is required.", nameof(exitEvents));
            }

            _exitEvents = exitEvents;
            _parentProcessId = parentProcessId;
            _watcherThread = new Thread(WatchLifetime)
            {
                IsBackground = true,
                Name = "Monitor lifetime cancellation watcher",
            };
            _watcherThread.Start();
        }

        public CancellationToken Token => _cancellationTokenSource.Token;

        public bool ExitRequested => _exitRequested;

        public void Dispose()
        {
            _disposed = true;
            _watcherThread.Join(TimeSpan.FromSeconds(1));
            _cancellationTokenSource.Dispose();
        }

        private void WatchLifetime()
        {
            while (!_disposed)
            {
                if (WaitHandle.WaitAny(_exitEvents, PollInterval) != WaitHandle.WaitTimeout ||
                    (_parentProcessId.HasValue && !IsProcessRunning(_parentProcessId.Value)))
                {
                    _exitRequested = true;
                    _cancellationTokenSource.Cancel();
                    return;
                }
            }
        }
    }
}
