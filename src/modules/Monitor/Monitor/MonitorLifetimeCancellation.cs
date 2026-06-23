// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;

namespace Microsoft.PowerToys.Monitor;

internal sealed class MonitorLifetimeCancellation : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly WaitHandle[] _exitEvents;
    private readonly int? _parentProcessId;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Thread _watcherThread;
    private volatile bool _disposed;
    private volatile bool _exitRequested;

    public MonitorLifetimeCancellation(int? parentProcessId, params EventWaitHandle[] exitEvents)
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

    public static bool WaitForNextCycleOrExit(int? parentProcessId, TimeSpan interval, params EventWaitHandle[] exitEvents)
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

    public void Dispose()
    {
        _disposed = true;
        _watcherThread.Join(TimeSpan.FromSeconds(1));
        _cancellationTokenSource.Dispose();
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

    private void WatchLifetime()
    {
        while (!_disposed)
        {
            if (WaitHandle.WaitAny(_exitEvents, PollInterval) != WaitHandle.WaitTimeout ||
                (_parentProcessId.HasValue && !IsProcessRunning(_parentProcessId.Value)))
            {
                _exitRequested = true;
                try
                {
                    _cancellationTokenSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Dispose won the race after Join timed out; the consumer is already tearing down.
                }

                return;
            }
        }
    }
}
