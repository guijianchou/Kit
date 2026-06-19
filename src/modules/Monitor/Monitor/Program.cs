// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Data.Sqlite;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Entry point for the Monitor worker process.
/// </summary>
public static class Program
{
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

            MonitorSettings settings = MonitorSettingsLoader.LoadOrDefault(MonitorRuntimePaths.ResolveSettingsPath(commandLine.SettingsPath));
            string downloadsPath = MonitorRuntimePaths.ResolveDownloadsPath(commandLine.DownloadsPath ?? settings.DownloadsPath);
            string csvPath = MonitorRuntimePaths.ResolveCsvPath(downloadsPath, commandLine.CsvPath ?? settings.CsvPath);
            string statusDatabasePath = MonitorRuntimePaths.ResolveStatusDatabasePath();
            MonitorScanRunCoordinator.MarkStaleStatusRuns(statusDatabasePath);

            if (commandLine.ScanOnce)
            {
                bool organize = commandLine.UseConfiguredActions ? settings.AutoOrganize : commandLine.Organize;
                bool cleanInstallers = commandLine.UseConfiguredActions ? settings.AutoCleanInstallers : commandLine.CleanInstallers;
                string scanId = MonitorRuntimePaths.ResolveScanId(commandLine.ScanId);
                using EventWaitHandle exitEvent = new(false, EventResetMode.ManualReset, MonitorWorkerEvents.MonitorExitEvent);
                using MonitorLifetimeCancellation lifetimeCancellation = new(commandLine.ParentProcessId, exitEvent);
                using CancellationTokenSource oneShotTimeoutCancellation = new(OneShotScanTimeout);
                using CancellationTokenSource oneShotCancellation = CancellationTokenSource.CreateLinkedTokenSource(oneShotTimeoutCancellation.Token, lifetimeCancellation.Token);
                try
                {
                    MonitorScanRunCoordinator.RunScanCycle(
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
                        oneShotCancellation.Token);
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    string message = lifetimeCancellation.ExitRequested ? "Scan interrupted" : "Scan timed out";
                    MonitorScanRunCoordinator.ReportOneShotScanFailed(scanId, message);
                    Console.Error.WriteLine(message + ".");
                    return lifetimeCancellation.ExitRequested ? 0 : 1;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    MonitorScanRunCoordinator.ReportOneShotScanFailed(scanId, "Scan failed");
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
        using EventWaitHandle exitEvent = new(false, EventResetMode.ManualReset, MonitorWorkerEvents.MonitorExitEvent);
        using EventWaitHandle backgroundExitEvent = new(false, EventResetMode.ManualReset, MonitorWorkerEvents.MonitorBackgroundExitEvent);
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(60, commandLine.IntervalSeconds ?? settings.IntervalSeconds));

        while (true)
        {
            // Scope the watcher to the active scan, then let the idle wait own both exit events.
            // This keeps background restarts responsive without sharing that signal with manual scans.
            MonitorLifetimeCancellation lifetimeCancellation = new(commandLine.ParentProcessId, exitEvent, backgroundExitEvent);
            try
            {
                MonitorScanRunCoordinator.RunScanCycle(downloadsPath, csvPath, settings, settings.AutoOrganize, settings.AutoCleanInstallers, MonitorRuntimePaths.CreateScanId(), MonitorScanTrigger.Background, statusDatabasePath, MonitorScanStatus.Warning, () => "Scan interrupted", lifetimeCancellation.Token);
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

            if (MonitorLifetimeCancellation.WaitForNextCycleOrExit(commandLine.ParentProcessId, interval, exitEvent, backgroundExitEvent))
            {
                return 0;
            }
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

}
