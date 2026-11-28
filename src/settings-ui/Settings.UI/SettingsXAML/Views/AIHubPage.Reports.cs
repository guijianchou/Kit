// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Kit.Settings.UI.Views;

public sealed partial class AIHubPage
{
    private void OnCopyAiReportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("AIHub_CopyReportTitle".FormatLocalized("# {0} - AI Diagnostic Report", ViewModel.SecurityAuditTitle));
            sb.AppendLine("AIHub_CopyReportGeneratedAt".FormatLocalized("Generated: {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)));
            if (!string.IsNullOrWhiteSpace(ViewModel.AiReportMetaText))
            {
                sb.AppendLine("AIHub_CopyReportEnvironment".FormatLocalized("Environment: {0}", ViewModel.AiReportMetaText));
            }

            if (!string.IsNullOrWhiteSpace(ViewModel.AiReportSummaryText))
            {
                sb.AppendLine();
                sb.AppendLine("AIHub_CopyReportSummaryHeading".GetLocalizedOrFallback("## Summary"));
                sb.AppendLine(ViewModel.AiReportSummaryText);
            }

            if (ViewModel.AiReportFindings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("AIHub_CopyReportFindingsHeading".FormatLocalized("## Diagnostic Findings and Recommendations ({0} items)", ViewModel.AiReportFindings.Count));
                int index = 1;
                foreach (var finding in ViewModel.AiReportFindings)
                {
                    sb.AppendLine();
                    sb.AppendLine(CultureInfo.InvariantCulture, $"### {index}. {finding.Title} [{finding.SeverityLabel}]");
                    if (!string.IsNullOrWhiteSpace(finding.EventId))
                    {
                        sb.AppendLine("AIHub_CopyReportEventId".FormatLocalized("- Event ID: {0}", finding.EventId));
                    }

                    if (!string.IsNullOrWhiteSpace(finding.Description))
                    {
                        sb.AppendLine("AIHub_CopyReportDetails".FormatLocalized("- Details: {0}", finding.Description));
                    }

                    if (!string.IsNullOrWhiteSpace(finding.Recommendation))
                    {
                        sb.AppendLine("AIHub_CopyReportRecommendation".FormatLocalized("- Recommendation: {0}", finding.Recommendation));
                    }

                    index++;
                }
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(sb.ToString());
            Clipboard.SetContent(dataPackage);

            ViewModel.PostStatusNotification("AIHub_CopyReportSuccess".GetLocalizedOrFallback("AI diagnostic report copied to clipboard."), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ViewModel.PostStatusNotification("AIHub_CopyReportFailed".FormatLocalized("Copy failed: {0}", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async void OnWeeklyReportClick(object sender, RoutedEventArgs args) => await ShowWeeklyReportAsync();

    private AuditPeriodReportWindow _periodReportWindow;

    private async Task ShowWeeklyReportAsync()
    {
        if (!_currentPage || _disposed)
        {
            return;
        }
        try
        {
            if (_periodReportWindow == null || _periodReportWindow.IsClosed)
            {
                _periodReportWindow = new AuditPeriodReportWindow(_settingsWindow);
            }

            // Capture the selected cutoff: later page navigation cannot retarget an open report.
            var date = DateOnly.FromDateTime(ViewModel.SelectedAuditDate?.Date ?? DateTime.Today);
            await _periodReportWindow.ShowWeeklyReportAsync((mode, forceRefresh, progress, cancellationToken) =>
                ViewModel.LoadWindowsStabilityWeeklyReportAsync(mode, date, forceRefresh, progress, cancellationToken), date,
                (mode, cancellationToken) => ViewModel.ReadSavedWindowsStabilityWeeklyReportAsync(mode, date, cancellationToken));
        }
        catch (Exception exception)
        {
            ManagedCommon.Logger.LogWarning($"Period report window could not open ({exception.GetType().Name}).");
            ViewModel.PostStatusNotification("AIHub_WeeklyReadFailed".GetLocalizedOrFallback("Audit history could not be read. Reopen the report to retry."), InfoBarSeverity.Error);
        }
    }
    private void OnCopySystemDiagnosisClick(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.HasSystemDiagnosis)
        {
            return;
        }
        try
        {
            var data = new DataPackage();
            data.SetText(ViewModel.SystemDiagnosisText);
            Clipboard.SetContent(data);
            ViewModel.PostStatusNotification("AIHub_ReportCopied".GetLocalizedOrFallback("Report copied."), InfoBarSeverity.Success, optimization: true);
        }
        catch (Exception exception)
        {
            ViewModel.PostStatusNotification("AIHub_CopyReportFailed".FormatLocalized("Copy failed: {0}", exception.Message), InfoBarSeverity.Error, optimization: true);
        }
    }

    internal static string FormatWeeklyReport(WeeklyAuditReport report)
    {
        var lines = new List<string>
        {
            "AIHub_WeeklyRange".FormatLocalized("{0:d} – {1:d}", report.EndDate.AddDays(-6), report.EndDate),
        };
        if (report.Days.Count == 0)
        {
            lines.Add("AIHub_WeeklyEmpty".GetLocalizedOrFallback("No saved audits for these seven days."));
            return string.Join(Environment.NewLine, lines);
        }

        double score = report.Days.Average(day => day.HealthScore);
        lines.Add("AIHub_WeeklyCoverage".FormatLocalized("{0} audits across {1} of 7 days. Each day uses its latest saved audit.", report.Days.Sum(day => day.Audits), report.Days.Count));
        lines.Add("AIHub_WeeklyScore".FormatLocalized("Average health score {0:0.#}/100", score));
        lines.Add(report.PreviousDays.Count == 0
            ? "AIHub_WeeklyNoBaseline".GetLocalizedOrFallback("The preceding seven days have no saved audits to compare.")
            : "AIHub_WeeklyTrend".FormatLocalized("Change from the preceding seven days ({0} days observed): {1:+0.#;-0.#;0} points", report.PreviousDays.Count, score - report.PreviousDays.Average(day => day.HealthScore)));
        lines.Add(string.Empty);
        lines.Add("AIHub_WeeklyDaily".GetLocalizedOrFallback("Daily findings"));
        foreach (var day in report.Days)
        {
            lines.Add("AIHub_WeeklyDay".FormatLocalized("{0:d} · score {1} · {2} high, {3} medium, {4} low", day.Date, day.HealthScore, day.High, day.Medium, day.Low));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
