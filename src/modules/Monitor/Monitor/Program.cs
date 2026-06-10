// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Entry point for the Monitor worker process.
/// </summary>
public static class Program
{
    private const string MonitorExitEvent = @"Local\KitMonitorExitEvent-0b94f553-2821-4690-a940-76d04c3ef7e8";
    private const string MonitorScanCompletedEvent = @"Local\KitMonitorScanCompletedEvent-b7fb014b-c1fd-46c4-9d33-b517ef54824c";
    private const string MonitorProgressFileName = "scan-progress.json";

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

            if (commandLine.ScanOnce)
            {
                bool organize = commandLine.UseConfiguredActions ? settings.AutoOrganize : commandLine.Organize;
                bool cleanInstallers = commandLine.UseConfiguredActions ? settings.AutoCleanInstallers : commandLine.CleanInstallers;
                RunScanCycle(downloadsPath, csvPath, settings, organize, cleanInstallers);
                return 0;
            }

            return RunContinuous(commandLine, downloadsPath, csvPath, settings);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunContinuous(MonitorCommandLine commandLine, string downloadsPath, string csvPath, MonitorSettings settings)
    {
        using EventWaitHandle exitEvent = new(false, EventResetMode.AutoReset, MonitorExitEvent);
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(60, commandLine.IntervalSeconds ?? settings.IntervalSeconds));

        while (true)
        {
            RunScanCycle(downloadsPath, csvPath, settings, settings.AutoOrganize, settings.AutoCleanInstallers);
            if (WaitForNextCycleOrExit(exitEvent, commandLine.ParentProcessId, interval))
            {
                return 0;
            }
        }
    }

    private static MonitorWorkerResult RunScanCycle(string downloadsPath, string csvPath, MonitorSettings settings, bool organize, bool cleanInstallers)
    {
        MonitorScanProgressFileReporter progressReporter = new(ResolveProgressPath(), TimeSpan.FromMilliseconds(500));
        MonitorWorkerResult result = MonitorWorker.RunOnce(downloadsPath, csvPath, settings, organize, cleanInstallers, progressReporter: progressReporter);

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
        SignalScanCompleted();
        return result;
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
            "  --interval-seconds <value>  Reserved for continuous monitoring.",
            "  --pid <value>               Internal runner parent process ID.",
            "  --help                      Show help.");
    }

    private static bool WaitForNextCycleOrExit(EventWaitHandle exitEvent, int? parentProcessId, TimeSpan interval)
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

            if (exitEvent.WaitOne(wait))
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
}
