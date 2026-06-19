// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.PowerToys.Settings.UI.Helpers;
using Microsoft.PowerToys.Settings.UI.ViewModels;
using Microsoft.UI.Xaml.Media;

using MonitorScanStatus = Microsoft.PowerToys.Monitor.MonitorScanStatus;
using MonitorStatusDay = Microsoft.PowerToys.Monitor.MonitorStatusDay;
using MonitorStatusSummary = Microsoft.PowerToys.Monitor.MonitorStatusSummary;

namespace Microsoft.PowerToys.Settings.UI.Services
{
    public sealed class MonitorStatusPresentationService
    {
        public MonitorStatusPresentation CreateSummaryPresentation(MonitorStatusSummary summary)
        {
            ArgumentNullException.ThrowIfNull(summary);

            string runCountText = summary.TotalRuns.ToString(CultureInfo.InvariantCulture);
            string successRateText = summary.TotalRuns == 0
                ? "--"
                : Math.Round((double)summary.SuccessRuns / summary.TotalRuns * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            string issueCountText = (summary.WarningRuns + summary.FailedRuns).ToString(CultureInfo.InvariantCulture);

            return new MonitorStatusPresentation(
                FormatStatus(summary.OverallStatus),
                summary.TotalRuns == 0 ? GetResourceString("Monitor_StatusNoScans", "No scans yet") : FormatStatusDescription(summary),
                summary.LastMessage ?? string.Empty,
                CreateChartAccessibilityName(summary),
                CreateMetrics(runCountText, successRateText, issueCountText),
                summary.Days.Select(CreateDayViewModel).ToArray(),
                CreateLegendItems());
        }

        public MonitorStatusPresentation CreateUnavailablePresentation()
        {
            return new MonitorStatusPresentation(
                GetResourceString("Monitor_StatusUnavailable", "Unavailable"),
                GetResourceString("Monitor_StatusDatabaseUnavailable", "Status database could not be read"),
                string.Empty,
                GetResourceString("Monitor_StatusChartUnavailable", "Activity chart unavailable"),
                CreateMetrics("--", "--", "--"),
                Array.Empty<MonitorStatusDayViewModel>(),
                CreateLegendItems());
        }

        private static IReadOnlyList<MonitorStatusMetricViewModel> CreateMetrics(string runCountText, string successRateText, string issueCountText)
        {
            return new[]
            {
                new MonitorStatusMetricViewModel(GetResourceString("Monitor_StatusRunsMetric", "Runs"), runCountText),
                new MonitorStatusMetricViewModel(GetResourceString("Monitor_StatusSuccessMetric", "Success"), successRateText),
                new MonitorStatusMetricViewModel(GetResourceString("Monitor_StatusIssuesMetric", "Issues"), issueCountText),
            };
        }

        private static IReadOnlyList<MonitorStatusLegendItemViewModel> CreateLegendItems()
        {
            return new[]
            {
                new MonitorStatusLegendItemViewModel(MonitorStatusBrushes.Success, GetResourceString("Monitor_StatusLegendHealthy", "Healthy")),
                new MonitorStatusLegendItemViewModel(MonitorStatusBrushes.Warning, GetResourceString("Monitor_StatusLegendWarningRunning", "Warning / running")),
                new MonitorStatusLegendItemViewModel(MonitorStatusBrushes.Failed, GetResourceString("Monitor_StatusLegendFailed", "Failed")),
                new MonitorStatusLegendItemViewModel(MonitorStatusBrushes.Empty, GetResourceString("Monitor_StatusLegendNoData", "No data")),
            };
        }

        private static string CreateChartAccessibilityName(MonitorStatusSummary summary)
        {
            string format = GetResourceString("Monitor_StatusChartSummary", "Activity chart: {0} runs, {1} success, {2} warnings, {3} failed, {4} running");
            return string.Format(
                CultureInfo.CurrentCulture,
                format,
                summary.TotalRuns,
                summary.SuccessRuns,
                summary.WarningRuns,
                summary.FailedRuns,
                GetRunningRunCount(summary));
        }

        private static MonitorStatusDayViewModel CreateDayViewModel(MonitorStatusDay day)
        {
            string status = FormatStatus(day.Status);
            string toolTip = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ": " + status;
            if (day.TotalRuns > 0)
            {
                toolTip += " (" +
                    day.TotalRuns.ToString(CultureInfo.InvariantCulture) +
                    " runs, " +
                    day.SuccessRuns.ToString(CultureInfo.InvariantCulture) +
                    " success, " +
                    day.WarningRuns.ToString(CultureInfo.InvariantCulture) +
                    " warnings, " +
                    day.FailedRuns.ToString(CultureInfo.InvariantCulture) +
                    " failed, " +
                    GetRunningRunCount(day).ToString(CultureInfo.InvariantCulture) +
                    " running)";
            }

            return new MonitorStatusDayViewModel(GetBrush(day.Status), toolTip);
        }

        private static int GetRunningRunCount(MonitorStatusSummary summary)
        {
            return Math.Max(0, summary.TotalRuns - summary.SuccessRuns - summary.WarningRuns - summary.FailedRuns);
        }

        private static int GetRunningRunCount(MonitorStatusDay day)
        {
            return Math.Max(0, day.TotalRuns - day.SuccessRuns - day.WarningRuns - day.FailedRuns);
        }

        private static Brush GetBrush(MonitorScanStatus? status)
        {
            return status switch
            {
                MonitorScanStatus.Success => MonitorStatusBrushes.Success,
                MonitorScanStatus.Warning or MonitorScanStatus.Running => MonitorStatusBrushes.Warning,
                MonitorScanStatus.Failed => MonitorStatusBrushes.Failed,
                _ => MonitorStatusBrushes.Empty,
            };
        }

        private static string FormatStatus(MonitorScanStatus? status)
        {
            return status switch
            {
                MonitorScanStatus.Success => GetResourceString("Monitor_StatusHealthy", "Healthy"),
                MonitorScanStatus.Warning => GetResourceString("Monitor_StatusWarning", "Warning"),
                MonitorScanStatus.Failed => GetResourceString("Monitor_StatusFailed", "Failed"),
                MonitorScanStatus.Running => GetResourceString("Monitor_StatusRunning", "Running"),
                _ => GetResourceString("Monitor_StatusNoData", "No data"),
            };
        }

        private static string FormatStatusDescription(MonitorStatusSummary summary)
        {
            return summary.OverallStatus switch
            {
                MonitorScanStatus.Success => GetResourceString("Monitor_StatusLatestSuccess", "Latest scan completed successfully"),
                MonitorScanStatus.Warning => GetResourceString("Monitor_StatusLatestWarning", "Latest scan completed with warnings"),
                MonitorScanStatus.Failed => GetResourceString("Monitor_StatusLatestFailed", "Latest scan failed"),
                MonitorScanStatus.Running => GetResourceString("Monitor_StatusLatestRunning", "Scan is running"),
                _ => GetResourceString("Monitor_StatusNoScans", "No scans yet"),
            };
        }

        private static string GetResourceString(string resourceKey, string fallback)
        {
            string value = ResourceLoaderInstance.ResourceLoader.GetString(resourceKey);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }

    public sealed record MonitorStatusPresentation(
        string StatusText,
        string StatusDescription,
        string LastStatusMessage,
        string ChartAccessibilityName,
        IReadOnlyList<MonitorStatusMetricViewModel> Metrics,
        IReadOnlyList<MonitorStatusDayViewModel> Days,
        IReadOnlyList<MonitorStatusLegendItemViewModel> LegendItems);
}
