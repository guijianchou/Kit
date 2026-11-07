// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using Kit.WingetLib;
using ManagedCommon;
using ManagedCommon.ServiceHost;

namespace Kit.WingetService;

internal static class Program
{
    private const string ExitEventName = @"Local\Kit.Winget.Exit-41d22b8c-40d5-4eb4-b9db-51aa37802d53";
    private const string RefreshEventName = @"Local\Kit.Winget.Refresh-416a1b25-968d-47d8-a341-4d2a844edda6";

    private static async Task<int> Main(string[] args)
    {
        Logger.InitializeLogger(@"\Winget\Service\Logs");
        try
        {
            int runnerPid = ParseRunnerPid(args);
            if (runnerPid <= 0)
            {
                Logger.LogWarning("[Winget] A Runner PID is required. Enable the module from Kit.");
                return 2;
            }

            // Hold the process object/handle for the lifetime of the service. Reopening a
            // numeric PID on a timer would allow PID reuse to bind us to another process.
            using var runner = Process.GetProcessById(runnerPid);
            _ = runner.SafeHandle;
            if (runner.HasExited)
            {
                return 0;
            }

            // Existence, rather than thread-affine mutex ownership, provides a lifetime
            // singleton that remains valid across async continuations.
            using var instance = new Mutex(false, @"Local\Kit.Winget.Service-07076d57-944b-4e82-9ebd-1fe39aad5cfb", out bool createdNew);
            if (!createdNew)
            {
                return 0;
            }

            using var shutdown = new CancellationTokenSource();
            using var exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName);
            using var refreshEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RefreshEventName);
            using var wake = new SemaphoreSlim(0, 1);
            string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "Winget");
            await using var runtime = new WingetServiceRuntime(new WingetClient(message => Logger.LogInfo("[Winget] " + message)), dataDirectory, new WingetUpdateNotifier());
            var pipe = new KitServicePipeServer("Winget", runtime.HandleRequestAsync);
            RegisteredWaitHandle? exitWait = null;
            RegisteredWaitHandle? refreshWait = null;
            Task parentWatch = WatchRunnerAsync(runner, shutdown);
            try
            {
                exitWait = ThreadPool.RegisterWaitForSingleObject(
                    exitEvent,
                    (_, _) =>
                {
                    try
                    {
                        shutdown.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    },
                    null,
                    Timeout.Infinite,
                    executeOnlyOnce: true);
                refreshWait = ThreadPool.RegisterWaitForSingleObject(
                    refreshEvent,
                    (_, _) =>
                {
                    try
                    {
                        wake.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    },
                    null,
                    Timeout.Infinite,
                    executeOnlyOnce: false);
                pipe.Start();
                runtime.StartDetection();
                Logger.LogInfo("[Winget] Service started; package operations use the current user's token.");

                while (!shutdown.IsCancellationRequested)
                {
                    runtime.Tick();
                    await wake.WaitAsync(TimeSpan.FromSeconds(30), shutdown.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                exitWait?.Unregister(null);
                refreshWait?.Unregister(null);
                await shutdown.CancelAsync().ConfigureAwait(false);
                await parentWatch.ConfigureAwait(false);

                // Stop accepting requests before draining the one active package operation.
                await pipe.DisposeAsync().ConfigureAwait(false);
                await runtime.DisposeAsync().ConfigureAwait(false);
            }

            Logger.LogInfo("[Winget] Service stopped.");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Logger.LogWarning($"[Winget] Runner is no longer available: {ex.Message}");
            return 0;
        }
        catch (Exception ex)
        {
            Logger.LogError("[Winget] Service failed.", ex);
            return 1;
        }
    }

    private static async Task WatchRunnerAsync(Process runner, CancellationTokenSource shutdown)
    {
        try
        {
            await runner.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
            await shutdown.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[Winget] Runner watch failed: {ex.Message}");
            await shutdown.CancelAsync().ConfigureAwait(false);
        }
    }

    private static int ParseRunnerPid(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], "--pid", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0)
            {
                return pid;
            }
        }

        return 0;
    }
}
