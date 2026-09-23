// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.LocalserverWorker;

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LocalserverLib.Common;
using ManagedCommon;

/// <summary>
/// Entry point for the headless Localserver supervisor. It keeps the service runners - and
/// therefore health monitoring and restart-with-backoff - alive after the Settings window
/// closes, which the in-process Settings page cannot do.
/// </summary>
/// <remarks>
/// Follows the documented worker contract: the runner starts this executable from the module
/// output directory and passes the parent PID with --pid. The worker watches that process and
/// shuts itself down when the runner exits.
/// </remarks>
public static class Program
{
    /// <summary>How often the parent process is checked for liveness.</summary>
    private static readonly TimeSpan ParentCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Marker file the module writes when it is disabled. Its presence tells the worker to
    /// stop the supervised services and exit, so a disabled module leaves nothing running.
    /// </summary>
    private const string ModuleDisabledFlagFileName = "module-disabled.flag";

    public static async Task<int> Main(string[] args)
    {
        Logger.InitializeLogger(@"\Localserver\Logs");
        Logger.LogInfo("[Localserver.Worker] Starting.");

        int? parentPid = ParseParentPid(args);
        string dataDirectory = ResolveDataDirectory(args);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Logger.LogInfo("[Localserver.Worker] Shutdown requested.");
            shutdown.Cancel();
        };

        try
        {
            using var supervisor = new ServiceSupervisor(dataDirectory);
            await supervisor.LoadAsync(shutdown.Token).ConfigureAwait(false);
            await supervisor.RecoverRunningServicesAsync(shutdown.Token).ConfigureAwait(false);

            Logger.LogInfo($"[Localserver.Worker] Supervising {supervisor.SupervisedCount} service(s).");

            _ = await WaitForShutdownAsync(parentPid, dataDirectory, supervisor, shutdown.Token).ConfigureAwait(false);

            // Whatever ended the watch - the module was disabled, the parent (Kit runner)
            // exited, or shutdown was requested - stop every supervised service before
            // leaving, so no exit path can leave orphaned process trees behind. A final
            // recovery pass adopts services the Settings page started after the last poll.
            Logger.LogInfo("[Localserver.Worker] Shutting down; stopping supervised services.");
            await supervisor.RecoverRunningServicesAsync(shutdown.Token).ConfigureAwait(false);
            await supervisor.StopAllAsync(shutdown.Token).ConfigureAwait(false);
            TryDeleteModuleDisabledFlag(dataDirectory);

            Logger.LogInfo("[Localserver.Worker] Stopped watching; releasing supervision.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Logger.LogError("[Localserver.Worker] Unhandled failure.", ex);
            return 1;
        }
    }

    /// <summary>
    /// Blocks until shutdown is requested, the parent process exits, or the owning module
    /// is disabled. Every path ends the watch; the caller then stops all supervised
    /// services, so no exit path leaves orphaned process trees behind. Each pass also
    /// adopts services the Settings page started after this worker booted, so they stay
    /// supervised and are covered by the shutdown stop. A missing parent is not treated as
    /// immediate shutdown so the worker can still be run standalone.
    /// </summary>
    private static async Task<bool> WaitForShutdownAsync(
        int? parentPid,
        string dataDirectory,
        ServiceSupervisor supervisor,
        CancellationToken cancellationToken)
    {
        string moduleDisabledFlagPath = ModuleDisabledFlagPath(dataDirectory);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ParentCheckInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (parentPid is int pid && !IsProcessAlive(pid))
            {
                Logger.LogInfo($"[Localserver.Worker] Parent process {pid} exited.");
                return false;
            }

            await supervisor.RecoverRunningServicesAsync(cancellationToken).ConfigureAwait(false);

            if (File.Exists(moduleDisabledFlagPath))
            {
                Logger.LogInfo("[Localserver.Worker] Module disable requested; stopping services.");
                return true;
            }
        }

        return false;
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

    private static string ModuleDisabledFlagPath(string dataDirectory) =>
        Path.Combine(dataDirectory, ModuleDisabledFlagFileName);

    private static void TryDeleteModuleDisabledFlag(string dataDirectory)
    {
        try
        {
            string path = ModuleDisabledFlagPath(dataDirectory);
            if (File.Exists(path))
            {
                File.Delete(path);
                Logger.LogInfo("[Localserver.Worker] Removed the module-disable flag.");
            }
        }
        catch (IOException)
        {
            // Best effort; the module also clears the flag on re-enable.
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

    /// <summary>Reads --data-dir &lt;path&gt;, defaulting to the Kit Localserver root.</summary>
    private static string ResolveDataDirectory(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return LocalserverPathHelper.RootDataDirectory;
    }
}
