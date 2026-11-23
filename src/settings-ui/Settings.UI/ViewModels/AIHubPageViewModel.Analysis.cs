// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Kit.AiHub.Engine;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AIHubPageViewModel
{
    private string _auditMergedSummary = string.Empty;

    public bool HasAuditBatchProgress => _auditClock.HasProgress && _auditClock.Progress.TotalBatches > 0;

    public string AuditProgressSummaryText => HasAuditBatchProgress
        ? AiTaskProgressText.BatchSummary(_auditClock.Progress, PreferChinese()) : string.Empty;

    public string AuditProgressActivityText => _auditClock.HasProgress
        ? AiTaskProgressText.BatchActivity(_auditClock.Progress, PreferChinese()) : AuditStatusText;

    public string AuditProgressQueueText => _auditClock.HasProgress
        ? AiTaskProgressText.QueueDetail(_auditClock.Progress, PreferChinese()) : string.Empty;

    public string AuditResultDetailText => !_auditIncomplete && !string.IsNullOrEmpty(_auditMergedSummary)
        ? _auditMergedSummary : AuditStatusText;

    public string AuditResultSupplementText => (!_auditIncomplete && !string.IsNullOrEmpty(_auditMergedSummary))
        || string.Equals(AiAnalysisRanText, AuditResultDetailText, StringComparison.Ordinal) ? string.Empty : AiAnalysisRanText;

    private void RefreshAuditDetailText()
    {
        OnPropertyChanged(nameof(HasAuditBatchProgress));
        OnPropertyChanged(nameof(AuditProgressSummaryText));
        OnPropertyChanged(nameof(AuditProgressActivityText));
        OnPropertyChanged(nameof(AuditProgressQueueText));
        OnPropertyChanged(nameof(AuditResultDetailText));
        OnPropertyChanged(nameof(AuditResultSupplementText));
    }

    internal static bool IsIncompleteAuditOutcome(string outcome) => outcome is "pending" or "partial" or "failed" or "cancelled";

    internal static (string ResourceKey, string Fallback, InfoBarSeverity Severity) DescribeAuditOutcome(string outcome, bool hasSkippedChannels)
    {
        var presentation = outcome switch
        {
            "pending" => ("AIHub_FinishAudit_Pending", "Local findings are ready; AI analysis is still running.", InfoBarSeverity.Informational),
            "partial" => ("AIHub_FinishAudit_Partial", "Audit partially completed; validated AI results were retained.", InfoBarSeverity.Warning),
            "failed" => ("AIHub_FinishAudit_Failed", "Local audit completed; AI analysis failed. Results contain rule-based findings only.", InfoBarSeverity.Warning),
            "cancelled" => ("AIHub_DeepAnalysis_Cancelled", "AI analysis cancelled", InfoBarSeverity.Informational),
            _ => ("AIHub_FinishAudit_Completed", "Audit completed.", InfoBarSeverity.Success),
        };
        return (presentation.Item1, presentation.Item2, hasSkippedChannels ? InfoBarSeverity.Warning : presentation.Item3);
    }

    internal static async Task<List<AuditResult>> ReadAuditHistoryAfterWritesAsync(Task pendingWrites, Func<Task<List<AuditResult>>> read)
    {
        await pendingWrites.ConfigureAwait(false);
        return await read().ConfigureAwait(false);
    }

    private void RefreshAuditCoverage()
    {
        OnPropertyChanged(nameof(AuditOverviewCoverage));
        OnPropertyChanged(nameof(HasAuditOverviewCoverage));
    }

    private void ApplyAiAnalysis(
        AiHubAuditAnalysisService.AuditAnalysisResult result,
        List<AuditIssueEnhanced> ruleIssues,
        IReadOnlyList<SecurityEvent> events,
        AuditResult snapshot,
        bool interim)
    {
        if (result.Issues is null)
        {
            AiStatusText = "AIHub_DeepAnalysis_IncompleteFormat".FormatLocalized("AI analysis did not complete: {0}", result.Failure);
            AiAnalysisRanText = AuditStatusText = AiStatusText;
            if (HasAiReport)
            {
                AiReportSummaryText = AiStatusText;
                _fluentAiReportPartial = true;
            }
            _auditIncomplete = true;
            AuditPhase = ScanPhase.Failed;
            FinishedText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            if (snapshot is not null)
            {
                snapshot.AnalysisOutcome = "failed";
            }
            RefreshAuditCoverage();
            ShowStatus(AiStatusText, InfoBarSeverity.Warning);
            return;
        }

        var issues = AiHubAuditAnalysisService.DeduplicateIssues(result.Issues);
        var merged = MergeIssues(ruleIssues, issues, events);
        _auditMergedSummary = "AIHub_AuditMergedSummaryFormat".FormatLocalized(
            "Local rules: {0} · AI findings: {1} · After merging: {2}", ruleIssues.Count, issues.Count, merged.Count);
        AllIssues.Clear();
        foreach (var issue in merged)
        {
            AllIssues.Add(issue);
        }
        HealthScore = HealthScoreCalculator.Calculate(merged);
        HealthScoreGrade = HealthScore >= 80 ? "AIHub_HealthScoreGrade_Good".GetLocalizedOrFallback("Good")
            : HealthScore >= 60 ? "AIHub_HealthScoreGrade_Warning".GetLocalizedOrFallback("Warning")
            : "AIHub_HealthScoreGrade_Critical".GetLocalizedOrFallback("Critical");
        HighCount = merged.Count(issue => issue.IsHigh);
        MediumCount = merged.Count(issue => issue.IsMedium);
        LowCount = merged.Count(issue => issue.IsLow);
        ActivityFindingsText = merged.Count.ToString(CultureInfo.InvariantCulture);
        UpdateHealthSummary(HealthScore, HighCount, MediumCount, LowCount);
        if (snapshot is not null)
        {
            snapshot.Findings = merged;
            snapshot.HealthScore = HealthScore;
            snapshot.AnalysisOutcome = interim ? "pending" : result.IsPartial ? "partial" : "ok";
        }
        RebuildSections();

        _aiReportFindings.Clear();
        foreach (var issue in issues.OrderByDescending(item => item.Severity == "High").ThenByDescending(item => item.Occurrences))
        {
            _aiReportFindings.Add(new AiReportFinding(
                PreferChinese() && !string.IsNullOrWhiteSpace(issue.TitleZh) ? issue.TitleZh : issue.Title,
                issue.Severity == "High" ? HighLabel : issue.Severity == "Medium" ? MediumLabel : LowLabel,
                string.Join(Environment.NewLine, new[]
                {
                    PreferChinese() && !string.IsNullOrWhiteSpace(issue.DescriptionZh) ? issue.DescriptionZh : issue.Description,
                    PreferChinese() && !string.IsNullOrWhiteSpace(issue.RootCauseZh) ? issue.RootCauseZh : issue.RootCause,
                }.Where(text => !string.IsNullOrWhiteSpace(text))),
                PreferChinese() && !string.IsNullOrWhiteSpace(issue.RecommendationZh) ? issue.RecommendationZh : issue.Recommendation,
                issue.EventId));
        }

        AiReportSummaryText = interim
            ? PreferChinese() ? $"AI 分析仍在进行，已验证 {issues.Count} 项发现。" : $"AI analysis continues; {issues.Count} validated finding(s) are available."
            : result.IsPartial
                ? "AIHub_DeepAnalysis_SummaryPartialFormat".FormatLocalized("AI partially completed: retained {0} enhanced finding(s) ({1}).", issues.Count, result.Failure)
                : "AIHub_DeepAnalysis_SummaryCompleteFormat".FormatLocalized("AI deep analysis completed: {0} enhanced finding(s).", issues.Count);
        AiReportMetaText = "AIHub_DeepAnalysis_MetaFormat".FormatLocalized("Kernel {0} · {1:HH:mm:ss}", AiHubEngine.Current.ActiveKernel, DateTime.Now);
        _fluentAiReportPartial = interim || result.IsPartial;
        HasAiReport = true;
        AiAnalysisRanText = AiReportSummaryText;
        if (interim)
        {
            return;
        }

        AiStatusText = AuditStatusText = AiReportSummaryText;
        _auditIncomplete = result.IsPartial;
        if (!result.IsPartial)
        {
            EndStage("Analyze findings", audit: true);
            EndStage("Finish", audit: true);
        }
        AuditPhase = ScanPhase.SelectingTargets;
        FinishedText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (result.IsPartial)
        {
            ShowStatus(AiStatusText, InfoBarSeverity.Warning);
        }
        else
        {
            FinishAuditStatus(HealthScore, merged.Count, "ok", snapshot?.SkippedChannels ?? []);
        }
        OnPropertyChanged(nameof(CanRunDeepAnalysis));
    }

    private void CompleteAiInterruption(AuditResult snapshot, bool cancelled)
    {
        AiStatusText = cancelled
            ? "AIHub_DeepAnalysis_Cancelled".GetLocalizedOrFallback("AI analysis cancelled")
            : "AIHub_DeepAnalysis_IncompleteFormat".FormatLocalized("AI analysis did not complete: {0}", "ExecutionFailed");
        AiAnalysisRanText = AuditStatusText = AiStatusText;
        if (HasAiReport)
        {
            AiReportSummaryText = AiStatusText;
        }
        _auditIncomplete = true;
        _fluentAiReportPartial = HasAiReport;
        if (snapshot is not null)
        {
            snapshot.AnalysisOutcome = cancelled ? "cancelled" : HasAiReport ? "partial" : "failed";
        }
        AuditPhase = ScanPhase.SelectingTargets;
        FinishedText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        IsAiAnalyzing = false;
        ShowStatus(AiStatusText, cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
        PersistCurrentAudit();
    }

    // Persist the local result and later replace it with the AI outcome. Serialize
    // snapshots so a slower local write cannot overwrite the newer AI result.
    private void PersistCurrentAudit()
    {
        if (!_persistCurrentAudit || _auditOverviewSnapshot is not { } snapshot)
        {
            return;
        }
        _persistCurrentAudit = false;
        var saved = new AuditResult
        {
            Timestamp = snapshot.Timestamp,
            HealthScore = snapshot.HealthScore,
            Findings = snapshot.Findings.ToList(),
            EventsScanned = snapshot.EventsScanned,
            ScanStart = snapshot.ScanStart,
            ScanEnd = snapshot.ScanEnd,
            SkippedChannels = snapshot.SkippedChannels?.ToList(),
            LimitedChannels = snapshot.LimitedChannels?.ToList(),
            AnalysisOutcome = snapshot.AnalysisOutcome,
            UsedModel = snapshot.UsedModel,
        };
        _auditHistoryWriteTask = SaveAuditAfterAsync(_auditHistoryWriteTask, saved);
    }

    private async Task SaveAuditAfterAsync(Task previous, AuditResult snapshot)
    {
        await previous.ConfigureAwait(false);
        await SaveHistoryAndRefreshStatisticsAsync(snapshot).ConfigureAwait(false);
    }
}
