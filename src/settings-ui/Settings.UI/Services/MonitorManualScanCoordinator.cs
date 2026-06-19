// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Microsoft.PowerToys.Settings.UI.Helpers;

using MonitorCore = Microsoft.PowerToys.Monitor;

namespace Microsoft.PowerToys.Settings.UI.Services
{
    public sealed class MonitorManualScanCoordinator
    {
        private static readonly TimeSpan ManualScanUiTimeout = TimeSpan.FromMinutes(5);

        private readonly MonitorProgressSnapshotReader _progressSnapshotReader;
        private readonly MonitorStatusQueryService _statusQueryService;
        private readonly string _progressPath;
        private readonly string _statusDatabasePath;
        private string _manualScanId = string.Empty;
        private DateTimeOffset _manualScanStartedAt = DateTimeOffset.MinValue;

        public MonitorManualScanCoordinator(
            MonitorProgressSnapshotReader progressSnapshotReader,
            MonitorStatusQueryService statusQueryService,
            string progressPath,
            string statusDatabasePath)
        {
            _progressSnapshotReader = progressSnapshotReader;
            _statusQueryService = statusQueryService;
            _progressPath = progressPath;
            _statusDatabasePath = statusDatabasePath;
        }

        public MonitorManualScanProgressUpdate StartManualScanProgress()
        {
            _manualScanId = CreateManualScanId();
            _manualScanStartedAt = DateTimeOffset.UtcNow;

            return new MonitorManualScanProgressUpdate(
                _manualScanId,
                IsVisible: true,
                IsRunning: true,
                IsIndeterminate: true,
                ProgressValue: 1,
                Detail: GetResourceString("Monitor_ManualScanStarting", "Starting scan"),
                ShouldStopTimer: false,
                ShouldRefreshStatus: false);
        }

        public MonitorManualScanProgressUpdate UpdateManualScanProgress()
        {
            MonitorCore.MonitorScanProgressSnapshot snapshot = _progressSnapshotReader.ReadForScanId(_progressPath, _manualScanId);
            bool manualScanTimedOut = DateTimeOffset.UtcNow - _manualScanStartedAt >= ManualScanUiTimeout;
            if (snapshot == null)
            {
                if (manualScanTimedOut)
                {
                    return FailManualScanProgress(GetResourceString("Monitor_ManualScanTimedOut", "Scan timed out"));
                }

                return new MonitorManualScanProgressUpdate(
                    _manualScanId,
                    IsVisible: true,
                    IsRunning: true,
                    IsIndeterminate: true,
                    ProgressValue: 1,
                    Detail: GetResourceString("Monitor_ManualScanWaitingForProgress", "Waiting for worker progress"),
                    ShouldStopTimer: false,
                    ShouldRefreshStatus: false);
            }

            bool manualScanCompleted = string.Equals(snapshot.Phase, "completed", StringComparison.OrdinalIgnoreCase);
            bool manualScanFailed = string.Equals(snapshot.Phase, "failed", StringComparison.OrdinalIgnoreCase);
            if (manualScanFailed)
            {
                return FailManualScanProgress(FormatProgressDetail(snapshot));
            }

            if (manualScanTimedOut && !manualScanCompleted)
            {
                return FailManualScanProgress(GetResourceString("Monitor_ManualScanTimedOut", "Scan timed out"));
            }

            if (manualScanCompleted)
            {
                return CompleteManualScanProgress(snapshot);
            }

            return ApplyWorkerProgressSnapshot(snapshot, isRunning: true, shouldStopTimer: false, shouldRefreshStatus: false);
        }

        public MonitorManualScanProgressUpdate RestoreManualScanProgressIfRunning()
        {
            _statusQueryService.RefreshStaleRunningScans(_statusDatabasePath, DateTimeOffset.UtcNow);
            MonitorCore.MonitorScanProgressSnapshot snapshot = _progressSnapshotReader.ReadLatest(_progressPath);
            if (snapshot == null ||
                IsTerminalScanPhase(snapshot.Phase) ||
                IsManualScanSnapshotExpired(snapshot) ||
                !_statusQueryService.IsLatestManualScanRunning(_statusDatabasePath, snapshot.ScanId))
            {
                return null;
            }

            _manualScanId = snapshot.ScanId;
            _manualScanStartedAt = snapshot.StartedAt == default ? DateTimeOffset.UtcNow : snapshot.StartedAt;
            return ApplyWorkerProgressSnapshot(snapshot, isRunning: true, shouldStopTimer: false, shouldRefreshStatus: false);
        }

        private MonitorManualScanProgressUpdate CompleteManualScanProgress(MonitorCore.MonitorScanProgressSnapshot snapshot)
        {
            return ApplyWorkerProgressSnapshot(snapshot, isRunning: false, shouldStopTimer: true, shouldRefreshStatus: true);
        }

        private MonitorManualScanProgressUpdate FailManualScanProgress(string detail)
        {
            return new MonitorManualScanProgressUpdate(
                _manualScanId,
                IsVisible: true,
                IsRunning: false,
                IsIndeterminate: false,
                ProgressValue: 100,
                Detail: string.IsNullOrWhiteSpace(detail) ? GetResourceString("Monitor_ManualScanFailed", "Scan failed") : detail,
                ShouldStopTimer: true,
                ShouldRefreshStatus: true);
        }

        private static MonitorManualScanProgressUpdate ApplyWorkerProgressSnapshot(MonitorCore.MonitorScanProgressSnapshot snapshot, bool isRunning, bool shouldStopTimer, bool shouldRefreshStatus)
        {
            bool hasTotal = snapshot.FilesTotal > 0;
            bool isCompleted = string.Equals(snapshot.Phase, "completed", StringComparison.OrdinalIgnoreCase);
            bool isFailed = string.Equals(snapshot.Phase, "failed", StringComparison.OrdinalIgnoreCase);
            double progressValue = 1;
            if (hasTotal)
            {
                progressValue = Math.Clamp((double)snapshot.FilesProcessed / snapshot.FilesTotal * 100, 1, 100);
            }

            if (isCompleted || isFailed)
            {
                progressValue = 100;
            }

            return new MonitorManualScanProgressUpdate(
                snapshot.ScanId,
                IsVisible: true,
                IsRunning: isRunning,
                IsIndeterminate: !hasTotal && !isCompleted && !isFailed,
                ProgressValue: progressValue,
                Detail: FormatProgressDetail(snapshot),
                ShouldStopTimer: shouldStopTimer,
                ShouldRefreshStatus: shouldRefreshStatus);
        }

        private static bool IsManualScanSnapshotExpired(MonitorCore.MonitorScanProgressSnapshot snapshot)
        {
            return snapshot.StartedAt != default && DateTimeOffset.UtcNow - snapshot.StartedAt >= ManualScanUiTimeout;
        }

        private static bool IsTerminalScanPhase(string phase)
        {
            return string.Equals(phase, "completed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, "failed", StringComparison.OrdinalIgnoreCase);
        }

        private static string CreateManualScanId()
        {
            return Guid.NewGuid().ToString("N");
        }

        private static string FormatProgressDetail(MonitorCore.MonitorScanProgressSnapshot snapshot)
        {
            string phase = snapshot.Phase switch
            {
                "hashing" => GetResourceString("Monitor_ManualScanPhaseHashing", "Hashing"),
                "categorizing" => GetResourceString("Monitor_ManualScanPhaseCategorizing", "Categorizing"),
                "writing" => GetResourceString("Monitor_ManualScanPhaseWriting", "Writing"),
                "completed" => GetResourceString("Monitor_ManualScanPhaseComplete", "Complete"),
                "failed" => GetResourceString("Monitor_ManualScanFailed", "Scan failed"),
                _ => GetResourceString("Monitor_ManualScanPhaseScanning", "Scanning"),
            };

            if (string.Equals(snapshot.Phase, "failed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(snapshot.Message))
            {
                return snapshot.Message;
            }

            if (string.Equals(snapshot.Phase, "completed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(snapshot.Message))
            {
                return snapshot.Message;
            }

            if (string.Equals(snapshot.Phase, "completed", StringComparison.OrdinalIgnoreCase) && snapshot.RecordCount.HasValue)
            {
                return FormatResourceString("Monitor_ManualScanCompletedFiles", "{0}: {1} files", phase, snapshot.RecordCount.Value);
            }

            return snapshot.FilesTotal > 0
                ? FormatResourceString("Monitor_ManualScanProgressFiles", "{0}: {1}/{2}", phase, snapshot.FilesProcessed, snapshot.FilesTotal)
                : phase;
        }

        [SuppressMessage("Performance", "CA1863:Cache a CompositeFormat for repeated use", Justification = "The format string is loaded from localizable resources.")]
        private static string FormatResourceString(string resourceKey, string fallback, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, GetResourceString(resourceKey, fallback), args);
        }

        private static string GetResourceString(string resourceKey, string fallback)
        {
            string value = ResourceLoaderInstance.ResourceLoader.GetString(resourceKey);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
