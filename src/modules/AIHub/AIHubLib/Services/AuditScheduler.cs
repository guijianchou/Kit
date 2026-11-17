// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.AIHubLib.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;

/// <summary>
/// Runs audits on a fixed cadence, continuing from the previous scan so each run only
/// re-reads the new tail of the event log.
/// </summary>
/// <remarks>
/// Cadence and window arithmetic come from <see cref="AuditSchedule"/>, so the rules
/// stay in one place and this type only owns the loop. It has no UI dependency, which
/// is what lets a headless worker keep auditing after Settings closes.
/// </remarks>
public sealed partial class AuditScheduler : IDisposable, IAsyncDisposable
{
    private readonly AuditPipeline _pipeline;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _fullRange;
    private readonly int _fullRangeDays;
    private readonly Func<bool> _isEnabled;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _auditGate = new(1, 1);
    private int _intervalHours;

    private EventLogService.AuditMode _mode;
    private CancellationTokenSource? _runCts;
    private Task? _loop;
    private Task? _disposeTask;
    private DateTime? _lastCompletedUtc;
    private DateTime? _lastScanEndUtc;
    private bool _disposed;

    public AuditScheduler(
        AuditPipeline pipeline,
        int intervalHours,
        Func<bool>? isEnabled = null,
        TimeSpan? pollInterval = null,
        TimeSpan? fullRange = null,
        EventLogService.AuditMode mode = EventLogService.AuditMode.Extended,
        DateTime? lastCompletedUtc = null,
        DateTime? lastScanEndUtc = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _intervalHours = AuditSchedule.NormalizeIntervalHours(intervalHours);
        _isEnabled = isEnabled ?? (() => true);
        _pollInterval = pollInterval ?? TimeSpan.FromMinutes(1);
        _fullRange = fullRange ?? TimeSpan.FromDays(1);
        _fullRangeDays = Math.Max(1, (int)Math.Round(_fullRange.TotalDays));
        _mode = mode;
        _lastCompletedUtc = lastCompletedUtc;
        _lastScanEndUtc = lastScanEndUtc ?? lastCompletedUtc;
    }

    /// <summary>Configured cadence in hours (0 disables scheduling).</summary>
    public int IntervalHours { get { lock (_gate) { return _intervalHours; } } }

    /// <summary>When the last audit completed, or null when none has run.</summary>
    public DateTime? LastCompletedUtc
    {
        get { lock (_gate) { return _lastCompletedUtc; } }
    }

    /// <summary>End of the covered event window, independent of execution duration.</summary>
    public DateTime? LastScanEndUtc
    {
        get { lock (_gate) { return _lastScanEndUtc; } }
    }

    public bool IsRunning { get { lock (_gate) { return _loop is { IsCompleted: false }; } } }

    /// <summary>Audit mode applied to subsequent runs.</summary>
    public EventLogService.AuditMode Mode { get { lock (_gate) { return _mode; } } }

    /// <summary>Applies a new cadence; 0 stops scheduling at the next tick.</summary>
    public void UpdateInterval(int intervalHours)
    {
        lock (_gate)
        {
            _intervalHours = AuditSchedule.NormalizeIntervalHours(intervalHours);
        }
    }

    /// <summary>Applies a new audit mode for subsequent runs.</summary>
    public void UpdateMode(EventLogService.AuditMode mode)
    {
        lock (_gate)
        {
            _mode = mode;
        }
    }

    /// <summary>True when a run is due right now.</summary>
    public bool IsDue()
    {
        lock (_gate)
        {
            return _isEnabled() && AuditSchedule.IsDue(_lastCompletedUtc, DateTime.UtcNow, IntervalHours);
        }
    }

    /// <summary>Runs one audit immediately, regardless of the cadence.</summary>
    public async Task<AuditResult?> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isEnabled() || !await _auditGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DateTime nowUtc = DateTime.UtcNow;
            // Scheduled runs may continue from the previous scan; manual scans never do.
            (DateTime fromUtc, DateTime toUtc) = AuditSchedule.ComputeWindow(
                ScanIntent.Scheduled, LastScanEndUtc, nowUtc, _fullRangeDays);

            AuditResult result = await _pipeline
                .RunAsync(fromUtc, toUtc, Mode, persist: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _lastCompletedUtc = DateTime.UtcNow;
                _lastScanEndUtc = result.ScanEnd;
            }

            return result;
        }
        finally
        {
            _auditGate.Release();
        }
    }

    /// <summary>Starts the background loop. Calling it twice is a no-op.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Guard under the lock: checking IsRunning first would let two concurrent
            // callers both observe a null/complete loop and start a second one.
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            _runCts = new CancellationTokenSource();
            CancellationToken token = _runCts.Token;
            _loop = Task.Run(() => LoopAsync(token), token);
        }
    }

    /// <summary>Stops the loop and waits for the in-flight run to finish.</summary>
    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            loop = _loop;
            cts = _runCts;
            cts?.Cancel();
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_runCts, cts))
                {
                    _runCts = null;
                    _loop = null;
                }

                cts.Dispose();
            }
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (IsDue())
                {
                    await RunOnceAsync(cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // One failed audit must not kill the schedule; wait for the next tick.
                try
                {
                    await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync().ConfigureAwait(false);
        // Also wait for a manual run; do not dispose the gate while a waiter can still enter it.
        await _auditGate.WaitAsync().ConfigureAwait(false);
        _auditGate.Dispose();
    }
}
