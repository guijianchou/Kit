// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.Security;
using Kit.AiHub.Storage;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.AIHubLib.Storage;
using Kit.GPOWrapper;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Library.Interfaces;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.ViewModels;

public sealed class FindingSection : Observable
{
    private const double BarWidth = 96;
    private const double MinSegmentWidth = 6;
    private bool _isExpanded;

    public FindingSection(string name, IReadOnlyList<AuditIssueEnhanced> issues, bool isExpanded)
    {
        Name = name;
        Issues = new ObservableCollection<AuditIssueEnhanced>(issues);
        HighCount = issues.Count(issue => issue.IsHigh);
        MediumCount = issues.Count(issue => issue.IsMedium);
        LowCount = issues.Count(issue => issue.IsLow);
        _isExpanded = isExpanded;

        int total = Math.Max(1, issues.Count);
        HighBarWidth = SegmentWidth(HighCount, total);
        MediumBarWidth = SegmentWidth(MediumCount, total);
        LowBarWidth = SegmentWidth(LowCount, total);

        var parts = new List<string>();
        if (HighCount > 0)
        {
            parts.Add("AIHub_SeveritySummary_HighFormat".FormatLocalized("{0} high", HighCount));
        }

        if (MediumCount > 0)
        {
            parts.Add("AIHub_SeveritySummary_MediumFormat".FormatLocalized("{0} medium", MediumCount));
        }

        if (LowCount > 0)
        {
            parts.Add("AIHub_SeveritySummary_LowFormat".FormatLocalized("{0} low", LowCount));
        }

        SeveritySummary = string.Join(", ", parts);
    }

    public string Name { get; }
    public ObservableCollection<AuditIssueEnhanced> Issues { get; }
    public int Count => Issues.Count;
    public int HighCount { get; }
    public int MediumCount { get; }
    public int LowCount { get; }
    public bool HasHigh => HighCount > 0;
    public bool HasMedium => MediumCount > 0;
    public bool HasLow => LowCount > 0;
    public double HighBarWidth { get; }
    public double MediumBarWidth { get; }
    public double LowBarWidth { get; }
    public string SeveritySummary { get; }

    public string CountLabel
    {
        get
        {
            return Count == 1
                ? "AIHub_FindingCount_Single".GetLocalizedOrFallback("1 finding")
                : "AIHub_FindingCount_PluralFormat".FormatLocalized("{0} findings", Count);
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    private static double SegmentWidth(int count, int total)
    {
        if (count == 0)
        {
            return 0;
        }

        return Math.Max(MinSegmentWidth, Math.Round(BarWidth * count / total));
    }
}

/// <summary>
/// One AI-produced advisory finding shown in the Security Audit AI report panel.
/// The AI engine only ever returns advisory text; no action is executed.
/// </summary>
public sealed class AiReportFinding
{
    public AiReportFinding(string title, string severityLabel, string description, string recommendation, string eventId)
    {
        Title = title;
        Severity = severityLabel;
        Description = description;
        Recommendation = recommendation;
        EventId = eventId;
    }

    public string Title { get; }

    public string Severity { get; }

    public string Description { get; }

    public string Recommendation { get; }

    public string EventId { get; }

    public string SeverityLabel => Severity;
}

public sealed class AIHubPageViewModel : Observable, IDisposable
{
    private static readonly char[] LineSeparators = ['\r', '\n'];

    private readonly ISettingsRepository<GeneralSettings> _generalSettingsRepository;
    private readonly ISettingsRepository<AIHubSettings> _moduleSettingsRepository;
    private readonly Func<string, int> _ipcSendMethod;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly GpoRuleConfigured _gpoConfiguration;
    private readonly CacheCleanupService _cacheCleanupService = new();
    private readonly OptimizationAnalysisService _optimizationAnalysisService = new();
    private readonly DownloadOrganizerService _downloadOrganizerService = new();
    private readonly AuditHistoryStorage _auditHistoryStorage = new();
    private readonly SecurityPolicyService _securityService = new();
    private readonly ObservableCollection<AiReportFinding> _aiReportFindings = new();
    private readonly List<SecurityEvent> _lastAuditEvents = new();
    private readonly ScanProgressModel _scanProgress = new();

    // Audit stages differ from the optimization scan stages.
    private readonly ScanProgressModel _auditProgress = new(
        new[] { "Collect event logs", "Analyze findings", "Finish" });

    private DateTime? _lastCompletedAuditUtc;
    private bool _hasAuditResult;
    private ScanPhase _scanPhase = ScanPhase.Idle;
    private ScanPhase _auditPhase = ScanPhase.Idle;
    private double? _auditDetailedPercent;
    private bool _auditWasDeepAnalysis;
    private bool _auditIncomplete;
    private string _aiAnalysisRanText = string.Empty;
    private AiReadinessLevel _aiReadinessLevel = AiReadinessLevel.Unverified;
    private string _aiReadinessText = string.Empty;
    private CancellationTokenSource _auditCts;
    private CancellationTokenSource _optimizationCts;
    private CancellationTokenSource _readinessCts;
    private bool _isCheckingAiReadiness;
    private bool _disposed;
    private int _activeTabIndex;

    // Security Audit state
    private int _healthScore = 100;
    private string _healthScoreGrade = "AIHub_HealthScoreGrade_Good".GetLocalizedOrFallback("Good");
    private int _highCount;
    private int _mediumCount;
    private int _lowCount;
    private string _auditTimestampText = "AIHub_AuditTimestamp_NotScanned".GetLocalizedOrFallback("Not scanned yet");
    private int _selectedScopeIndex; // 0 = 2d, 1 = 1w, 2 = 1mo
    private string _severityFilter = "All"; // "All", "High", "Medium", "Low"
    private string _sourceFilter = "All"; // "All", "Application", "Security", "System", "Setup", "ForwardedEvents"
    private bool _isPriorityView;
    private bool _showAllFindings = true;
    private string _findingSearchText = string.Empty;
    private bool _isAuditing;
    private string _auditStatusText = "AIHub_AuditStatus_Ready".GetLocalizedOrFallback("Ready");
    private bool _isAiAnalyzing;
    private string _aiStatusText = string.Empty;
    private bool _hasAiReport;
    private string _aiReportSummaryText = string.Empty;
    private string _aiReportMetaText = string.Empty;

    private string _healthVerdict = "AIHub_HealthVerdict_NotScanned".GetLocalizedOrFallback("Not scanned yet");
    private string _healthSummaryText = "AIHub_HealthSummary_NotScanned".GetLocalizedOrFallback("Comprehensive event scan has not been performed yet. Click scan above to begin.");
    private string _selectedAuditLabel = "AIHub_SelectedAudit_Latest".GetLocalizedOrFallback("Latest audit");
    private DateTimeOffset? _selectedAuditDate = DateTimeOffset.Now;
    private bool _suppressAuditDateLoad;
    private string _activityScansText = "0";
    private string _activityFindingsText = "0";
    private string _activeDaysText = "0";
    private string _healthAverageText = "—";
    private string _scanTypeText = "AIHub_FastScanLabel".GetLocalizedOrFallback("Fast scan");
    private string _finishedText = "—";
    private string _windowText = "—";
    private string _eventCountText = "AIHub_EventCount_Zero".GetLocalizedOrFallback("0 events");

    // Optimization state
    private bool _isOptimizing;
    private string _optimizationWorkflowStatus = "AIHub_OptimizationWorkflow_Ready".GetLocalizedOrFallback("Ready to scan");
    private int _candidatesCount;
    private long _candidatesBytes;
    private int _selectedCandidatesCount;
    private long _selectedCandidatesBytes;
    private string _downloadsStatusText = "Not scanned yet";
    private int _downloadsItemCount;
    private long _downloadsBytes;
    private string _temporaryFilesStatusText = "Not scanned yet";
    private int _temporaryFilesItemCount;
    private long _temporaryFilesBytes;

    // Status bar
    private string _statusMessage = string.Empty;
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    private string _optimizationStatusMessage = string.Empty;
    private InfoBarSeverity _optimizationStatusSeverity = InfoBarSeverity.Informational;
    private bool _isOptimizationStatusOpen;
    private bool _isStatusOpen;

    public AIHubPageViewModel(
        ISettingsRepository<GeneralSettings> generalSettingsRepository,
        ISettingsRepository<AIHubSettings> moduleSettingsRepository,
        Func<string, int> ipcSendMethod,
        DispatcherQueue dispatcherQueue = null)
    {
        _generalSettingsRepository = generalSettingsRepository ?? throw new ArgumentNullException(nameof(generalSettingsRepository));
        _moduleSettingsRepository = moduleSettingsRepository ?? throw new ArgumentNullException(nameof(moduleSettingsRepository));
        _ipcSendMethod = ipcSendMethod ?? throw new ArgumentNullException(nameof(ipcSendMethod));
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();

        _gpoConfiguration = ModuleGpoHelper.GetModuleGpoConfiguration(ModuleType.AIHub);

        AiHub = new AiHubViewModel(_dispatcherQueue) { TaskPolicyIndex = 0 };

        // Load persisted tab
        try
        {
            _activeTabIndex = _moduleSettingsRepository.SettingsConfig.Properties.ActiveTabIndex.Value;
        }
        catch
        {
            _activeTabIndex = 0;
        }

        // Commands
        FastScanCommand = new RelayCommand(() => RunAudit(fast: true), () => IsEnabled && !_isAuditing && !_isAiAnalyzing);
        FullScanCommand = new RelayCommand(() => RunAudit(fast: false), () => IsEnabled && !_isAuditing && !_isAiAnalyzing);
        SelectLatestCommand = new RelayCommand(LoadRecentAudit, () => IsEnabled && !_isAuditing);
        ReanalyzeRangeCommand = new RelayCommand(() => RunAudit(fast: false), () => IsEnabled && !_isAuditing && !_isAiAnalyzing);
        DeepAnalyzeCommand = new RelayCommand(RunDeepAnalysis, () => CanRunDeepAnalysis);
        CancelAuditCommand = new RelayCommand(() => _auditCts?.Cancel(), () => IsAuditing || IsAiAnalyzing);
        ResetFindingFiltersCommand = new RelayCommand(ResetFindingFilters);
        FilterHighSeverityCommand = new RelayCommand(() => SetSeverityFilter("High"));
        JumpToAiReportCommand = new RelayCommand(() => RequestJumpToAiReport?.Invoke(this, EventArgs.Empty));

        ScanAllCommand = new RelayCommand(RunScanAll, () => IsEnabled && !_isOptimizing);
        ScanDownloadsCommand = new RelayCommand(RunScanDownloads, () => IsEnabled && !_isOptimizing);
        ScanTemporaryFilesCommand = new RelayCommand(RunScanTemporaryFiles, () => IsEnabled && !_isOptimizing);
        SelectAllCandidatesCommand = new RelayCommand(SelectAllCandidates, () => HasCandidates && !_isOptimizing);
        DeselectAllCandidatesCommand = new RelayCommand(DeselectAllCandidates, () => HasCandidates && !_isOptimizing);
        ExecuteOptimizationCommand = new RelayCommand(() => RunExecuteOptimization(), () => CanExecuteOptimization);
        CleanAllCommand = new RelayCommand(() => RunExecuteOptimization(OptimizationCandidates.Where(item => item.Action == "delete").ToList()), () => IsEnabled && !IsOptimizing && OptimizationCandidates.Any(item => item.Action == "delete"));
        OrganizeDownloadsCommand = new RelayCommand(() => RunExecuteOptimization(OptimizationCandidates.Where(item => item.Action == "move").ToList()), () => IsEnabled && !IsOptimizing && OptimizationCandidates.Any(item => item.Action == "move"));
        CancelOptimizationCommand = new RelayCommand(() => _optimizationCts?.Cancel(), () => IsOptimizing);

        // Load cached audit history
        LoadRecentAudit();

        // Seed the indicator from the cached verdict. This deliberately does not probe:
        // opening the page must not start a kernel process, and the constraint is that the
        // AI service is only exercised while the module is enabled.
        RefreshAiReadinessFromCache();

        // Follow the shared service: toggling it on General (or any config change) must be
        // reflected here immediately, not only on the next scan or re-check.
        AiHubEngine.Current.StateChanged += OnEngineStateChanged;
    }

    private static bool PreferChinese() => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// AI service configuration (kernel, endpoints, policy) hosted by this page.
    /// </summary>
    /// <remarks>
    /// Scheduled for relocation to General: the service is shared by every module, so its
    /// settings belong with the base configuration rather than inside one consumer.
    /// </remarks>
    public AiHubViewModel AiHub { get; }

    public bool IsEnabledGpoConfigured => _gpoConfiguration is GpoRuleConfigured.Enabled or GpoRuleConfigured.Disabled;

    public bool IsEnabled
    {
        get
        {
            if (_gpoConfiguration == GpoRuleConfigured.Enabled)
            {
                return true;
            }

            if (_gpoConfiguration == GpoRuleConfigured.Disabled)
            {
                return false;
            }

            return _generalSettingsRepository.SettingsConfig.Enabled.AiHub;
        }
        set
        {
            if (IsEnabledGpoConfigured)
            {
                return;
            }

            if (_generalSettingsRepository.SettingsConfig.Enabled.AiHub != value)
            {
                ModuleHelper.SetIsModuleEnabled(_generalSettingsRepository.SettingsConfig, ModuleType.AIHub, value);

                OnPropertyChanged();
                OnPropertyChanged(nameof(CanToggle));
                OnPropertyChanged(nameof(CanRecheckAiReadiness));

                if (!value)
                {
                    _readinessCts?.Cancel();
                    _auditCts?.Cancel();
                    _optimizationCts?.Cancel();
                }

                RefreshCommands();

                var outgoing = new OutGoingGeneralSettings(_generalSettingsRepository.SettingsConfig);
                _ipcSendMethod(outgoing.ToString());
            }
        }
    }

    public bool CanToggle => !IsEnabledGpoConfigured;

    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set
        {
            if (Set(ref _activeTabIndex, value))
            {
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(StatusSeverity));
                OnPropertyChanged(nameof(IsStatusOpen));
                try
                {
                    _moduleSettingsRepository.SettingsConfig.Properties.ActiveTabIndex.Value = value;
                    var snd = new SndAIHubSettings(_moduleSettingsRepository.SettingsConfig);
                    _ipcSendMethod(snd.ToJsonString());
                }
                catch (Exception ex)
                {
                    Logger.LogError("Failed to persist the AI Hub tab selection", ex);
                }
            }
        }
    }

    // Security Audit Properties
    public int HealthScore
    {
        get => _healthScore;
        private set
        {
            if (Set(ref _healthScore, value))
            {
                OnPropertyChanged(nameof(HealthScoreText));
                OnPropertyChanged(nameof(IsCriticalScore));
            }
        }
    }

    public string HealthScoreGrade
    {
        get => _healthScoreGrade;
        private set => Set(ref _healthScoreGrade, value);
    }

    public string HealthScoreLabel => "AIHub_HealthScoreLabel".GetLocalizedOrFallback("Health score");
    public string HealthScoreText => HasAuditData ? HealthScore.ToString(CultureInfo.CurrentCulture) : "—";

    public bool IsCriticalScore => HasAuditData && HealthScore < 60 && HighCount > 0;
    public string FilterHighQuickActionLabel => "AIHub_FilterHighQuickActionLabel".GetLocalizedOrFallback("Filter High findings");
    public string FilterHighQuickActionHint => "AIHub_FilterHighQuickActionHint".GetLocalizedOrFallback("Filter high-severity security and reliability events");

    public string ViewAiReportLabel => "AIHub_ViewAiReportLabel".GetLocalizedOrFallback("View AI report");
    public string JumpToAiReportHint => "AIHub_JumpToAiReportHint".GetLocalizedOrFallback("Jump directly to the AI diagnostic report below");
    public string CopyReportLabel => "AIHub_CopyReportLabel".GetLocalizedOrFallback("Copy report");
    public string CopyAiReportHint => "AIHub_CopyAiReportHint".GetLocalizedOrFallback("Copy AI diagnostic findings and recommendations to clipboard");

    public event EventHandler RequestJumpToAiReport;
    public ICommand FilterHighSeverityCommand { get; }
    public ICommand JumpToAiReportCommand { get; }

    public string HealthVerdict
    {
        get => _healthVerdict;
        private set => Set(ref _healthVerdict, value);
    }

    public string HealthSummaryText
    {
        get => _healthSummaryText;
        private set => Set(ref _healthSummaryText, value);
    }

    public string SelectedAuditLabel
    {
        get => _selectedAuditLabel;
        private set => Set(ref _selectedAuditLabel, value);
    }

    public DateTimeOffset? SelectedAuditDate
    {
        get => _selectedAuditDate;
        set
        {
            if (Set(ref _selectedAuditDate, value) && !_suppressAuditDateLoad && value.HasValue)
            {
                _ = LoadAuditForDateAsync(value.Value.LocalDateTime.Date);
            }
        }
    }

    public DateTimeOffset LatestSelectableAuditDate => DateTimeOffset.Now;

    public int HighCount
    {
        get => _highCount;
        private set
        {
            if (Set(ref _highCount, value))
            {
                OnPropertyChanged(nameof(IsCriticalScore));
            }
        }
    }

    public int MediumCount
    {
        get => _mediumCount;
        private set => Set(ref _mediumCount, value);
    }

    public int LowCount
    {
        get => _lowCount;
        private set => Set(ref _lowCount, value);
    }

    public int TotalFindings => AllIssues.Count;

    public string AuditTimestampText
    {
        get => _auditTimestampText;
        private set => Set(ref _auditTimestampText, value);
    }

    /// <summary>
    /// Selected range: 0 = 2 days, 1 = 1 week, 2 = 1 month. Once an audit exists,
    /// refresh its local findings without issuing AI requests or saving another history entry.
    /// </summary>
    public int FullScanRangeIndex
    {
        get => _selectedScopeIndex;
        set
        {
            if (value is >= 0 and <= 2 && Set(ref _selectedScopeIndex, value))
            {
                OnPropertyChanged(nameof(ScanRangeDescription));

                if (HasAuditData || IsAuditing || IsAiAnalyzing)
                {
                    RefreshSelectedRange();
                }
            }
        }
    }

    private void RefreshSelectedRange()
    {
        _auditCts?.Cancel();
        IsAuditing = false;
        IsAiAnalyzing = false;
        AllIssues.Clear();
        _hasAuditResult = false;
        lock (_lastAuditEvents)
        {
            _lastAuditEvents.Clear();
        }

        _aiReportFindings.Clear();
        HasAiReport = false;
        AiReportSummaryText = AiReportMetaText = AiStatusText = string.Empty;
        HighCount = MediumCount = LowCount = 0;
        HealthScoreGrade = "—";
        ActivityFindingsText = "0";
        EventCountText = FinishedText = "—";
        WindowText = ScanRangeDescription;
        ScanTypeText = "AIHub_ScanType_LocalFindings".GetLocalizedOrFallback("Local findings");
        HealthVerdict = "AIHub_HealthVerdict_Updating".GetLocalizedOrFallback("Updating");
        HealthSummaryText = "AIHub_HealthSummary_Updating".GetLocalizedOrFallback("Reading events and updating findings for the selected range.");
        AuditTimestampText = HealthSummaryText;
        RebuildSections();
        RunAudit(fast: false, rangeRefresh: true);
    }

    /// <summary>Selected range as a bilingual label for the status text.</summary>
    public string ScanRangeDescription => _selectedScopeIndex switch
    {
        1 => "AIHub_ScanRange_1w".GetLocalizedOrFallback("Past 1 week"),
        2 => "AIHub_ScanRange_1mo".GetLocalizedOrFallback("Past 1 month"),
        _ => "AIHub_ScanRange_2d".GetLocalizedOrFallback("Past 2 days"),
    };

    /// <summary>
    /// Audit scan mode: 0 = extended (standard privileges), 1 = full (elevated; also
    /// reads the Security and Windows Firewall channels).
    /// </summary>
    public int AuditModeIndex
    {
        get => _moduleSettingsRepository.SettingsConfig.Properties.AuditMode.Value;
        set
        {
            if (_moduleSettingsRepository.SettingsConfig.Properties.AuditMode.Value != value)
            {
                _moduleSettingsRepository.SettingsConfig.Properties.AuditMode.Value = value;
                OnPropertyChanged(nameof(AuditModeIndex));
                OnPropertyChanged(nameof(IsFullAuditMode));
                OnPropertyChanged(nameof(ScopeHint));
                OnPropertyChanged(nameof(AuditModeHint));

                try
                {
                    var snd = new SndAIHubSettings(_moduleSettingsRepository.SettingsConfig);
                    _ipcSendMethod(snd.ToJsonString());
                }
                catch
                {
                    // Persisting the mode is best-effort; the next scan picks up the change.
                }

                // The mode change is persisted only; switching modes must not start a scan
                // by itself, otherwise entering the tab fired one on binding initialization.
                if (!IsAuditing)
                {
                    AuditPhase = ScanPhase.Idle;
                }
            }
        }
    }

    public bool IsFullAuditMode => AuditModeIndex == 1;

    /// <summary>
    /// Full mode is only useful when elevated; without elevation the Security channel
    /// cannot be read, so the toggle stays disabled rather than silently degrading.
    /// </summary>
    public bool CanUseFullAuditMode => App.IsElevated;

    public string AuditModeLabel => "AIHub_AuditModeLabel".GetLocalizedOrFallback("Audit mode");

    public string AuditModeHint
    {
        get
        {
            if (!IsFullAuditMode)
            {
                return "AIHub_AuditModeHint_Extended".GetLocalizedOrFallback("Extended: standard privileges, reads System/Application/Setup/ForwardedEvents");
            }

            return App.IsElevated
                ? "AIHub_AuditModeHint_FullElevated".GetLocalizedOrFallback("Full: additionally reads the Security and Firewall channels")
                : "AIHub_AuditModeHint_FullNotElevated".GetLocalizedOrFallback("Full mode needs elevation; the Security channel is unreadable without it");
        }
    }

    public string FullScanRangeHint => "AIHub_FullScanRangeHint".GetLocalizedOrFallback("Select a time range and refresh local findings");

    public string RangeOption1DayLabel => "AIHub_RangeOption1DayLabel".GetLocalizedOrFallback("2d");

    public string RangeOption2DaysLabel => "AIHub_RangeOption2DaysLabel".GetLocalizedOrFallback("1w");

    public string RangeOption1WeekLabel => "AIHub_RangeOption1WeekLabel".GetLocalizedOrFallback("1mo");

    public string ScanOptionsLabel => "AIHub_ScanOptionsLabel".GetLocalizedOrFallback("Scan options");

    public string ReanalyzeRangeHint => "AIHub_ReanalyzeRangeHint".GetLocalizedOrFallback("Reanalyze events in selected range");

    /// <summary>Summarizes the active scan mode; reflects the real setting, not a fixed claim.</summary>
    public string ScopeHint => IsFullAuditMode
        ? "AIHub_ScopeHint_Full".GetLocalizedOrFallback("Full mode · Elevation required · Includes Security and Firewall")
        : "AIHub_ScopeHint_Extended".GetLocalizedOrFallback("Extended mode · No elevation · Skips Security");

    public string FindingsHeading => "AIHub_FindingsHeading".GetLocalizedOrFallback("Findings");

    public string FindingsSummaryText => "AIHub_FindingsSummaryFormat".FormatLocalized("{0} findings recorded under current filter", FindingSections.Sum(s => s.Count));

    public string AllFindingsLabel => "AIHub_AllFindingsLabelFormat".FormatLocalized("All findings ({0})", AllIssues.Count);

    public string PriorityPreviewLabel => "AIHub_PriorityPreviewLabelFormat".FormatLocalized("Top {0} priority action items", PriorityFindings.Count);

    public string OverviewLabel => "AIHub_OverviewLabelFormat".FormatLocalized("All ({0})", AllIssues.Count);
    public string ApplicationLabel => GetSourceFilterLabel("Application", "AIHub_SourceFilter_Application".GetLocalizedOrFallback("Application"));
    public string SecurityLabel => GetSourceFilterLabel("Security", "AIHub_SourceFilter_Security".GetLocalizedOrFallback("Security"));
    public string SystemLabel => GetSourceFilterLabel("System", "AIHub_SourceFilter_System".GetLocalizedOrFallback("System"));
    public string SetupLabel => GetSourceFilterLabel("Setup", "AIHub_SourceFilter_Setup".GetLocalizedOrFallback("Setup"));
    public string ForwardedLabel => GetSourceFilterLabel("ForwardedEvents", "AIHub_SourceFilter_Forwarded".GetLocalizedOrFallback("Forwarded"));

    private string GetSourceFilterLabel(string channel, string displayName)
    {
        int count = AllIssues.Count(i => i.LogName.Equals(channel, StringComparison.OrdinalIgnoreCase) || i.Category.Contains(channel, StringComparison.OrdinalIgnoreCase));
        return $"{displayName} ({count})";
    }

    public string ActivityScansText { get => _activityScansText; private set => Set(ref _activityScansText, value); }
    public string ActivityFindingsText { get => _activityFindingsText; private set => Set(ref _activityFindingsText, value); }
    public string ActiveDaysText { get => _activeDaysText; private set => Set(ref _activeDaysText, value); }
    public string HealthAverageText { get => _healthAverageText; private set => Set(ref _healthAverageText, value); }
    public string ScanTypeText { get => _scanTypeText; private set => Set(ref _scanTypeText, value); }
    public string FinishedText { get => _finishedText; private set => Set(ref _finishedText, value); }
    public string WindowText { get => _windowText; private set => Set(ref _windowText, value); }
    public string EventCountText { get => _eventCountText; private set => Set(ref _eventCountText, value); }

    public bool HasAuditData => _hasAuditResult || AllIssues.Count > 0;
    public bool ShowNoAudit => !HasAuditData && !IsAuditing;
    public bool ShowEmptyIssues => HasAuditData && TotalFindings == 0 && !IsAuditing;
    public bool ShowNoFilterMatches => HasAuditData && FindingSections.Count == 0 && TotalFindings > 0;
    public string EmptyFindingsTitle => "AIHub_EmptyFindingsTitle".GetLocalizedOrFallback("No security or reliability anomalies detected");
    public string EmptyFindingsDescription => "AIHub_EmptyFindingsDescription".GetLocalizedOrFallback("No anomalies were found in the selected period and the logs read. You can scan a different period.");

    // Pipeline Labels
    public string PipelineTitle => "AIHub_PipelineTitle".GetLocalizedOrFallback("AI Hub Pipeline");
    public string PipelinePolicyText => "AIHub_PipelinePolicyText".GetLocalizedOrFallback("Security policy + AGENTS.md");
    public string PipelineSummaryText => "AIHub_PipelineSummaryText".GetLocalizedOrFallback("Analysis summary");
    public string PipelineActionText => "AIHub_PipelineActionText".GetLocalizedOrFallback("Suggested action");

    // Tab Labels
    public string SecurityAuditTabLabel => "AIHub_SecurityAuditTabLabel".GetLocalizedOrFallback("Security Audit");
    public string OptimizationTabLabel => "AIHub_OptimizationTabLabel".GetLocalizedOrFallback("Optimization");

    // Audit Header & Action Labels
    public string SecurityAuditTitle => "AIHub_SecurityAuditTitle".GetLocalizedOrFallback("Windows Event Security Audit");
    public string FastScanLabel => "AIHub_FastScanLabel".GetLocalizedOrFallback("Fast scan");
    public string FullScanLabel => "AIHub_FullScanLabel".GetLocalizedOrFallback("Full scan");
    public string ReanalyzeLabel => "AIHub_ReanalyzeLabel".GetLocalizedOrFallback("Reanalyze selected range");

    /// <summary>Label for the AI deep-analysis action (runs the configured kernel).</summary>
    public string DeepAnalyzeLabel => "AIHub_DeepAnalyzeLabel".GetLocalizedOrFallback("AI deep analysis");

    /// <summary>Explains what the deep-analysis action does and when it is unavailable.</summary>
    public string DeepAnalyzeHint => "AIHub_DeepAnalyzeHint".GetLocalizedOrFallback("Sends the current findings (highest severity first, up to 12) to the configured AI kernel for supplementary diagnostics under the security-audit policy. No action is executed automatically.");

    // Overview Card
    public string HealthOverviewLabel => "AIHub_HealthOverviewLabel".GetLocalizedOrFallback("Health overview");
    public string LatestAuditLabel => "AIHub_LatestAuditLabel".GetLocalizedOrFallback("Latest audit");
    public string FindingsInAuditLabel => "AIHub_FindingsInAuditLabel".GetLocalizedOrFallback("Findings in this audit");
    public string HighLabel => "AIHub_HighLabel".GetLocalizedOrFallback("High");
    public string MediumLabel => "AIHub_MediumLabel".GetLocalizedOrFallback("Medium");
    public string LowLabel => "AIHub_LowLabel".GetLocalizedOrFallback("Low");

    // Activity Stats
    public string AuditActivityLabel => "AIHub_AuditActivityLabel".GetLocalizedOrFallback("Audit activity");
    public string AuditsLabel => "AIHub_AuditsLabel".GetLocalizedOrFallback("Audits");
    public string RecordedFindingsLabel => "AIHub_RecordedFindingsLabel".GetLocalizedOrFallback("Recorded findings");
    public string ActiveDaysLabel => "AIHub_ActiveDaysLabel".GetLocalizedOrFallback("Active days");
    public string HealthAverageLabel => "AIHub_HealthAverageLabel".GetLocalizedOrFallback("Health average");

    // Details Grid
    public string ScanLabel => "AIHub_ScanLabel".GetLocalizedOrFallback("Scan");
    public string FinishedLabel => "AIHub_FinishedLabel".GetLocalizedOrFallback("Finished");
    public string WindowLabel => "AIHub_WindowLabel".GetLocalizedOrFallback("Window");
    public string EventsReadLabel => "AIHub_EventsReadLabel".GetLocalizedOrFallback("Events read");

    // Filters & Search
    public string FilterAllLabel => "AIHub_FilterAllLabel".GetLocalizedOrFallback("All");
    public string PriorityViewLabel => "AIHub_PriorityViewLabel".GetLocalizedOrFallback("Priority view");
    public string SearchPlaceholder => "AIHub_SearchPlaceholder".GetLocalizedOrFallback("Search event ID, provider or title...");
    public string NoMatchingFindingsTitle => "AIHub_NoMatchingFindingsTitle".GetLocalizedOrFallback("No matching findings");
    public string NoMatchingFindingsDescription => "AIHub_NoMatchingFindingsDescription".GetLocalizedOrFallback("Try a different search keyword, channel, or severity filter.");
    public string ResetFindingFiltersLabel => "AIHub_ResetFindingFiltersLabel".GetLocalizedOrFallback("Clear filters");
    public bool HasFindingFilters => _severityFilter != "All" || _sourceFilter != "All" || !string.IsNullOrWhiteSpace(_findingSearchText);
    public ICommand ResetFindingFiltersCommand { get; }

    // Optimization Tab Labels
    public string OptimizationDescription => "AIHub_OptimizationDescription".GetLocalizedOrFallback("Organize loose downloads into five categories and review old temporary/cache files.");
    public string ScanAllLabel => "AIHub_ScanAllLabel".GetLocalizedOrFallback("Rescan");
    public string OptimizationWorkflowTitle => "AIHub_OptimizationWorkflowTitle".GetLocalizedOrFallback("Optimization workflow");
    public string OptimizationWorkflowDescription => "AIHub_OptimizationWorkflowDescription".GetLocalizedOrFallback("Scan, review and confirm before any write action.");
    public string CandidatesLabel => "AIHub_CandidatesLabel".GetLocalizedOrFallback("Candidates");
    public string DetectedObjectsLabel => "AIHub_DetectedObjectsLabel".GetLocalizedOrFallback("Detected objects");
    public string SelectedItemsLabel => "AIHub_SelectedItemsLabel".GetLocalizedOrFallback("Selected items");
    public string StagedForExecutionLabel => "AIHub_StagedForExecutionLabel".GetLocalizedOrFallback("Staged for execution");
    public string SafetyBoundaryLabel => "AIHub_SafetyBoundaryLabel".GetLocalizedOrFallback("Safety boundary");
    public string DownloadsLabel => "AIHub_DownloadsLabel".GetLocalizedOrFallback("Downloads");
    public string DownloadsRootOnlyLabel => "AIHub_DownloadsRootOnlyLabel".GetLocalizedOrFallback("Downloads root only");
    public string DownloadsDescription => "AIHub_DownloadsDescription".GetLocalizedOrFallback("Move loose files unchanged for 10 minutes into Documents, Compressed, Programs, Music or Video. Leave images, unknown types and incomplete downloads in place.");
    public string TemporaryFilesLabel => "AIHub_TemporaryFilesLabel".GetLocalizedOrFallback("Caches & temporary files");
    public string WhitelistOnlyLabel => "AIHub_WhitelistOnlyLabel".GetLocalizedOrFallback("Whitelist only");
    public string CacheDescription => "AIHub_CacheDescription".GetLocalizedOrFallback("Review whitelisted cache files and Temp .tmp/.temp files unchanged for 7 days. Skip files in use and links; delete confirmed files individually.");
    public string ScanButtonLabel => "AIHub_ScanButtonLabel".GetLocalizedOrFallback("Scan");
    public string SelectAllLabel => "AIHub_SelectAllLabel".GetLocalizedOrFallback("Select all");
    public string DeselectAllLabel => "AIHub_DeselectAllLabel".GetLocalizedOrFallback("Deselect all");
    public string ConfirmAndExecuteLabel => "AIHub_ConfirmAndExecuteLabel".GetLocalizedOrFallback("Confirm and execute");
    public string ReadOnlyScanTitle => "AIHub_ReadOnlyScanTitle".GetLocalizedOrFallback("Read-only scan");
    public string ReadOnlyScanDescription => "AIHub_ReadOnlyScanDescription".GetLocalizedOrFallback("Only file metadata is read before confirmation.");
    public string PathBoundariesTitle => "AIHub_PathBoundariesTitle".GetLocalizedOrFallback("Path boundaries");
    public string PathBoundariesDescription => "AIHub_PathBoundariesDescription".GetLocalizedOrFallback("Downloads and cache whitelists are validated again.");
    public string RecoverableCleanupTitle => "AIHub_RecoverableCleanupTitle".GetLocalizedOrFallback("Reviewed snapshot only");
    public string RecoverableCleanupDescription => "AIHub_RecoverableCleanupDescription".GetLocalizedOrFallback("Scope, age and changes are rechecked per file before deletion; new, changed, locked or inaccessible files are skipped.");

    public AuditIssueEnhanced AuditPriorityFinding => AllIssues.OrderByDescending(issue => issue.IsHigh)
        .ThenByDescending(issue => issue.IsMedium).ThenByDescending(issue => issue.Occurrences).FirstOrDefault();
    public bool HasAuditPriority => AuditPriorityFinding != null;
    public string PriorityCalloutBadgeText => "AIHub_PriorityCalloutBadgeText".GetLocalizedOrFallback("Review first");
    public string AuditPriorityTitle => AuditPriorityFinding?.DisplayTitle ?? string.Empty;
    public string AuditPriorityAction => AuditPriorityFinding?.PriorityActionText ?? string.Empty;
    public string AuditSummaryEmptyTitle => HasAuditData ? EmptyFindingsTitle : "AIHub_AuditSummaryEmptyTitle_Ready".GetLocalizedOrFallback("Ready for your first scan");
    public string AuditSummaryEmptyText => HasAuditData ? EmptyFindingsDescription : "AIHub_AuditSummaryEmptyText_Ready".GetLocalizedOrFallback("Scan to see findings and suggested actions here.");
    public bool ShowPriorityFindings => IsPriorityView && PriorityFindings.Count > 0;

    // Filters
    public bool IsFilterAll => _severityFilter == "All";
    public bool IsFilterHigh => _severityFilter == "High";
    public bool IsFilterMedium => _severityFilter == "Medium";
    public bool IsFilterLow => _severityFilter == "Low";

    public bool IsSourceAll => _sourceFilter == "All";
    public bool IsSourceApplication => _sourceFilter == "Application";
    public bool IsSourceSecurity => _sourceFilter == "Security";
    public bool IsSourceSystem => _sourceFilter == "System";
    public bool IsSourceSetup => _sourceFilter == "Setup";
    public bool IsSourceForwarded => _sourceFilter == "ForwardedEvents";

    public bool IsPriorityView
    {
        get => _isPriorityView;
        set
        {
            if (Set(ref _isPriorityView, value))
            {
                OnPropertyChanged(nameof(ShowPriorityFindings));
                RebuildSections();
            }
        }
    }

    public bool ShowAllFindings
    {
        get => _showAllFindings;
        set
        {
            if (Set(ref _showAllFindings, value))
            {
                RebuildSections();
            }
        }
    }

    public string FindingSearchText
    {
        get => _findingSearchText;
        set
        {
            if (Set(ref _findingSearchText, value))
            {
                RebuildSections();
            }
        }
    }

    public string SearchQuery
    {
        get => _findingSearchText;
        set
        {
            if (Set(ref _findingSearchText, value))
            {
                RebuildSections();
            }
        }
    }

    public void SetSeverityFilter(string tag)
    {
        _severityFilter = tag;
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterHigh));
        OnPropertyChanged(nameof(IsFilterMedium));
        OnPropertyChanged(nameof(IsFilterLow));
        RebuildSections(expandMatches: true);
    }

    public void ResetFindingFilters()
    {
        _severityFilter = "All";
        _sourceFilter = "All";
        _findingSearchText = string.Empty;
        foreach (string property in new[]
        {
            nameof(IsFilterAll), nameof(IsFilterHigh), nameof(IsFilterMedium), nameof(IsFilterLow),
            nameof(IsSourceAll), nameof(IsSourceApplication), nameof(IsSourceSecurity), nameof(IsSourceSystem),
            nameof(IsSourceSetup), nameof(IsSourceForwarded), nameof(FindingSearchText), nameof(SearchQuery),
        })
        {
            OnPropertyChanged(property);
        }

        RebuildSections(expandMatches: true);
    }

    public void SetSourceFilter(string tag)
    {
        _sourceFilter = tag;
        OnPropertyChanged(nameof(IsSourceAll));
        OnPropertyChanged(nameof(IsSourceApplication));
        OnPropertyChanged(nameof(IsSourceSecurity));
        OnPropertyChanged(nameof(IsSourceSystem));
        OnPropertyChanged(nameof(IsSourceSetup));
        OnPropertyChanged(nameof(IsSourceForwarded));
        RebuildSections(expandMatches: true);
    }

    public void SetViewMode(string tag)
    {
        if (tag == "Priority")
        {
            _isPriorityView = true;
            _showAllFindings = false;
        }
        else
        {
            _isPriorityView = false;
            _showAllFindings = true;
        }

        OnPropertyChanged(nameof(IsPriorityView));
        OnPropertyChanged(nameof(ShowAllFindings));
        OnPropertyChanged(nameof(ShowPriorityFindings));
        RebuildSections();
    }

    public bool IsAuditing
    {
        get => _isAuditing;
        private set
        {
            if (Set(ref _isAuditing, value))
            {
                RefreshCommands();
                OnPropertyChanged(nameof(CanRunDeepAnalysis));
                OnPropertyChanged(nameof(ShowNoAudit));
                OnPropertyChanged(nameof(ShowEmptyIssues));
                PublishScanProgress();
            }
        }
    }

    public string AuditStatusText
    {
        get => _auditStatusText;
        private set => Set(ref _auditStatusText, value);
    }

    public ObservableCollection<AuditIssueEnhanced> AllIssues { get; } = new();

    public ObservableCollection<AuditIssueEnhanced> FilteredIssues { get; } = new();

    public ObservableCollection<FindingSection> FindingSections { get; } = new();

    public ObservableCollection<AuditIssueEnhanced> PriorityFindings { get; } = new();

    public ICommand FastScanCommand { get; }

    public ICommand FullScanCommand { get; }

    public ICommand SelectLatestCommand { get; }

    public ICommand ReanalyzeRangeCommand { get; }

    /// <summary>Runs the configured AI kernel over the highest-severity findings.</summary>
    public ICommand DeepAnalyzeCommand { get; }

    public ICommand CancelAuditCommand { get; }

    public string CancelTaskLabel => "AIHub_CancelTaskLabel".GetLocalizedOrFallback("Cancel task");

    // Optimization Properties (1:1 with Screenshot)
    public bool IsOptimizing
    {
        get => _isOptimizing;
        private set
        {
            if (Set(ref _isOptimizing, value))
            {
                foreach (var group in OptimizationCategories)
                {
                    group.IsEnabled = IsEnabled && !value && group.ApprovedCount > 0;
                }

                if (!value)
                {
                    IsExecutingOptimization = false;
                }

                RefreshCommands();
            }
        }
    }

    public string OptimizationWorkflowStatus
    {
        get => _optimizationWorkflowStatus;
        private set => Set(ref _optimizationWorkflowStatus, value);
    }

    /// <summary>Current lifecycle stage of the optimization scan.</summary>
    public ScanPhase ScanPhase
    {
        get => _scanPhase;
        private set
        {
            if (Set(ref _scanPhase, value))
            {
                OnPropertyChanged(nameof(IsScanning));
                OnPropertyChanged(nameof(IsWorkflowVisible));
                OnPropertyChanged(nameof(ScanPhaseText));
                PublishScanProgress();
            }
        }
    }

    /// <summary>True while a scan runs, so the UI can show a progress bar.</summary>
    public bool IsScanning => ScanPhase == ScanPhase.Scanning;

    /// <summary>True once a scan produced results worth showing.</summary>
    public bool IsWorkflowVisible => ScanPhase is ScanPhase.Scanning or ScanPhase.SelectingTargets or ScanPhase.ExecutionPending or ScanPhase.Failed;

    /// <summary>Bilingual phase label shown beside the progress bar.</summary>
    public string ScanPhaseText => ScanPhase switch
    {
        ScanPhase.Scanning => "AIHub_ScanPhase_Scanning".GetLocalizedOrFallback("Scanning"),
        ScanPhase.SelectingTargets => "AIHub_ScanPhase_ReviewResults".GetLocalizedOrFallback("Review scan results"),
        ScanPhase.ExecutionPending => "AIHub_ScanPhase_Applying".GetLocalizedOrFallback("Applying changes"),
        ScanPhase.Failed => "AIHub_ScanPhase_Failed".GetLocalizedOrFallback("Scan failed"),
        _ => "AIHub_OptimizationWorkflow_Ready".GetLocalizedOrFallback("Ready to scan"),
    };

    /// <summary>Weighted 0-100 scan completion.</summary>
    public double ScanProgressPercent => _scanProgress.Percent;

    public string ScanProgressPercentText => _scanProgress.PercentText;

    public string ScanStageCountText => _scanProgress.StageCountText;

    public double OptimizationWorkflowPercent => ScanPhase == ScanPhase.ExecutionPending ? OptimizationProgress : ScanProgressPercent;

    public string OptimizationWorkflowPercentText => IsScanning ? string.Empty : OptimizationWorkflowPercent.ToString("0'%'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Current usability of the shared AI service, shown beside the scan actions so the
    /// operator knows before starting whether the AI pass will participate.
    /// </summary>
    public AiReadinessLevel AiReadinessLevel
    {
        get => _aiReadinessLevel;
        private set
        {
            if (Set(ref _aiReadinessLevel, value))
            {
                OnPropertyChanged(nameof(IsAiReady));
                OnPropertyChanged(nameof(IsAiUsable));
                OnPropertyChanged(nameof(AiReadinessGlyph));
            }
        }
    }

    /// <summary>Bilingual one-line explanation of the current readiness.</summary>
    public string AiReadinessText
    {
        get => _aiReadinessText;
        private set => Set(ref _aiReadinessText, value);
    }

    /// <summary>Tab label for the per-chain policy editor.</summary>
    public string TaskPoliciesTabLabel => "AIHub_TaskPoliciesTabLabel".GetLocalizedOrFallback("Task policies");

    /// <summary>Heading for the per-chain policy editor.</summary>
    public string TaskPolicyTitle => "AIHub_TaskPolicyTitle".GetLocalizedOrFallback("Task instructions");

    /// <summary>Explains what the per-chain policies govern and where the global one lives.</summary>
    public string TaskPolicyDescription => "AIHub_TaskPolicyDescription".GetLocalizedOrFallback("Customize AI instructions for security audits and optimization. Global safety rules are in AI service settings.");

    /// <summary>Label for the manual readiness re-check action.</summary>
    public string RecheckAiLabel => IsCheckingAiReadiness
        ? "AIHub_RecheckAi_Checking".GetLocalizedOrFallback("Checking…")
        : "AIHub_RecheckAi_Action".GetLocalizedOrFallback("Re-check");

    public string AiServiceSettingsLabel => "AIHub_AiServiceSettingsLabel".GetLocalizedOrFallback("AI service settings");

    public bool IsCheckingAiReadiness
    {
        get => _isCheckingAiReadiness;
        private set
        {
            if (Set(ref _isCheckingAiReadiness, value))
            {
                OnPropertyChanged(nameof(RecheckAiLabel));
                OnPropertyChanged(nameof(CanRecheckAiReadiness));
            }
        }
    }

    public bool CanRecheckAiReadiness => IsEnabled && !IsCheckingAiReadiness && !_disposed;

    /// <summary>True only once a probe has confirmed the route.</summary>
    public bool IsAiReady => AiReadinessLevel == AiReadinessLevel.Ready;

    /// <summary>True when an AI call will be attempted, including the unverified case.</summary>
    public bool IsAiUsable => AiReadinessLevel
        is AiReadinessLevel.Ready or AiReadinessLevel.Degraded or AiReadinessLevel.Unverified;

    /// <summary>Segoe Fluent glyph matching the readiness level.</summary>
    public string AiReadinessGlyph => AiReadinessLevel switch
    {
        AiReadinessLevel.Ready => "\uE73E",
        AiReadinessLevel.Degraded => "\uE7BA",
        AiReadinessLevel.Unverified => "\uE9CE",
        AiReadinessLevel.NotConfigured => "\uEA39",
        _ => "\uE7BA",
    };

    /// <summary>Re-checks the AI route and refreshes the indicator.</summary>
    public async Task RefreshAiReadinessAsync()
    {
        if (!CanRecheckAiReadiness)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _readinessCts = cancellation;
        IsCheckingAiReadiness = true;
        try
        {
            AiReadiness readiness = await AiHubEngine.Current
                .ProbeReadinessAsync(cancellation.Token)
                .ConfigureAwait(true);

            cancellation.Token.ThrowIfCancellationRequested();

            EnqueueOnUI(() =>
            {
                AiReadinessLevel = readiness.Level;
                AiReadinessText = DescribeReadiness(readiness);
                UpdateEngineAndModelInfo(readiness);
            });
        }
        catch (OperationCanceledException)
        {
            // A cancelled probe leaves the previous verdict in place.
        }
        catch (Exception ex)
        {
            Logger.LogError("AI readiness probe failed", ex);
            ShowStatus("AIHub_RecheckFailed".GetLocalizedOrFallback("Check failed. Retry or review AI service settings."), InfoBarSeverity.Error);
        }
        finally
        {
            _readinessCts = null;
            EnqueueOnUI(() => IsCheckingAiReadiness = false);
        }
    }

    /// <summary>
    /// Re-reads the cached readiness verdict. Cheap (no probe, no kernel process), so it is
    /// safe to run on page load and on engine state-change notifications.
    /// </summary>
    private void RefreshAiReadinessFromCache()
    {
        try
        {
            AiReadiness readiness = AiHubEngine.Current.GetReadiness();
            AiReadinessLevel = readiness.Level;
            AiReadinessText = DescribeReadiness(readiness);
            UpdateEngineAndModelInfo(readiness);
        }
        catch (Exception ex)
        {
            Logger.LogError("Could not read AI readiness state", ex);
        }
    }

    private string _currentEngineAndModelLabel = string.Empty;

    public string CurrentEngineAndModelLabel
    {
        get => _currentEngineAndModelLabel;
        private set
        {
            if (Set(ref _currentEngineAndModelLabel, value))
            {
                OnPropertyChanged(nameof(HasActiveModelInfo));
            }
        }
    }

    public bool HasActiveModelInfo => !string.IsNullOrWhiteSpace(CurrentEngineAndModelLabel);

    private void UpdateEngineAndModelInfo(AiReadiness readiness)
    {
        if (!AiHubEngine.Current.IsEnabled || string.IsNullOrWhiteSpace(readiness.ActiveKernel))
        {
            CurrentEngineAndModelLabel = string.Empty;
            return;
        }

        string kernelDisplay = readiness.ActiveKernel.Equals("codex", StringComparison.OrdinalIgnoreCase)
            ? "Codex CLI"
            : (readiness.ActiveKernel.Equals("pi", StringComparison.OrdinalIgnoreCase) ? "Pi Engine" : readiness.ActiveKernel);

        CurrentEngineAndModelLabel = string.IsNullOrWhiteSpace(readiness.ActiveModel)
            ? kernelDisplay
            : $"{kernelDisplay} · {readiness.ActiveModel}";
    }

    /// <summary>
    /// The engine raises this whenever the service configuration or enable state changes
    /// (settings saved here or on General, IPC updates, file watcher), so the page keeps
    /// its enabled/status indication in step with the service.
    /// </summary>
    private void OnEngineStateChanged(object sender, AiHubStateChangedEventArgs args)
    {
        EnqueueOnUI(RefreshAiReadinessFromCache);
    }

    /// <summary>Renders a readiness verdict as a short bilingual line.</summary>
    private string DescribeReadiness(AiReadiness readiness)
    {
        string kernelModel = string.IsNullOrWhiteSpace(readiness.ActiveModel)
            ? readiness.ActiveKernel
            : $"{readiness.ActiveKernel} / {readiness.ActiveModel}";

        return readiness.Level switch
        {
            AiReadinessLevel.Ready => "AIHub_Readiness_ReadyFormat".FormatLocalized("AI service enabled · ready · {0}", kernelModel),
            AiReadinessLevel.Degraded => "AIHub_Readiness_DegradedFormat".FormatLocalized("AI service enabled · main route unavailable; fallback will be used · {0}", readiness.Detail),
            AiReadinessLevel.Unverified => "AIHub_Readiness_UnverifiedFormat".FormatLocalized("AI service enabled · configured (unverified) · {0}", kernelModel),
            AiReadinessLevel.NotConfigured => "AIHub_Readiness_NotConfiguredFormat".FormatLocalized("AI service not configured: {0}", readiness.Detail),
            _ => "AIHub_Readiness_Disabled".GetLocalizedOrFallback("AI service disabled · rule-based detection only"),
        };
    }

    /// <summary>True when the AI step should run for this scan.</summary>
    public bool CanUseAi => IsAiUsable;

    /// <summary>
    /// What the AI step did during the last scan. The scan previously never reached the
    /// kernel, and nothing in the UI said so; this makes that visible either way.
    /// </summary>
    public string AiAnalysisRanText
    {
        get => _aiAnalysisRanText;
        private set => Set(ref _aiAnalysisRanText, value);
    }

    /// <summary>Name of the stage currently running.</summary>
    public ScanPhase AuditPhase
    {
        get => _auditPhase;
        private set
        {
            if (Set(ref _auditPhase, value))
            {
                PublishScanProgress();
            }
        }
    }

    public string CurrentStageText => _auditProgress.CurrentStage switch
    {
        "Collect event logs" => "AIHub_CurrentStage_CollectLogs".GetLocalizedOrFallback("Collect event logs"),
        "Analyze findings" => "AIHub_CurrentStage_AnalyzeFindings".GetLocalizedOrFallback("Analyze findings"),
        "Finish" => "AIHub_CurrentStage_FinalizeResults".GetLocalizedOrFallback("Finalize results"),
        _ => string.Empty,
    };

    private void BeginStage(string title, bool audit = false)
    {
        (audit ? _auditProgress : _scanProgress).StartStage(title);
        if (audit)
        {
            _auditDetailedPercent = title == "Collect event logs" ? 0 : null;
        }

        PublishScanProgress();
    }

    /// <summary>Completes a stage, which also completes every earlier stage.</summary>
    private void EndStage(string title, bool audit = false)
    {
        (audit ? _auditProgress : _scanProgress).CompleteStage(title);
        if (audit && title == "Finish")
        {
            _auditDetailedPercent = 100;
        }

        PublishScanProgress();
    }

    private void ApplyAuditCollectionProgress(EventCollectionProgress progress)
    {
        if (progress.TotalChannels > 0)
        {
            _auditDetailedPercent = Math.Max(_auditDetailedPercent ?? 0, 30.0 * progress.CompletedChannels / progress.TotalChannels);
        }
        string state = progress.Skipped ? "AIHub_CollectionProgress_Unavailable".GetLocalizedOrFallback("Unavailable")
            : progress.IsComplete ? "AIHub_CollectionProgress_Read".GetLocalizedOrFallback("Read") : "AIHub_CollectionProgress_Reading".GetLocalizedOrFallback("Reading");
        AuditStatusText = "AIHub_CollectionProgress_StatusFormat".FormatLocalized(
            "{0} {1} · {2}/{3} log channels · {4} events in channel · {5} events collected",
            state, progress.Channel, progress.CompletedChannels, progress.TotalChannels, progress.ChannelEvents, progress.TotalEvents);
        PublishScanProgress();
    }

    private void ApplyAuditAiProgress(AiTaskProgress progress)
    {
        if (progress.TotalBatches > 0)
        {
            double percent = (_auditWasDeepAnalysis ? 0 : 30) + (_auditWasDeepAnalysis ? 95.0 : 65.0) * Math.Clamp((double)progress.CompletedBatches / progress.TotalBatches, 0, 1);
            _auditDetailedPercent = Math.Max(_auditDetailedPercent ?? 0, percent);
        }
        AuditStatusText = progress.StatusMessage;
        if (_auditWasDeepAnalysis)
        {
            AiStatusText = progress.StatusMessage;
        }

        PublishScanProgress();
    }

    private void ResetScanProgress()
    {
        _scanProgress.Reset();
        PublishScanProgress();
    }

    private void PublishScanProgress()
    {
        OnPropertyChanged(nameof(CurrentStageText));
        OnPropertyChanged(nameof(ScanProgressPercent));
        OnPropertyChanged(nameof(ScanProgressPercentText));
        OnPropertyChanged(nameof(ScanStageCountText));
        OnPropertyChanged(nameof(OptimizationWorkflowPercent));
        OnPropertyChanged(nameof(OptimizationWorkflowPercentText));

        // The shell mirrors this progress in the navigation pane, so it needs the same
        // notifications under the sidebar names.
        OnPropertyChanged(nameof(SidebarWorkflowVisible));
        OnPropertyChanged(nameof(SidebarWorkflowTitle));
        OnPropertyChanged(nameof(SidebarWorkflowGlyph));
        OnPropertyChanged(nameof(SidebarWorkflowPercent));
        OnPropertyChanged(nameof(SidebarWorkflowPercentText));
        OnPropertyChanged(nameof(IsAuditProgressIndeterminate));
        OnPropertyChanged(nameof(SidebarWorkflowCountText));
        OnPropertyChanged(nameof(SidebarWorkflowTooltip));

        // Mirror into the shared state the navigation pane binds to. The pane outlives this
        // page, so it cannot bind to the view model directly.
        AuditProgressState.Current.Update(
            SidebarWorkflowVisible,
            SidebarWorkflowPercent,
            SidebarWorkflowTitle,
            SidebarWorkflowGlyph,
            SidebarWorkflowPercentText,
            SidebarWorkflowCountText,
            SidebarWorkflowTooltip);
    }

    /// <summary>
    /// True while the navigation pane should show audit progress: during a scan or once a
    /// result exists. Mirrors the original app, whose sidebar hosts the workflow progress.
    /// </summary>
    public bool SidebarWorkflowVisible => AuditPhase is ScanPhase.Scanning or ScanPhase.SelectingTargets or ScanPhase.Failed;

    /// <summary>Sidebar progress bar value (0-100).</summary>
    public double SidebarWorkflowPercent => _auditDetailedPercent ?? (_auditIncomplete ? 0 : _auditProgress.Percent);

    public bool IsAuditProgressIndeterminate => (IsAuditing || IsAiAnalyzing) && !_auditDetailedPercent.HasValue;

    public string SidebarWorkflowPercentText => IsAuditProgressIndeterminate
        ? "AIHub_Sidebar_Analyzing".GetLocalizedOrFallback("Analyzing")
        : SidebarWorkflowPercent.ToString("0'%'", CultureInfo.InvariantCulture);

    public string SidebarWorkflowCountText => _auditProgress.StageCountText;

    public string SidebarWorkflowTitle => AuditPhase switch
    {
        ScanPhase.Scanning => _auditWasDeepAnalysis ? "AIHub_Sidebar_DeepAnalysis".GetLocalizedOrFallback("AI deep analysis") : "AIHub_Sidebar_Scanning".GetLocalizedOrFallback("Scanning"),
        ScanPhase.SelectingTargets when _auditIncomplete => "AIHub_Sidebar_PartiallyCompleted".GetLocalizedOrFallback("Partially completed"),
        ScanPhase.SelectingTargets => _auditWasDeepAnalysis ? "AIHub_Sidebar_DeepAnalysisComplete".GetLocalizedOrFallback("AI analysis complete") : "AIHub_Sidebar_ScanComplete".GetLocalizedOrFallback("Scan complete"),
        ScanPhase.Failed => _auditWasDeepAnalysis ? "AIHub_Sidebar_DeepAnalysisIncomplete".GetLocalizedOrFallback("AI analysis incomplete") : "AIHub_Sidebar_ScanFailed".GetLocalizedOrFallback("Scan failed"),
        _ => "AIHub_Sidebar_AuditWorkflow".GetLocalizedOrFallback("Audit workflow"),
    };

    /// <summary>Segoe Fluent icon matching the current workflow state.</summary>
    public string SidebarWorkflowGlyph => AuditPhase switch
    {
        ScanPhase.Scanning => "\uE895",
        ScanPhase.SelectingTargets => _auditIncomplete ? "\uE7BA" : "\uE73E",
        ScanPhase.Failed => "\uEA39",
        _ => "\uEA3A",
    };

    public string SidebarWorkflowTooltip => $"{SidebarWorkflowTitle} · {SidebarWorkflowPercentText} · {AuditStatusText}";

    public int CandidatesCount
    {
        get => _candidatesCount;
        private set => Set(ref _candidatesCount, value);
    }

    public long CandidatesBytes
    {
        get => _candidatesBytes;
        private set => Set(ref _candidatesBytes, value);
    }

    public string CandidatesSummary => $"{CandidatesCount} / {FormatBytes(CandidatesBytes)}";

    public int SelectedCandidatesCount
    {
        get => _selectedCandidatesCount;
        private set => Set(ref _selectedCandidatesCount, value);
    }

    public long SelectedCandidatesBytes
    {
        get => _selectedCandidatesBytes;
        private set => Set(ref _selectedCandidatesBytes, value);
    }

    public string SelectedCandidatesSummary => $"{SelectedCandidatesCount} / {FormatBytes(SelectedCandidatesBytes)}";

    public string ScannedCandidatesLabel => "AIHub_ScannedCandidatesLabel".GetLocalizedOrFallback("Files scanned");
    public string ApprovedCandidatesLabel => "AIHub_ApprovedCandidatesLabel".GetLocalizedOrFallback("AI approved");
    public string CleanableCapacityLabel => "AIHub_CleanableCapacityLabel".GetLocalizedOrFallback("Cleanable space");
    public string ScannedCandidatesText => _observedOptimizationItems.Count.ToString("N0", CultureInfo.CurrentCulture);
    public string ApprovedCandidatesText => OptimizationCandidates.Count.ToString("N0", CultureInfo.CurrentCulture);
    public string CleanableCapacityText => FormatBytes(OptimizationCandidates.Where(item => item.Action == "delete").Sum(item => item.SizeInBytes));

    public string DownloadsStatusText
    {
        get => _downloadsStatusText;
        private set => Set(ref _downloadsStatusText, value);
    }

    public int DownloadsItemCount
    {
        get => _downloadsItemCount;
        private set => Set(ref _downloadsItemCount, value);
    }

    public long DownloadsBytes
    {
        get => _downloadsBytes;
        private set => Set(ref _downloadsBytes, value);
    }

    public string TemporaryFilesStatusText
    {
        get => _temporaryFilesStatusText;
        private set => Set(ref _temporaryFilesStatusText, value);
    }

    public int TemporaryFilesItemCount
    {
        get => _temporaryFilesItemCount;
        private set => Set(ref _temporaryFilesItemCount, value);
    }

    public long TemporaryFilesBytes
    {
        get => _temporaryFilesBytes;
        private set => Set(ref _temporaryFilesBytes, value);
    }

    public ObservableCollection<TempFileInfo> OptimizationCandidates { get; } = new();
    public ObservableCollection<OptimizationCategoryViewModel> OptimizationCategories { get; } = new();
    public ObservableCollection<OptimizationCategoryViewModel> DownloadCategories { get; } = new();
    public ObservableCollection<OptimizationCategoryViewModel> CacheCategories { get; } = new();
    public string UserDirectoryLabel => "AIHub_UserDirectoryLabel".GetLocalizedOrFallback("User directory space analysis");
    public bool HasUserDirectorySummaries => UserDirectorySummaries.Count > 0;

    public ObservableCollection<string> UserDirectorySummaries { get; } = new();

    public ObservableCollection<OptimizationGroupViewModel> CacheGroups { get; } = new();
    private readonly List<TempFileInfo> _observedOptimizationItems = new();
    private bool _downloadsScanned;
    private bool _cachesScanned;
    public ICommand OrganizeDownloadsCommand { get; }
    public string OrganizeDownloadsLabel => "AIHub_OrganizeDownloadsLabel".GetLocalizedOrFallback("Organize downloads");
    public string DownloadsPath => _downloadOrganizerService.GetDownloadsPath() ?? "AIHub_DownloadsUnavailable".GetLocalizedOrFallback("Downloads folder unavailable");
    public bool HasDownloadResults => DownloadCategories.Count > 0;
    public bool HasCacheResults => CacheCategories.Count > 0;
    public string DownloadsResultSummary => DescribeOptimizationScope("move", _downloadsScanned);
    public string CacheResultSummary => DescribeOptimizationScope("delete", _cachesScanned);
    public string OptimizationResultsSummary => "AIHub_OptimizationResultsSummaryFormat".FormatLocalized(
        "{0:N0} candidates scanned · {1:N0} AI approved · {2} cleanable",
        _observedOptimizationItems.Count, OptimizationCandidates.Count, FormatBytes(OptimizationCandidates.Where(item => item.Action == "delete").Sum(item => item.SizeInBytes)));
    public ICommand CleanAllCommand { get; }
    public string CleanAllLabel => "AIHub_CleanAllLabel".GetLocalizedOrFallback("Clean all");
    private double _optimizationProgress;
    private bool _isExecutingOptimization;
    public double OptimizationProgress
    {
        get => _optimizationProgress;
        private set
        {
            if (Set(ref _optimizationProgress, value))
            {
                OnPropertyChanged(nameof(OptimizationWorkflowPercent));
                OnPropertyChanged(nameof(OptimizationWorkflowPercentText));
            }
        }
    }
    public bool IsExecutingOptimization { get => _isExecutingOptimization; private set => Set(ref _isExecutingOptimization, value); }

    public bool HasCandidates => OptimizationCandidates.Count > 0;

    public bool CanExecuteOptimization => IsEnabled && !IsOptimizing && SelectedCandidatesCount > 0;

    public bool CanCleanAll => IsEnabled && !IsOptimizing && OptimizationCandidates.Any(item => item.Action == "delete");

    public ICommand ScanAllCommand { get; }

    public ICommand ScanDownloadsCommand { get; }

    public ICommand ScanTemporaryFilesCommand { get; }

    public ICommand SelectAllCandidatesCommand { get; }

    public ICommand DeselectAllCandidatesCommand { get; }

    public ICommand ExecuteOptimizationCommand { get; }

    public ICommand CancelOptimizationCommand { get; }

    // Status Bar Properties
    public string StatusMessage => ActiveTabIndex == 1 ? _optimizationStatusMessage : _statusMessage;

    public InfoBarSeverity StatusSeverity => ActiveTabIndex == 1 ? _optimizationStatusSeverity : _statusSeverity;

    public bool IsStatusOpen
    {
        get => ActiveTabIndex == 1 ? _isOptimizationStatusOpen : _isStatusOpen;
        set
        {
            if (ActiveTabIndex == 1)
            {
                Set(ref _isOptimizationStatusOpen, value);
            }
            else
            {
                Set(ref _isStatusOpen, value);
            }
        }
    }

    // Methods & Execution Logic
    public void RefreshEnabledState()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsEnabledGpoConfigured));
        OnPropertyChanged(nameof(CanRecheckAiReadiness));
        if (!IsEnabled)
        {
            _readinessCts?.Cancel();
            _auditCts?.Cancel();
            _optimizationCts?.Cancel();
        }
        RefreshCommands();
        RefreshAiReadinessFromCache();
    }

    private void RefreshCommands()
    {
        ((RelayCommand)FastScanCommand).OnCanExecuteChanged();
        ((RelayCommand)FullScanCommand).OnCanExecuteChanged();
        ((RelayCommand)SelectLatestCommand).OnCanExecuteChanged();
        ((RelayCommand)ReanalyzeRangeCommand).OnCanExecuteChanged();
        ((RelayCommand)DeepAnalyzeCommand).OnCanExecuteChanged();
        ((RelayCommand)CancelAuditCommand).OnCanExecuteChanged();
        ((RelayCommand)ScanAllCommand).OnCanExecuteChanged();
        ((RelayCommand)ScanDownloadsCommand).OnCanExecuteChanged();
        ((RelayCommand)ScanTemporaryFilesCommand).OnCanExecuteChanged();
        ((RelayCommand)SelectAllCandidatesCommand).OnCanExecuteChanged();
        ((RelayCommand)DeselectAllCandidatesCommand).OnCanExecuteChanged();
        ((RelayCommand)ExecuteOptimizationCommand).OnCanExecuteChanged();
        ((RelayCommand)CleanAllCommand).OnCanExecuteChanged();
        OnPropertyChanged(nameof(CanCleanAll));
        ((RelayCommand)OrganizeDownloadsCommand).OnCanExecuteChanged();
        foreach (var group in OptimizationCategories)
        {
            group.IsEnabled = IsEnabled && !IsOptimizing && group.ApprovedCount > 0;
        }
        foreach (var group in CacheGroups.Concat(CacheGroups.SelectMany(group => group.Projects)))
        {
            group.IsEnabled = IsEnabled && !IsOptimizing && group.ApprovedCount > 0;
        }
        ((RelayCommand)CancelOptimizationCommand).OnCanExecuteChanged();
    }

    private void ShowStatus(string message, InfoBarSeverity severity, bool optimization = false)
    {
        if (optimization)
        {
            _optimizationStatusMessage = message;
            _optimizationStatusSeverity = severity;
            _isOptimizationStatusOpen = true;
        }
        else
        {
            _statusMessage = message;
            _statusSeverity = severity;
            _isStatusOpen = true;
        }

        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(StatusSeverity));
        OnPropertyChanged(nameof(IsStatusOpen));
    }

    public void PostStatusNotification(string message, InfoBarSeverity severity, bool optimization = false) => ShowStatus(message, severity, optimization);

    public bool IsAiAnalyzing
    {
        get => _isAiAnalyzing;
        private set
        {
            if (Set(ref _isAiAnalyzing, value))
            {
                OnPropertyChanged(nameof(CanRunDeepAnalysis));
                RefreshCommands();
                PublishScanProgress();
            }
        }
    }

    /// <summary>Enables the deep-analysis action only when AI Hub is on and audited events exist.</summary>
    public bool CanRunDeepAnalysis
    {
        get
        {
            lock (_lastAuditEvents)
            {
                return IsEnabled && !_isAuditing && !_isAiAnalyzing && _lastAuditEvents.Count > 0 && AiHubAuditAnalysisService.IsAvailable;
            }
        }
    }

    public string AiStatusText
    {
        get => _aiStatusText;
        private set => Set(ref _aiStatusText, value);
    }

    public bool HasAiReport
    {
        get => _hasAiReport;
        private set => Set(ref _hasAiReport, value);
    }

    public string AiReportSummaryText
    {
        get => _aiReportSummaryText;
        private set => Set(ref _aiReportSummaryText, value);
    }

    public string AiReportMetaText
    {
        get => _aiReportMetaText;
        private set => Set(ref _aiReportMetaText, value);
    }

    public System.Collections.ObjectModel.ObservableCollection<AiReportFinding> AiReportFindings => _aiReportFindings;

    /// <summary>
    /// Scheduled audit interval in hours. 0 disables scheduling.
    /// </summary>
    public int ScanIntervalHours
    {
        get => _moduleSettingsRepository.SettingsConfig.Properties.ScanIntervalHours.Value;
        set
        {
            int normalized = AuditSchedule.NormalizeIntervalHours(value);
            if (_moduleSettingsRepository.SettingsConfig.Properties.ScanIntervalHours.Value == normalized)
            {
                return;
            }

            _moduleSettingsRepository.SettingsConfig.Properties.ScanIntervalHours.Value = normalized;
            OnPropertyChanged(nameof(ScanIntervalHours));
            OnPropertyChanged(nameof(IsSchedulingEnabled));
            OnPropertyChanged(nameof(ScheduleSummaryText));

            try
            {
                var snd = new SndAIHubSettings(_moduleSettingsRepository.SettingsConfig);
                _ipcSendMethod(snd.ToJsonString());
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to persist the AI Hub scan interval", ex);
            }
        }
    }

    public bool IsSchedulingEnabled => AuditSchedule.IsSchedulingEnabled(ScanIntervalHours);

    /// <summary>Explains the current schedule state for the settings UI.</summary>
    public string ScheduleSummaryText
    {
        get
        {
            if (!IsSchedulingEnabled)
            {
                return "AIHub_ScheduleSummary_Off".GetLocalizedOrFallback("Off: audits run only when triggered manually");
            }

            DateTime now = DateTime.UtcNow;
            DateTime? next = AuditSchedule.NextRunUtc(_lastCompletedAuditUtc, now, ScanIntervalHours);
            string cadence = "AIHub_ScheduleSummary_CadenceFormat".FormatLocalized("Every {0} h", ScanIntervalHours);

            return next is null
                ? cadence
                : "AIHub_ScheduleSummary_NextFormat".FormatLocalized("{0} · next around {1:MM-dd HH:mm}", cadence, next.Value.ToLocalTime());
        }
    }

    /// <summary>
    /// Persists an audit result and refreshes the dashboard activity counters from the
    /// stored history (never from hardcoded values).
    /// </summary>
    private async Task SaveHistoryAndRefreshStatisticsAsync(AuditResult result)
    {
        try
        {
            int retentionDays = _moduleSettingsRepository.SettingsConfig.Properties.RetentionDays.Value;
            await _auditHistoryStorage.SaveHistoryAsync(result, retentionDays).ConfigureAwait(false);
            AuditHistoryStatistics statistics = await _auditHistoryStorage.GetStatisticsAsync().ConfigureAwait(false);
            EnqueueOnUI(() => ApplyStatistics(statistics));
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to persist AI Hub audit history", ex);
        }
    }

    /// <summary>Projects stored history statistics onto the activity counters.</summary>
    private void ApplyStatistics(AuditHistoryStatistics statistics)
    {
        ActivityScansText = statistics.AuditCountText;
        ActiveDaysText = statistics.ActiveDayCountText;
        HealthAverageText = statistics.AverageHealthScoreText;
    }

    /// <summary>
    /// Sends the audited events to the sandboxed "security-audit" AI chain and
    /// surfaces the advisory issues in the Security Audit tab.
    /// </summary>
    private void RunDeepAnalysis()
    {
        if (!CanRunDeepAnalysis)
        {
            return;
        }

        List<SecurityEvent> events;
        lock (_lastAuditEvents)
        {
            events = _lastAuditEvents.ToList();
        }
        _aiReportFindings.Clear();
        HasAiReport = false;
        _auditProgress.Reset();
        _auditWasDeepAnalysis = true;
        _auditIncomplete = false;
        AiAnalysisRanText = string.Empty;
        IsAiAnalyzing = true;
        AuditPhase = ScanPhase.Scanning;
        EndStage("Collect event logs", audit: true);
        BeginStage("Analyze findings", audit: true);
        AiStatusText = "AIHub_DeepAnalysis_Requesting".GetLocalizedOrFallback("Requesting AI deep analysis...");
        AuditStatusText = AiStatusText;

        _auditCts?.Cancel();
        _auditCts?.Dispose();
        _auditCts = new CancellationTokenSource();
        CancellationToken token = _auditCts.Token;
        var progress = new Progress<AiTaskProgress>(update => EnqueueOnUI(() =>
        {
            if (_auditCts?.Token == token && IsAiAnalyzing && AuditPhase == ScanPhase.Scanning)
            {
                ApplyAuditAiProgress(update);
            }
        }, token));

        Task.Run(async () =>
        {
            try
            {
                var result = await AiHubAuditAnalysisService
                    .AnalyzeEventsAsync(events, progress, token)
                    .ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                IReadOnlyList<AuditIssue> issues = result.Issues;
                bool partial = result.IsPartial;
                string failure = result.Failure;

                EnqueueOnUI(() =>
                {
                    if (_auditCts?.Token != token)
                    {
                        return;
                    }

                    if (issues is null)
                    {
                        AiStatusText = "AIHub_DeepAnalysis_IncompleteFormat".FormatLocalized("AI analysis did not complete: {0}", failure);
                        AuditStatusText = AiStatusText;
                        _auditIncomplete = true;
                        AuditPhase = ScanPhase.Failed;
                        ShowStatus(AiStatusText, InfoBarSeverity.Warning);
                        return;
                    }

                    foreach (AuditIssue issue in issues.OrderByDescending(item => item.Severity == "High").ThenByDescending(item => item.Occurrences))
                    {
                        _aiReportFindings.Add(new AiReportFinding(
                            PreferChinese() && !string.IsNullOrWhiteSpace(issue.TitleZh) ? issue.TitleZh : issue.Title,
                            PreferChinese() && !string.IsNullOrWhiteSpace(issue.RootCauseZh) ? issue.RootCauseZh : issue.RootCause,
                            PreferChinese() && !string.IsNullOrWhiteSpace(issue.DescriptionZh) ? issue.DescriptionZh : issue.Description,
                            PreferChinese() && !string.IsNullOrWhiteSpace(issue.RecommendationZh) ? issue.RecommendationZh : issue.Recommendation,
                            $"{issue.Severity} · Event {issue.EventId} · {issue.Occurrences}x"));
                    }

                    AiReportSummaryText = partial
                        ? "AIHub_DeepAnalysis_SummaryPartialFormat".FormatLocalized("AI partially completed: retained {0} enhanced finding(s) ({1}).", issues.Count, failure)
                        : "AIHub_DeepAnalysis_SummaryCompleteFormat".FormatLocalized("AI deep analysis completed: {0} enhanced finding(s).", issues.Count);
                    AiReportMetaText = "AIHub_DeepAnalysis_MetaFormat".FormatLocalized("Kernel {0} · {1:HH:mm:ss}", AiHubEngine.Current.ActiveKernel, DateTime.Now);
                    HasAiReport = true;
                    AiStatusText = AiReportSummaryText;
                    AuditStatusText = AiStatusText;
                    _auditIncomplete = partial;
                    if (!partial)
                    {
                        EndStage("Analyze findings", audit: true);
                        EndStage("Finish", audit: true);
                    }

                    AuditPhase = ScanPhase.SelectingTargets;
                    if (partial)
                    {
                        ShowStatus(AiStatusText, InfoBarSeverity.Warning);
                    }

                    OnPropertyChanged(nameof(CanRunDeepAnalysis));
                }, token);
            }
            catch (OperationCanceledException)
            {
                EnqueueOnUI(() =>
                {
                    if (_auditCts?.Token == token)
                    {
                        AiStatusText = "AIHub_DeepAnalysis_Cancelled".GetLocalizedOrFallback("AI analysis cancelled");
                        AuditStatusText = AiStatusText;
                        _auditIncomplete = true;
                        AuditPhase = ScanPhase.Failed;
                    }
                });
            }
            catch (Exception ex)
            {
                EnqueueOnUI(() =>
                {
                    if (_auditCts?.Token == token)
                    {
                        AiStatusText = "AIHub_DeepAnalysis_FailedFormat".FormatLocalized("AI analysis failed: {0}", ex.Message);
                        AuditStatusText = AiStatusText;
                        _auditIncomplete = true;
                        AuditPhase = ScanPhase.Failed;
                    }
                });
            }
            finally
            {
                EnqueueOnUI(() =>
                {
                    if (_auditCts?.Token != token)
                    {
                        return;
                    }

                    if (token.IsCancellationRequested && AuditPhase == ScanPhase.Scanning)
                    {
                        AiStatusText = "AIHub_DeepAnalysis_Cancelled".GetLocalizedOrFallback("AI analysis cancelled");
                        AuditStatusText = AiStatusText;
                        _auditIncomplete = true;
                        AuditPhase = ScanPhase.Failed;
                    }

                    IsAiAnalyzing = false;
                });
            }
        });
    }

    private void RunAudit(bool fast, bool rangeRefresh = false)
    {
        if (!IsEnabled || IsAuditing || IsAiAnalyzing)
        {
            return;
        }

        // Claim the slot before cancelling anything: setting the flag afterwards left a
        // window where two rapid clicks both passed the guard, and the second run then
        // cancelled the first mid-collection and reported an empty audit.
        IsAuditing = true;

        // Audit progress uses its own stages so the pane shows what the audit is doing.
        _auditProgress.Reset();
        _auditWasDeepAnalysis = false;
        _auditIncomplete = false;
        _aiReportFindings.Clear();
        HasAiReport = false;
        AiStatusText = AiReportSummaryText = AiReportMetaText = string.Empty;
        AiAnalysisRanText = string.Empty;
        AuditPhase = ScanPhase.Scanning;
        BeginStage("Collect event logs", audit: true);

        _auditCts?.Cancel();
        _auditCts?.Dispose();
        _auditCts = new CancellationTokenSource();
        CancellationToken token = _auditCts.Token;
        AuditStatusText = fast ? "AIHub_RunAudit_FastInProgress".GetLocalizedOrFallback("Fast scan in progress...") : "AIHub_RunAudit_FullInProgress".GetLocalizedOrFallback("Full event audit in progress...");

        // Snapshot the user's selection before dispatching. Later selections cancel this run.
        int rangeDays = _selectedScopeIndex switch { 1 => 7, 2 => 30, _ => 2 };
        string rangeDescription = ScanRangeDescription;
        int maxEvents = fast ? 500 : 5000;
        var auditMode = IsFullAuditMode ? EventLogService.AuditMode.Full : EventLogService.AuditMode.Extended;
        DateTime nowUtc = DateTime.UtcNow;
        ScanIntent intent = fast ? ScanIntent.Fast : ScanIntent.ManualFull;
        (DateTime windowFrom, DateTime windowTo) = AuditSchedule.ComputeWindow(intent, _lastCompletedAuditUtc, nowUtc, rangeDays);
        bool incremental = AuditSchedule.IsIncremental(intent, _lastCompletedAuditUtc, nowUtc, rangeDays);
        var eventLogService = new EventLogService();
        var collectionProgress = new Progress<EventCollectionProgress>(update => EnqueueOnUI(() =>
        {
            if (_auditCts?.Token == token && IsAuditing && _auditProgress.CurrentStage == "Collect event logs")
            {
                ApplyAuditCollectionProgress(update);
            }
        }, token));

        Task.Run(async () =>
        {
            try
            {
                var events = await eventLogService.CollectEventsAsync(windowFrom, windowTo, auditMode, maxEvents, collectionProgress, token);
                token.ThrowIfCancellationRequested();

                // Advance only after the work is done; completing the stage first made the
                // progress bar claim collection was finished while it had not started.
                EnqueueOnUI(() =>
                {
                    if (_auditCts?.Token != token)
                    {
                        return;
                    }

                    EndStage("Collect event logs", audit: true);
                    BeginStage("Analyze findings", audit: true);
                    AuditStatusText = "AIHub_RunAudit_CheckingLocalRulesFormat".FormatLocalized("Checking local rules for {0} events...", events.Count);
                }, token);

                // Decide up front whether the AI pass can run, and say so before spending
                // time on it. Previously availability was an optimistic switch check, so a
                // misconfigured route only surfaced after the whole scan had finished.
                AiReadiness readiness = AiHubEngine.Current.GetReadiness();

                EnqueueOnUI(() =>
                {
                    AiReadinessText = DescribeReadiness(readiness);
                    AiReadinessLevel = readiness.Level;
                }, token);

                Logger.LogInfo(
                    $"Audit readiness: level={readiness.Level}, kernel={readiness.ActiveKernel}, " +
                    $"model={readiness.ActiveModel}, cached={readiness.FromCache}, detail={readiness.Detail ?? "-"}");

                bool aiEligible = !rangeRefresh && readiness.CanAttempt;

                // Rule-based detection first: it is local, instant, and always available, so
                // a machine with AI disabled still gets a usable result.
                var ruleIssues = RunRuleBasedAuditAnalysis(events);
                var issues = new List<AuditIssueEnhanced>(ruleIssues);
                string aiOutcome = "skipped";

                // Then hand the same evidence to the selected kernel. The original app does
                // this inside the scan itself rather than behind a separate action; without it
                // a scan never reached the model at all.
                if (events.Count > 0 && aiEligible)
                {
                    EnqueueOnUI(() => BeginStage("Analyze findings", audit: true), token);

                    Logger.LogInfo(
                        $"Audit sending {events.Count} event(s) to {AiHubEngine.Current.ActiveKernel} for analysis.");

                    var progress = new Progress<AiTaskProgress>(update =>
                        EnqueueOnUI(() =>
                        {
                            if (_auditCts?.Token == token && IsAuditing && AuditPhase == ScanPhase.Scanning)
                            {
                                ApplyAuditAiProgress(update);
                            }
                        }, token));

                    try
                    {
                        var result = await AiHubAuditAnalysisService
                            .AnalyzeEventsAsync(events, progress, token)
                            .ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        var aiIssues = result.Issues;
                        bool partial = result.IsPartial;
                        aiOutcome = partial ? "partial" : aiIssues != null ? "ok" : "failed";

                        // Bound properties back a TextBlock, so they must be set on the UI
                        // thread; assigning them here raised RPC_E_WRONG_THREAD and surfaced
                        // as "Audit failed" even though the audit itself had succeeded.
                        if (aiIssues is { Count: > 0 })
                        {
                            issues = MergeIssues(ruleIssues, aiIssues, events);
                            string summary = partial
                                ? "AIHub_RunAudit_AiPartialFormat".FormatLocalized("AI partially completed; retained {0} enhanced finding(s) ({1})", aiIssues.Count, result.Failure)
                                : "AIHub_RunAudit_AiReturnedFindingsFormat".FormatLocalized("{0} returned {1} enhanced finding(s)", AiHubEngine.Current.ActiveKernel, aiIssues.Count);
                            EnqueueOnUI(() => AiAnalysisRanText = summary, token);
                        }
                        else
                        {
                            // Distinguish "the model had nothing to add" from "the call never
                            // succeeded"; the engine's failure reason explains which.
                            var failure = result.Failure;
                            string summary = partial
                                ? "AIHub_RunAudit_AiPartialNoExtraFormat".FormatLocalized("AI partially completed; validated batches had no extra findings ({0})", failure)
                                : failure is null
                                ? "AIHub_RunAudit_AiNoExtraFormat".FormatLocalized("{0} returned no extra findings", AiHubEngine.Current.ActiveKernel)
                                : "AIHub_RunAudit_AiCallFailedFormat".FormatLocalized("AI call failed: {0}", failure);

                            EnqueueOnUI(() =>
                            {
                                AiAnalysisRanText = summary;
                                if (failure is not null)
                                {
                                    ShowStatus(
                                        "AIHub_RunAudit_AiDidNotSucceedFormat".FormatLocalized("AI analysis did not succeed: {0}", failure),
                                        InfoBarSeverity.Warning);
                                }
                            }, token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // An AI failure must not discard the rule-based result.
                        aiOutcome = "failed";
                        Logger.LogError($"AI analysis during audit failed: type={ex.GetType().Name}, hresult=0x{ex.HResult:X8}");
                        string summary = "AIHub_RunAudit_AiAnalysisFailedFormat".FormatLocalized("AI analysis failed: {0}", ex.Message);
                        EnqueueOnUI(() => AiAnalysisRanText = summary, token);
                    }
                }
                else if (rangeRefresh)
                {
                    EnqueueOnUI(() => AiAnalysisRanText = "AIHub_RunAudit_RangeRefreshed".GetLocalizedOrFallback("Local findings refreshed; AI deep analysis is available on request"), token);
                }
                else if (events.Count == 0)
                {
                    EnqueueOnUI(() => AiAnalysisRanText = "AIHub_RunAudit_NoEventsAiNotCalled".GetLocalizedOrFallback("No events; AI not called"), token);
                }
                else
                {
                    EnqueueOnUI(() => AiAnalysisRanText = "AIHub_RunAudit_AiHubDisabledSkipped".GetLocalizedOrFallback("AI Hub disabled; AI analysis skipped"), token);
                }

                // Calculate health score
                token.ThrowIfCancellationRequested();
                int score = HealthScoreCalculator.Calculate(issues);

                // Record the outcome: without this the log stopped after "sending ... events"
                // and a failure was indistinguishable from a success.
                Logger.LogInfo(
                    $"Audit analysis finished: type={(fast ? "fast" : "full")}, events={events.Count}, " +
                    $"ruleFindings={ruleIssues.Count}, totalFindings={issues.Count}, healthScore={score}, " +
                    $"ai={aiOutcome}");

                EnqueueOnUI(() =>
                {
                    if (_auditCts?.Token != token || token.IsCancellationRequested)
                    {
                        return;
                    }

                    _auditIncomplete = aiOutcome is "partial" or "failed";
                    if (!_auditIncomplete)
                    {
                        EndStage("Analyze findings", audit: true);
                        EndStage("Finish", audit: true);
                    }

                    AuditPhase = ScanPhase.SelectingTargets;

                    // Only the accepted result can supply evidence to a later deep analysis.
                    lock (_lastAuditEvents)
                    {
                        _lastAuditEvents.Clear();
                        _lastAuditEvents.AddRange(events);
                    }

                    AllIssues.Clear();
                    foreach (var issue in issues)
                    {
                        AllIssues.Add(issue);
                    }

                    _hasAuditResult = true;
                    HealthScore = score;
                    HealthScoreGrade = score >= 80 ? "AIHub_HealthScoreGrade_Good".GetLocalizedOrFallback("Good") : score >= 60 ? "AIHub_HealthScoreGrade_Warning".GetLocalizedOrFallback("Warning") : "AIHub_HealthScoreGrade_Critical".GetLocalizedOrFallback("Critical");
                    HighCount = issues.Count(i => i.IsHigh);
                    MediumCount = issues.Count(i => i.IsMedium);
                    LowCount = issues.Count(i => i.IsLow);

                    AuditTimestampText = "AIHub_AuditTimestamp_LastScannedFormat".FormatLocalized("Last scanned: {0:yyyy-MM-dd HH:mm:ss} ({1} findings)", DateTime.Now, issues.Count);

                    // Update Verdict & Health Summary
                    UpdateHealthSummary(score, HighCount, MediumCount, LowCount);
                    OnPropertyChanged(nameof(ScheduleSummaryText));

                    // Update Scan Details & Activity
                    if (!rangeRefresh)
                    {
                        _lastCompletedAuditUtc = DateTime.UtcNow;
                    }

                    ScanTypeText = rangeRefresh ? "AIHub_ScanType_LocalFindings".GetLocalizedOrFallback("Local findings") : fast
                        ? "AIHub_FastScanLabel".GetLocalizedOrFallback("Fast scan")
                        : incremental
                            ? "AIHub_ScanType_Incremental".GetLocalizedOrFallback("Incremental audit")
                            : "AIHub_ScanType_Full".GetLocalizedOrFallback("Full scan");
                    FinishedText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

                    // Report the window actually scanned instead of a hard-coded label; the
                    // old text disagreed with the range the user had selected.
                    WindowText = fast
                        ? "AIHub_Window_Today".GetLocalizedOrFallback("Today")
                        : rangeDescription;
                    EventCountText = "AIHub_EventCountFormat".FormatLocalized("{0} events", events.Count);
                    ActivityFindingsText = issues.Count.ToString(CultureInfo.InvariantCulture);
                    SelectedAuditLabel = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    _suppressAuditDateLoad = true;
                    try
                    {
                        SelectedAuditDate = DateTimeOffset.Now;
                    }
                    finally
                    {
                        _suppressAuditDateLoad = false;
                    }

                    RebuildSections();

                    // Save history, then derive the activity counters from what is stored
                    // so scan/active-day/average figures reflect the real history.
                    if (!rangeRefresh)
                    {
                        _ = SaveHistoryAndRefreshStatisticsAsync(new AuditResult
                        {
                            Timestamp = DateTime.UtcNow,
                            HealthScore = score,
                            Findings = issues,
                            EventsScanned = events.Count,
                            ScanStart = windowFrom,
                            ScanEnd = windowTo,
                        });
                    }

                    FinishAuditStatus(score, issues.Count, aiOutcome, eventLogService.SkippedChannels);
                }, token);
            }
            catch (OperationCanceledException)
            {
                EnqueueOnUI(() =>
                {
                    if (_auditCts?.Token == token)
                    {
                        StopAudit(cancelled: true);
                    }
                });
            }
            catch (Exception ex)
            {
                EnqueueOnUI(() =>
                {
                    if (_auditCts?.Token == token)
                    {
                        StopAudit(cancelled: false);
                    }
                });
                Logger.LogError($"Audit failed: type={ex.GetType().Name}, hresult=0x{ex.HResult:X8}");
            }
            finally
            {
                EnqueueOnUI(() =>
                {
                    if (_auditCts?.Token != token)
                    {
                        return;
                    }

                    if (token.IsCancellationRequested && AuditPhase == ScanPhase.Scanning)
                    {
                        StopAudit(cancelled: true);
                    }

                    IsAuditing = false;
                    OnPropertyChanged(nameof(CanRunDeepAnalysis));
                });
            }
        });
    }

    private void FinishAuditStatus(int score, int findingCount, string aiOutcome, IReadOnlyList<string> skippedChannels)
    {
        string message = aiOutcome switch
        {
            "partial" => "AIHub_FinishAudit_Partial".GetLocalizedOrFallback("Audit partially completed; validated AI results were retained."),
            "failed" => "AIHub_FinishAudit_Failed".GetLocalizedOrFallback("Local audit completed; AI analysis failed. Results contain rule-based findings only."),
            _ => "AIHub_FinishAudit_Completed".GetLocalizedOrFallback("Audit completed."),
        };
        message += "AIHub_FinishAudit_ScoreAndFindingsFormat".FormatLocalized(" Health score {0}/100; {1} finding(s).", score, findingCount);
        if (skippedChannels.Count > 0)
        {
            message += "AIHub_FinishAudit_SkippedChannelsFormat".FormatLocalized(" Could not read: {0}.", string.Join(", ", skippedChannels));
        }

        AuditStatusText = message;
        ShowStatus(message, aiOutcome is "partial" or "failed" || skippedChannels.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private void StopAudit(bool cancelled)
    {
        _auditProgress.Reset();
        _auditDetailedPercent = 0;
        AuditStatusText = cancelled ? "AIHub_StopAudit_Cancelled".GetLocalizedOrFallback("Audit cancelled") : "AIHub_StopAudit_Failed".GetLocalizedOrFallback("Audit failed");
        AuditPhase = cancelled ? ScanPhase.Idle : ScanPhase.Failed;
        IsAuditing = false;
        PublishScanProgress();
        ShowStatus(AuditStatusText, cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Error);
    }

    private void UpdateHealthSummary(int score, int high, int medium, int low)
    {
        if (high > 0)
        {
            HealthVerdict = "AIHub_HealthVerdict_Critical".GetLocalizedOrFallback("Attention needed · Critical issues detected");
            HealthSummaryText = "AIHub_HealthSummary_CriticalFormat".FormatLocalized("{0} high-priority issue(s) and {1} warning(s) detected. Action recommended.", high, medium);
        }
        else if (medium > 0)
        {
            HealthVerdict = "AIHub_HealthVerdict_Good".GetLocalizedOrFallback("Good · Most indicators stable");
            HealthSummaryText = "AIHub_HealthSummary_GoodFormat".FormatLocalized("No critical failures found. Recorded {0} warning(s) and {1} info event(s).", medium, low);
        }
        else
        {
            HealthVerdict = "AIHub_HealthVerdict_Excellent".GetLocalizedOrFallback("Excellent · System fully healthy");
            HealthSummaryText = "AIHub_HealthSummary_Excellent".GetLocalizedOrFallback("No security violations or stability anomalies detected in the scanned period.");
        }

        OnPropertyChanged(nameof(HealthVerdict));
        OnPropertyChanged(nameof(HealthSummaryText));
    }

    /// <summary>
    /// Combines the rule-based and AI findings. An AI finding for an event the rules already
    /// flagged replaces it, because the AI result carries richer bilingual guidance; AI
    /// findings for events the rules missed are appended.
    /// </summary>
    private static List<AuditIssueEnhanced> MergeIssues(
        List<AuditIssueEnhanced> ruleIssues,
        IReadOnlyList<AuditIssue> aiIssues,
        IReadOnlyList<SecurityEvent> events)
    {
        var merged = new List<AuditIssueEnhanced>(ruleIssues);
        var seenEventIds = new HashSet<string>(ruleIssues.Select(issue => issue.EventId), StringComparer.Ordinal);

        foreach (AuditIssue ai in aiIssues)
        {
            if (ai is null || string.IsNullOrWhiteSpace(ai.Title))
            {
                continue;
            }

            // The model may omit the channel; recover it from the event so the finding stays
            // reachable through the source filter.
            if (string.IsNullOrWhiteSpace(ai.LogName) && int.TryParse(ai.EventId, out int eventId))
            {
                var origin = events.FirstOrDefault(e => e.EventId == eventId);
                if (origin is not null)
                {
                    ai.LogName = origin.LogName ?? string.Empty;
                }
            }

            if (seenEventIds.Contains(ai.EventId))
            {
                int index = merged.FindIndex(issue => issue.EventId == ai.EventId);
                if (index >= 0)
                {
                    merged[index] = ToEnhanced(ai);
                }
            }
            else
            {
                seenEventIds.Add(ai.EventId);
                merged.Add(ToEnhanced(ai));
            }
        }

        return merged;
    }

    /// <summary>
    /// Projects an AI finding onto the shape the page displays.
    /// </summary>
    /// <remarks>
    /// LogName and Category must survive the projection: the source filter matches on them,
    /// and dropping them made AI findings disappear from the list and reset the visible
    /// count to zero even though the analysis had returned results.
    /// </remarks>
    private static AuditIssueEnhanced ToEnhanced(AuditIssue ai) =>
        new()
        {
            Key = ai.Key,
            EventRef = ai.EventRef,
            EventId = ai.EventId,
            LogName = ai.LogName,
            Category = string.IsNullOrWhiteSpace(ai.Category) ? "Other" : ai.Category,
            Severity = ai.Severity,
            Confidence = ai.Confidence,
            Title = ai.Title,
            TitleZh = ai.TitleZh,
            Description = ai.Description,
            DescriptionZh = ai.DescriptionZh,
            RootCause = ai.RootCause,
            RootCauseZh = ai.RootCauseZh,
            Recommendation = ai.Recommendation,
            RecommendationZh = ai.RecommendationZh,
            Occurrences = ai.Occurrences,
            Source = "AI",
        };

    private static List<AuditIssueEnhanced> RunRuleBasedAuditAnalysis(List<SecurityEvent> events)
        => AuditRuleEngine.Analyze(events);

    private void RebuildSections(bool expandMatches = false)
    {
        var previousState = FindingSections.ToDictionary(section => section.Name, section => section.IsExpanded);

        // 1. Source filter
        var sourceIssues = _sourceFilter == "All"
            ? AllIssues.ToList()
            : AllIssues.Where(issue =>
                string.Equals(issue.LogName, _sourceFilter, StringComparison.OrdinalIgnoreCase) ||
                issue.Category.Contains(_sourceFilter, StringComparison.OrdinalIgnoreCase) ||
                issue.CategoryLabel.Contains(_sourceFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        // 2. Severity filter
        var visibleIssues = _severityFilter switch
        {
            "High" => sourceIssues.Where(i => i.IsHigh).ToList(),
            "Medium" => sourceIssues.Where(i => i.IsMedium).ToList(),
            "Low" => sourceIssues.Where(i => i.IsLow).ToList(),
            _ => sourceIssues.ToList(),
        };

        // 3. Search filter
        if (!string.IsNullOrWhiteSpace(_findingSearchText))
        {
            var terms = _findingSearchText.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length > 0)
            {
                visibleIssues = visibleIssues.Where(issue => terms.All(term =>
                    string.Equals(issue.EventId, term, StringComparison.OrdinalIgnoreCase) ||
                    issue.DisplayTitle.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    issue.EvidenceSummaryText.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    issue.PriorityActionText.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    issue.Affected.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
            }
        }

        // 4. Group by CategoryLabel
        var groups = visibleIssues
            .GroupBy(issue => issue.CategoryLabel)
            .OrderByDescending(group => group.Count(i => i.IsHigh))
            .ThenByDescending(group => group.Count(i => i.IsMedium))
            .ThenByDescending(group => group.Sum(i => i.Occurrences))
            .ToList();

        var sections = new List<FindingSection>();
        foreach (var group in groups)
        {
            var issues = group
                .OrderByDescending(i => i.IsHigh)
                .ThenByDescending(i => i.IsMedium)
                .ThenByDescending(i => i.Occurrences)
                .ToList();

            bool expanded = expandMatches || (previousState.TryGetValue(group.Key, out bool wasExpanded) ? wasExpanded : true);
            sections.Add(new FindingSection(group.Key, issues, expanded));
        }

        FindingSections.Clear();
        foreach (var s in sections)
        {
            FindingSections.Add(s);
        }

        // 5. Priority findings (top 5)
        PriorityFindings.Clear();
        foreach (var item in visibleIssues.OrderByDescending(i => i.IsHigh).ThenByDescending(i => i.IsMedium).ThenByDescending(i => i.Occurrences).Take(5))
        {
            PriorityFindings.Add(item);
        }

        // 6. Filtered issues (for backward compatibility)
        FilteredIssues.Clear();
        foreach (var item in visibleIssues)
        {
            FilteredIssues.Add(item);
        }

        // Notify properties
        OnPropertyChanged(nameof(CanRunDeepAnalysis));
        OnPropertyChanged(nameof(TotalFindings));
        OnPropertyChanged(nameof(FindingsSummaryText));
        OnPropertyChanged(nameof(AllFindingsLabel));
        OnPropertyChanged(nameof(PriorityPreviewLabel));
        OnPropertyChanged(nameof(AuditPriorityFinding));
        OnPropertyChanged(nameof(HasAuditPriority));
        OnPropertyChanged(nameof(AuditPriorityTitle));
        OnPropertyChanged(nameof(AuditPriorityAction));
        OnPropertyChanged(nameof(AuditSummaryEmptyTitle));
        OnPropertyChanged(nameof(AuditSummaryEmptyText));
        OnPropertyChanged(nameof(ShowPriorityFindings));
        OnPropertyChanged(nameof(ShowNoAudit));
        OnPropertyChanged(nameof(ShowEmptyIssues));
        OnPropertyChanged(nameof(ShowNoFilterMatches));
        OnPropertyChanged(nameof(HasAuditData));
        OnPropertyChanged(nameof(HealthScoreText));
        OnPropertyChanged(nameof(HasFindingFilters));
        OnPropertyChanged(nameof(OverviewLabel));
        OnPropertyChanged(nameof(ApplicationLabel));
        OnPropertyChanged(nameof(SecurityLabel));
        OnPropertyChanged(nameof(SystemLabel));
        OnPropertyChanged(nameof(SetupLabel));
        OnPropertyChanged(nameof(ForwardedLabel));
    }

    private void LoadRecentAudit()
    {
        var auditAtLoad = _auditCts;
        Task.Run(async () =>
        {
            try
            {
                AuditHistoryStatistics statistics = await _auditHistoryStorage.GetStatisticsAsync().ConfigureAwait(false);
                EnqueueOnUI(() =>
                {
                    if (!IsAuditing && !IsAiAnalyzing && _auditCts == auditAtLoad)
                    {
                        ApplyStatistics(statistics);
                    }
                });

                var history = await _auditHistoryStorage.LoadLatestHistoryAsync();
                if (history != null)
                {
                    EnqueueOnUI(() =>
                    {
                        if (IsAuditing || IsAiAnalyzing || _auditCts != auditAtLoad)
                        {
                            return;
                        }

                        ApplyHistoricalAudit(history);
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to load the most recent AI Hub audit snapshot", ex);
            }
        });
    }

    private void ApplyHistoricalAudit(AuditResult history)
    {
        AllIssues.Clear();
        foreach (var issue in history.Findings)
        {
            AllIssues.Add(issue);
        }

        _hasAuditResult = true;
        HealthScore = history.HealthScore;
        HealthScoreGrade = HealthScore >= 80 ? "AIHub_HealthScoreGrade_Good".GetLocalizedOrFallback("Good") : HealthScore >= 60 ? "AIHub_HealthScoreGrade_Warning".GetLocalizedOrFallback("Warning") : "AIHub_HealthScoreGrade_Critical".GetLocalizedOrFallback("Critical");
        HighCount = history.HighCount;
        MediumCount = history.MediumCount;
        LowCount = history.LowCount;

        AuditTimestampText = "AIHub_AuditTimestamp_LastScannedFormat".FormatLocalized("Last scanned: {0:yyyy-MM-dd HH:mm:ss} ({1} findings)", history.Timestamp.ToLocalTime(), history.Findings.Count);

        UpdateHealthSummary(HealthScore, HighCount, MediumCount, LowCount);

        ScanTypeText = "AIHub_ScanType_Historical".GetLocalizedOrFallback("Historical snapshot");
        FinishedText = history.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        WindowText = "AIHub_Window_AllTimes".GetLocalizedOrFallback("All times");
        EventCountText = "AIHub_EventCount_HistoricalFormat".FormatLocalized("{0} findings", history.Findings.Count);
        ActivityFindingsText = history.Findings.Count.ToString(CultureInfo.InvariantCulture);
        SelectedAuditLabel = history.Timestamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        _suppressAuditDateLoad = true;
        try
        {
            SelectedAuditDate = history.Timestamp.ToLocalTime();
        }
        finally
        {
            _suppressAuditDateLoad = false;
        }

        RebuildSections();
    }

    private async Task LoadAuditForDateAsync(DateTime localDate)
    {
        try
        {
            var historyList = await _auditHistoryStorage.LoadHistoryAsync().ConfigureAwait(false);
            var match = historyList
                .Where(h => h.Timestamp.ToLocalTime().Date == localDate)
                .OrderByDescending(h => h.Timestamp)
                .FirstOrDefault();

            EnqueueOnUI(() =>
            {
                if (IsAuditing || IsAiAnalyzing)
                {
                    return;
                }

                if (match != null)
                {
                    ApplyHistoricalAudit(match);
                }
                else
                {
                    SelectedAuditLabel = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    ShowStatus(
                        ResourceExtensions.FormatLocalized(
                            "AIHub_AuditNoSnapshotForDateFormat",
                            "No audit snapshot found for {0:yyyy-MM-dd}.",
                            localDate),
                        InfoBarSeverity.Informational);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to load AI Hub audit snapshot for selected date", ex);
        }
    }

    // Optimization Execution (1:1 with Screenshot)
    private void ClearOptimizationCandidates(string action)
    {
        _observedOptimizationItems.RemoveAll(item => action is null || item.Action == action);
        foreach (var item in OptimizationCandidates.Where(item => action is null || item.Action == action).ToList())
        {
            item.PropertyChanged -= Candidate_PropertyChanged;
            OptimizationCandidates.Remove(item);
        }

        RecalculateCandidateMetrics();
    }

    private async Task<OptimizationAnalysisResult> AnalyzeOptimizationCandidatesAsync(List<TempFileInfo> items, CancellationToken token, string action = null)
    {
        EnqueueOnUI(() =>
        {
            if (action is null || action == "move")
            {
                _downloadsScanned = true;
            }

            if (action is null || action == "delete")
            {
                _cachesScanned = true;
                UserDirectorySummaries.Clear();
                foreach (string summary in _cacheCleanupService.UserDirectorySummaries)
                {
                    UserDirectorySummaries.Add(summary);
                }

                OnPropertyChanged(nameof(HasUserDirectorySummaries));
            }

            _observedOptimizationItems.AddRange(items);
            RecalculateCandidateMetrics();
            OptimizationWorkflowStatus = "AIHub_OptimizationWorkflow_LocalCompleteReviewing".GetLocalizedOrFallback("Local scan complete. Reviewing with AI…");
        }, token);
        int finished = 0;
        var progress = new Progress<AiTaskProgress>(update =>
        {
            if (update.Stage == "Route")
            {
                Logger.LogInfo($"Optimization route: {update.StatusMessage}");
                return;
            }

            EnqueueOnUI(() =>
            {
                if (Volatile.Read(ref finished) != 0)
                {
                    return;
                }
                OptimizationWorkflowStatus = update.Stage == "Failover"
                    ? "AIHub_OptimizationWorkflow_Failover".GetLocalizedOrFallback("Main route did not complete. Reviewing with Fallback…")
                    : update.TotalBatches > 0
                    ? "AIHub_OptimizationWorkflow_BatchesFormat".FormatLocalized("AI analysis ({0}/{1} batches)", update.CompletedBatches, update.TotalBatches)
                    : "AIHub_OptimizationWorkflow_Analyzing".GetLocalizedOrFallback("Analyzing with AI...");
            }, token);
        });
        try
        {
            var result = await _optimizationAnalysisService.AnalyzeAsync(items, PreferChinese() ? "zh-CN" : "en-US", progress, token);
            Logger.LogInfo($"Optimization review finished: code={result.ErrorCode}, approved={result.Items.Count}, unreviewed={result.UnreviewedCount}, skipped={result.SkippedCount}");
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            foreach (var item in items)
            {
                item.ReviewState = "failed";
            }

            EnqueueOnUI(RecalculateCandidateMetrics);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref finished, 1);
        }
    }

    private void RunScanAll()
    {
        if (!IsEnabled || IsOptimizing)
        {
            return;
        }

        ClearOptimizationCandidates(null);

        _optimizationCts?.Dispose();
        _optimizationCts = new CancellationTokenSource();
        var token = _optimizationCts.Token;

        IsOptimizing = true;
        ResetScanProgress();
        ScanPhase = ScanPhase.Scanning;
        OptimizationWorkflowStatus = "AIHub_OptimizationWorkflow_Scanning".GetLocalizedOrFallback("Scanning...");
        BeginStage("Preparing scan");

        Task.Run(async () =>
        {
            try
            {
                EnqueueOnUI(() =>
                {
                    EndStage("Preparing scan");
                    BeginStage("Scan cache locations");
                    BeginStage("Scan Downloads");
                }, token);

                var downloadsTask = _downloadOrganizerService.ScanAsync(token);
                var cacheTask = _cacheCleanupService.ScanAsync(token);

                await Task.WhenAll(downloadsTask, cacheTask);

                var downloads = await downloadsTask;
                var caches = await cacheTask;
                var analysis = await AnalyzeOptimizationCandidatesAsync(downloads.Concat(caches).ToList(), token);
                downloads = analysis.Items.Where(item => item.Action == "move").ToList();
                caches = analysis.Items.Where(item => item.Action == "delete").ToList();

                long dlBytes = downloads.Sum(d => d.SizeInBytes);
                long cacheBytes = caches.Sum(c => c.SizeInBytes);

                var allCandidates = new List<TempFileInfo>();
                allCandidates.AddRange(downloads);
                allCandidates.AddRange(caches);

                EnqueueOnUI(() =>
                {
                    EndStage("Scan cache locations");
                    EndStage("Scan Downloads");
                    EndStage("Finish");
                    ScanPhase = ScanPhase.SelectingTargets;

                    DownloadsItemCount = downloads.Count;
                    DownloadsBytes = dlBytes;
                    DownloadsStatusText = "AIHub_DownloadsStatus_FoundFormat".FormatLocalized("{0} item(s) found ({1})", downloads.Count, FormatBytes(dlBytes));

                    TemporaryFilesItemCount = caches.Count;
                    TemporaryFilesBytes = cacheBytes;
                    TemporaryFilesStatusText = "AIHub_TemporaryFilesStatus_FoundFormat".FormatLocalized("{0} item(s) found ({1})", caches.Count, FormatBytes(cacheBytes));

                    OptimizationCandidates.Clear();
                    foreach (var item in allCandidates)
                    {
                        item.PropertyChanged += Candidate_PropertyChanged;
                        OptimizationCandidates.Add(item);
                    }

                    RecalculateCandidateMetrics();
                    OptimizationWorkflowStatus = analysis.Describe(PreferChinese());
                    if (_cacheCleanupService.ScanTruncated)
                    {
                        OptimizationWorkflowStatus += "AIHub_Optimization_ScanLimitReached".GetLocalizedOrFallback(" · Scan limit reached; showing reviewed subset");
                    }

                    ShowStatus(analysis.Describe(PreferChinese()), analysis.IsSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Warning, optimization: true);
                }, token);
            }
            catch (OperationCanceledException)
            {
                EnqueueOnUI(() => StopOptimization(cancelled: true));
            }
            catch (Exception ex)
            {
                Logger.LogError($"Optimization scan failed: type={ex.GetType().Name}, hresult=0x{ex.HResult:X8}");
                EnqueueOnUI(() => StopOptimization(cancelled: false));
            }
            finally
            {
                EnqueueOnUI(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        StopOptimization(cancelled: true);
                    }

                    IsOptimizing = false;
                });
            }
        });
    }

    private void RunScanDownloads()
    {
        if (!IsEnabled || IsOptimizing)
        {
            return;
        }

        ClearOptimizationCandidates("move");

        _optimizationCts?.Dispose();
        _optimizationCts = new CancellationTokenSource();
        var token = _optimizationCts.Token;

        IsOptimizing = true;
        ResetScanProgress();
        ScanPhase = ScanPhase.Scanning;
        OptimizationWorkflowStatus = "AIHub_OptimizationWorkflow_ScanningDownloads".GetLocalizedOrFallback("Scanning Downloads root...");
        BeginStage("Scan Downloads");

        Task.Run(async () =>
        {
            try
            {
                var downloads = await _downloadOrganizerService.ScanAsync(token);
                var analysis = await AnalyzeOptimizationCandidatesAsync(downloads, token, "move");
                downloads = analysis.Items.ToList();
                long dlBytes = downloads.Sum(d => d.SizeInBytes);

                EnqueueOnUI(() =>
                {
                    EndStage("Scan Downloads");
                    EndStage("Finish");
                    ScanPhase = ScanPhase.SelectingTargets;

                    DownloadsItemCount = downloads.Count;
                    DownloadsBytes = dlBytes;
                    DownloadsStatusText = "AIHub_DownloadsStatus_FoundFormat".FormatLocalized("{0} item(s) found ({1})", downloads.Count, FormatBytes(dlBytes));

                    // Remove previous download items
                    var toRemove = OptimizationCandidates.Where(c => c.Action == "move").ToList();
                    foreach (var r in toRemove)
                    {
                        OptimizationCandidates.Remove(r);
                    }

                    foreach (var item in downloads)
                    {
                        item.PropertyChanged += Candidate_PropertyChanged;
                        OptimizationCandidates.Add(item);
                    }

                    RecalculateCandidateMetrics();
                    OptimizationWorkflowStatus = analysis.Describe(PreferChinese());
                    if (_cacheCleanupService.ScanTruncated)
                    {
                        OptimizationWorkflowStatus += "AIHub_Optimization_ScanLimitReached".GetLocalizedOrFallback(" · Scan limit reached; showing reviewed subset");
                    }

                    ShowStatus(analysis.Describe(PreferChinese()), analysis.IsSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Warning, optimization: true);
                }, token);
            }
            catch (OperationCanceledException)
            {
                EnqueueOnUI(() => StopOptimization(cancelled: true));
            }
            catch (Exception ex)
            {
                Logger.LogError($"Optimization scan failed: type={ex.GetType().Name}, hresult=0x{ex.HResult:X8}");
                EnqueueOnUI(() => StopOptimization(cancelled: false));
            }
            finally
            {
                EnqueueOnUI(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        StopOptimization(cancelled: true);
                    }

                    IsOptimizing = false;
                });
            }
        });
    }

    private void RunScanTemporaryFiles()
    {
        if (!IsEnabled || IsOptimizing)
        {
            return;
        }

        ClearOptimizationCandidates("delete");

        _optimizationCts?.Dispose();
        _optimizationCts = new CancellationTokenSource();
        var token = _optimizationCts.Token;

        IsOptimizing = true;
        ResetScanProgress();
        ScanPhase = ScanPhase.Scanning;
        OptimizationWorkflowStatus = "AIHub_OptimizationWorkflow_ScanningTempFiles".GetLocalizedOrFallback("Scanning Temporary files...");
        BeginStage("Scan cache locations");

        Task.Run(async () =>
        {
            try
            {
                var caches = await _cacheCleanupService.ScanAsync(token);
                var analysis = await AnalyzeOptimizationCandidatesAsync(caches, token, "delete");
                caches = analysis.Items.ToList();
                long cacheBytes = caches.Sum(c => c.SizeInBytes);

                EnqueueOnUI(() =>
                {
                    EndStage("Scan cache locations");
                    EndStage("Finish");
                    ScanPhase = ScanPhase.SelectingTargets;

                    TemporaryFilesItemCount = caches.Count;
                    TemporaryFilesBytes = cacheBytes;
                    TemporaryFilesStatusText = "AIHub_TemporaryFilesStatus_FoundFormat".FormatLocalized("{0} item(s) found ({1})", caches.Count, FormatBytes(cacheBytes));

                    // Remove previous cache items
                    var toRemove = OptimizationCandidates.Where(c => c.Action == "delete").ToList();
                    foreach (var r in toRemove)
                    {
                        OptimizationCandidates.Remove(r);
                    }

                    foreach (var item in caches)
                    {
                        item.PropertyChanged += Candidate_PropertyChanged;
                        OptimizationCandidates.Add(item);
                    }

                    RecalculateCandidateMetrics();
                    OptimizationWorkflowStatus = analysis.Describe(PreferChinese());
                    if (_cacheCleanupService.ScanTruncated)
                    {
                        OptimizationWorkflowStatus += "AIHub_Optimization_ScanLimitReached".GetLocalizedOrFallback(" · Scan limit reached; showing reviewed subset");
                    }

                    ShowStatus(analysis.Describe(PreferChinese()), analysis.IsSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Warning, optimization: true);
                }, token);
            }
            catch (OperationCanceledException)
            {
                EnqueueOnUI(() => StopOptimization(cancelled: true));
            }
            catch (Exception ex)
            {
                Logger.LogError($"Optimization scan failed: type={ex.GetType().Name}, hresult=0x{ex.HResult:X8}");
                EnqueueOnUI(() => StopOptimization(cancelled: false));
            }
            finally
            {
                EnqueueOnUI(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        StopOptimization(cancelled: true);
                    }

                    IsOptimizing = false;
                });
            }
        });
    }

    private void SelectAllCandidates()
    {
        foreach (var item in OptimizationCandidates)
        {
            item.IsSelected = true;
        }
        RecalculateCandidateMetrics();
    }

    private void StopOptimization(bool cancelled)
    {
        _scanProgress.Reset();
        ScanPhase = cancelled ? ScanPhase.Idle : ScanPhase.Failed;
        OptimizationWorkflowStatus = cancelled ? "AIHub_OptimizationWorkflow_ScanCancelled".GetLocalizedOrFallback("Scan cancelled") : "AIHub_OptimizationWorkflow_ScanFailed".GetLocalizedOrFallback("Scan failed");
        PublishScanProgress();
        ShowStatus(OptimizationWorkflowStatus, cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Error, optimization: true);
    }

    private void DeselectAllCandidates()
    {
        foreach (var item in OptimizationCandidates)
        {
            item.IsSelected = false;
        }
        RecalculateCandidateMetrics();
    }

    private void Candidate_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TempFileInfo.IsSelected))
        {
            RecalculateCandidateMetrics();
        }
    }

    private string DescribeOptimizationScope(string action, bool scanned)
    {
        var items = _observedOptimizationItems.Concat(OptimizationCandidates).Distinct().Where(item => item.Action == action).ToList();
        if (items.Count == 0)
        {
            return scanned ? "AIHub_DescribeScope_NoEligible".GetLocalizedOrFallback("No eligible files found") : "AIHub_DescribeScope_NotScanned".GetLocalizedOrFallback("Not scanned yet");
        }

        int approved = OptimizationCandidates.Count(item => item.Action == action);
        return "AIHub_DescribeScope_FoundFormat".FormatLocalized(
            "{0:N0} files found · {1} · {2:N0} actionable",
            items.Count, FormatBytes(items.Sum(item => item.SizeInBytes)), approved);
    }

    private void RecalculateCandidateMetrics()
    {
        OptimizationCategories.Clear();
        DownloadCategories.Clear();
        CacheCategories.Clear();
        CacheGroups.Clear();
        var approved = OptimizationCandidates.ToHashSet();
        foreach (var items in _observedOptimizationItems.Concat(OptimizationCandidates).Distinct().GroupBy(item => (item.Action, item.Category, Root: string.IsNullOrEmpty(item.CacheRoot) ? Path.GetDirectoryName(item.FilePath) : item.CacheRoot))
            .OrderBy(group => group.Key.Action == "move" ? 0 : 1).ThenByDescending(group => group.Sum(item => item.SizeInBytes)))
        {
            var snapshot = items.ToList();
            var actionable = snapshot.Where(approved.Contains).ToList();
            var group = new OptimizationCategoryViewModel(snapshot, actionable.Count, () => RunExecuteOptimization(actionable)) { IsEnabled = IsEnabled && !IsOptimizing && actionable.Count > 0 };
            OptimizationCategories.Add(group);
            (items.Key.Action == "move" ? DownloadCategories : CacheCategories).Add(group);
        }

        foreach (string family in new[] { "development", "packages", "editors", "browsers", "system" })
        {
            var categories = CacheCategories.Where(category => category.Family == family).ToList();
            if (categories.Count == 0)
            {
                continue;
            }

            var actionable = categories.SelectMany(category => category.Items).Where(approved.Contains).ToList();
            var projects = new List<OptimizationGroupViewModel>();
            if (family == "development")
            {
                foreach (var project in categories.GroupBy(category => category.Items[0].DevelopmentRepository, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(project => project.Sum(category => category.Items.Sum(item => item.SizeInBytes))))
                {
                    var projectItems = project.SelectMany(category => category.Items).Where(approved.Contains).ToList();
                    projects.Add(new OptimizationGroupViewModel("project", project.ToList(), () => RunExecuteOptimization(projectItems))
                    {
                        Name = Path.GetFileName(project.Key),
                        Location = project.Key,
                    });
                }
            }

            CacheGroups.Add(new OptimizationGroupViewModel(family, categories, () => RunExecuteOptimization(actionable)) { Projects = projects });
        }

        OnPropertyChanged(nameof(DownloadsResultSummary));
        OnPropertyChanged(nameof(CacheResultSummary));
        OnPropertyChanged(nameof(OptimizationResultsSummary));
        OnPropertyChanged(nameof(ScannedCandidatesText));
        OnPropertyChanged(nameof(ApprovedCandidatesText));
        OnPropertyChanged(nameof(CleanableCapacityText));
        OnPropertyChanged(nameof(HasDownloadResults));
        OnPropertyChanged(nameof(HasCacheResults));
        CandidatesCount = OptimizationCandidates.Count;
        CandidatesBytes = OptimizationCandidates.Sum(c => c.SizeInBytes);

        var selected = OptimizationCandidates.Where(c => c.IsSelected).ToList();
        SelectedCandidatesCount = selected.Count;
        SelectedCandidatesBytes = selected.Sum(c => c.SizeInBytes);

        OnPropertyChanged(nameof(CandidatesSummary));
        OnPropertyChanged(nameof(SelectedCandidatesSummary));
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(CanExecuteOptimization));
        RefreshCommands();
    }

    private void RunExecuteOptimization(List<TempFileInfo> requestedItems = null)
    {
        if (!IsEnabled || IsOptimizing)
        {
            return;
        }

        _optimizationCts?.Dispose();
        _optimizationCts = new CancellationTokenSource();
        var token = _optimizationCts.Token;

        var available = OptimizationCandidates.ToHashSet();
        var selectedItems = (requestedItems ?? OptimizationCandidates.Where(c => c.IsSelected).ToList())
            .Where(available.Contains)
            .OrderBy(item => item.FileName.Equals(".nupkg.metadata", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        if (selectedItems.Count == 0)
        {
            return;
        }

        IsOptimizing = true;
        IsExecutingOptimization = true;
        OptimizationProgress = 0;
        foreach (var group in OptimizationCategories)
        {
            group.Processed = 0;
        }

        var categoryByItem = OptimizationCategories.SelectMany(group => group.Items.Select(item => (item, group))).ToDictionary(pair => pair.item, pair => pair.group);
        ScanPhase = ScanPhase.ExecutionPending;
        OptimizationWorkflowStatus = "AIHub_OptimizationWorkflow_ExecutingConfirmed".GetLocalizedOrFallback("Executing confirmed actions...");

        Task.Run(() =>
        {
            var outcome = new OptimizationOutcome();
            int processed = 0;
            var processedItems = new List<TempFileInfo>();

            try
            {
                foreach (var item in selectedItems)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        if (item.Action == "delete")
                        {
                            // Delete only the reviewed leaf snapshot after local revalidation.
                            var (ok, error) = _cacheCleanupService.CleanupItem(item);
                            outcome.Add(ok
                                ? OptimizationItemResult.Success(item.FileName, "delete")
                                : OptimizationItemResult.Failure(item.FileName, "delete", error));
                        }
                        else if (item.Action == "move")
                        {
                            // Organize Downloads within root
                            bool ok = _downloadOrganizerService.OrganizeItem(item);
                            outcome.Add(ok
                                ? OptimizationItemResult.Success(item.FileName, "move")
                                : OptimizationItemResult.Failure(item.FileName, "move", "AIHub_Optimization_DownloadIneligible".GetLocalizedOrFallback("File is ineligible, in use or changed; scan again")));
                        }
                    }
                    catch (Exception ex)
                    {
                        // Keep the reason: a silent count tells the user nothing actionable.
                        outcome.Add(OptimizationItemResult.Failure(item.FileName, item.Action, ex.Message));
                    }

                    if (outcome.Results.Count > 0 && outcome.Results[^1].Succeeded)
                    {
                        processedItems.Add(item);
                    }
                    else if (outcome.Results.Count > 0)
                    {
                        item.ExecutionError = outcome.Results[^1].Reason ?? "AIHub_Optimization_ActionFailed".GetLocalizedOrFallback("Action failed; scan again");
                    }

                    processed++;
                    int done = processed;
                    EnqueueOnUI(() =>
                    {
                        OptimizationProgress = 100.0 * done / selectedItems.Count;
                        if (categoryByItem.TryGetValue(item, out var group))
                        {
                            group.Processed++;
                        }

                        OptimizationWorkflowStatus = "AIHub_OptimizationWorkflow_ExecutingProgressFormat".FormatLocalized(
                            "Executing confirmed actions... ({0}/{1})", done, selectedItems.Count);
                    });
                }

                EnqueueOnUI(() =>
                {
                    var removed = processedItems.ToHashSet();
                    _observedOptimizationItems.RemoveAll(removed.Contains);
                    var remaining = OptimizationCandidates.Where(item => !removed.Contains(item)).ToList();
                    foreach (var item in processedItems)
                    {
                        item.PropertyChanged -= Candidate_PropertyChanged;
                    }
                    OptimizationCandidates.Clear();
                    foreach (var item in remaining)
                    {
                        OptimizationCandidates.Add(item);
                    }

                    RecalculateCandidateMetrics();

                    // Surface the actual failure reasons instead of an opaque count.
                    var failureDetail = outcome.DescribeFailures(PreferChinese());
                    ScanPhase = token.IsCancellationRequested ? ScanPhase.Idle : ScanPhase.SelectingTargets;
                    OptimizationWorkflowStatus = token.IsCancellationRequested
                        ? "AIHub_OptimizationWorkflow_ExecutionCancelledFormat".FormatLocalized("Execution cancelled ({0}/{1} processed)", processed, selectedItems.Count)
                        : outcome.HasFailures
                        ? "AIHub_OptimizationWorkflow_ExecutionCompletedWithFailuresFormat".FormatLocalized("Execution completed ({0} failed)", outcome.FailedCount)
                        : "AIHub_OptimizationWorkflow_ExecutionCompleted".GetLocalizedOrFallback("Execution completed");

                    ShowStatus(
                        token.IsCancellationRequested ? OptimizationWorkflowStatus : failureDetail is null
                            ? outcome.Describe(PreferChinese())
                            : $"{outcome.Describe(PreferChinese())} — {failureDetail}",
                        token.IsCancellationRequested || outcome.HasFailures ? InfoBarSeverity.Warning : InfoBarSeverity.Success, optimization: true);
                });
            }
            catch (Exception ex)
            {
                Logger.LogError($"Optimization execution failed: type={ex.GetType().Name}, hresult=0x{ex.HResult:X8}");
                EnqueueOnUI(() => StopOptimization(cancelled: token.IsCancellationRequested));
            }
            finally
            {
                EnqueueOnUI(() => IsOptimizing = false);
            }
        });
    }

    private void EnqueueOnUI(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed)
                {
                    action();
                }
            });
        }
        else
        {
            action();
        }
    }

    private void EnqueueOnUI(Action action, CancellationToken token) => EnqueueOnUI(() =>
    {
        if (!token.IsCancellationRequested)
        {
            action();
        }
    });

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} GB", bytes / (1024.0 * 1024 * 1024));
        }

        if (bytes >= 1024 * 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} MB", bytes / (1024.0 * 1024));
        }

        if (bytes >= 1024)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1} KB", bytes / 1024.0);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0} B", bytes);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _readinessCts?.Cancel();
            AiHub?.Dispose();
            AiHubEngine.Current.StateChanged -= OnEngineStateChanged;
            _auditCts?.Cancel();
            _auditCts?.Dispose();
            _optimizationCts?.Cancel();
            _optimizationCts?.Dispose();
        }
    }
}
