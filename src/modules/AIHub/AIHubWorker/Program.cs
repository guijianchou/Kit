// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.AIHubWorker;

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Models;
using Kit.AiHub.Storage;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using ManagedCommon;

/// <summary>
/// Headless AI Hub audit host. It owns the scheduled rule-based audit so the cadence
/// keeps running after the Settings window closes, which the in-page scheduler cannot do.
/// </summary>
/// <remarks>
/// Follows the documented worker contract: the runner starts this executable from the
/// module output directory and passes the parent PID with --pid. The worker watches that
/// process and exits with it. It never invokes an AI kernel: deep analysis stays an
/// explicit user action in the UI.
/// </remarks>
public static class Program
{
    /// <summary>Named event the module interface signals to request a clean stop.</summary>
    private const string ExitEventName = "Local\\KitAIHubWorkerStopEvent-3f8c1a52-6d47-4b9e-8a11-2c7d5e9f4b60";

    /// <summary>How often the parent process is checked for liveness.</summary>
    private static readonly TimeSpan ParentCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often the schedule is re-evaluated.</summary>
    private static readonly TimeSpan SchedulePollInterval = TimeSpan.FromMinutes(1);

    public static async Task<int> Main(string[] args)
    {
        Logger.InitializeLogger(@"\AIHub\Logs");
        Logger.LogInfo("[AIHub.Worker] Starting.");

        int? parentPid = ParseParentPid(args);
        string? dataDirectory = ParseDataDirectory(args);

        using var shutdown = new CancellationTokenSource();

        // The module interface signals this event on disable so the worker can drain and exit
        // promptly instead of waiting to be terminated.
        using var stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Logger.LogInfo("[AIHub.Worker] Shutdown requested.");
            shutdown.Cancel();
        };

        AuditScheduler? scheduler = null;
        try
        {
            var store = dataDirectory is null ? new AiHubSettingsStore() : new AiHubSettingsStore(dataDirectory);
            AiHubConfig config = store.Load();

            var pipeline = new AuditPipeline(
                historyStorage: new Kit.AIHubLib.Storage.AuditHistoryStorage(store.DataDirectory));

            EventLogService.AuditMode mode = config.AuditModeIndex == 1
                ? EventLogService.AuditMode.Full
                : EventLogService.AuditMode.Extended;

            scheduler = new AuditScheduler(
                pipeline,
                config.ScanIntervalHours,
                isEnabled: () => ReadIsEnabled(store),
                pollInterval: SchedulePollInterval,
                fullRange: TimeSpan.FromDays(1),
                mode: mode);

            Logger.LogInfo($"[AIHub.Worker] Interval {scheduler.IntervalHours} h; mode {(config.AuditModeIndex == 1 ? "full" : "extended")}.");

            if (!AuditSchedule.IsSchedulingEnabled(scheduler.IntervalHours))
            {
                // Plugin guidance: do not keep a process alive purely for health checks.
                // With no cadence configured there is no scheduled work, so exit instead of
                // idling in the tray for the whole session.
                Logger.LogInfo("[AIHub.Worker] Scheduling is off; exiting without staying resident.");
                return 0;
            }

            scheduler.Start();

            await WaitForShutdownAsync(parentPid, stopEvent, shutdown.Token).ConfigureAwait(false);

            Logger.LogInfo("[AIHub.Worker] Stopped watching; ending the schedule.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Logger.LogError("[AIHub.Worker] Unhandled failure.", ex);
            return 1;
        }
        finally
        {
            if (scheduler is not null)
            {
                await scheduler.StopAsync().ConfigureAwait(false);
                scheduler.Dispose();
            }
        }
    }

    /// <summary>
    /// Re-reads the enabled flag from disk so toggling AI Hub off in Settings stops the
    /// scheduled audit without restarting the worker.
    /// </summary>
    private static bool ReadIsEnabled(AiHubSettingsStore store)
    {
        try
        {
            return store.Load().IsEnabled;
        }
        catch (Exception ex)
        {
            // Fail closed: without a readable configuration the worker must not audit.
            Logger.LogError("[AIHub.Worker] Could not read AI Hub settings; skipping this tick.", ex);
            return false;
        }
    }

    private static async Task WaitForShutdownAsync(int? parentPid, EventWaitHandle stopEvent, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Wait on either the cooperative stop event or the parent-liveness poll.
            if (stopEvent.WaitOne(ParentCheckInterval))
            {
                Logger.LogInfo("[AIHub.Worker] Stop event signalled.");
                return;
            }

            if (parentPid is int pid && !IsProcessAlive(pid))
            {
                Logger.LogInfo($"[AIHub.Worker] Parent process {pid} exited.");
                return;
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
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
    }

    /// <summary>Reads --pid &lt;id&gt;, ignoring malformed values.</summary>
    private static int? ParseParentPid(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--pid", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
                && pid > 0)
            {
                return pid;
            }
        }

        return null;
    }

    /// <summary>Reads --data-dir &lt;path&gt;, defaulting to the Kit AI Hub root.</summary>
    private static string? ParseDataDirectory(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
