// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.AIHubWorker;

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Storage;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.AIHubLib.Storage;
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

    /// <summary>Named event the module interface signals when settings change.</summary>
    private const string RefreshSettingsEventName = "Local\\Kit_AIHub_RefreshSettings_Event_a1b2c3d6";

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
        using var refreshEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RefreshSettingsEventName);
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

            // The plugin settings file owns the audit schedule; the page writes it and signals
            // the refresh event, so no service-level configuration gates background audits.
            PluginScheduleSettings schedule = PluginScheduleSettings.Read(store.DataDirectory);

            // History lives in the same State directory the page reads, so scheduled runs
            // show up in the dashboard and the watermark below resumes the cadence.
            var historyStorage = new AuditHistoryStorage(Path.Combine(store.DataDirectory, "State"));
            DateTime? lastCompletedUtc = (await historyStorage.LoadLatestHistoryAsync().ConfigureAwait(false))?.Timestamp;
            var pipeline = new AuditPipeline(historyStorage: historyStorage);

            EventLogService.AuditMode mode = ToAuditMode(schedule.AuditModeIndex);

            scheduler = new AuditScheduler(
                pipeline,
                schedule.ScanIntervalHours,
                pollInterval: SchedulePollInterval,
                fullRange: TimeSpan.FromDays(1),
                mode: mode,
                lastCompletedUtc: lastCompletedUtc);

            Logger.LogInfo($"[AIHub.Worker] Interval {scheduler.IntervalHours} h; mode {mode}; last completed {lastCompletedUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "never"}.");

            scheduler.Start();

            await WaitForShutdownAsync(parentPid, stopEvent, refreshEvent, scheduler, store, shutdown.Token).ConfigureAwait(false);

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

    /// <summary>Maps the plugin audit-mode index to the event-log audit mode.</summary>
    private static EventLogService.AuditMode ToAuditMode(int auditModeIndex)
        => auditModeIndex == 1 ? EventLogService.AuditMode.Full : EventLogService.AuditMode.Extended;

    private static async Task WaitForShutdownAsync(int? parentPid, EventWaitHandle stopEvent, EventWaitHandle refreshEvent, AuditScheduler scheduler, AiHubSettingsStore store, CancellationToken cancellationToken)
    {
        WaitHandle[] handles = { stopEvent, refreshEvent, cancellationToken.WaitHandle };

        while (!cancellationToken.IsCancellationRequested)
        {
            int index = WaitHandle.WaitAny(handles, ParentCheckInterval);
            if (index == 0)
            {
                Logger.LogInfo("[AIHub.Worker] Stop event signalled.");
                return;
            }

            if (index == 1)
            {
                Logger.LogInfo("[AIHub.Worker] Settings refresh signaled; reloading the plugin schedule.");
                try
                {
                    PluginScheduleSettings schedule = PluginScheduleSettings.Read(store.DataDirectory);
                    if (scheduler.IntervalHours != schedule.ScanIntervalHours)
                    {
                        Logger.LogInfo($"[AIHub.Worker] Interval updated from {scheduler.IntervalHours} to {schedule.ScanIntervalHours} h.");
                        scheduler.UpdateInterval(schedule.ScanIntervalHours);
                    }

                    EventLogService.AuditMode updatedMode = ToAuditMode(schedule.AuditModeIndex);
                    if (scheduler.Mode != updatedMode)
                    {
                        Logger.LogInfo($"[AIHub.Worker] Audit mode updated from {scheduler.Mode} to {updatedMode}.");
                        scheduler.UpdateMode(updatedMode);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[AIHub.Worker] Failed to refresh settings: {ex.Message}");
                }

                continue;
            }

            if (index == 2 || cancellationToken.IsCancellationRequested)
            {
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
