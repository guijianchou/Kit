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
        public MonitorCore.MonitorStatusSummary GetSummaryWithStaleRefresh(string statusDatabasePath, MonitorCore.MonitorStatusRange selectedRange, DateTimeOffset now)
        {
            if (!MonitorCore.MonitorStatusStore.TryGetSummary(statusDatabasePath, selectedRange, now, out MonitorCore.MonitorStatusSummary summary))
            {
                return summary;
            }

            MonitorCore.MonitorStatusStore.RefreshStaleRunningScans(statusDatabasePath, now);
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
    }
}
