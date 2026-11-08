// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.Storage;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AiHubViewModel
{
    private readonly object _diagnosticsRefreshGate = new();
    private AiDiagnosticsStore? _diagnosticsStore;
    private AiDiagnosticsSnapshot _diagnosticsSnapshot = new();
    private long _diagnosticsStoreGeneration;
    private long _diagnosticsRequestedVersion;
    private bool _diagnosticsRefreshRunning;
    private bool _diagnosticsUiQueued;
    private PendingDiagnosticSnapshot? _pendingDiagnosticSnapshot;

    public string DiagnosticsSummary => string.Format(CultureInfo.CurrentCulture, Text("AiHub_DiagnosticsSummaryFormat"),
        _diagnosticsSnapshot.RequestCount, _diagnosticsSnapshot.Succeeded, _diagnosticsSnapshot.Failed, _diagnosticsSnapshot.NativeRetryCount);

    public string DiagnosticsDetails
    {
        get
        {
            AiDiagnosticsSnapshot snapshot = _diagnosticsSnapshot;
            string summary = string.Format(CultureInfo.CurrentCulture, Text("AiHub_DiagnosticsDetailsFormat"),
                snapshot.Cancelled, snapshot.Timeout, snapshot.Partial, snapshot.MainAttempts, snapshot.FallbackAttempts,
                snapshot.ElapsedMilliseconds / 1000.0, snapshot.InputTokens, snapshot.CachedInputTokens,
                snapshot.CacheWriteTokens, snapshot.OutputTokens, snapshot.KnownUsageRoutes, snapshot.RouteAttempts);
            string recent = string.Join(Environment.NewLine, snapshot.RecentRecords
                .Where(record => record.Kind == "task" && record.Outcome is "failed" or "timeout" or "partial")
                .Take(3)
                .Select(record => $"{record.TimestampUtc.ToLocalTime():HH:mm:ss} · {record.PluginId}/{record.TaskId} · {record.Route} · {record.ErrorCode}\n{AiDiagnosticText.Format(record.Diagnostics)}"));
            return string.IsNullOrEmpty(recent) ? summary : summary + Environment.NewLine + Environment.NewLine + recent;
        }
    }

    public string DiagnosticsPersistenceStatus => string.Format(CultureInfo.CurrentCulture,
        Text(_diagnosticsSnapshot.PersistenceEnabled ? "AiHub_DiagnosticsPersistenceOnFormat" : "AiHub_DiagnosticsPersistenceOffFormat"),
        _diagnosticsSnapshot.PersistenceFailures);

    private void InitializeDiagnostics()
    {
        DisposeDiagnostics();
        lock (_diagnosticsRefreshGate)
        {
            if (_disposed)
            {
                return;
            }

            _diagnosticsStore = (_engine as TaskAiEngine)?.Diagnostics;
            _diagnosticsStoreGeneration++;
            if (_diagnosticsStore is not null)
            {
                _diagnosticsStore.Changed += OnDiagnosticsChanged;
            }
        }

        RefreshDiagnostics();
    }

    private void OnDiagnosticsChanged(object? sender, EventArgs args) => RefreshDiagnostics();

    public void RefreshDiagnostics()
    {
        lock (_diagnosticsRefreshGate)
        {
            if (_disposed)
            {
                return;
            }

            _diagnosticsRequestedVersion++;
            if (_diagnosticsRefreshRunning)
            {
                return;
            }

            _diagnosticsRefreshRunning = true;
        }

        // GetSnapshot reads the logging preference; keep this I/O off the dispatcher.
        // One worker and one pending UI callback coalesce a burst of completed batches.
        _ = Task.Run(ReadDiagnosticSnapshots);
    }

    private void ReadDiagnosticSnapshots()
    {
        while (true)
        {
            AiDiagnosticsStore? store;
            long generation;
            long version;
            lock (_diagnosticsRefreshGate)
            {
                if (_disposed)
                {
                    _diagnosticsRefreshRunning = false;
                    return;
                }

                store = _diagnosticsStore;
                generation = _diagnosticsStoreGeneration;
                version = _diagnosticsRequestedVersion;
            }

            AiDiagnosticsSnapshot? snapshot;
            try
            {
                snapshot = store?.GetSnapshot() ?? new();
            }
            catch (Exception)
            {
                // Keep the last usable statistics if an observational read cannot complete.
                snapshot = null;
            }

            bool queueUpdate = false;
            bool repeat;
            lock (_diagnosticsRefreshGate)
            {
                if (!_disposed && snapshot is not null && generation == _diagnosticsStoreGeneration
                    && version == _diagnosticsRequestedVersion && ReferenceEquals(store, _diagnosticsStore))
                {
                    _pendingDiagnosticSnapshot = new(snapshot, store, generation, version);
                    if (!_diagnosticsUiQueued)
                    {
                        _diagnosticsUiQueued = true;
                        queueUpdate = true;
                    }
                }

                repeat = !_disposed && (version != _diagnosticsRequestedVersion || generation != _diagnosticsStoreGeneration);
                if (!repeat)
                {
                    _diagnosticsRefreshRunning = false;
                }
            }

            if (queueUpdate)
            {
                if (_dispatcherQueue is null)
                {
                    // Headless callers have no WinUI dispatcher or thread-bound bindings.
                    ApplyPendingDiagnosticSnapshot();
                }
                else if (!_dispatcherQueue.TryEnqueue(ApplyPendingDiagnosticSnapshot))
                {
                    lock (_diagnosticsRefreshGate)
                    {
                        _diagnosticsUiQueued = false;
                        _pendingDiagnosticSnapshot = null;
                    }
                }
            }

            if (!repeat)
            {
                return;
            }
        }
    }

    private void ApplyPendingDiagnosticSnapshot()
    {
        lock (_diagnosticsRefreshGate)
        {
            _diagnosticsUiQueued = false;
            PendingDiagnosticSnapshot? pending = _pendingDiagnosticSnapshot;
            _pendingDiagnosticSnapshot = null;
            if (_disposed || pending is null || pending.Generation != _diagnosticsStoreGeneration
                || pending.Version != _diagnosticsRequestedVersion || !ReferenceEquals(pending.Store, _diagnosticsStore))
            {
                return;
            }

            _diagnosticsSnapshot = pending.Snapshot;
        }

        OnPropertyChanged(nameof(DiagnosticsSummary));
        OnPropertyChanged(nameof(DiagnosticsDetails));
        OnPropertyChanged(nameof(DiagnosticsPersistenceStatus));
    }

    private void DisposeDiagnostics()
    {
        lock (_diagnosticsRefreshGate)
        {
            _diagnosticsStoreGeneration++;
            _diagnosticsRequestedVersion++;
            _pendingDiagnosticSnapshot = null;
            if (_diagnosticsStore is not null)
            {
                _diagnosticsStore.Changed -= OnDiagnosticsChanged;
                _diagnosticsStore = null;
            }
        }
    }

    private sealed record PendingDiagnosticSnapshot(AiDiagnosticsSnapshot Snapshot, AiDiagnosticsStore? Store, long Generation, long Version);
}
