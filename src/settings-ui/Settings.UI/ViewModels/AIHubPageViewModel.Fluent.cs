// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Kit.AiHub.Contract;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library.Helpers;
using Microsoft.UI.Xaml;

namespace Kit.Settings.UI.ViewModels;

public sealed class AuditCategoryRow(AuditCategorySummary summary)
{
    public string Category => summary.Category;

    public string Title => summary.Category.ToLowerInvariant() switch
    {
        "reliability" => "AIHub_AuditCategoryReliability".GetLocalizedOrFallback("System reliability"),
        "stability" => "AIHub_AuditCategoryStability".GetLocalizedOrFallback("Crashes & restarts"),
        "configuration" => "AIHub_AuditCategoryConfiguration".GetLocalizedOrFallback("Configuration"),
        "network" => "AIHub_AuditCategoryNetwork".GetLocalizedOrFallback("Network"),
        "application" => "AIHub_AuditCategoryApplication".GetLocalizedOrFallback("Applications"),
        "system" => "AIHub_AuditCategorySystem".GetLocalizedOrFallback("System components"),
        _ => summary.Priority.CategoryLabel,
    };

    public string CategoryGlyph => summary.Category.ToLowerInvariant() switch
    {
        "reliability" => "\uE9D9",
        "stability" => "\uE945",
        "configuration" => "\uE713",
        "network" => "\uE774",
        "application" => "\uE74C",
        "system" => "\uE7F4",
        "firewall" => "\uE72E",
        "privilege" or "authentication" or "security" => "\uE8D7",
        _ => "\uE946",
    };

    public string Counts => summary.Findings == 1
        ? "AIHub_AuditCategorySingleIssue".GetLocalizedOrFallback("1 issue")
        : "AIHub_AuditCategoryIssueCount".FormatLocalized("{0} issues", summary.Findings);

    public string Occurrences => summary.Occurrences == 1
        ? "AIHub_AuditCategorySingleOccurrence".GetLocalizedOrFallback("Occurred once")
        : "AIHub_AuditCategoryOccurrences".FormatLocalized("{0} occurrences", summary.Occurrences);

    public AuditIssueEnhanced PriorityIssue => summary.Priority;

    public bool IsHigh => summary.High > 0;

    public bool IsMedium => !IsHigh && summary.Medium > 0;

    public bool IsLow => !IsHigh && !IsMedium;

    public string Severity => summary.Priority.RiskBadgeText;

    public string ActionLabel => "AIHub_WorkspaceCategoryAction".GetLocalizedOrFallback("View all issues in this category");

    public string AccessibleSummary => $"{Title} · {Counts} · {Severity} · {Priority} · {ActionLabel}";

    public string Priority => summary.Priority.DisplayTitle;

    public string Recommendation => summary.Priority.PriorityActionText;
}

public sealed partial class AIHubPageViewModel
{
    public ObservableCollection<AuditCategoryRow> AuditCategories { get; } = new();

    private string _auditTotals = string.Empty;
    private AuditCategorySummary[] _auditCategorySnapshot = [];
    private AuditResult _auditOverviewSnapshot;
    private AuditSummary _auditOverview = AuditSummary.From(Array.Empty<AuditIssueEnhanced>());

    public string AuditTotals
    {
        get => _auditTotals;
        private set => Set(ref _auditTotals, value);
    }

    public string AuditSummaryHeading => "AIHub_AuditOverviewHeading".FormatLocalized("System issue overview · {0}", AuditOverviewRange);

    public string AuditOverviewRange => _auditOverview.RangeDays switch
    {
        2 => "2D", 7 => "1W", 30 => "1M",
        _ => "AIHub_AuditOverviewRecordedRange".GetLocalizedOrFallback("Recorded period"),
    };

    public string AuditOverviewWindow => _auditOverview.ScanStart is { } start && _auditOverview.ScanEnd is { } end && start <= end
        ? "AIHub_AuditOverviewWindow".FormatLocalized("{0:g} – {1:g} · local time", start.ToLocalTime(), end.ToLocalTime())
        : "AIHub_AuditOverviewUnknownRange".GetLocalizedOrFallback("This snapshot has no recorded scan range. Run a full scan to create a period overview.");

    public string AuditOverviewScore => HasAuditData
        ? "AIHub_AuditOverviewScore".FormatLocalized("Health score {0}/100", _auditOverview.HealthScore)
        : PreferChinese() ? "所选时段尚无可核验评分" : "No verified score for this period";

    public int AuditOverviewHighCount => _auditOverview.High;

    public int AuditOverviewMediumCount => _auditOverview.Medium;

    public int AuditOverviewLowCount => _auditOverview.Low;

    public bool HasNoAuditOverviewIssues => _auditOverview.Findings == 0;

    public string AuditOverviewEvidence => "AIHub_AuditOverviewEvidence".FormatLocalized("{0} events checked · {1} issues found", _auditOverview.EventsScanned, _auditOverview.Findings);

    public string AuditOverviewHighLabel => "AIHub_AuditOverviewHighLabel".GetLocalizedOrFallback("High risk");

    public string AuditOverviewMediumLabel => "AIHub_AuditOverviewMediumLabel".GetLocalizedOrFallback("Medium risk");

    public string AuditOverviewLowLabel => "AIHub_AuditOverviewLowLabel".GetLocalizedOrFallback("Low risk");

    public string AuditOverviewHelpLabel => "AIHub_AuditOverviewHelpLabel".GetLocalizedOrFallback("About these counts");

    public string AuditOverviewStatus => _auditOverview.High > 0
        ? "AIHub_AuditOverviewHigh".FormatLocalized("{0} high-priority issue(s) need attention", _auditOverview.High)
        : _auditOverview.Findings > 0
            ? "AIHub_AuditOverviewWarnings".GetLocalizedOrFallback("Review the issues recorded during this period")
            : "AIHub_AuditOverviewNoIssues".GetLocalizedOrFallback("No issues found in the collected events");

    public string AuditOverviewDescription => "AIHub_AuditOverviewDescription".FormatLocalized(
        "{0} events examined · {1} findings across {2} categories · {3} recorded occurrences",
        _auditOverview.EventsScanned, _auditOverview.Findings, _auditOverview.Categories.Count, _auditOverview.Occurrences);

    public string AuditOverviewCoverage
    {
        get
        {
            var parts = new List<string>();
            if (HasAuditRangeCoverage)
            {
                parts.Add(AuditRangeCoverageText);
            }
            else if (_auditOverviewSnapshot?.EvidenceIncomplete == true)
            {
                parts.Add(PreferChinese() ? "当前报告缺少部分时段的证据，无法代表完整扫描范围。" : "Evidence is missing for part of this period; the report does not represent the complete scan window.");
            }
            if (_auditOverviewSnapshot?.SkippedChannels is { Count: > 0 } skipped)
            {
                parts.Add("AIHub_AuditOverviewSkipped".FormatLocalized("Unreadable channels: {0}.", string.Join(", ", skipped)));
            }

            if (_auditOverviewSnapshot?.LimitedChannels is { Count: > 0 } limited)
            {
                parts.Add("AIHub_AuditOverviewLimited".FormatLocalized("Event limit reached: {0}. This period may contain more events.", string.Join(", ", limited)));
            }

            if (IsIncompleteAuditOutcome(_auditOverviewSnapshot?.AnalysisOutcome)
                && (_auditOverviewSnapshot?.AnalysisOutcome != "pending" || !IsAiAnalyzing))
            {
                parts.Add("AIHub_AuditOverviewPartialAi".GetLocalizedOrFallback("AI analysis was incomplete; this overview retains local findings and any validated AI findings."));
            }

            return string.Join(" ", parts);
        }
    }

    public bool HasAuditOverviewCoverage => !string.IsNullOrEmpty(AuditOverviewCoverage);

    public bool HasAuditOverviewScopeChange => HasAuditRangeCoverage;

    public string AuditOverviewNextScanHint => AuditRangeCoverageText;

    public string AuditOccurrencesHint => "AIHub_AuditOccurrencesHint".GetLocalizedOrFallback("Occurrences are counted per finding; multiple findings can refer to the same event. Findings filters only change the detail list.");

    private void RefreshAuditSummary()
    {
        var summary = _auditOverviewSnapshot is null ? AuditSummary.From(AllIssues) : AuditSummary.From(_auditOverviewSnapshot);
        _auditOverview = summary;
        AuditTotals = "AIHub_AuditTotals".FormatLocalized("{0} categories · {1} findings · {2} high / {3} medium / {4} low",
            summary.Categories.Count, summary.Findings, summary.High, summary.Medium, summary.Low);
        if (!_auditCategorySnapshot.SequenceEqual(summary.Categories))
        {
            _auditCategorySnapshot = summary.Categories.ToArray();
            AuditCategories.Clear();
            foreach (var category in summary.Categories)
            {
                AuditCategories.Add(new(category));
            }
        }

        OnPropertyChanged(nameof(AuditSummaryHeading));
        OnPropertyChanged(nameof(AuditOverviewRange));
        OnPropertyChanged(nameof(AuditOverviewWindow));
        OnPropertyChanged(nameof(AuditOverviewScore));
        OnPropertyChanged(nameof(AuditOverviewHighCount));
        OnPropertyChanged(nameof(AuditOverviewMediumCount));
        OnPropertyChanged(nameof(AuditOverviewLowCount));
        OnPropertyChanged(nameof(AuditOverviewEvidence));
        OnPropertyChanged(nameof(HasNoAuditOverviewIssues));
        OnPropertyChanged(nameof(AuditOverviewStatus));
        OnPropertyChanged(nameof(AuditOverviewDescription));
        OnPropertyChanged(nameof(AuditOverviewCoverage));
        OnPropertyChanged(nameof(HasAuditOverviewCoverage));
        OnPropertyChanged(nameof(HasAuditOverviewScopeChange));
        RefreshAuditWorkspace();
    }

    private int _selectedScanModeIndex = 1;
    private bool _fluentAiReportPartial;
    private RelayCommand _startSelectedScanCommand;

    /// <summary>Next scan mode: 0 is quick, 1 is full. Existing results are not changed.</summary>
    public int SelectedScanModeIndex
    {
        get => _selectedScanModeIndex;
        set
        {
            if (value is >= 0 and <= 1 && Set(ref _selectedScanModeIndex, value))
            {
                OnPropertyChanged(nameof(IsFullScanSelected));
                OnPropertyChanged(nameof(SelectedScanLabel));
                OnPropertyChanged(nameof(SelectedScanRangeText));
                OnPropertyChanged(nameof(SelectedScanHint));
                OnPropertyChanged(nameof(HasAuditOverviewScopeChange));
            }
        }
    }

    public bool IsFullScanSelected => SelectedScanModeIndex == 1;

    public string SelectedScanLabel => IsFullScanSelected ? FullScanLabel : FastScanLabel;

    public string SelectedScanRangeText => ScanRangeDescription;

    public string SelectedScanHint
    {
        get
        {
            string action = IsFullScanSelected
                ? "AIHub_FullScan_Hint".GetLocalizedOrFallback("Full scan rereads the selected period, then updates the audit with the current AI model.")
                : "AIHub_QuickRefresh_Hint".GetLocalizedOrFallback("Quick refresh reuses collected events and reads missing intervals and the recent tail.");
            string window = _auditHistoricalSelection && _auditRangeAnchorUtc is DateTime end
                ? "AIHub_AuditScan_HistoricalWindowFormat".FormatLocalized("Historical window ends {0:g}. Select Latest to scan up to now.", end.ToLocalTime())
                : "AIHub_AuditScan_LiveWindow".GetLocalizedOrFallback("The selected period ends at the current time.");
            return $"{action} {SelectedScanRangeText}. {window}";
        }
    }

    public bool CanConfigureAudit => IsEnabled && !IsAuditing && !IsAiAnalyzing && !_disposed;

    public ICommand StartSelectedScanCommand => _startSelectedScanCommand ??= new RelayCommand(
        () =>
        {
            ICommand command = IsFullScanSelected ? FullScanCommand : FastScanCommand;
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
        },
        () => CanConfigureAudit);

    public ObservableCollection<AuditIssueEnhanced> OverviewFindings { get; } = new();

    public bool HasOverviewFindings => OverviewFindings.Count > 0;

    public bool IsHealthGood => HasAuditData && HealthScore >= 80;

    public bool IsHealthWarning => HasAuditData && HealthScore >= 60 && HealthScore < 80;

    public bool IsHealthCritical => HasAuditData && HealthScore < 60;

    public ObservableCollection<AuditIssueEnhanced> RelatedEvidenceFindings { get; } = new();

    public bool HasRelatedEvidence => HasAiReport && HasAuditData && RelatedEvidenceFindings.Count > 0;

    public string OverviewFindingsSummary => "AIHub_Fluent_MatchingFindings".FormatLocalized("{0} matching / {1} total", FilteredIssues.Count, TotalFindings);

    public string FindingsCardHeading => HasAuditData && TotalFindings > 0
        ? $"{FindingsHeading} · {TotalFindings}"
        : FindingsHeading;

    public string AuditHistoryLabel => "AIHub_Fluent_AuditHistory/Content".GetLocalizedOrFallback("Audit history");

    public string OverviewAuditTimestampText => !HasAuditData
        ? SelectedAuditLabel
        : "AIHub_Fluent_LatestAuditFormat".FormatLocalized(
            "Security audit · {0}",
            !string.IsNullOrWhiteSpace(FinishedText) && FinishedText.Length >= 5 ? $"{SelectedAuditLabel} {FinishedText[..5]}" : SelectedAuditLabel);

    public string OverviewHeadline => !HasAuditData
        ? HasAuditRangeCoverage ? (PreferChinese() ? "所选时段需要补全扫描" : "Scan this period to complete its evidence")
            : "AIHub_Fluent_FirstAudit".GetLocalizedOrFallback("Start with your first security audit")
        : HighCount > 0
            ? HighCount == 1
                ? "AIHub_AuditPrioritySingle".GetLocalizedOrFallback("1 issue needs priority attention")
                : "AIHub_AuditPriorityMultiple".FormatLocalized("{0} issues need priority attention", HighCount)
            : HealthVerdict;

    public string OverviewDescription => HasAuditData ? AuditOverviewEvidence : HealthSummaryText;

    public string AiReadinessCompactText => !IsEnabled
        ? "AIHub_Fluent_ModuleDisabled".GetLocalizedOrFallback("Module disabled")
        : AiReadinessLevel switch
        {
            AiReadinessLevel.Ready => "AIHub_Fluent_ServiceReady".GetLocalizedOrFallback("AI service ready"),
            AiReadinessLevel.Degraded => "AIHub_Fluent_ServiceFallback".GetLocalizedOrFallback("Using fallback route"),
            AiReadinessLevel.Unverified => "AIHub_Fluent_ServiceUnverified".GetLocalizedOrFallback("AI service not verified"),
            AiReadinessLevel.NotConfigured => "AIHub_Fluent_ServiceNotConfigured".GetLocalizedOrFallback("AI service not configured"),
            _ => "AIHub_Fluent_ServiceDisabled".GetLocalizedOrFallback("AI service disabled"),
        };

    public string AiSummaryStatusText => IsAuditing
        ? HasAuditData
            ? "AIHub_Fluent_PreviousResult".GetLocalizedOrFallback("Previous result")
            : SidebarWorkflowTitle
        : IsAiAnalyzing
            ? "AIHub_Fluent_Analyzing".GetLocalizedOrFallback("Analyzing")
            : HasAiReport
                ? AuditPhase == ScanPhase.Failed
                    ? "AIHub_Fluent_PreviousAnalysis".GetLocalizedOrFallback("Previous analysis retained")
                    : _fluentAiReportPartial
                        ? "AIHub_Fluent_PartialAnalysis".GetLocalizedOrFallback("Partially completed")
                        : "AIHub_Fluent_AnalysisComplete".GetLocalizedOrFallback("Analysis complete")
                : "AIHub_Fluent_AnalysisPending".GetLocalizedOrFallback("No deep analysis for this result");

    public bool IsAiSummaryCompleted => !IsAuditing && !IsAiAnalyzing && HasAiReport && AuditPhase != ScanPhase.Failed && !_fluentAiReportPartial;

    public bool IsAiSummaryCaution => IsAuditing || IsAiAnalyzing || (HasAiReport && (_fluentAiReportPartial || AuditPhase == ScanPhase.Failed));

    public bool IsAiSummaryNeutral => !IsAiSummaryCompleted && !IsAiSummaryCaution;

    public string AiSummaryText
    {
        get
        {
            if (IsAiAnalyzing && HasAiReport)
            {
                return (PreferChinese() ? "新报告生成中，以下为上次完整结论。" : "A new report is being generated. The previous complete assessment follows.")
                    + Environment.NewLine + AiReportSummaryText;
            }

            if (HasAiReport)
            {
                return AiReportSummaryText;
            }

            if (IsAuditing)
            {
                return AuditStatusText;
            }

            if (!HasAuditData)
            {
                return "AIHub_Fluent_AnalysisAfterScan".GetLocalizedOrFallback("Scan Windows events first, then review their evidence and analysis.");
            }

            if (IsAiAnalyzing || (_auditWasDeepAnalysis && AuditPhase == ScanPhase.Failed))
            {
                return AiStatusText;
            }

            if (!IsAiUsable)
            {
                return AiReadinessText;
            }

            return CanRunDeepAnalysis
                ? "AIHub_Fluent_AnalysisReady".GetLocalizedOrFallback("Analyze the events from this audit to review related evidence and recommendations.")
                : "AIHub_Fluent_AnalysisNeedsEvents".GetLocalizedOrFallback("Stored findings remain available. Run a scan to collect events for deep analysis.");
        }
    }

    public string AiSummaryPoint1
    {
        get
        {
            if (HasAiReport && AiReportFindings.Count > 0 && !string.IsNullOrWhiteSpace(AiReportFindings[0].Recommendation))
            {
                return $"1. {AiReportFindings[0].Recommendation}";
            }

            return string.Empty;
        }
    }

    public string AiSummaryPoint2
    {
        get
        {
            if (HasAiReport && AiReportFindings.Count > 1 && !string.IsNullOrWhiteSpace(AiReportFindings[1].Recommendation))
            {
                return $"2. {AiReportFindings[1].Recommendation}";
            }

            return string.Empty;
        }
    }

    public bool HasAiSummaryPoint1 => !string.IsNullOrWhiteSpace(AiSummaryPoint1);

    public bool HasAiSummaryPoint2 => !string.IsNullOrWhiteSpace(AiSummaryPoint2);

    public string AiSummaryFooterMetaText => !string.IsNullOrWhiteSpace(AiReportMetaText)
        ? AiReportMetaText
        : CurrentEngineAndModelLabel;

    public bool HasAiSummaryFooterMeta => !string.IsNullOrWhiteSpace(AiSummaryFooterMetaText);

    public bool ShowAiSummaryAction => !HasAiReport && HasAuditData;

    public string SelectedDeleteSizeText => FormatBytes(SelectedDeleteBytes);

    public string SelectedMoveSizeText => FormatBytes(SelectedMoveBytes);

    public bool HasSelectedDeletes => SelectedDeleteBytes > 0 || OptimizationCandidates.Any(item => item.IsSelected && item.IsActionable && item.Action == "delete");

    public bool HasSelectedMoves => SelectedMoveBytes > 0 || OptimizationCandidates.Any(item => item.IsSelected && item.IsActionable && item.Action == "move");

    private long SelectedDeleteBytes => OptimizationCandidates.Where(item => item.IsSelected && item.IsActionable && item.Action == "delete").Sum(item => item.SizeInBytes);

    private long SelectedMoveBytes => OptimizationCandidates.Where(item => item.IsSelected && item.IsActionable && item.Action == "move").Sum(item => item.SizeInBytes);

    private long ActionableDeleteBytes => OptimizationCandidates.Where(item => item.IsActionable && item.Action == "delete").Sum(item => item.SizeInBytes);

    private long ReviewOrMoveBytes => _observedOptimizationItems.Concat(OptimizationCandidates).Distinct()
        .Where(item => (item.IsActionable && item.Action == "move") || item.ReviewState == "failed" || !string.IsNullOrEmpty(item.ExecutionError))
        .Sum(item => item.SizeInBytes);

    private long RetainedBytes => _observedOptimizationItems.Concat(OptimizationCandidates).Distinct()
        .Where(item => item.ReviewState == "skipped")
        .Sum(item => item.SizeInBytes);

    public GridLength ActionableBarWidth
    {
        get
        {
            long total = ActionableDeleteBytes + ReviewOrMoveBytes + RetainedBytes;
            if (total <= 0)
            {
                return new GridLength(0, GridUnitType.Star);
            }

            double ratio = Math.Max(ActionableDeleteBytes > 0 ? 0.06 : 0.0, (double)ActionableDeleteBytes / total);
            return new GridLength(ratio * 100.0, GridUnitType.Star);
        }
    }

    public GridLength ReviewBarWidth
    {
        get
        {
            long total = ActionableDeleteBytes + ReviewOrMoveBytes + RetainedBytes;
            if (total <= 0)
            {
                return new GridLength(0, GridUnitType.Star);
            }

            double ratio = Math.Max(ReviewOrMoveBytes > 0 ? 0.06 : 0.0, (double)ReviewOrMoveBytes / total);
            return new GridLength(ratio * 100.0, GridUnitType.Star);
        }
    }

    public GridLength RetainedBarWidth
    {
        get
        {
            long total = ActionableDeleteBytes + ReviewOrMoveBytes + RetainedBytes;
            if (total <= 0)
            {
                return new GridLength(1, GridUnitType.Star);
            }

            double ratio = Math.Max(RetainedBytes > 0 ? 0.06 : 0.0, (double)RetainedBytes / total);
            return new GridLength(ratio * 100.0, GridUnitType.Star);
        }
    }

    public string ActionableLegendText => "AIHub_Fluent_LegendActionableFormat".FormatLocalized("Actionable {0}", FormatBytes(ActionableDeleteBytes));

    public string ReviewLegendText => "AIHub_Fluent_LegendReviewFormat".FormatLocalized("Review {0}", FormatBytes(ReviewOrMoveBytes));

    public string RetainedLegendText => "AIHub_Fluent_LegendRetainedFormat".FormatLocalized("Retained {0}", FormatBytes(RetainedBytes));

    public bool IsOptFilterAll => OptimizationFilterIndex == 0;

    public bool IsOptFilterApproved => OptimizationFilterIndex == 1;

    public bool IsOptFilterSkipped => OptimizationFilterIndex == 2;

    public bool IsOptFilterFailed => OptimizationFilterIndex == 3;

    public bool IsOptFilterSelected => OptimizationFilterIndex == 4;

    public int OptFilterAllCount => _observedOptimizationItems.Concat(OptimizationCandidates).Distinct().Count();

    public int OptFilterApprovedCount => _observedOptimizationItems.Concat(OptimizationCandidates).Distinct().Count(i => i.ReviewState == "approved");

    public int OptFilterSkippedCount => _observedOptimizationItems.Concat(OptimizationCandidates).Distinct().Count(i => i.ReviewState == "skipped");

    public int OptFilterFailedCount => _observedOptimizationItems.Concat(OptimizationCandidates).Distinct().Count(i => i.ReviewState == "failed" || !string.IsNullOrEmpty(i.ExecutionError));

    public int OptFilterSelectedCount => SelectedCandidatesCount;

    public string OptFilterSkippedLabel => OptimizationFilterOptions.Count > 2 ? OptimizationFilterOptions[2] : "AIHub_OptFilter_Skipped".GetLocalizedOrFallback("Retained");

    public string OptFilterFailedLabel => OptimizationFilterOptions.Count > 3 ? OptimizationFilterOptions[3] : "AIHub_OptFilter_Failed".GetLocalizedOrFallback("Needs review");

    public string OptFilterSelectedLabel => OptimizationFilterOptions.Count > 4 ? OptimizationFilterOptions[4] : "AIHub_OptFilter_Selected".GetLocalizedOrFallback("Selected");

    public string TaskPolicyEditorHeading => AiHub.IsWeeklyReportPolicyTab
        ? "AIHub_WeeklyPolicyHeading".GetLocalizedOrFallback("Weekly report instructions")
        : AiHub.IsOptimizationPolicyTab
        ? "AIHub_Fluent_OptPolicyHeading".GetLocalizedOrFallback("Optimization instructions")
        : "AIHub_Fluent_AuditPolicyHeading".GetLocalizedOrFallback("Security audit instructions");

    public string TaskPolicyCharCountText => string.Format(
        CultureInfo.InvariantCulture,
        "{0} / 64000",
        AiHub.CurrentPolicyContent?.Length ?? 0);

    public bool IsTaskPolicyClean => !AiHub.IsCurrentPolicyDirty;

    private void InitializeFluentView()
    {
        PropertyChanged += OnFluentSourceChanged;
        AiReportFindings.CollectionChanged += OnFluentReportFindingsChanged;
        if (AiHub != null)
        {
            AiHub.PropertyChanged += OnFluentAiHubChanged;
        }
    }

    private void DisposeFluentView()
    {
        PropertyChanged -= OnFluentSourceChanged;
        AiReportFindings.CollectionChanged -= OnFluentReportFindingsChanged;
        if (AiHub != null)
        {
            AiHub.PropertyChanged -= OnFluentAiHubChanged;
        }
    }

    private void OnFluentAiHubChanged(object sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(AiHubViewModel.TaskPolicyIndex):
            case nameof(AiHubViewModel.ActivePolicyIndex):
            case nameof(AiHubViewModel.IsOptimizationPolicyTab):
            case nameof(AiHubViewModel.IsSecurityAuditPolicyTab):
            case nameof(AiHubViewModel.IsWeeklyReportPolicyTab):
                OnPropertyChanged(nameof(TaskPolicyEditorHeading));
                OnPropertyChanged(nameof(TaskPolicyCharCountText));
                OnPropertyChanged(nameof(IsTaskPolicyClean));
                break;
            case nameof(AiHubViewModel.CurrentPolicyContent):
            case nameof(AiHubViewModel.IsCurrentPolicyDirty):
            case nameof(AiHubViewModel.HasUnsavedPolicyChanges):
                OnPropertyChanged(nameof(TaskPolicyCharCountText));
                OnPropertyChanged(nameof(IsTaskPolicyClean));
                break;
        }
    }

    private void OnFluentSourceChanged(object sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(ScanRangeDescription):
                OnPropertyChanged(nameof(SelectedScanRangeText));
                OnPropertyChanged(nameof(HasAuditOverviewScopeChange));
                OnPropertyChanged(nameof(AuditOverviewNextScanHint));
                break;
            case nameof(SelectedAuditLabel):
            case nameof(FinishedText):
                OnPropertyChanged(nameof(OverviewAuditTimestampText));
                break;
            case nameof(HealthVerdict):
            case nameof(HealthSummaryText):
            case nameof(HealthScore):
            case nameof(HighCount):
            case nameof(HasAuditData):
            case nameof(AuditPriorityTitle):
            case nameof(TotalFindings):
                OnPropertyChanged(nameof(OverviewAuditTimestampText));
                OnPropertyChanged(nameof(OverviewHeadline));
                OnPropertyChanged(nameof(OverviewDescription));
                OnPropertyChanged(nameof(FindingsCardHeading));
                OnPropertyChanged(nameof(HasRelatedEvidence));
                OnPropertyChanged(nameof(IsHealthGood));
                OnPropertyChanged(nameof(IsHealthWarning));
                OnPropertyChanged(nameof(IsHealthCritical));
                RefreshAiSummary();
                break;
            case nameof(IsEnabled):
            case nameof(AiReadinessLevel):
            case nameof(CurrentEngineAndModelLabel):
                OnPropertyChanged(nameof(AiReadinessCompactText));
                OnPropertyChanged(nameof(AiSummaryFooterMetaText));
                OnPropertyChanged(nameof(HasAiSummaryFooterMeta));
                RefreshAiSummary();
                RefreshAuditActions();
                break;
            case nameof(HasAiReport):
                RebuildRelatedEvidence();
                RefreshAiSummary();
                break;
            case nameof(IsAuditing):
            case nameof(IsAiAnalyzing):
            case nameof(AuditPhase):
            case nameof(AiReportSummaryText):
            case nameof(AiReportMetaText):
            case nameof(AiStatusText):
            case nameof(AuditStatusText):
            case nameof(AiAnalysisRanText):
            case nameof(AiReadinessText):
            case nameof(CanRunDeepAnalysis):
                RefreshAiSummary();
                RefreshAuditDetailText();
                break;
            case nameof(OptimizationFilterIndex):
            case nameof(FilteredCandidatesSummary):
                RefreshFluentOptimizationFilters();
                break;
        }
    }

    private void OnFluentReportFindingsChanged(object sender, NotifyCollectionChangedEventArgs args)
    {
        RebuildRelatedEvidence();
        RefreshAiSummary();
    }

    private void RefreshAiSummary()
    {
        OnPropertyChanged(nameof(AiSummaryText));
        OnPropertyChanged(nameof(AiSummaryStatusText));
        OnPropertyChanged(nameof(IsAiSummaryCompleted));
        OnPropertyChanged(nameof(IsAiSummaryCaution));
        OnPropertyChanged(nameof(IsAiSummaryNeutral));
        OnPropertyChanged(nameof(AiSummaryPoint1));
        OnPropertyChanged(nameof(AiSummaryPoint2));
        OnPropertyChanged(nameof(HasAiSummaryPoint1));
        OnPropertyChanged(nameof(HasAiSummaryPoint2));
        OnPropertyChanged(nameof(AiSummaryFooterMetaText));
        OnPropertyChanged(nameof(HasAiSummaryFooterMeta));
        OnPropertyChanged(nameof(ShowAiSummaryAction));
    }

    private void RefreshFluentCommands()
    {
        _startSelectedScanCommand?.OnCanExecuteChanged();
        OnPropertyChanged(nameof(CanConfigureAudit));
    }

    private void RebuildOverviewFindings()
    {
        var ordered = AuditRiskPolicy.OrderFindings(FilteredIssues).ToList();
        OverviewFindings.Clear();
        foreach (var issue in ordered)
        {
            OverviewFindings.Add(issue);
        }

        RebuildRelatedEvidence();

        OnPropertyChanged(nameof(OverviewFindingsSummary));
        OnPropertyChanged(nameof(HasOverviewFindings));
        OnPropertyChanged(nameof(FindingsCardHeading));
        OnPropertyChanged(nameof(HasRelatedEvidence));
        RefreshAiSummary();
        RefreshAuditWorkspace();
    }

    private void RebuildRelatedEvidence()
    {
        // The AI report refers to the complete audit snapshot. List filters must
        // neither replace its evidence with unrelated findings nor hide a match.
        var related = (HasAiReport
            ? AiReportFindings.Where(report => !string.IsNullOrWhiteSpace(report.EventId))
                .SelectMany(report => AllIssues.Where(issue => string.Equals(issue.EventId, report.EventId, StringComparison.OrdinalIgnoreCase)))
                .Distinct()
                .Take(3)
            : Enumerable.Empty<AuditIssueEnhanced>()).ToList();
        if (!RelatedEvidenceFindings.SequenceEqual(related))
        {
            RelatedEvidenceFindings.Clear();
            foreach (var issue in related)
            {
                RelatedEvidenceFindings.Add(issue);
            }
        }

        OnPropertyChanged(nameof(HasRelatedEvidence));
    }

    private void RefreshFluentSelection()
    {
        OnPropertyChanged(nameof(SelectedDeleteSizeText));
        OnPropertyChanged(nameof(SelectedMoveSizeText));
        OnPropertyChanged(nameof(HasSelectedDeletes));
        OnPropertyChanged(nameof(HasSelectedMoves));
        OnPropertyChanged(nameof(ActionableBarWidth));
        OnPropertyChanged(nameof(ReviewBarWidth));
        OnPropertyChanged(nameof(RetainedBarWidth));
        OnPropertyChanged(nameof(ActionableLegendText));
        OnPropertyChanged(nameof(ReviewLegendText));
        OnPropertyChanged(nameof(RetainedLegendText));
        RefreshFluentOptimizationFilters();
    }

    private void RefreshFluentOptimizationFilters()
    {
        OnPropertyChanged(nameof(IsOptFilterAll));
        OnPropertyChanged(nameof(IsOptFilterApproved));
        OnPropertyChanged(nameof(IsOptFilterSkipped));
        OnPropertyChanged(nameof(IsOptFilterFailed));
        OnPropertyChanged(nameof(IsOptFilterSelected));
        OnPropertyChanged(nameof(OptFilterAllCount));
        OnPropertyChanged(nameof(OptFilterApprovedCount));
        OnPropertyChanged(nameof(OptFilterSkippedCount));
        OnPropertyChanged(nameof(OptFilterFailedCount));
        OnPropertyChanged(nameof(OptFilterSelectedCount));
    }
}
