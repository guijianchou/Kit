// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

using Microsoft.Data.Sqlite;

using MonitorCore = Microsoft.PowerToys.Monitor;

namespace Microsoft.PowerToys.Settings.UI.Services
{
    public sealed class MonitorStatusQueryService
    {
        private readonly MonitorProgressSnapshotReader _progressSnapshotReader;

        public MonitorStatusQueryService()
            : this(new MonitorProgressSnapshotReader())
        {
        }

        public MonitorStatusQueryService(MonitorProgressSnapshotReader progressSnapshotReader)
        {
            _progressSnapshotReader = progressSnapshotReader;
        }

        public MonitorCore.MonitorStatusSummary GetSummaryWithStaleRefresh(string statusDatabasePath, MonitorCore.MonitorStatusRange selectedRange, DateTimeOffset now)
        {
            return GetSummaryWithStaleRefresh(statusDatabasePath, string.Empty, selectedRange, now);
        }

        public MonitorCore.MonitorStatusSummary GetSummaryWithStaleRefresh(string statusDatabasePath, string progressPath, MonitorCore.MonitorStatusRange selectedRange, DateTimeOffset now)
        {
            if (!MonitorCore.MonitorStatusStore.TryGetSummary(statusDatabasePath, selectedRange, now, out MonitorCore.MonitorStatusSummary summary))
            {
                return summary;
            }

            MonitorCore.MonitorStatusStore.RefreshStaleRunningScans(statusDatabasePath, now, MonitorCore.MonitorScanProgressFreshness.GetFreshProgressScanId(progressPath, now, _progressSnapshotReader.ReadLatest));
            return MonitorCore.MonitorStatusStore.GetSummary(statusDatabasePath, selectedRange, now);
        }

        public void RefreshStaleRunningScans(string statusDatabasePath, DateTimeOffset now)
        {
            try
            {
                MonitorCore.MonitorStatusStore.RefreshStaleRunningScans(statusDatabasePath, now);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)
            {
            }
        }

        public void RefreshStaleRunningScans(string statusDatabasePath, string progressPath, DateTimeOffset now)
        {
            try
            {
                MonitorCore.MonitorStatusStore.RefreshStaleRunningScans(statusDatabasePath, now, MonitorCore.MonitorScanProgressFreshness.GetFreshProgressScanId(progressPath, now, _progressSnapshotReader.ReadLatest));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)
            {
            }
        }

        public bool IsLatestManualScanRunning(string statusDatabasePath, string scanId)
        {
            if (string.IsNullOrWhiteSpace(scanId))
            {
                return false;
            }

            try
            {
                if (!MonitorCore.MonitorStatusStore.TryGetLatestRun(statusDatabasePath, out MonitorCore.MonitorStatusRun latestRun) || latestRun == null)
                {
                    return false;
                }

                return latestRun.Trigger == MonitorCore.MonitorScanTrigger.Manual &&
                       latestRun.Status == MonitorCore.MonitorScanStatus.Running &&
                       string.Equals(latestRun.ScanId, scanId, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)
            {
                return false;
            }
        }

        public void RecordManualScanFailure(string statusDatabasePath, string scanId, DateTimeOffset startedAt, string message)
        {
            if (string.IsNullOrWhiteSpace(scanId))
            {
                return;
            }

            try
            {
                DateTimeOffset completedAt = DateTimeOffset.UtcNow;
                int completedRuns = MonitorCore.MonitorStatusStore.CompleteLatestRunningRun(
                    statusDatabasePath,
                    scanId,
                    MonitorCore.MonitorScanTrigger.Manual,
                    MonitorCore.MonitorScanStatus.Failed,
                    completedAt,
                    recordCount: null,
                    warningCount: 0,
                    message: message);
                if (completedRuns > 0 || IsLatestRunForScanId(statusDatabasePath, scanId))
                {
                    return;
                }

                long runId = MonitorCore.MonitorStatusStore.BeginRun(
                    statusDatabasePath,
                    scanId,
                    MonitorCore.MonitorScanTrigger.Manual,
                    startedAt == default ? completedAt : startedAt);
                MonitorCore.MonitorStatusStore.CompleteRun(statusDatabasePath, runId, MonitorCore.MonitorScanStatus.Failed, completedAt, null, 0, message);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)
            {
            }
        }

        private bool IsLatestRunForScanId(string statusDatabasePath, string scanId)
        {
            return MonitorCore.MonitorStatusStore.TryGetLatestRun(statusDatabasePath, out MonitorCore.MonitorStatusRun latestRun) &&
                   latestRun != null &&
                   string.Equals(latestRun.ScanId, scanId, StringComparison.Ordinal);
        }
    }
}
