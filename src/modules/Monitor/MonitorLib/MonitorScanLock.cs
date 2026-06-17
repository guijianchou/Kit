// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

internal sealed partial class MonitorScanLock : IDisposable
{
    private const string MonitorScanMutexName = @"Local\KitMonitorScanMutex-5f9871fa-25bd-4e04-9a9c-cd6ce8a2f78f";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    private readonly Mutex _mutex;
    private bool _disposed;

    private MonitorScanLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static MonitorScanLock Acquire(CancellationToken cancellationToken)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MonitorScanMutexName);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (mutex.WaitOne(RetryInterval))
                    {
                        MonitorScanLock scanLock = new(mutex);
                        mutex = null;
                        return scanLock;
                    }
                }
                catch (AbandonedMutexException)
                {
                    if (mutex is null)
                    {
                        throw;
                    }

                    MonitorScanLock scanLock = new(mutex);
                    mutex = null;
                    return scanLock;
                }
            }
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _disposed = true;
    }
}
