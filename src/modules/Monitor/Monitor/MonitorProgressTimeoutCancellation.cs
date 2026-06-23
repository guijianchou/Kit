// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

internal sealed class MonitorProgressTimeoutCancellation : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _noProgressTimeout;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ManualResetEventSlim _disposeEvent = new(false);
    private readonly Thread _watcherThread;
    private volatile bool _disposed;
    private volatile bool _timedOut;
    private long _lastProgressTicks;

    public MonitorProgressTimeoutCancellation(TimeSpan noProgressTimeout)
    {
        _noProgressTimeout = noProgressTimeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : noProgressTimeout;
        Interlocked.Exchange(ref _lastProgressTicks, DateTimeOffset.UtcNow.UtcTicks);
        _watcherThread = new Thread(WatchProgress)
        {
            IsBackground = true,
            Name = "Monitor no-progress timeout watcher",
        };
        _watcherThread.Start();
    }

    public CancellationToken Token => _cancellationTokenSource.Token;

    public bool TimedOut => _timedOut;

    public IMonitorScanProgressReporter CreateReporter(IMonitorScanProgressReporter innerReporter)
    {
        ArgumentNullException.ThrowIfNull(innerReporter);

        return new ProgressTrackingReporter(this, innerReporter);
    }

    public void Dispose()
    {
        _disposed = true;
        _disposeEvent.Set();
        _watcherThread.Join(millisecondsTimeout: (int)PollInterval.TotalMilliseconds);
        _disposeEvent.Dispose();
        _cancellationTokenSource.Dispose();
    }

    private void MarkProgress()
    {
        Interlocked.Exchange(ref _lastProgressTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    private void WatchProgress()
    {
        while (!_disposed)
        {
            DateTimeOffset lastProgressAt = new(Interlocked.Read(ref _lastProgressTicks), TimeSpan.Zero);
            if (DateTimeOffset.UtcNow - lastProgressAt >= _noProgressTimeout)
            {
                _timedOut = true;
                _cancellationTokenSource.Cancel();
                return;
            }

            _disposeEvent.Wait(PollInterval);
        }
    }

    private sealed class ProgressTrackingReporter : IMonitorScanProgressReporter
    {
        private readonly MonitorProgressTimeoutCancellation _owner;
        private readonly IMonitorScanProgressReporter _innerReporter;

        public ProgressTrackingReporter(MonitorProgressTimeoutCancellation owner, IMonitorScanProgressReporter innerReporter)
        {
            _owner = owner;
            _innerReporter = innerReporter;
        }

        public void Report(MonitorScanProgressSnapshot snapshot, bool force = false)
        {
            _innerReporter.Report(snapshot, force);
            _owner.MarkProgress();
        }
    }
}
