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

        bool isZh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var parts = new List<string>();
        if (HighCount > 0)
        {
            parts.Add(isZh ? $"{HighCount} 项高" : $"{HighCount} high");
        }

        if (MediumCount > 0)
        {
            parts.Add(isZh ? $"{MediumCount} 项中" : $"{MediumCount} medium");
        }

        if (LowCount > 0)
        {
            parts.Add(isZh ? $"{LowCount} 项低" : $"{LowCount} low");
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
            bool isZh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            return isZh ? $"{Count} 项发现" : (Count == 1 ? "1 finding" : $"{Count} findings");
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
    private readonly EventLogService _eventLogService = new();
    private readonly CacheCleanupService _cacheCleanupService = new();
    private readonly DownloadOrganizerService _downloadOrganizerService = new();
    private readonly RecycleBinHelper _recycleBinHelper = new();
    private readonly AuditHistoryStorage _auditHistoryStorage = new();
    private readonly SecurityPolicyService _securityService = new();
    private readonly ObservableCollection<AiReportFinding> _aiReportFindings = new();
    private readonly List<SecurityEvent> _lastAuditEvents = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _scheduleTimer;
    private DateTime? _lastCompletedAuditUtc;

    private CancellationTokenSource _currentCts;
    private bool _disposed;
    private int _activeTabIndex;

    // Security Audit state
    private int _healthScore = 100;
    private string _healthScoreGrade = IsChinese ? "良好" : "Good";
    private int _highCount;
    private int _mediumCount;
    private int _lowCount;
    private string _auditTimestampText = IsChinese ? "尚未扫描" : "Not scanned yet";
    private int _selectedScopeIndex; // 0 = 1d, 1 = 2d, 2 = 1w
    private string _severityFilter = "All"; // "All", "High", "Medium", "Low"
    private string _sourceFilter = "All"; // "All", "Application", "Security", "System", "Setup", "ForwardedEvents"
    private bool _isPriorityView;
    private bool _showAllFindings = true;
    private string _findingSearchText = string.Empty;
    private bool _isAuditing;
    private string _auditStatusText = IsChinese ? "就绪" : "Ready";
    private bool _isAiAnalyzing;
    private string _aiStatusText = string.Empty;
    private bool _hasAiReport;
    private string _aiReportSummaryText = string.Empty;
    private string _aiReportMetaText = string.Empty;

    private string _healthVerdict = IsChinese ? "系统状态良好 · 多数指标稳定" : "System state healthy · Majority of indicators stable";
    private string _healthSummaryText = IsChinese ? "尚未进行全面事件扫描，点击上方扫描按钮以开始。" : "Comprehensive event scan has not been performed yet. Click scan above to begin.";
    private string _selectedAuditLabel = IsChinese ? "最新审计" : "Latest audit";
    private DateTimeOffset? _selectedAuditDate = DateTimeOffset.Now;
    private string _activityScansText = "1";
    private string _activityFindingsText = "0";
    private string _activeDaysText = "1";
    private string _healthAverageText = "100";
    private string _scanTypeText = IsChinese ? "快速扫描" : "Fast scan";
    private string _finishedText = IsChinese ? "刚刚" : "Just now";
    private string _windowText = IsChinese ? "最近 1 小时" : "Last 1 hour";
    private string _eventCountText = IsChinese ? "0 事件" : "0 events";

    // Optimization state
    private bool _isOptimizing;
    private string _optimizationWorkflowStatus = IsChinese ? "就绪，等待扫描" : "Ready to scan";
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
        FastScanCommand = new RelayCommand(() => RunAudit(fast: true), () => IsEnabled && !_isAuditing);
        FullScanCommand = new RelayCommand(() => RunAudit(fast: false), () => IsEnabled && !_isAuditing);
        SelectLatestCommand = new RelayCommand(LoadRecentAudit, () => IsEnabled && !_isAuditing);
        ReanalyzeRangeCommand = new RelayCommand(() => RunAudit(fast: false), () => IsEnabled && !_isAuditing);
        DeepAnalyzeCommand = new RelayCommand(RunDeepAnalysis, () => CanRunDeepAnalysis);

        ScanAllCommand = new RelayCommand(RunScanAll, () => IsEnabled && !_isOptimizing);
        ScanDownloadsCommand = new RelayCommand(RunScanDownloads, () => IsEnabled && !_isOptimizing);
        ScanTemporaryFilesCommand = new RelayCommand(RunScanTemporaryFiles, () => IsEnabled && !_isOptimizing);
        SelectAllCandidatesCommand = new RelayCommand(SelectAllCandidates, () => HasCandidates && !_isOptimizing);
        DeselectAllCandidatesCommand = new RelayCommand(DeselectAllCandidates, () => HasCandidates && !_isOptimizing);
        ExecuteOptimizationCommand = new RelayCommand(RunExecuteOptimization, () => CanExecuteOptimization);

        // Scheduled audits: check every few minutes whether the configured cadence is due.
        _scheduleTimer = _dispatcherQueue.CreateTimer();
        _scheduleTimer.Interval = TimeSpan.FromMinutes(5);
        _scheduleTimer.Tick += (_, _) => RunScheduledAuditIfDue();

        // Load cached audit history
        LoadRecentAudit();
    }

    public static bool IsChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

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
                _generalSettingsRepository.SettingsConfig.Enabled.AiHub = value;

                try
                {
                    new AiHubSettingsStore().Update(c => c.IsEnabled = value);
                    AiHubEngine.RaiseStateChanged();
                }
                catch (Exception ex)
                {
                    Logger.LogError("Failed to persist the AI Hub enabled state", ex);
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(CanToggle));

                if (!value)
                {
                    _currentCts?.Cancel();
                    IsAuditing = false;
                    IsOptimizing = false;
                    OptimizationWorkflowStatus = IsChinese ? "就绪，等待扫描" : "Ready to scan";
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
        private set => Set(ref _healthScore, value);
    }

    public string HealthScoreGrade
    {
        get => _healthScoreGrade;
        private set => Set(ref _healthScoreGrade, value);
    }

    public string HealthScoreLabel => IsChinese ? "系统健康评分" : "Health score";

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
        set => Set(ref _selectedAuditDate, value);
    }

    public DateTimeOffset LatestSelectableAuditDate => DateTimeOffset.Now;

    public int HighCount
    {
        get => _highCount;
        private set => Set(ref _highCount, value);
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
    /// Selected full-scan range: 0 = 1 day, 1 = 2 days, 2 = 1 week.
    /// </summary>
    public int FullScanRangeIndex
    {
        get => _selectedScopeIndex;
        set
        {
            if (Set(ref _selectedScopeIndex, value))
            {
                RunAudit(fast: false);
            }
        }
    }

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
                    // Persisting the mode is best-effort; the scan below still runs.
                }

                RunAudit(fast: false);
            }
        }
    }

    public bool IsFullAuditMode => AuditModeIndex == 1;

    /// <summary>
    /// Full mode is only useful when elevated; without elevation the Security channel
    /// cannot be read, so the toggle stays disabled rather than silently degrading.
    /// </summary>
    public bool CanUseFullAuditMode => App.IsElevated;

    public string AuditModeLabel => IsChinese ? "审计模式" : "Audit mode";

    public string AuditModeHint
    {
        get
        {
            if (!IsFullAuditMode)
            {
                return IsChinese ? "拓展模式：标准权限，读取 System/Application/Setup/ForwardedEvents" : "Extended: standard privileges, reads System/Application/Setup/ForwardedEvents";
            }

            return App.IsElevated
                ? (IsChinese ? "完整模式：额外读取 Security 与防火墙通道" : "Full: additionally reads the Security and Firewall channels")
                : (IsChinese ? "完整模式需要管理员权限；当前未提权，Security 通道将不可读" : "Full mode needs elevation; the Security channel is unreadable without it");
        }
    }

    public string FullScanRangeHint => IsChinese ? "选择全量扫描时间跨度" : "Select full scan time range";

    public string RangeOption1DayLabel => IsChinese ? "1 天" : "1d";

    public string RangeOption2DaysLabel => IsChinese ? "2 天" : "2d";

    public string RangeOption1WeekLabel => IsChinese ? "1 周" : "1w";

    public string ScanOptionsLabel => IsChinese ? "扫描选项" : "Scan options";

    public string ReanalyzeRangeHint => IsChinese ? "重新分析选定时段内的日志" : "Reanalyze events in selected range";

    /// <summary>Summarizes the active scan mode; reflects the real setting, not a fixed claim.</summary>
    public string ScopeHint => IsFullAuditMode
        ? (IsChinese ? "完整模式 · 需提权 · 含 Security 与防火墙" : "Full mode · Elevation required · Includes Security and Firewall")
        : (IsChinese ? "拓展模式 · 无需提权 · 跳过 Security" : "Extended mode · No elevation · Skips Security");

    public string FindingsHeading => IsChinese ? "发现列表" : "Findings";

    public string FindingsSummaryText => IsChinese
        ? $"当前筛选下记录了 {FindingSections.Sum(s => s.Count)} 项发现"
        : $"{FindingSections.Sum(s => s.Count)} findings recorded under current filter";

    public string AllFindingsLabel => IsChinese ? $"全部发现 ({AllIssues.Count})" : $"All findings ({AllIssues.Count})";

    public string PriorityPreviewLabel => IsChinese ? $"前 {PriorityFindings.Count} 项优先处理建议" : $"Top {PriorityFindings.Count} priority action items";

    public string OverviewLabel => IsChinese ? $"全部 ({AllIssues.Count})" : $"All ({AllIssues.Count})";
    public string ApplicationLabel => GetSourceFilterLabel("Application", IsChinese ? "应用日志" : "Application");
    public string SecurityLabel => GetSourceFilterLabel("Security", IsChinese ? "安全" : "Security");
    public string SystemLabel => GetSourceFilterLabel("System", IsChinese ? "系统" : "System");
    public string SetupLabel => GetSourceFilterLabel("Setup", IsChinese ? "设置" : "Setup");
    public string ForwardedLabel => GetSourceFilterLabel("ForwardedEvents", IsChinese ? "转发事件" : "Forwarded");

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

    public bool HasAuditData => AllIssues.Count > 0;
    public bool ShowNoAudit => AllIssues.Count == 0 && !IsAuditing;
    public bool ShowEmptyIssues => HasAuditData && TotalFindings == 0;
    public bool ShowNoFilterMatches => HasAuditData && FindingSections.Count == 0 && TotalFindings > 0;
    public string EmptyFindingsTitle => IsChinese ? "未检测到安全或稳定性异常" : "No security or reliability anomalies detected";
    public string EmptyFindingsDescription => IsChinese ? "系统所有日志均在基线运行标准内。" : "All system events are operating within healthy parameters.";

    // Pipeline Labels
    public string PipelineTitle => IsChinese ? "AI Hub 执行链路" : "AI Hub Pipeline";
    public string PipelinePolicyText => IsChinese ? "安全策略 + 专有 agents.md" : "Security policy + AGENTS.md";
    public string PipelineSummaryText => IsChinese ? "分析摘要" : "Analysis summary";
    public string PipelineActionText => IsChinese ? "建议操作" : "Suggested action";

    // Tab Labels
    public string SecurityAuditTabLabel => IsChinese ? "安全审计" : "Security Audit";
    public string OptimizationTabLabel => IsChinese ? "系统优化" : "Optimization";

    // Audit Header & Action Labels
    public string SecurityAuditTitle => IsChinese ? "Windows 事件安全审计" : "Windows Event Security Audit";
    public string FastScanLabel => IsChinese ? "快速扫描" : "Fast scan";
    public string FullScanLabel => IsChinese ? "全量审计" : "Full scan";
    public string ReanalyzeLabel => IsChinese ? "重新分析选定时段" : "Reanalyze selected range";

    /// <summary>Label for the AI deep-analysis action (runs the configured kernel).</summary>
    public string DeepAnalyzeLabel => IsChinese ? "AI 深度分析" : "AI deep analysis";

    /// <summary>Explains what the deep-analysis action does and when it is unavailable.</summary>
    public string DeepAnalyzeHint => IsChinese
        ? "将当前发现（高危优先，最多 12 项）提交给已配置的 AI 内核，按 security-audit 策略生成补充诊断与建议。不会自动执行任何操作。"
        : "Sends the current findings (highest severity first, up to 12) to the configured AI kernel for supplementary diagnostics under the security-audit policy. No action is executed automatically.";

    // Overview Card
    public string HealthOverviewLabel => IsChinese ? "健康总览" : "Health overview";
    public string LatestAuditLabel => IsChinese ? "最新审计" : "Latest audit";
    public string FindingsInAuditLabel => IsChinese ? "本次审计发现" : "Findings in this audit";
    public string HighLabel => IsChinese ? "高" : "High";
    public string MediumLabel => IsChinese ? "中" : "Medium";
    public string LowLabel => IsChinese ? "低" : "Low";

    // Activity Stats
    public string AuditActivityLabel => IsChinese ? "审计动态" : "Audit activity";
    public string AuditsLabel => IsChinese ? "审计次数" : "Audits";
    public string RecordedFindingsLabel => IsChinese ? "已记录发现" : "Recorded findings";
    public string ActiveDaysLabel => IsChinese ? "活跃天数" : "Active days";
    public string HealthAverageLabel => IsChinese ? "平均健康分" : "Health average";

    // Details Grid
    public string ScanLabel => IsChinese ? "扫描" : "Scan";
    public string FinishedLabel => IsChinese ? "完成时间" : "Finished";
    public string WindowLabel => IsChinese ? "时间窗口" : "Window";
    public string EventsReadLabel => IsChinese ? "读取事件" : "Events read";

    // Filters & Search
    public string FilterAllLabel => IsChinese ? "全部" : "All";
    public string PriorityViewLabel => IsChinese ? "优先处理视图" : "Priority view";
    public string SearchPlaceholder => IsChinese ? "搜索事件 ID、提供者或标题..." : "Search event ID, provider or title...";
    public string NoMatchingFindingsTitle => IsChinese ? "未匹配到发现项" : "No matching findings";
    public string NoMatchingFindingsDescription => IsChinese ? "尝试更换搜索关键词、日志通道或严重度筛选。" : "Try a different search keyword, channel, or severity filter.";

    // Optimization Tab Labels
    public string OptimizationDescription => IsChinese ? "整理“下载”目录并清理安全缓存白名单项。" : "Organize Downloads and clean safe cache-whitelisted items.";
    public string ScanAllLabel => IsChinese ? "全量扫描" : "Scan all";
    public string OptimizationWorkflowTitle => IsChinese ? "优化工作流" : "Optimization workflow";
    public string OptimizationWorkflowDescription => IsChinese ? "在执行任何写入操作前，先扫描、审查并确认。" : "Scan, review and confirm before any write action.";
    public string CandidatesLabel => IsChinese ? "候选项目" : "Candidates";
    public string DetectedObjectsLabel => IsChinese ? "检测到的对象" : "Detected objects";
    public string SelectedItemsLabel => IsChinese ? "选定项目" : "Selected items";
    public string StagedForExecutionLabel => IsChinese ? "待执行状态" : "Staged for execution";
    public string SafetyBoundaryLabel => IsChinese ? "安全边界" : "Safety boundary";
    public string DownloadsLabel => IsChinese ? "下载目录" : "Downloads";
    public string DownloadsRootOnlyLabel => IsChinese ? "仅根目录" : "Downloads root only";
    public string DownloadsDescription => IsChinese ? "下载扫描在移动前预览文件类型、目标路径与冲突。" : "Download scan previews types, destinations and collisions before any move.";
    public string TemporaryFilesLabel => IsChinese ? "临时文件" : "Temporary files";
    public string WhitelistOnlyLabel => IsChinese ? "仅白名单" : "Whitelist only";
    public string CacheDescription => IsChinese ? "缓存清理严格使用许可路径，仅移动选定项至回收站。" : "Cache scan uses approved locations and moves selected items only to the recycle bin.";
    public string ScanButtonLabel => IsChinese ? "扫描" : "Scan";
    public string SelectAllLabel => IsChinese ? "全选" : "Select all";
    public string DeselectAllLabel => IsChinese ? "取消全选" : "Deselect all";
    public string ConfirmAndExecuteLabel => IsChinese ? "确认并执行" : "Confirm and execute";
    public string ReadOnlyScanTitle => IsChinese ? "只读扫描" : "Read-only scan";
    public string ReadOnlyScanDescription => IsChinese ? "确认前仅读取文件元数据。" : "Only file metadata is read before confirmation.";
    public string PathBoundariesTitle => IsChinese ? "路径边界" : "Path boundaries";
    public string PathBoundariesDescription => IsChinese ? "下载目录及缓存白名单均通过二次校验。" : "Downloads and cache whitelists are validated again.";
    public string RecoverableCleanupTitle => IsChinese ? "可逆清理" : "Recoverable cleanup";
    public string RecoverableCleanupDescription => IsChinese ? "清理操作移入回收站，杜绝不可逆彻底删除。" : "Cleanup uses recycle bin and never permanently deletes.";

    public AuditIssueEnhanced AuditPriorityFinding => PriorityFindings.FirstOrDefault();
    public bool HasAuditPriority => AuditPriorityFinding != null;
    public string PriorityCalloutBadgeText => IsChinese ? "优先处理" : "Review first";
    public string AuditPriorityTitle => AuditPriorityFinding?.DisplayTitle ?? string.Empty;
    public string AuditPriorityAction => AuditPriorityFinding?.PriorityActionText ?? string.Empty;
    public string AuditSummaryEmptyTitle => IsChinese ? "未发现优先处理项" : "No priority items";
    public string AuditSummaryEmptyText => IsChinese ? "当前审计未发现需要立即干预的高风险问题。" : "No high-risk issues requiring immediate intervention.";
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

    // Optimization Properties (1:1 with Screenshot)
    public bool IsOptimizing
    {
        get => _isOptimizing;
        private set
        {
            if (Set(ref _isOptimizing, value))
            {
                RefreshCommands();
            }
        }
    }

    public string OptimizationWorkflowStatus
    {
        get => _optimizationWorkflowStatus;
        private set => Set(ref _optimizationWorkflowStatus, value);
    }

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

    public string SafetyBoundary => "Read-only";

    public string SafetyBoundaryDesc => "Recycle Bin only; no permanent delete";

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

    public bool HasCandidates => OptimizationCandidates.Count > 0;

    public bool CanExecuteOptimization => IsEnabled && !IsOptimizing && SelectedCandidatesCount > 0;

    public ICommand ScanAllCommand { get; }

    public ICommand ScanDownloadsCommand { get; }

    public ICommand ScanTemporaryFilesCommand { get; }

    public ICommand SelectAllCandidatesCommand { get; }

    public ICommand DeselectAllCandidatesCommand { get; }

    public ICommand ExecuteOptimizationCommand { get; }

    // Status Bar Properties
    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        private set => Set(ref _statusSeverity, value);
    }

    public bool IsStatusOpen
    {
        get => _isStatusOpen;
        set => Set(ref _isStatusOpen, value);
    }

    // Methods & Execution Logic
    public void RefreshEnabledState()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsEnabledGpoConfigured));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ((RelayCommand)FastScanCommand).OnCanExecuteChanged();
        ((RelayCommand)FullScanCommand).OnCanExecuteChanged();
        ((RelayCommand)SelectLatestCommand).OnCanExecuteChanged();
        ((RelayCommand)ReanalyzeRangeCommand).OnCanExecuteChanged();
        ((RelayCommand)ScanAllCommand).OnCanExecuteChanged();
        ((RelayCommand)ScanDownloadsCommand).OnCanExecuteChanged();
        ((RelayCommand)ScanTemporaryFilesCommand).OnCanExecuteChanged();
        ((RelayCommand)SelectAllCandidatesCommand).OnCanExecuteChanged();
        ((RelayCommand)DeselectAllCandidatesCommand).OnCanExecuteChanged();
        ((RelayCommand)ExecuteOptimizationCommand).OnCanExecuteChanged();
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        void Update()
        {
            StatusMessage = message;
            StatusSeverity = severity;
            IsStatusOpen = true;
        }

        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(Update);
        }
        else
        {
            Update();
        }
    }

    public bool IsAiAnalyzing
    {
        get => _isAiAnalyzing;
        private set
        {
            if (Set(ref _isAiAnalyzing, value))
            {
                OnPropertyChanged(nameof(CanRunDeepAnalysis));
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

            UpdateScheduleTimerState();
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
                return IsChinese ? "已关闭：仅在手动触发时审计" : "Off: audits run only when triggered manually";
            }

            DateTime now = DateTime.UtcNow;
            DateTime? next = AuditSchedule.NextRunUtc(_lastCompletedAuditUtc, now, ScanIntervalHours);
            string cadence = IsChinese ? $"每 {ScanIntervalHours} 小时" : $"Every {ScanIntervalHours} h";

            return next is null
                ? cadence
                : (IsChinese
                    ? $"{cadence} · 下次约 {next.Value.ToLocalTime():MM-dd HH:mm}"
                    : $"{cadence} · next around {next.Value.ToLocalTime():MM-dd HH:mm}");
        }
    }

    /// <summary>Starts or stops the scheduler timer to match the configured cadence.</summary>
    private void UpdateScheduleTimerState()
    {
        EnqueueOnUI(() =>
        {
            if (IsEnabled && IsSchedulingEnabled)
            {
                _scheduleTimer.Start();
            }
            else
            {
                _scheduleTimer.Stop();
            }

            OnPropertyChanged(nameof(ScheduleSummaryText));
        });
    }

    /// <summary>
    /// Runs an audit when the configured cadence is due. Skips when a scan is already
    /// running, when the module is disabled, or when AI Hub scheduling is off.
    /// </summary>
    private void RunScheduledAuditIfDue()
    {
        if (!IsEnabled || !IsSchedulingEnabled || IsAuditing || _disposed)
        {
            return;
        }

        if (!AuditSchedule.IsDue(_lastCompletedAuditUtc, DateTime.UtcNow, ScanIntervalHours))
        {
            OnPropertyChanged(nameof(ScheduleSummaryText));
            return;
        }

        Logger.LogInfo("AI Hub scheduled audit is due; starting an incremental run.");
        RunAudit(fast: false);
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
        IsAiAnalyzing = true;
        AiStatusText = IsChinese ? "正在请求 AI 深度分析..." : "Requesting AI deep analysis...";

        var progress = new Progress<string>(message => EnqueueOnUI(() => AiStatusText = message));

        Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<AuditIssue> issues = await AiHubAuditAnalysisService
                    .AnalyzeEventsAsync(events, progress, CancellationToken.None)
                    .ConfigureAwait(false);

                EnqueueOnUI(() =>
                {
                    if (issues is null)
                    {
                        AiStatusText = IsChinese
                            ? "AI 分析不可用或未返回有效结果。请在“常规 → AI 服务”中确认已启用 AI Hub 并选择内核。"
                            : "AI analysis unavailable or returned nothing valid. Enable AI Hub and select a kernel under General > AI Services.";
                        return;
                    }

                    foreach (AuditIssue issue in issues.OrderByDescending(item => item.Severity == "High").ThenByDescending(item => item.Occurrences))
                    {
                        _aiReportFindings.Add(new AiReportFinding(
                            IsChinese && !string.IsNullOrWhiteSpace(issue.TitleZh) ? issue.TitleZh : issue.Title,
                            IsChinese && !string.IsNullOrWhiteSpace(issue.RootCauseZh) ? issue.RootCauseZh : issue.RootCause,
                            IsChinese && !string.IsNullOrWhiteSpace(issue.DescriptionZh) ? issue.DescriptionZh : issue.Description,
                            IsChinese && !string.IsNullOrWhiteSpace(issue.RecommendationZh) ? issue.RecommendationZh : issue.Recommendation,
                            $"{issue.Severity} · Event {issue.EventId} · {issue.Occurrences}x"));
                    }

                    AiReportSummaryText = IsChinese
                        ? $"AI 深度分析完成：{issues.Count} 项增强发现。"
                        : $"AI deep analysis completed: {issues.Count} enhanced finding(s).";
                    AiReportMetaText = IsChinese
                        ? $"内核 {AiHubEngine.Current.ActiveKernel} · {DateTime.Now:HH:mm:ss}"
                        : $"Kernel {AiHubEngine.Current.ActiveKernel} · {DateTime.Now:HH:mm:ss}";
                    HasAiReport = true;
                    AiStatusText = IsChinese ? "AI 深度分析完成" : "AI deep analysis completed";

                    OnPropertyChanged(nameof(CanRunDeepAnalysis));
                });
            }
            catch (OperationCanceledException)
            {
                EnqueueOnUI(() => AiStatusText = IsChinese ? "AI 分析已取消" : "AI analysis cancelled");
            }
            catch (Exception ex)
            {
                EnqueueOnUI(() =>
                {
                    AiStatusText = IsChinese ? $"AI 分析失败：{ex.Message}" : $"AI analysis failed: {ex.Message}";
                });
            }
            finally
            {
                EnqueueOnUI(() => IsAiAnalyzing = false);
            }
        });
    }

    private void RunAudit(bool fast)
    {
        if (!IsEnabled || IsAuditing)
        {
            return;
        }

        _currentCts?.Cancel();
        _currentCts = new CancellationTokenSource();
        var token = _currentCts.Token;

        IsAuditing = true;
        AuditStatusText = fast ? (IsChinese ? "正在执行快速安全扫描..." : "Fast scan in progress...") : (IsChinese ? "正在全量检索系统事件日志..." : "Full event audit in progress...");

        Task.Run(async () =>
        {
            try
            {
                TimeSpan scope = fast ? TimeSpan.FromHours(1) : _selectedScopeIndex switch
                {
                    1 => TimeSpan.FromDays(2),
                    2 => TimeSpan.FromDays(7),
                    _ => TimeSpan.FromDays(1),
                };

                int maxEvents = fast ? 250 : 2000;
                var auditMode = IsFullAuditMode ? EventLogService.AuditMode.Full : EventLogService.AuditMode.Extended;

                // Continue from the previous scan when it falls inside the selected range,
                // so repeated runs re-read only the new tail instead of the whole period.
                DateTime nowUtc = DateTime.UtcNow;
                (DateTime windowFrom, DateTime windowTo) = fast
                    ? (nowUtc - scope, nowUtc)
                    : AuditSchedule.ComputeWindow(_lastCompletedAuditUtc, nowUtc, scope);
                bool incremental = !fast && AuditSchedule.IsIncremental(_lastCompletedAuditUtc, nowUtc, scope);

                var events = await _eventLogService.CollectEventsAsync(windowFrom, windowTo, auditMode, maxEvents, token);

                // Retain the raw events so AI deep analysis can send the same evidence.
                lock (_lastAuditEvents)
                {
                    _lastAuditEvents.Clear();
                    _lastAuditEvents.AddRange(events);
                }

                // Run audit analysis (rule-based detection with rich bilingual solutions)
                var issues = RunRuleBasedAuditAnalysis(events);

                // Calculate health score
                int score = HealthScoreCalculator.Calculate(issues);

                EnqueueOnUI(() =>
                {
                    AllIssues.Clear();
                    foreach (var issue in issues)
                    {
                        AllIssues.Add(issue);
                    }

                    HealthScore = score;
                    HealthScoreGrade = score >= 80 ? (IsChinese ? "良好" : "Good") : score >= 60 ? (IsChinese ? "警告" : "Warning") : (IsChinese ? "严重" : "Critical");
                    HighCount = issues.Count(i => i.IsHigh);
                    MediumCount = issues.Count(i => i.IsMedium);
                    LowCount = issues.Count(i => i.IsLow);

                    AuditTimestampText = string.Format(
                        CultureInfo.InvariantCulture,
                        IsChinese ? "上次扫描: {0:yyyy-MM-dd HH:mm:ss} ({1} 项发现)" : "Last scanned: {0:yyyy-MM-dd HH:mm:ss} ({1} findings)",
                        DateTime.Now,
                        issues.Count);

                    AuditStatusText = IsChinese ? "审计完成" : "Audit completed";

                    // Update Verdict & Health Summary
                    UpdateHealthSummary(score, HighCount, MediumCount, LowCount);
                    OnPropertyChanged(nameof(ScheduleSummaryText));

                    // Update Scan Details & Activity
                    _lastCompletedAuditUtc = DateTime.UtcNow;
                    ScanTypeText = fast
                        ? (IsChinese ? "快速扫描" : "Fast scan")
                        : incremental
                            ? (IsChinese ? "增量审计" : "Incremental audit")
                            : (IsChinese ? "全量审计" : "Full scan");
                    FinishedText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    WindowText = fast ? (IsChinese ? "最近 1 小时" : "Past 1 hour") : _selectedScopeIndex switch
                    {
                        1 => IsChinese ? "最近 2 天" : "Past 2 days",
                        2 => IsChinese ? "最近 7 天" : "Past 7 days",
                        _ => IsChinese ? "最近 1 天" : "Past 1 day",
                    };
                    EventCountText = IsChinese ? $"{events.Count} 事件" : $"{events.Count} events";
                    ActivityFindingsText = issues.Count.ToString(CultureInfo.InvariantCulture);
                    SelectedAuditLabel = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    SelectedAuditDate = DateTimeOffset.Now;

                    RebuildSections();

                    // Save history, then derive the activity counters from what is stored
                    // so scan/active-day/average figures reflect the real history.
                    _ = SaveHistoryAndRefreshStatisticsAsync(new AuditResult
                    {
                        Timestamp = DateTime.UtcNow,
                        HealthScore = score,
                        Findings = issues,
                    });

                    ShowStatus(
                        IsChinese
                            ? $"扫描完成。健康评分: {score}/100，发现 {issues.Count} 项问题。"
                            : $"Audit completed. Health score: {score}/100 with {issues.Count} findings.",
                        InfoBarSeverity.Success);
                });
            }
            catch (OperationCanceledException)
            {
                EnqueueOnUI(() => AuditStatusText = IsChinese ? "扫描已取消" : "Audit cancelled");
            }
            catch (Exception ex)
            {
                EnqueueOnUI(() =>
                {
                    AuditStatusText = IsChinese ? "扫描失败" : "Audit failed";
                    ShowStatus($"Audit failed: {ex.Message}", InfoBarSeverity.Error);
                });
            }
            finally
            {
                EnqueueOnUI(() =>
                {
                    IsAuditing = false;
                    OnPropertyChanged(nameof(CanRunDeepAnalysis));
                });
            }
        }, token);
    }

    private void UpdateHealthSummary(int score, int high, int medium, int low)
    {
        if (high > 0)
        {
            HealthVerdict = IsChinese ? "需要关注 · 存在高危异常" : "Attention needed · Critical issues detected";
            HealthSummaryText = IsChinese
                ? $"检测到 {high} 项高危故障及 {medium} 项警告。建议优先执行推荐的操作建议进行修复。"
                : $"{high} high-priority issue(s) and {medium} warning(s) detected. Action recommended.";
        }
        else if (medium > 0)
        {
            HealthVerdict = IsChinese ? "良好 · 多数指标平稳" : "Good · Most indicators stable";
            HealthSummaryText = IsChinese
                ? $"未检测到严重故障，记录了 {medium} 项常规警告与 {low} 项系统信息日志。已生成相应操作建议。"
                : $"No critical failures found. Recorded {medium} warning(s) and {low} info event(s).";
        }
        else
        {
            HealthVerdict = IsChinese ? "优秀 · 系统健康稳定" : "Excellent · System fully healthy";
            HealthSummaryText = IsChinese
                ? "最近扫描时段内未发现任何安全违规或稳定性异常，所有日志指标正常。"
                : "No security violations or stability anomalies detected in the scanned period.";
        }

        OnPropertyChanged(nameof(HealthVerdict));
        OnPropertyChanged(nameof(HealthSummaryText));
    }

    private List<AuditIssueEnhanced> RunRuleBasedAuditAnalysis(List<SecurityEvent> events)
    {
        var issues = new List<AuditIssueEnhanced>();
        var grouped = events.GroupBy(e => (e.LogName, e.EventId, e.ProviderName));

        int keySeq = 1;
        foreach (var g in grouped)
        {
            var first = g.First();
            int count = g.Count();
            int eventId = first.EventId;
            string provider = first.ProviderName ?? string.Empty;
            string log = first.LogName ?? "System";

            // Clean message excerpt
            string rawMsg = (first.Message ?? string.Empty).Trim();
            string cleanedMsg = CleanMessageSummary(rawMsg);

            // Default baseline
            string severity = "Low";
            string category = string.Equals(log, "Setup", StringComparison.OrdinalIgnoreCase) ? "Configuration"
                : string.Equals(log, "Application", StringComparison.OrdinalIgnoreCase) ? "Application"
                : string.Equals(log, "ForwardedEvents", StringComparison.OrdinalIgnoreCase) ? "Network"
                : "System";

            string title = $"{provider} (Event {eventId})";
            string titleZh = $"{provider} (事件 ID: {eventId})";
            string desc = !string.IsNullOrWhiteSpace(cleanedMsg) ? cleanedMsg : $"{provider} recorded event {eventId} in {log} channel.";
            string descZh = !string.IsNullOrWhiteSpace(cleanedMsg) ? cleanedMsg : $"在 {log} 日志中记录到来自 {provider} 的事件 {eventId}。";

            string rootCause = $"Operational status event {eventId} recorded {count} time(s) by {provider} under {log}.";
            string rootCauseZh = $"组件 {provider} 在系统日志 {log} 中记录了 {count} 次状态代码为 {eventId} 的事件。";
            string recommendation = "1. Open Reliability Monitor ('perfmon /rel') to inspect system stability around this timestamp.\n2. Review component status in services.msc.\n3. If routine operations are unaffected, continue regular monitoring.";
            string recommendationZh = "1. 按 Win+R 运行 'perfmon /rel' 打开可靠性监视器核查该时段系统稳定性记录。\n2. 在 services.msc 中检查相关服务或驱动状态。\n3. 若系统整体平稳且未影响日常使用，可保持常规关注。";

            // 1. Kernel & Power
            if (eventId == 41 || (eventId == 6008 && provider.Contains("EventLog", StringComparison.OrdinalIgnoreCase)))
            {
                severity = "High";
                category = "Stability";
                title = eventId == 41 ? "Unexpected Kernel Shutdown" : "Unexpected System Shutdown";
                titleZh = eventId == 41 ? "系统内核异常断电或未正常关机 (Kernel-Power 41)" : "系统上一次异常关机 (EventLog 6008)";
                rootCause = "The system rebooted without cleanly shutting down first. Possible causes: abrupt power loss, hardware watchdog trip, overheating, or kernel bugcheck deadlock.";
                rootCauseZh = "系统在未正常关机的情况下重新启动。可能原因包括供电突发中断、硬件看门狗超时、过热保护或系统死锁蓝屏。";
                recommendation = "1. Inspect wall power, PSU cables, and UPS battery stability.\n2. Check for crash dump files under 'C:\\Windows\\Minidump'.\n3. Press Win+R, run 'mdsched.exe' (Windows Memory Diagnostic) to verify RAM integrity.\n4. Check CPU/GPU temperatures in BIOS/UEFI.";
                recommendationZh = "1. 排查插座供电、电源连线及 UPS 后备电源稳定性。\n2. 检查 'C:\\Windows\\Minidump' 目录是否存在近期内核崩溃转储文件。\n3. 按 Win+R 运行 'mdsched.exe' 执行 Windows 内存诊断工具排查物理内存故障。\n4. 在 BIOS/UEFI 中查看 CPU 和主板供电温度，防范过热保护关机。";
            }
            else if (eventId == 1001 && (provider.Contains("BugCheck", StringComparison.OrdinalIgnoreCase) || provider.Contains("WER", StringComparison.OrdinalIgnoreCase)))
            {
                // 2. Windows Error Reporting & Crash BugCheck
                severity = "High";
                category = "Stability";
                title = "System BugCheck or WER Crash Report";
                titleZh = "系统蓝屏崩溃或内核故障报告 (WER BugCheck 1001)";
                rootCause = "Windows Error Reporting archived a fatal kernel crash stop code (BSOD) or critical application fault.";
                rootCauseZh = "Windows 错误报告成功归档了致命内核崩溃停机码 (BSOD) 或关键进程崩溃转储。";
                recommendation = "1. Use WinDbg or BlueScreenView to inspect the memory dump in 'C:\\Windows\\Minidump'.\n2. Identify the offending driver (.sys) and roll back or update via Device Manager ('devmgmt.msc').\n3. Run 'sfc /scannow' and 'DISM /Online /Cleanup-Image /RestoreHealth' in an elevated terminal.";
                recommendationZh = "1. 使用 WinDbg 或 BlueScreenView 工具分析 'C:\\Windows\\Minidump' 中的转储文件调用栈。\n2. 定位引起故障的内核驱动 (.sys) 并在设备管理器 ('devmgmt.msc') 中回退或升级。\n3. 在管理员命令提示符运行 'sfc /scannow' 与 'DISM /Online /Cleanup-Image /RestoreHealth' 修复系统损坏组件。";
            }
            else if (eventId == 1000)
            {
                // 3. Application Crash
                severity = "Medium";
                category = "Application";
                title = $"Application Crash: {provider}";
                titleZh = $"应用程序异常崩溃: {provider}";
                rootCause = "Application process crashed due to an unhandled exception or memory access violation (such as 0xc0000005).";
                rootCauseZh = "应用程序进程因未捕获的代码异常或内存访问违规 (例如 0xc0000005) 崩溃退出。";
                recommendation = "1. Update the software to its latest release patch.\n2. Reinstall or repair Microsoft Visual C++ Redistributable runtime packages.\n3. Run 'sfc /scannow' in CMD to ensure system DLL integrity.\n4. Check for conflicts with anti-cheat software or third-party overlays.";
                recommendationZh = "1. 检查并将该应用程序升级至官方最新补丁版本。\n2. 重新安装或修复 Microsoft Visual C++ Redistributable 常用运行库组件。\n3. 在管理员终端运行 'sfc /scannow' 确保系统底层动态链接库完整性。\n4. 排查后台防外挂软件或第三方屏幕覆层插件冲突。";
            }
            else if (eventId == 1002)
            {
                // 4. Application Hang
                severity = "Medium";
                category = "Application";
                title = $"Application Hang: {provider}";
                titleZh = $"应用程序主线程冻结挂起: {provider}";
                rootCause = "Application process stopped responding to Windows message queues due to thread deadlock or blocked I/O.";
                rootCauseZh = "应用程序进程主线程死锁或遭遇磁盘/网络 I/O 阻塞，无法及时处理 Windows 消息循环。";
                recommendation = "1. Open Task Manager to inspect CPU or disk saturation bottlenecks.\n2. Disable unneeded third-party plugins or extensions for this application.\n3. End unresponsive background tasks and restart the application.";
                recommendationZh = "1. 打开任务管理器检查 CPU 或磁盘 I/O 是否达到饱和瓶颈。\n2. 禁用或卸载该软件安装的第三方非必要插件或扩展项。\n3. 结束无响应进程并排查杀毒软件实时扫描导致的文件锁定。";
            }
            else if (eventId is 7000 or 7001)
            {
                // 5. Service Control Manager Failures
                severity = "High";
                category = "System";
                title = $"Service Start Failure: {provider}";
                titleZh = $"后台服务启动失败或依赖缺失: {provider}";
                rootCause = "Windows Service Control Manager failed to start a service due to missing dependency services, invalid logon credentials, or missing binary path.";
                rootCauseZh = "服务控制管理器无法启动指定服务，原因通常为依存服务未启动、服务登录账号凭据失效或可执行文件路径不存在。";
                recommendation = "1. Press Win+R, run 'services.msc' and locate the service.\n2. Inspect the 'Dependencies' tab to ensure all required services are running.\n3. Verify logon account credentials and binary path in service properties.\n4. Set startup type to 'Automatic' or 'Manual' as appropriate.";
                recommendationZh = "1. 按 Win+R 运行 'services.msc' 找到对应服务属性。\n2. 查看“依存关系”标签页，确认其依赖的所有上游系统服务均处于运行状态。\n3. 在“常规”与“登录”标签页中核对可执行文件路径与账户凭据。\n4. 确认服务启动类型配置为适合的“自动”或“手动”。";
            }
            else if (eventId == 7022)
            {
                // 6. Service Hung on Starting
                severity = "Medium";
                category = "System";
                title = $"Service Hung on Starting: {provider}";
                titleZh = $"系统服务启动阶段挂起超时: {provider}";
                rootCause = "The service hung during initialization and did not report running status within the timeout period.";
                rootCauseZh = "服务在开机初始化阶段超时挂起，未能按时向服务控制管理器汇报就绪状态。";
                recommendation = "1. Check network connectivity if the service relies on remote databases or directory services.\n2. In services.msc, try manually starting the service after full boot.\n3. Adjust ServicesPipeTimeout in registry if the server workload is heavy during startup.";
                recommendationZh = "1. 若该服务依赖远程数据库或网络验证，请排查局域网连接与 DNS。\n2. 在系统完全启动后，打开 services.msc 尝试手动启动该服务。\n3. 在注册表 HKLM\\SYSTEM\\CurrentControlSet\\Control 调整 ServicesPipeTimeout 启动超时限制。";
            }
            else if (eventId is 7023 or 7024)
            {
                // 7. Service Terminated with Error
                severity = "Medium";
                category = "System";
                title = $"Service Terminated with Error: {provider}";
                titleZh = $"系统服务异常终止并返回错误码: {provider}";
                rootCause = "The service terminated with a service-specific error code or internal failure.";
                rootCauseZh = "后台系统服务在运行过程中抛出服务专有错误代码并异常退出。";
                recommendation = "1. Review specific exit error code inside event details.\n2. Open services.msc, switch to 'Recovery' tab, and configure 'Restart the Service' for subsequent failures.\n3. Verify service registry keys under 'HKLM\\SYSTEM\\CurrentControlSet\\Services'.";
                recommendationZh = "1. 查看事件描述中附带的具体退出错误代码。\n2. 打开 services.msc，在服务属性“恢复”标签页中为后续失败配置“重新启动服务”。\n3. 核查注册表 'HKLM\\SYSTEM\\CurrentControlSet\\Services' 对应服务项是否受损。";
            }
            else if (eventId is 7031 or 7034)
            {
                // 8. Service Crashed Unexpectedly
                severity = "High";
                category = "System";
                title = $"Service Terminated Unexpectedly: {provider}";
                titleZh = $"系统核心服务意外崩溃退出: {provider}";
                rootCause = "The service process terminated unexpectedly without receiving a clean shutdown command.";
                rootCauseZh = "后台服务进程意外崩溃或被外部系统机制异常终止。";
                recommendation = "1. Check the Application event log for an Event 1000 at the exact same timestamp to identify the faulting DLL module.\n2. In services.msc, set recovery action to 'Restart the Service'.\n3. Update or reinstall the parent software package.";
                recommendationZh = "1. 查看同一时刻 Application 日志中的 Event 1000 崩溃记录，定位报错的 DLL 动态库。\n2. 在 services.msc 服务属性“恢复”中设置“重新启动服务”。\n3. 更新或重新安装该服务所属的应用程序软件包。";
            }
            else if (eventId == 7026)
            {
                // 9. Driver Load Failure
                severity = "Medium";
                category = "System";
                title = "Driver Failed to Load on Boot";
                titleZh = "引导或系统启动驱动程序加载失败 (SCM 7026)";
                rootCause = "One or more boot-start or system-start drivers failed to load during Windows boot.";
                rootCauseZh = "开机引导或系统启动型硬件驱动未能成功载入内存。";
                recommendation = "1. Open Device Manager ('devmgmt.msc'), click 'View' -> 'Show hidden devices', and uninstall orphaned drivers.\n2. Run 'pnputil /enum-drivers' in CMD to inspect installed third-party drivers.\n3. Remove obsolete anti-cheat, virtual soundboard, or VPN filter drivers.";
                recommendationZh = "1. 打开设备管理器 ('devmgmt.msc')，在“查看”菜单中勾选“显示隐藏的设备”，卸载失效硬件驱动。\n2. 在管理员终端运行 'pnputil /enum-drivers' 查看已安装的第三方驱动包。\n3. 清理已卸载软件残留的虚拟网卡驱动或反作弊驱动。";
            }
            else if (eventId == 7040)
            {
                // 10. Service Start Type Changed
                severity = "Low";
                category = "Configuration";
                title = $"Service Start Type Changed: {provider}";
                titleZh = $"系统服务启动类型发生变更: {provider}";
                rootCause = "The startup type of a service was modified (e.g., from Auto to Disabled).";
                rootCauseZh = "指定系统服务的启动类型被管理员或安装程序修改（例如从自动更改为禁用）。";
                recommendation = "1. Verify whether this change was performed intentionally by an administrator or optimization utility.\n2. In services.msc, restore the startup type if unexpected.";
                recommendationZh = "1. 核实该服务的启动类型变更是否为系统优化工具或软件安装预期的行为。\n2. 若非预期，请在 services.msc 中将该服务恢复为默认启动类型。";
            }
            else if (eventId == 7045)
            {
                severity = "Medium";
                category = "Configuration";
                title = $"New Service Installed: {provider}";
                titleZh = $"系统中注册安装了新服务: {provider}";
                rootCause = "A new service was added to the system service table.";
                rootCauseZh = "检测到系统服务列表中注册并安装了新的后台服务。";
                recommendation = "1. Verify the binary path and publisher digital signature of the newly registered service.\n2. Ensure it originates from trusted authorized software to prevent persistence mechanisms.";
                recommendationZh = "1. 检查新安装服务的可执行文件路径与数字签名，确保其来自正规官方软件。\n2. 防范未知第三方程序利用服务注册实现持久化后门。";
            }
            else if (eventId == 10016)
            {
                // 11. DCOM Local Activation
                severity = "Low";
                category = "Configuration";
                title = "DCOM Local Activation Isolation";
                titleZh = "DistributedCOM 激活权限隔离提示 (DCOM 10016)";
                rootCause = "The application-specific permission settings do not grant Local Activation permission for the COM Server application CLSID/APPID.";
                rootCauseZh = "特定应用程序的默认组件权限未显式授予当前账户对该 CLSID/APPID 的本地激活权限。";
                recommendation = "1. This is standard benign Windows behavior; Microsoft officially recommends ignoring Event 10016.\n2. No remediation required unless a specific user application fails to launch.";
                recommendationZh = "1. 这是 Windows 内部组件通信的标准安全隔离现象；微软官方技术支持明确建议安全忽略。\n2. 只要当前系统和软件运行平稳，无需进行任何注册表干预。";
            }
            else if (eventId == 10010)
            {
                severity = "Medium";
                category = "Configuration";
                title = "DCOM Server Registration Timeout";
                titleZh = "DCOM 服务器注册超时 (DCOM 10010)";
                rootCause = "A DCOM server component did not register with DCOM within the required timeout period.";
                rootCauseZh = "DCOM 服务器组件在规定的超时期限内未能完成与 DCOM 基础结构的注册。";
                recommendation = "1. Check if the hosting application or background service is disabled.\n2. Restart the associated background service in services.msc.";
                recommendationZh = "1. 检查托管该 DCOM 服务器的应用程序或服务是否被禁用。\n2. 在 services.msc 中重新启动对应的后台系统服务。";
            }
            else if (eventId == 219)
            {
                // 12. Plug-and-Play Driver Warning
                severity = "Medium";
                category = "Configuration";
                title = "PnP Driver Load Warning";
                titleZh = "即插即用硬件驱动加载失败 (Kernel-PnP 219)";
                rootCause = "A driver failed to load for a connected Plug-and-Play device during hardware initialization.";
                rootCauseZh = "硬件设备在即插即用枚举识别过程中未能成功加载匹配的驱动程序。";
                recommendation = "1. Open Device Manager ('devmgmt.msc') and check for devices with yellow exclamation marks.\n2. Right-click the affected device and select 'Update driver'.\n3. Download and install the latest motherboard chipset drivers from the manufacturer website.";
                recommendationZh = "1. 按 Win+X 打开“设备管理器”('devmgmt.msc')，排查标有黄色感叹号的设备。\n2. 右键点击受影响的设备并选择“更新驱动程序”。\n3. 前往电脑或主板品牌官网下载安装最新的芯片组驱动。";
            }
            else if (eventId == 1530)
            {
                // 13. User Profile Registry Hive Leak
                severity = "Low";
                category = "System";
                title = "User Profile Registry Handle Leak";
                titleZh = "用户注销时注册表句柄未释放 (User Profile 1530)";
                rootCause = "Windows detected that the registry file (user hive) was still in use by background applications during logoff.";
                rootCauseZh = "用户注销或关机时，后台软件或常驻服务仍占用注册表配置单元文件句柄未及时释放。";
                recommendation = "1. Gracefully close background tray apps and torrent clients before shutdown.\n2. Update third-party security software and system utilities to their latest versions.";
                recommendationZh = "1. 关机或注销前正常退出后台托盘程序与常驻工具。\n2. 检查并升级第三方防病毒软件和系统优化工具至最新版本。";
            }
            else if (eventId == 1014)
            {
                // 14. DNS Resolution Failure
                severity = "Medium";
                category = "Network";
                title = "DNS Name Resolution Timed Out";
                titleZh = "DNS 域名解析响应超时 (DNS Client 1014)";
                rootCause = "Name resolution for an internet address timed out after none of the configured DNS servers responded.";
                rootCauseZh = "本地网络请求域名解析时超时，配置的 DNS 服务器均未在限定时间内返回解析结果。";
                recommendation = "1. Open an elevated terminal and run 'ipconfig /flushdns' to clear local cache.\n2. Configure reliable public DNS servers (e.g. 223.5.5.5, 119.29.29.29, or 1.1.1.1) in network adapter properties.\n3. Restart your router to resolve gateway DNS latency.";
                recommendationZh = "1. 在管理员终端运行 'ipconfig /flushdns' 刷新本地 DNS 缓存。\n2. 在网络适配器属性中配置高可用公共 DNS（如 223.5.5.5、119.29.29.29 或 1.1.1.1）。\n3. 重启路由器或光猫排查网关 DNS 转发死锁。";
            }
            else if (eventId is 1008 or 1020 or 2001 or 2003)
            {
                // 15. Performance Counters (Perflib)
                severity = "Low";
                category = "System";
                title = $"Performance Counter DLL Warning: {provider}";
                titleZh = $"性能计数器动态库错误 (Perflib {eventId})";
                rootCause = "The Open or Collect procedure for a performance counter service DLL failed or returned an error.";
                rootCauseZh = "性能计数器服务打开或采集指定 DLL 动态库时失败或返回错误代码。";
                recommendation = "1. Open an elevated Command Prompt and run 'lodctr /r' to rebuild all performance counter registry strings.\n2. Run 'winmgmt /resyncperf' to resynchronize performance counters with WMI.";
                recommendationZh = "1. 在管理员命令提示符中运行 'lodctr /r' 重新构建系统性能计数器注册表设置。\n2. 运行 'winmgmt /resyncperf' 重新同步性能计数器与 WMI 存储库。";
            }
            else if (eventId == 10 && provider.Contains("WMI", StringComparison.OrdinalIgnoreCase))
            {
                // 16. WMI Error
                severity = "Medium";
                category = "System";
                title = "WMI Filter Query Error";
                titleZh = "WMI 事件筛选器查询错误 (WMI 10)";
                rootCause = "WMI Event filter query failed to activate in namespace due to syntax or query incompatibility.";
                rootCauseZh = "WMI 事件筛选器查询无法在命名空间中激活，通常由于查询语法或权限不兼容。";
                recommendation = "1. Run 'winmgmt /verifyrepository' in an elevated CMD to check repository integrity.\n2. If inconsistency is detected, run 'winmgmt /salvagerepository' to repair.";
                recommendationZh = "1. 在管理员 CMD 中运行 'winmgmt /verifyrepository' 校验 WMI 存储库完整性。\n2. 若提示存在不一致，运行 'winmgmt /salvagerepository' 执行安全修复。";
            }
            else if (eventId is 131 or 200 or 201 or 202)
            {
                // 17. DeviceSetupManager
                severity = "Low";
                category = "Configuration";
                title = "Device Setup Metadata Connection Warning";
                titleZh = "设备安装元数据服务器连接超时 (DeviceSetupManager)";
                rootCause = "Device Setup Manager failed to connect to Windows Update servers to retrieve device metadata packages.";
                rootCauseZh = "设备安装管理器无法连接到微软 Windows Update 服务器检索设备元数据包（通常由于网络离线或防火墙阻断）。";
                recommendation = "1. Benign and safe to ignore for already-functioning hardware peripherals.\n2. Check general internet connectivity if newly plugged devices lack custom icons.";
                recommendationZh = "1. 对于已正常工作的硬件外设，此提示完全无害，可安全忽略。\n2. 若新接入的外设缺少设备图标，检查外网连通性或稍后重试。";
            }
            else if (eventId is 16384 or 16394)
            {
                // 18. Volume Shadow Copy (VSS)
                severity = "Medium";
                category = "System";
                title = "Volume Shadow Copy Service Error";
                titleZh = "卷影复制服务快照异常 (VSS)";
                rootCause = "Volume Shadow Copy Service encountered an error during shadow copy creation or backup snapshot synchronization.";
                rootCauseZh = "卷影复制服务 (VSS) 在创建系统还原点或进行备份快照同步时遇到错误。";
                recommendation = "1. Run 'vssadmin list writers' in an elevated CMD to ensure all VSS writers report 'Stable'.\n2. Ensure sufficient free disk space on the system drive (at least 15% recommended).\n3. Restart the Volume Shadow Copy service in services.msc.";
                recommendationZh = "1. 在管理员 CMD 运行 'vssadmin list writers' 检查所有 VSS 写入程序状态是否稳定 (State: [1] Stable)。\n2. 确保系统盘拥有充足的剩余可用空间（建议保留至少 15%）。\n3. 在 services.msc 中重新启动 'Volume Shadow Copy' 系统服务。";
            }
            else if (eventId is 13 or 15 or 16 or 55)
            {
                // 19. Disk & File System (NTFS / Volume)
                severity = "High";
                category = "Reliability";
                title = $"Disk File System Error: {provider}";
                titleZh = $"磁盘文件系统或坏道异常: {provider}";
                rootCause = "NTFS or disk subsystem detected data structure corruption or unreadable disk sectors.";
                rootCauseZh = "NTFS 文件系统或磁盘子系统检测到数据结构受损或扇区读取异常。";
                recommendation = "1. Immediately back up critical files to external storage.\n2. Open an elevated Command Prompt, run 'chkdsk C: /f /r', and schedule scan on next reboot.\n3. Use CrystalDiskInfo to inspect drive S.M.A.R.T. health and bad sectors.";
                recommendationZh = "1. 立即备份重要个人数据与工作文档至外部存储设备。\n2. 在管理员命令行运行 'chkdsk C: /f /r'，并确认在下一次重启时执行全盘扇区扫描与修复。\n3. 使用 CrystalDiskInfo 查看硬盘 S.M.A.R.T. 健康状态排查硬件物理坏道。";
            }
            else if (eventId is 10000 or 10001 or 8000)
            {
                // 20. WLAN Connection Failures
                severity = "Medium";
                category = "Network";
                title = "Wireless Network Connection Interruption";
                titleZh = "无线网络握手协商中断 (WLAN-AutoConfig)";
                rootCause = "Wireless network adapter disconnected or failed to complete 802.11 authentication with the access point.";
                rootCauseZh = "无线网卡意外断开连接或与 Wi-Fi 路由器访问点握手协商认证失败。";
                recommendation = "1. Update Wi-Fi adapter drivers from the manufacturer website.\n2. Forget the Wi-Fi network in Windows Settings and reconnect.\n3. Run 'netsh winsock reset' in an elevated terminal and reboot.";
                recommendationZh = "1. 前往网卡芯片厂商官网下载更新无线网卡驱动。\n2. 在 Windows 设置中“忘记此网络”并重新输入密码连接。\n3. 在管理员终端运行 'netsh winsock reset' 重置网络套接字并重启电脑。";
            }
            else if (first.Level <= 2)
            {
                // 21. General Critical / Error Level
                severity = "High";
                category = "Reliability";
                title = $"Critical Error: {provider} ({eventId})";
                titleZh = $"{provider} 严重错误与异常 (事件 ID: {eventId})";
                rootCause = $"Critical or error-level event recorded {count} time(s). Component '{provider}' under '{log}' encountered an unexpected failure.";
                rootCauseZh = $"在“{log}”通道记录到 {count} 次严重或错误级别事件。组件“{provider}”在执行过程中遇到未处理异常。";
                recommendation = $"1. Press Win+R and run 'perfmon /rel' to inspect system stability history around this event.\n2. Review '{provider}' service dependencies in services.msc.\n3. Run 'sfc /scannow' in an elevated terminal to verify system integrity.";
                recommendationZh = $"1. 按 Win+R 运行 'perfmon /rel' 打开可靠性监视器核查该时刻前后的系统故障记录。\n2. 在 services.msc 中检查“{provider}”关联服务的运行状态与依赖项。\n3. 在管理员终端运行 'sfc /scannow' 扫描修复受损的 Windows 系统核心文件。";
            }
            else if (first.Level == 3)
            {
                // 22. General Warning Level
                severity = "Medium";
                category = "Configuration";
                title = $"Warning: {provider} ({eventId})";
                titleZh = $"{provider} 运行警告 (事件 ID: {eventId})";
                rootCause = $"Component '{provider}' reported operational warning or configuration delay (code {eventId}) {count} time(s).";
                rootCauseZh = $"组件“{provider}”在“{log}”日志中报告了 {count} 次运行警告或配置响应延迟（状态码: {eventId}）。";
                recommendation = $"1. Check if '{provider}' component functions properly in daily tasks.\n2. If operations are stable, monitor for repeated occurrences.\n3. Review Windows Update history for pending driver or cumulative updates.";
                recommendationZh = $"1. 确认“{provider}”相关功能在日常使用中是否表现正常。\n2. 若系统整体运行平稳，通常属于非阻塞性运行警告，可保持常规观察。\n3. 检查 Windows 更新历史记录，安装待处理的驱动补丁或累积更新。";
            }

            issues.Add(new AuditIssueEnhanced
            {
                Key = $"issue-{keySeq++:D4}",
                EventRef = eventId.ToString(CultureInfo.InvariantCulture),
                EventId = eventId.ToString(CultureInfo.InvariantCulture),
                EventTimestamp = first.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
                Title = title,
                TitleZh = titleZh,
                Description = desc,
                DescriptionZh = descZh,
                Severity = severity,
                Category = category,
                Affected = provider,
                RootCause = rootCause,
                RootCauseZh = rootCauseZh,
                Recommendation = recommendation,
                RecommendationZh = recommendationZh,
                Occurrences = count,
            });
        }

        return issues.OrderByDescending(i => i.IsHigh).ThenByDescending(i => i.IsMedium).ThenByDescending(i => i.Occurrences).ToList();
    }

    private static string CleanMessageSummary(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var lines = raw.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        string firstLine = lines[0];
        if (firstLine.Length > 280)
        {
            firstLine = firstLine[..280] + "...";
        }

        return firstLine;
    }

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
        OnPropertyChanged(nameof(OverviewLabel));
        OnPropertyChanged(nameof(ApplicationLabel));
        OnPropertyChanged(nameof(SecurityLabel));
        OnPropertyChanged(nameof(SystemLabel));
        OnPropertyChanged(nameof(SetupLabel));
        OnPropertyChanged(nameof(ForwardedLabel));
    }

    private void LoadRecentAudit()
    {
        Task.Run(async () =>
        {
            try
            {
                AuditHistoryStatistics statistics = await _auditHistoryStorage.GetStatisticsAsync().ConfigureAwait(false);
                EnqueueOnUI(() => ApplyStatistics(statistics));

                var history = await _auditHistoryStorage.LoadLatestHistoryAsync();
                if (history != null && history.Findings.Count > 0)
                {
                    EnqueueOnUI(() =>
                    {
                        AllIssues.Clear();
                        foreach (var issue in history.Findings)
                        {
                            AllIssues.Add(issue);
                        }

                        HealthScore = history.HealthScore;
                        HealthScoreGrade = HealthScore >= 80 ? (IsChinese ? "良好" : "Good") : HealthScore >= 60 ? (IsChinese ? "警告" : "Warning") : (IsChinese ? "严重" : "Critical");
                        HighCount = history.HighCount;
                        MediumCount = history.MediumCount;
                        LowCount = history.LowCount;

                        AuditTimestampText = string.Format(
                            CultureInfo.InvariantCulture,
                            IsChinese ? "上次扫描: {0:yyyy-MM-dd HH:mm:ss} ({1} 项发现)" : "Last scanned: {0:yyyy-MM-dd HH:mm:ss} ({1} findings)",
                            history.Timestamp.ToLocalTime(),
                            history.Findings.Count);

                        UpdateHealthSummary(HealthScore, HighCount, MediumCount, LowCount);

                        ScanTypeText = IsChinese ? "历史审计快照" : "Historical snapshot";
                        FinishedText = history.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                        WindowText = IsChinese ? "全时段" : "All times";
                        EventCountText = IsChinese ? $"{history.Findings.Count} 发现项" : $"{history.Findings.Count} findings";
                        ActivityFindingsText = history.Findings.Count.ToString(CultureInfo.InvariantCulture);
                        SelectedAuditLabel = history.Timestamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        SelectedAuditDate = history.Timestamp.ToLocalTime();

                        RebuildSections();
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to load the most recent AI Hub audit snapshot", ex);
            }
        });
    }

    // Optimization Execution (1:1 with Screenshot)
    private void RunScanAll()
    {
        if (!IsEnabled || IsOptimizing)
        {
            return;
        }

        _currentCts?.Cancel();
        _currentCts = new CancellationTokenSource();
        var token = _currentCts.Token;

        IsOptimizing = true;
        OptimizationWorkflowStatus = IsChinese ? "正在扫描..." : "Scanning...";

        Task.Run(async () =>
        {
            try
            {
                var downloadsTask = _downloadOrganizerService.ScanAsync(token);
                var cacheTask = _cacheCleanupService.ScanAsync(token);

                await Task.WhenAll(downloadsTask, cacheTask);

                var downloads = await downloadsTask;
                var caches = await cacheTask;

                long dlBytes = downloads.Sum(d => d.SizeInBytes);
                long cacheBytes = caches.Sum(c => c.SizeInBytes);

                var allCandidates = new List<TempFileInfo>();
                allCandidates.AddRange(downloads);
                allCandidates.AddRange(caches);

                EnqueueOnUI(() =>
                {
                    DownloadsItemCount = downloads.Count;
                    DownloadsBytes = dlBytes;
                    DownloadsStatusText = IsChinese ? $"发现 {downloads.Count} 项 ({FormatBytes(dlBytes)})" : $"{downloads.Count} item(s) found ({FormatBytes(dlBytes)})";

                    TemporaryFilesItemCount = caches.Count;
                    TemporaryFilesBytes = cacheBytes;
                    TemporaryFilesStatusText = IsChinese ? $"发现 {caches.Count} 项 ({FormatBytes(cacheBytes)})" : $"{caches.Count} item(s) found ({FormatBytes(cacheBytes)})";

                    OptimizationCandidates.Clear();
                    foreach (var item in allCandidates)
                    {
                        item.PropertyChanged += Candidate_PropertyChanged;
                        OptimizationCandidates.Add(item);
                    }

                    RecalculateCandidateMetrics();
                    OptimizationWorkflowStatus = allCandidates.Count > 0
                        ? (IsChinese ? "请检查候选项目并确认" : "Review candidates and confirm")
                        : (IsChinese ? "扫描就绪 (未发现候选项目)" : "Ready to scan (0 items found)");
                    ShowStatus(IsChinese ? $"扫描完成: 检测到 {allCandidates.Count} 个候选项目 ({FormatBytes(dlBytes + cacheBytes)})。" : $"Scan complete: {allCandidates.Count} candidate items detected ({FormatBytes(dlBytes + cacheBytes)}).", InfoBarSeverity.Success);
                });
            }
            catch (OperationCanceledException)
            {
                EnqueueOnUI(() => OptimizationWorkflowStatus = IsChinese ? "扫描已取消" : "Scan cancelled");
            }
            catch (Exception ex)
            {
                EnqueueOnUI(() =>
                {
                    OptimizationWorkflowStatus = IsChinese ? "扫描失败" : "Scan failed";
                    ShowStatus($"Optimization scan failed: {ex.Message}", InfoBarSeverity.Error);
                });
            }
            finally
            {
                EnqueueOnUI(() => IsOptimizing = false);
            }
        }, token);
    }

    private void RunScanDownloads()
    {
        if (!IsEnabled || IsOptimizing)
        {
            return;
        }

        _currentCts?.Cancel();
        _currentCts = new CancellationTokenSource();
        var token = _currentCts.Token;

        IsOptimizing = true;
        OptimizationWorkflowStatus = IsChinese ? "正在扫描下载目录..." : "Scanning Downloads root...";

        Task.Run(async () =>
        {
            try
            {
                var downloads = await _downloadOrganizerService.ScanAsync(token);
                long dlBytes = downloads.Sum(d => d.SizeInBytes);

                EnqueueOnUI(() =>
                {
                    DownloadsItemCount = downloads.Count;
                    DownloadsBytes = dlBytes;
                    DownloadsStatusText = IsChinese ? $"发现 {downloads.Count} 项 ({FormatBytes(dlBytes)})" : $"{downloads.Count} item(s) found ({FormatBytes(dlBytes)})";

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
                    OptimizationWorkflowStatus = OptimizationCandidates.Count > 0
                        ? (IsChinese ? "请检查候选项目并确认" : "Review candidates and confirm")
                        : (IsChinese ? "就绪，等待扫描" : "Ready to scan");
                    ShowStatus(IsChinese ? $"下载扫描完成: 发现 {downloads.Count} 个项目。" : $"Downloads scan complete: {downloads.Count} item(s) found.", InfoBarSeverity.Success);
                });
            }
            catch (Exception ex)
            {
                EnqueueOnUI(() => ShowStatus($"Downloads scan failed: {ex.Message}", InfoBarSeverity.Error));
            }
            finally
            {
                EnqueueOnUI(() => IsOptimizing = false);
            }
        }, token);
    }

    private void RunScanTemporaryFiles()
    {
        if (!IsEnabled || IsOptimizing)
        {
            return;
        }

        _currentCts?.Cancel();
        _currentCts = new CancellationTokenSource();
        var token = _currentCts.Token;

        IsOptimizing = true;
        OptimizationWorkflowStatus = IsChinese ? "正在扫描临时文件..." : "Scanning Temporary files...";

        Task.Run(async () =>
        {
            try
            {
                var caches = await _cacheCleanupService.ScanAsync(token);
                long cacheBytes = caches.Sum(c => c.SizeInBytes);

                EnqueueOnUI(() =>
                {
                    TemporaryFilesItemCount = caches.Count;
                    TemporaryFilesBytes = cacheBytes;
                    TemporaryFilesStatusText = IsChinese ? $"发现 {caches.Count} 项 ({FormatBytes(cacheBytes)})" : $"{caches.Count} item(s) found ({FormatBytes(cacheBytes)})";

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
                    OptimizationWorkflowStatus = OptimizationCandidates.Count > 0
                        ? (IsChinese ? "请检查候选项目并确认" : "Review candidates and confirm")
                        : (IsChinese ? "就绪，等待扫描" : "Ready to scan");
                    ShowStatus(IsChinese ? $"缓存扫描完成: 发现 {caches.Count} 个项目。" : $"Cache scan complete: {caches.Count} item(s) found.", InfoBarSeverity.Success);
                });
            }
            catch (Exception ex)
            {
                EnqueueOnUI(() => ShowStatus($"Cache scan failed: {ex.Message}", InfoBarSeverity.Error));
            }
            finally
            {
                EnqueueOnUI(() => IsOptimizing = false);
            }
        }, token);
    }

    private void SelectAllCandidates()
    {
        foreach (var item in OptimizationCandidates)
        {
            item.IsSelected = true;
        }
        RecalculateCandidateMetrics();
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

    private void RecalculateCandidateMetrics()
    {
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

    private void RunExecuteOptimization()
    {
        if (!CanExecuteOptimization)
        {
            return;
        }

        _currentCts?.Cancel();
        _currentCts = new CancellationTokenSource();
        var token = _currentCts.Token;

        IsOptimizing = true;
        OptimizationWorkflowStatus = IsChinese ? "正在执行已确认的操作..." : "Executing confirmed actions...";

        var selectedItems = OptimizationCandidates.Where(c => c.IsSelected).ToList();

        Task.Run(() =>
        {
            var outcome = new OptimizationOutcome();
            int processed = 0;

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
                        // Clean temp file using Recycle Bin ONLY
                        var (ok, error) = _recycleBinHelper.MoveToRecycleBin(item.FilePath);
                        outcome.Add(ok
                            ? OptimizationItemResult.Success(item.FileName, "delete")
                            : OptimizationItemResult.Failure(item.FileName, "delete", error));
                    }
                    else if (item.Action == "move")
                    {
                        // Organize Downloads within root
                        bool ok = _downloadOrganizerService.OrganizeItem(item.FilePath, item.TargetRelativePath);
                        outcome.Add(ok
                            ? OptimizationItemResult.Success(item.FileName, "move")
                            : OptimizationItemResult.Failure(item.FileName, "move", IsChinese ? "整理失败" : "organization failed"));
                    }
                }
                catch (Exception ex)
                {
                    // Keep the reason: a silent count tells the user nothing actionable.
                    outcome.Add(OptimizationItemResult.Failure(item.FileName, item.Action, ex.Message));
                }

                processed++;
                int done = processed;
                EnqueueOnUI(() => OptimizationWorkflowStatus = IsChinese
                    ? $"正在执行已确认的操作... ({done}/{selectedItems.Count})"
                    : $"Executing confirmed actions... ({done}/{selectedItems.Count})");
            }

            EnqueueOnUI(() =>
            {
                foreach (var item in selectedItems)
                {
                    item.PropertyChanged -= Candidate_PropertyChanged;
                    OptimizationCandidates.Remove(item);
                }

                RecalculateCandidateMetrics();

                // Surface the actual failure reasons instead of an opaque count.
                var failureDetail = outcome.DescribeFailures(IsChinese);
                OptimizationWorkflowStatus = outcome.HasFailures
                    ? (IsChinese ? $"执行完成（{outcome.FailedCount} 个失败）" : $"Execution completed ({outcome.FailedCount} failed)")
                    : (IsChinese ? "执行完成" : "Execution completed");

                ShowStatus(
                    failureDetail is null
                        ? outcome.Describe(IsChinese)
                        : $"{outcome.Describe(IsChinese)} — {failureDetail}",
                    outcome.HasFailures ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            });

            EnqueueOnUI(() => IsOptimizing = false);
        }, token);
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
            _scheduleTimer.Stop();
            _currentCts?.Cancel();
            _currentCts?.Dispose();
        }
    }
}
