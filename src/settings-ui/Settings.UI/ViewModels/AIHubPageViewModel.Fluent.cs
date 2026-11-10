// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Kit.AiHub.Contract;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library.Helpers;
using Microsoft.UI.Xaml;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AIHubPageViewModel
{
    private int _selectedScanModeIndex = 1;
    private bool _fluentAiReportPartial;
    private RelayCommand _startSelectedScanCommand;
    private RelayCommand _toggleOverviewFindingsCommand;

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
            }
        }
    }

    public bool IsFullScanSelected => SelectedScanModeIndex == 1;

    public string SelectedScanLabel => IsFullScanSelected ? FullScanLabel : FastScanLabel;

    public string SelectedScanRangeText => IsFullScanSelected
        ? ScanRangeDescription
        : "AIHub_Window_Today".GetLocalizedOrFallback("Today");

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

    public ObservableCollection<AuditIssueEnhanced> RelatedEvidenceFindings { get; } = new();

    public bool HasRelatedEvidence => HasAiReport && HasAuditData && RelatedEvidenceFindings.Count > 0;

    public bool CanToggleOverviewFindings => !HasFindingFilters && FilteredIssues.Count > 3;

    public string OverviewFindingsToggleLabel => ShowAllFindings
        ? "AIHub_Fluent_ShowPriority".GetLocalizedOrFallback("Show priority findings")
        : "AIHub_Fluent_ShowAll".GetLocalizedOrFallback("Show all findings");

    public string OverviewFindingsToggleChevron => ShowAllFindings ? "\uE70E" : "\uE70D";

    public string OverviewFindingsSummary => HasFindingFilters
        ? "AIHub_Fluent_MatchingFindings".FormatLocalized("{0} matching / {1} total", FilteredIssues.Count, TotalFindings)
        : "AIHub_Fluent_VisibleFindings".FormatLocalized("Showing {0} / {1} findings", OverviewFindings.Count, TotalFindings);

    public string FindingsCardHeading => HasAuditData && TotalFindings > 0
        ? $"{FindingsHeading} · {TotalFindings}"
        : FindingsHeading;

    public string AuditHistoryLabel => "AIHub_Fluent_AuditHistory/Content".GetLocalizedOrFallback("Audit history");

    public ICommand ToggleOverviewFindingsCommand => _toggleOverviewFindingsCommand ??= new RelayCommand(
        () => SetViewMode(ShowAllFindings ? "Priority" : "All"),
        () => CanToggleOverviewFindings);

    public string OverviewAuditTimestampText => !HasAuditData
        ? SelectedAuditLabel
        : "AIHub_Fluent_LatestAuditFormat".FormatLocalized(
            "Security audit · {0}",
            !string.IsNullOrWhiteSpace(FinishedText) && FinishedText.Length >= 5 ? $"{SelectedAuditLabel} {FinishedText[..5]}" : SelectedAuditLabel);

    public string OverviewHeadline => !HasAuditData
        ? "AIHub_Fluent_FirstAudit".GetLocalizedOrFallback("Start with your first security audit")
        : HighCount > 0
            ? "AIHub_Fluent_PriorityHeadline".FormatLocalized("{0} finding(s) need your attention first", HighCount)
            : HealthVerdict;

    public string OverviewDescription => HasAuditData && HighCount > 0 && HasAuditPriority
        ? (!string.IsNullOrWhiteSpace(AuditPriorityFinding?.PriorityReasonText)
            ? $"{AuditPriorityTitle} — {AuditPriorityFinding.PriorityReasonText}"
            : AuditPriorityTitle)
        : HealthSummaryText;

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

    public string TaskPolicyEditorHeading => AiHub.IsOptimizationPolicyTab
        ? "AIHub_Fluent_OptPolicyHeading".GetLocalizedOrFallback("Optimization instructions")
        : "AIHub_Fluent_AuditPolicyHeading".GetLocalizedOrFallback("Security audit instructions");

    public string TaskPolicyCharCountText => string.Format(
        CultureInfo.InvariantCulture,
        "{0} / 64000",
        AiHub.CurrentPolicyContent?.Length ?? 0);

    public string TaskPolicyScopePrimaryText => AiHub.IsOptimizationPolicyTab
        ? "AIHub_Fluent_OptPolicyScopePrimary".GetLocalizedOrFallback("Governs how candidate files are categorized, cleanup suggestions are formed, and retention boundaries are applied.")
        : "AIHub_Fluent_AuditPolicyScopePrimary".GetLocalizedOrFallback("Governs event analysis priorities, conclusion structure, and recommended action output.");

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
                OnPropertyChanged(nameof(TaskPolicyEditorHeading));
                OnPropertyChanged(nameof(TaskPolicyCharCountText));
                OnPropertyChanged(nameof(TaskPolicyScopePrimaryText));
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
                break;
            case nameof(SelectedAuditLabel):
            case nameof(FinishedText):
                OnPropertyChanged(nameof(OverviewAuditTimestampText));
                break;
            case nameof(HealthVerdict):
            case nameof(HealthSummaryText):
            case nameof(HighCount):
            case nameof(HasAuditData):
            case nameof(AuditPriorityTitle):
            case nameof(TotalFindings):
                OnPropertyChanged(nameof(OverviewAuditTimestampText));
                OnPropertyChanged(nameof(OverviewHeadline));
                OnPropertyChanged(nameof(OverviewDescription));
                OnPropertyChanged(nameof(FindingsCardHeading));
                OnPropertyChanged(nameof(HasRelatedEvidence));
                RefreshAiSummary();
                break;
            case nameof(IsEnabled):
            case nameof(AiReadinessLevel):
            case nameof(CurrentEngineAndModelLabel):
                OnPropertyChanged(nameof(AiReadinessCompactText));
                OnPropertyChanged(nameof(AiSummaryFooterMetaText));
                OnPropertyChanged(nameof(HasAiSummaryFooterMeta));
                RefreshAiSummary();
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
            case nameof(AiReadinessText):
            case nameof(CanRunDeepAnalysis):
                RefreshAiSummary();
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
        var ordered = FilteredIssues.OrderByDescending(issue => issue.IsHigh)
            .ThenByDescending(issue => issue.IsMedium)
            .ThenByDescending(issue => issue.Occurrences)
            .ToList();
        var visible = ShowAllFindings || HasFindingFilters ? ordered.AsEnumerable() : ordered.Take(3);
        OverviewFindings.Clear();
        foreach (var issue in visible)
        {
            OverviewFindings.Add(issue);
        }

        RebuildRelatedEvidence();

        OnPropertyChanged(nameof(OverviewFindingsSummary));
        OnPropertyChanged(nameof(CanToggleOverviewFindings));
        OnPropertyChanged(nameof(OverviewFindingsToggleLabel));
        OnPropertyChanged(nameof(OverviewFindingsToggleChevron));
        OnPropertyChanged(nameof(FindingsCardHeading));
        OnPropertyChanged(nameof(HasRelatedEvidence));
        RefreshAiSummary();
        _toggleOverviewFindingsCommand?.OnCanExecuteChanged();
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
