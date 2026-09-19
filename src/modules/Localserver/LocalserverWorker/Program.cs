// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.LocalserverWorker;

using System;
using System.Globalization;
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

    public static async Task<int> Main(string[] args)
    {
        Logger.InitializeLogger(@"\\Localserver\\Logs");
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
            await supervisor.StartAutoStartServicesAsync(shutdown.Token).ConfigureAwait(false);

            Logger.LogInfo($"[Localserver.Worker] Supervising {supervisor.SupervisedCount} service(s).");

            await WaitForShutdownAsync(parentPid, shutdown.Token).ConfigureAwait(false);

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
    /// Blocks until shutdown is requested or the parent process exits. A missing parent is not
    /// treated as immediate shutdown so the worker can still be run standalone.
    /// </summary>
    private static async Task WaitForShutdownAsync(int? parentPid, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ParentCheckInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (parentPid is int pid && !IsProcessAlive(pid))
            {
                Logger.LogInfo($"[Localserver.Worker] Parent process {pid} exited.");
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
