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
        if (!interim && result.Failure is not null && result.Issues is not null)
        {
            result = result with { CompleteEvidenceCoverage = false, IsPartial = true };
        }
        if (!interim && _fullScanLogContext is not null && result.Issues is not null && !result.CompleteEvidenceCoverage)
        {
            result = result with { IsPartial = true };
        }
        if (!interim && result.CompleteEvidenceCoverage
            && !string.Equals(result.EvidenceFingerprint, AiHubAuditAnalysisService.CreateEvidenceFingerprint(events), StringComparison.Ordinal))
        {
            result = result with { CompleteEvidenceCoverage = false, IsPartial = true, Failure = "AnalysisEvidenceChanged" };
        }
        if (_retainingAiReport && (interim || result.Issues is null || result.IsPartial || !result.CompleteEvidenceCoverage))
        {
            if (interim)
            {
                AiStatusText = PreferChinese() ? "新分析仍在进行；继续显示上次完整报告。" : "The new analysis is running; the last complete report remains available.";
                return;
            }
            _lastAuditAnalysisAttempt = result.IsPartial ? "partial" : "failed";
            _auditIncomplete = true;
            _persistCurrentAudit = false;
            _retainingAiReport = false;
            AuditPhase = ScanPhase.Failed;
            FinishedText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            AiAnalysisRanText = AuditStatusText = AiStatusText = PreferChinese()
                ? "本次分析未完成，已保留上次完整报告。可重试分析。"
                : "This analysis did not complete. The previous complete report was retained; retry is available.";
            ShowStatus(AiStatusText, InfoBarSeverity.Warning);
            RefreshAuditActions();
            return;
        }

        if (result.Issues is null)
        {
            _lastAuditAnalysisAttempt = "failed";
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
        bool replaceReviewedEvidence = !interim && !result.IsPartial && result.Failure is null && result.CompleteEvidenceCoverage;
        var merged = MergeIssues(ruleIssues, issues, events, replaceReviewedEvidence);
        _auditMergedSummary = replaceReviewedEvidence
            ? PreferChinese() ? $"AI 已完成所选证据复核，以最终判定更新本次审计；未覆盖事件保留本地判断。当前共 {merged.Count} 项问题。"
                : $"AI completed its selected evidence review and updated this audit. Unreviewed events retain local findings. {merged.Count} findings remain."
            : "AIHub_AuditMergedSummaryFormat".FormatLocalized("Local rules: {0} · AI findings: {1} · After merging: {2}", ruleIssues.Count, issues.Count, merged.Count);
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
            snapshot.EventsScanned = events.Count;
            snapshot.AnalysisOutcome = interim ? "pending" : result.IsPartial ? "partial" : "ok";
        }
        RebuildSections();

        _aiReportFindings.Clear();
        foreach (var issue in AuditRiskPolicy.OrderFindings(merged.Where(IsPersistedAiFinding)))
        {
            _aiReportFindings.Add(new AiReportFinding(
                PreferChinese() && !string.IsNullOrWhiteSpace(issue.TitleZh) ? issue.TitleZh : issue.Title,
                issue.Severity == "High" ? HighLabel : issue.Severity == "Medium" ? MediumLabel : LowLabel,
                string.Join(Environment.NewLine, new[]
                {
                    PreferChinese() && !string.IsNullOrWhiteSpace(issue.DescriptionZh) ? issue.DescriptionZh : issue.Description,
                    PreferChinese() && !string.IsNullOrWhiteSpace(issue.RootCauseZh) ? issue.RootCauseZh : issue.RootCause,
                }.Where(text => !string.IsNullOrWhiteSpace(text))),
                ToEnhanced(issue).PriorityActionText,
                issue.EventId));
        }

        AiReportSummaryText = interim
            ? PreferChinese() ? $"AI 分析仍在进行，已验证 {issues.Count} 项发现。" : $"AI analysis continues; {issues.Count} validated finding(s) are available."
            : result.IsPartial
                ? "AIHub_DeepAnalysis_SummaryPartialFormat".FormatLocalized("AI partially completed: retained {0} enhanced finding(s) ({1}).", issues.Count, result.Failure)
                : BuildAuditNarrative(merged, events.Count, result.AnalyzedEvents + result.ReusedEvents, PreferChinese());
        AiReportMetaText = "AIHub_DeepAnalysis_MetaFormat".FormatLocalized("Kernel {0} · {1:HH:mm:ss}", AiHubEngine.Current.ActiveKernel, DateTime.Now);
        AiReportMetaText += " · " + AuditModelProvenance(result, PreferChinese());
        if (!interim)
        {
            AiReportMetaText += PreferChinese()
                ? $" · 复用 {result.ReusedEvents} 条 · 本次分析 {result.AnalyzedEvents} 条"
                : $" · {result.ReusedEvents} reused · {result.AnalyzedEvents} analyzed";
        }
        if (snapshot is not null)
        {
            snapshot.UsedModel = result.UsedModel;
            snapshot.AiReportSummary = AiReportSummaryText;
            snapshot.AiReportMeta = AiReportMetaText;
            snapshot.AiAnalysisScope = result.Scope;
            snapshot.AiEvidenceFingerprint = result.EvidenceFingerprint;
            snapshot.AiAnalyzedEvents = result.AnalyzedEvents;
            snapshot.AiReusedEvents = result.ReusedEvents;
        }
        _fluentAiReportPartial = interim || result.IsPartial;
        HasAiReport = true;
        AiAnalysisRanText = AiReportSummaryText;
        if (interim)
        {
            return;
        }

        _retainingAiReport = false;
        _lastAuditAnalysisAttempt = result.IsPartial ? "partial" : string.Empty;
        _auditReportNeedsRefresh = !result.MatchesRequestedModel;
        if (result.MatchesRequestedModel)
        {
            _knownAuditAnalysisScope = result.Scope;
        }
        if (ReferenceEquals(snapshot, _auditSourceSnapshot))
        {
            _auditSourceEvents = events.ToList();
        }
        EventCountText = "AIHub_EventCountFormat".FormatLocalized("{0} events", events.Count);

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
        OnPropertyChanged(nameof(DeepAnalyzeHint));
        ((RelayCommand)DeepAnalyzeCommand).OnCanExecuteChanged();
        RefreshAuditActions();
    }

    internal static string AuditModelProvenance(AiHubAuditAnalysisService.AuditAnalysisResult result, bool chinese)
    {
        string model = string.IsNullOrWhiteSpace(result.UsedModel) ? chinese ? "模型待确认" : "Model unconfirmed" : result.UsedModel;
        string route = string.IsNullOrWhiteSpace(result.UsedRoute) ? chinese ? "路由待确认" : "Route unconfirmed" : result.UsedRoute;
        return model + " · " + route;
    }

    internal static string BuildAuditNarrative(IReadOnlyList<AuditIssueEnhanced> findings, int collected, int reviewed, bool chinese)
    {
        int high = findings.Count(finding => finding.IsHigh);
        int medium = findings.Count(finding => finding.IsMedium);
        var priorities = AuditRiskPolicy.OrderFindings(findings).Take(3).ToList();
        string headline = chinese
            ? $"本次共检查 {collected} 条事件，AI 审阅其中 {reviewed} 条。综合本地规则与 AI 发现，记录 {findings.Count} 项问题，其中高优先级 {high} 项、中优先级 {medium} 项。"
            : $"This audit checked {collected} events; AI reviewed {reviewed}. Local rules and AI identified {findings.Count} issues, including {high} high-priority and {medium} medium-priority issues.";
        if (priorities.Count == 0)
        {
            return headline + (chinese ? " 已收集的证据中未发现需要处理的问题；这不代表未采集的日志或系统状态均正常。" : " No actionable issue was identified in the collected evidence; uncollected logs and system state remain outside this assessment.");
        }
        string focus = string.Join(chinese ? "；" : "; ", priorities.Select(finding => chinese && !string.IsNullOrWhiteSpace(finding.TitleZh) ? finding.TitleZh : finding.Title));
        return headline + Environment.NewLine + Environment.NewLine
            + (chinese ? $"当前应优先核查：{focus}。先按各问题的检查步骤确认原因，再选择条件修复，并通过恢复验证确认结果。"
                : $"Prioritize: {focus}. Confirm each cause using its diagnostic checks, choose the applicable repair, and verify recovery.")
            + (reviewed < collected ? chinese ? " 本次 AI 未覆盖全部事件，其余事件仅经过本地规则检查。" : " AI did not review every event; remaining events received local rule checks only." : string.Empty);
    }

    internal static bool IsPersistedAiFinding(AuditIssueEnhanced finding) =>
        finding is not null
        && !string.IsNullOrWhiteSpace(finding.EventRef)
        && finding.EventRef.StartsWith("item-", StringComparison.Ordinal);

    internal static bool HasPersistedAiAnalysis(AuditResult history) =>
        history is not null
        && (string.Equals(history.AnalysisOutcome, "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(history.AnalysisOutcome, "partial", StringComparison.OrdinalIgnoreCase))
        && (!string.IsNullOrWhiteSpace(history.AiReportSummary)
            || (history.Findings is not null && history.Findings.Any(IsPersistedAiFinding)));

    private void RestorePersistedAiReport(AuditResult history)
    {
        if (!HasPersistedAiAnalysis(history))
        {
            return;
        }

        var aiFindings = AuditRiskPolicy.OrderFindings((history.Findings ?? []).Where(IsPersistedAiFinding)).ToList();

        _aiReportFindings.Clear();
        foreach (var issue in aiFindings)
        {
            string details = string.Join(
                Environment.NewLine,
                new[] { issue.DisplayDescription, issue.PriorityReasonText }.Where(text => !string.IsNullOrWhiteSpace(text)));
            _aiReportFindings.Add(new AiReportFinding(
                issue.DisplayTitle,
                issue.IsHigh ? HighLabel : issue.IsMedium ? MediumLabel : LowLabel,
                details,
                issue.PriorityActionText,
                issue.EventId));
        }

        bool isPartial = string.Equals(history.AnalysisOutcome, "partial", StringComparison.OrdinalIgnoreCase);
        AiReportSummaryText = !string.IsNullOrWhiteSpace(history.AiReportSummary)
            ? history.AiReportSummary
            : isPartial
                ? "AIHub_DeepAnalysis_SummaryPartialFormat".FormatLocalized("AI partially completed: retained {0} enhanced finding(s) ({1}).", aiFindings.Count, "partial")
                : "AIHub_DeepAnalysis_SummaryCompleteFormat".FormatLocalized("AI deep analysis completed: {0} enhanced finding(s).", aiFindings.Count);
        AiReportMetaText = !string.IsNullOrWhiteSpace(history.AiReportMeta)
            ? history.AiReportMeta
            : !string.IsNullOrWhiteSpace(history.UsedModel)
                ? $"{history.UsedModel} · {history.Timestamp.ToLocalTime():HH:mm:ss}"
                : string.Empty;
        _fluentAiReportPartial = isPartial;
        HasAiReport = true;
        _auditReportNeedsRefresh = string.IsNullOrEmpty(history.AiAnalysisScope)
            || history.AiAnalysisScope.Contains(":actual:", StringComparison.Ordinal)
            || (!string.IsNullOrEmpty(_knownAuditAnalysisScope) && !string.Equals(history.AiAnalysisScope, _knownAuditAnalysisScope, StringComparison.Ordinal));
        RefreshAuditActions();
    }

    private void CompleteAiInterruption(AuditResult snapshot, bool cancelled)
    {
        if (!IsAiAnalyzing && !_retainingAiReport && _lastAuditAnalysisAttempt is "cancelled" or "failed")
        {
            return;
        }
        _lastAuditAnalysisAttempt = cancelled ? "cancelled" : "failed";
        if (_retainingAiReport)
        {
            _retainingAiReport = false;
            _persistCurrentAudit = false;
            _auditIncomplete = true;
            AiAnalysisRanText = AuditStatusText = AiStatusText = PreferChinese()
                ? "本次分析已停止；上次完整报告已保留。"
                : "This analysis stopped; the previous complete report was retained.";
            AuditPhase = ScanPhase.Failed;
            FinishFullScanAnalysis(snapshot, cancelled ? "cancelled" : "failed");
            FinishedText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            IsAiAnalyzing = false;
            ShowStatus(AiStatusText, cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
            RefreshAuditActions();
            return;
        }
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
        FinishFullScanAnalysis(snapshot, cancelled ? "cancelled" : "failed");
        IsAiAnalyzing = false;
        ShowStatus(AiStatusText, cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
        PersistCurrentAudit();
    }

    // Persist the local result and later replace it with the AI outcome. Serialize
    // snapshots so a slower local write cannot overwrite the newer AI result.
    private void PersistCurrentAudit()
    {
        if (!_persistCurrentAudit || _auditOverviewSnapshot is not { } snapshot || snapshot.EvidenceIncomplete)
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
            CollectionProfile = snapshot.CollectionProfile,
            EvidenceIncomplete = snapshot.EvidenceIncomplete,
            AnalysisOutcome = snapshot.AnalysisOutcome,
            UsedModel = snapshot.UsedModel,
            AiReportSummary = snapshot.AiReportSummary,
            AiReportMeta = snapshot.AiReportMeta,
            AiAnalysisScope = snapshot.AiAnalysisScope,
            AiEvidenceFingerprint = snapshot.AiEvidenceFingerprint,
            AiAnalyzedEvents = snapshot.AiAnalyzedEvents,
            AiReusedEvents = snapshot.AiReusedEvents,
        };
        _auditHistoryWriteTask = SaveAuditAfterAsync(_auditHistoryWriteTask, saved);
        lock (_lastAuditEvents)
        {
            CaptureWeeklyScanSnapshot(saved, _lastAuditEvents);
        }
    }

    private async Task SaveAuditAfterAsync(Task previous, AuditResult snapshot)
    {
        await previous.ConfigureAwait(false);
        await SaveHistoryAndRefreshStatisticsAsync(snapshot).ConfigureAwait(false);
    }
}
