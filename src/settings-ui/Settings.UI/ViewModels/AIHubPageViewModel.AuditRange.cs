// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Services;
using ManagedCommon;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AIHubPageViewModel
{
    private sealed record AuditRangeView(AuditResult Snapshot, List<SecurityEvent> Events, bool HasEvidence, string Coverage);

    private readonly Dictionary<int, AuditRangeView> _auditRangeViews = new();
    private readonly Dictionary<(int Days, string Profile), AuditResult> _auditCompleteReports = new();
    private readonly AiHubAuditAnalysisService.AuditAnalysisRetryState _auditRetryState = new();
    private AuditEvidenceSession _auditEvidenceSession = new();
    private Task _auditRangeLoadTask = Task.CompletedTask;
    private DateTime? _auditRangeAnchorUtc;
    private DateTime _auditTailCheckedUtc;
    private string _auditRangeProfile = string.Empty;
    private long _auditRangeGeneration;
    private bool _auditHistoricalSelection;
    private bool _auditIsRangeNavigation;
    private AuditResult _auditSourceSnapshot;
    private List<SecurityEvent> _auditSourceEvents;
    private string _auditRangeCoverageText = string.Empty;
    private string _knownAuditAnalysisScope = string.Empty;
    private string _lastAuditAnalysisAttempt = string.Empty;
    private bool _auditReportNeedsRefresh;
    private bool _retainingAiReport;
    private bool _checkingAuditIdentity;
    private long _auditIdentityGeneration;

    public string DisplayRangeLabel => PreferChinese() ? "显示时段" : "Display period";
    public string AuditRangeCoverageText => _auditRangeCoverageText;
    public bool HasAuditRangeCoverage => !string.IsNullOrWhiteSpace(AuditRangeCoverageText);
    public bool CanSelectAuditRange => IsEnabled && !_disposed;

    private int SelectedAuditRangeDays => _selectedScopeIndex switch { 1 => 7, 2 => 30, _ => 2 };
    private bool CanViewCurrentAiReport => HasAiReport && !_fluentAiReportPartial && !_auditReportNeedsRefresh
        && _lastAuditAnalysisAttempt is not ("failed" or "cancelled" or "partial");

    internal static string ResolveAuditAnalysisAction(bool hasReport, bool incomplete, bool changed) =>
        incomplete ? "retry" : hasReport ? changed ? "update" : "view" : "generate";

    private string CurrentAuditAnalysisAction => ResolveAuditAnalysisAction(HasAiReport,
        _lastAuditAnalysisAttempt is "failed" or "cancelled" or "partial" || _fluentAiReportPartial,
        _auditReportNeedsRefresh);

    private void RefreshAuditActions()
    {
        OnPropertyChanged(nameof(DeepAnalyzeLabel));
        OnPropertyChanged(nameof(DeepAnalyzeHint));
        OnPropertyChanged(nameof(CanRunDeepAnalysis));
        OnPropertyChanged(nameof(CanStartDeepAnalysis));
        ((RelayCommand)DeepAnalyzeCommand)?.OnCanExecuteChanged();
        OnPropertyChanged(nameof(CanSelectAuditRange));
        OnPropertyChanged(nameof(SelectedScanHint));
        OnPropertyChanged(nameof(SelectedScanRangeText));
    }

    private void SetAuditRangeCoverage(string text)
    {
        _auditRangeCoverageText = text;
        OnPropertyChanged(nameof(AuditRangeCoverageText));
        OnPropertyChanged(nameof(HasAuditRangeCoverage));
        OnPropertyChanged(nameof(AuditOverviewCoverage));
        OnPropertyChanged(nameof(HasAuditOverviewCoverage));
    }

    private void RegisterAuditEvidence(AuditResult snapshot, List<SecurityEvent> events)
    {
        _auditSourceSnapshot = snapshot;
        _auditSourceEvents = events?.ToList();
        _auditRangeAnchorUtc = (snapshot.ScanEnd ?? snapshot.Timestamp).ToUniversalTime();
        _auditRangeProfile = snapshot.CollectionProfile ?? string.Empty;
        _auditRangeViews.Clear();
        _auditCompleteReports.Clear();
        _lastAuditAnalysisAttempt = string.Empty;
        _auditReportNeedsRefresh = false;
        SetAuditRangeCoverage(string.Empty);
    }

    /// <summary>Rebuild findings from timestamped events; aggregated findings cannot be sliced by their representative timestamp.</summary>
    internal static List<SecurityEvent> SelectAuditRangeEvents(IEnumerable<SecurityEvent> events, DateTime fromUtc, DateTime toUtc) =>
        events.Where(item => item.TimeCreated is DateTime time && time.ToUniversalTime() >= fromUtc && time.ToUniversalTime() <= toUtc).ToList();

    internal static bool CoversAuditRange(AuditResult source, DateTime fromUtc, DateTime toUtc) =>
        source?.ScanStart is DateTime start && source.ScanEnd is DateTime end
        && start.ToUniversalTime() <= fromUtc && end.ToUniversalTime() >= toUtc;

    private void ApplySelectedAuditRange() => StartAuditRangeLoad(fast: false, refresh: false);

    private void StartAuditRangeLoad(bool fast, bool refresh)
    {
        if (!IsEnabled || _disposed)
        {
            return;
        }
        CancelAuditForNavigation();
        int days = SelectedAuditRangeDays;
        var mode = IsFullAuditMode ? EventLogService.AuditMode.Full : EventLogService.AuditMode.Extended;
        string profile = AuditCollectionProfile.ForMode(mode);
        DateTime now = DateTime.UtcNow;
        DateTime end = _auditRangeAnchorUtc ?? now;
        bool historical = _auditHistoricalSelection;
        bool fullRead = refresh && !fast;
        string operation = refresh ? fast ? "quick-refresh" : "full-scan" : "period-view";
        var elapsed = Stopwatch.StartNew();
        foreach (var entry in _auditRangeViews)
        {
            var report = entry.Value.Snapshot;
            if (report.AnalysisOutcome == "ok" && HasPersistedAiAnalysis(report))
            {
                _auditCompleteReports[(entry.Key, report.CollectionProfile ?? string.Empty)] = report;
            }
        }

        _auditRangeViews.TryGetValue(days, out var previousView);
        bool advanceTail = !_auditHistoricalSelection && (refresh || now - end >= TimeSpan.FromMinutes(2));
        if (advanceTail)
        {
            end = now;
        }
        if (_auditRangeAnchorUtc != end || _auditRangeProfile != profile)
        {
            _auditRangeViews.Clear();
            _auditRangeAnchorUtc = end;
            _auditRangeProfile = profile;
        }
        DateTime start = end.AddDays(-days);
        long generation = ++_auditRangeGeneration;
        string logContext = $"request={generation}, operation={operation}, mode={mode}, historical={historical}, fromUtc={start:O}, toUtc={end:O}";
        Logger.LogInfo($"AI Hub audit started: {logContext}");
        if (refresh)
        {
            // Every displayed range derives from the shared raw evidence. A reread can
            // remove or change events, so other range projections must be rebuilt.
            _auditRangeViews.Clear();
        }
        if (!refresh && _auditRangeViews.TryGetValue(days, out var cached))
        {
            PresentAuditRangeView(cached);
            Logger.LogInfo($"AI Hub audit finished: {logContext}, outcome=cached, readEvents=0, reusedEvents={cached.Events.Count}, intervals=0, elapsedMs={elapsed.ElapsedMilliseconds}");
            return;
        }

        DateTime? refreshFrom = null;
        if (refresh || (!_auditHistoricalSelection && now - _auditTailCheckedUtc >= TimeSpan.FromMinutes(30)))
        {
            refreshFrom = end.AddMinutes(-2);
        }
        _auditCts = new CancellationTokenSource();
        CancellationToken token = _auditCts.Token;
        AuditPhase = ScanPhase.Idle;
        IsAuditing = true;
        _auditIsRangeNavigation = !refresh;
        _auditWasDeepAnalysis = false;
        _auditProgress.Reset();
        BeginStage("Collect event logs", audit: true);
        AuditPhase = ScanPhase.Scanning;
        SetAuditRangeCoverage(fullRead
            ? "AIHub_FullScan_Loading".GetLocalizedOrFallback("Reading Windows logs throughout the selected period…")
            : "AIHub_QuickRefresh_Loading".GetLocalizedOrFallback("Reusing collected events and reading missing intervals and the recent tail…"));
        AuditStatusText = AuditRangeCoverageText;
        var progress = new Progress<EventCollectionProgress>(update => EnqueueOnUI(() =>
        {
            if (IsCurrent() && IsAuditing && _auditProgress.CurrentStage == "Collect event logs")
            {
                ApplyAuditCollectionProgress(update);
            }
        }, token));
        bool IsCurrent() => generation == _auditRangeGeneration && _auditCts?.Token == token && !token.IsCancellationRequested;
        _auditRangeLoadTask = Task.Run(async () =>
        {
            try
            {
                var selection = await _auditEvidenceSession.LoadAsync(start, end, mode, fast ? 500 : 5000, refreshFrom, progress,
                    retrySkippedIntervals: refresh, refreshEntireWindow: fullRead, cancellationToken: token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var findings = RunRuleBasedAuditAnalysis(selection.Events);
                var snapshot = new AuditResult
                {
                    Timestamp = historical ? end : now,
                    ScanStart = start,
                    ScanEnd = end,
                    EventsScanned = selection.Events.Count,
                    Findings = findings,
                    HealthScore = HealthScoreCalculator.Calculate(findings),
                    SkippedChannels = selection.SkippedChannels,
                    LimitedChannels = selection.LimitedChannels,
                    EvidenceIncomplete = selection.HasMissingIntervals,
                    CollectionProfile = profile,
                    AnalysisOutcome = "skipped",
                };
                if (!fullRead && previousView is not null && HasPersistedAiAnalysis(previousView.Snapshot)
                    && previousView.Snapshot.CollectionProfile == profile
                    && SameAuditEvidence(previousView.Events, selection.Events)
                    && previousView.Snapshot.AiEvidenceFingerprint == AiHubAuditAnalysisService.CreateEvidenceFingerprint(selection.Events))
                {
                    snapshot.Findings = previousView.Snapshot.Findings;
                    snapshot.HealthScore = previousView.Snapshot.HealthScore;
                    snapshot.AnalysisOutcome = previousView.Snapshot.AnalysisOutcome;
                    snapshot.AiReportSummary = previousView.Snapshot.AiReportSummary;
                    snapshot.AiReportMeta = previousView.Snapshot.AiReportMeta;
                    snapshot.AiAnalysisScope = previousView.Snapshot.AiAnalysisScope;
                    snapshot.AiEvidenceFingerprint = previousView.Snapshot.AiEvidenceFingerprint;
                    snapshot.AiAnalyzedEvents = previousView.Snapshot.AiAnalyzedEvents;
                    snapshot.AiReusedEvents = previousView.Snapshot.AiReusedEvents;
                    snapshot.UsedModel = previousView.Snapshot.UsedModel;
                }
                string coverage = selection.HasMissingIntervals
                    ? (PreferChinese() ? "部分已采集区间超过内存保留限额，当前仅显示仍保留的证据；缺失区间会在下次加载时补采。"
                        : "Some intervals exceeded the evidence memory limit. Only retained evidence is shown; missing intervals will be read on the next load.")
                    : string.Empty;
                var view = new AuditRangeView(snapshot, selection.Events, !selection.HasMissingIntervals || selection.Events.Count > 0, coverage);
                long elapsedMilliseconds = elapsed.ElapsedMilliseconds;
                Logger.LogInfo($"AI Hub audit {(fullRead ? "collection finished" : "finished")}: {logContext}, outcome=collected, readEvents={selection.ReadEvents}, reusedEvents={selection.ReusedEvents}, intervals={selection.CollectedIntervals}, selectedEvents={selection.Events.Count}, skippedChannels={selection.SkippedChannels.Count}, limitedChannels={selection.LimitedChannels.Count}, missingIntervals={selection.HasMissingIntervals}, elapsedMs={elapsedMilliseconds}");
                EnqueueOnUI(() =>
                {
                    if (!IsCurrent())
                    {
                        return;
                    }
                    if (!selection.HasMissingIntervals)
                    {
                        _auditRangeViews[days] = view;
                    }
                    _auditSourceSnapshot = snapshot;
                    _auditSourceEvents = selection.Events;
                    _auditTailCheckedUtc = now;
                    if (refresh && !fullRead)
                    {
                        _lastCompletedAuditUtc = now;
                    }
                    if (!fullRead)
                    {
                        IsAuditing = false;
                    }
                    PresentAuditRangeView(view, scanCompleted: refresh);
                    CaptureWeeklyScanSnapshot(snapshot, selection.Events);
                    if (refresh && !fullRead)
                    {
                        EndStage("Collect event logs", audit: true);
                        EndStage("Analyze findings", audit: true);
                        EndStage("Finish", audit: true);
                        AuditPhase = ScanPhase.SelectingTargets;
                    }
                    ScanTypeText = refresh ? fast ? FastScanLabel : FullScanLabel
                        : "AIHub_AuditPeriod_Loaded".GetLocalizedOrFallback("Period loaded");
                    AuditStatusText = "AIHub_AuditScan_ResultFormat".FormatLocalized(
                        "{0} · {1} days · Read {2} events · Reused {3} · {4} interval(s) · {5:N0} ms",
                        ScanTypeText, days, selection.ReadEvents, selection.ReusedEvents, selection.CollectedIntervals, elapsedMilliseconds);
                    _persistCurrentAudit = refresh && !historical && !selection.HasMissingIntervals;
                    if (fullRead)
                    {
                        ContinueFullScanAnalysis(logContext);
                    }
                    else
                    {
                        PersistCurrentAudit();
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                Logger.LogInfo($"AI Hub audit finished: {logContext}, outcome=cancelled, elapsedMs={elapsed.ElapsedMilliseconds}");
                EnqueueOnUI(() =>
                {
                    if (generation == _auditRangeGeneration && _auditCts?.Token == token)
                    {
                        IsAuditing = false;
                        AuditPhase = ScanPhase.Idle;
                        SetAuditRangeCoverage(PreferChinese() ? "时段加载已取消，未完成区间未加入缓存。" : "Period loading was cancelled; unfinished intervals were not cached.");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"AI Hub audit finished: {logContext}, outcome=failed, elapsedMs={elapsed.ElapsedMilliseconds}");
                Logger.LogError("Could not load the selected audit period", ex);
                EnqueueOnUI(() =>
                {
                    if (!IsCurrent())
                    {
                        return;
                    }
                    IsAuditing = false;
                    AuditPhase = ScanPhase.Failed;
                    SetAuditRangeCoverage(PreferChinese() ? "无法加载所选时段；已有记录已保留。" : "This period could not be loaded; existing records were retained.");
                });
            }
        });
    }

    private void CancelAuditForNavigation()
    {
        _auditRangeGeneration++;
        _auditCts?.Cancel();
        if (IsAiAnalyzing)
        {
            CompleteAiInterruption(_auditOverviewSnapshot, cancelled: true);
        }
        _auditCts?.Dispose();
        _auditCts = null;
        IsAuditing = false;
        _auditIdentityGeneration++;
        _checkingAuditIdentity = false;
        Interlocked.Increment(ref _auditHistoryGeneration);
    }

    private static bool SameAuditEvidence(IReadOnlyList<SecurityEvent> first, IReadOnlyList<SecurityEvent> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }
        var counts = first.GroupBy(item => item).ToDictionary(group => group.Key, group => group.Count());
        return second.GroupBy(item => item).All(group => counts.TryGetValue(group.Key, out int count) && count == group.Count());
    }

    private void PresentAuditRangeView(AuditRangeView view, bool scanCompleted = false)
    {
        _auditSourceSnapshot = view.Snapshot;
        _auditSourceEvents = view.Events;
        _auditOverviewSnapshot = view.Snapshot;
        lock (_lastAuditEvents)
        {
            _lastAuditEvents.Clear();
            _lastAuditEvents.AddRange(view.Events);
        }
        _hasAuditResult = view.HasEvidence;
        _auditIncomplete = IsIncompleteAuditOutcome(view.Snapshot.AnalysisOutcome);
        _auditMergedSummary = string.Empty;
        AiAnalysisRanText = AiStatusText = string.Empty;
        _fluentAiReportPartial = false;
        _aiReportFindings.Clear();
        HasAiReport = false;
        AiReportSummaryText = AiReportMetaText = string.Empty;
        _lastAuditAnalysisAttempt = view.Snapshot.AnalysisOutcome is "failed" or "cancelled" or "partial" ? view.Snapshot.AnalysisOutcome : string.Empty;
        _auditReportNeedsRefresh = false;
        AllIssues.Clear();
        foreach (var finding in view.Snapshot.Findings)
        {
            AllIssues.Add(finding);
        }
        HealthScore = view.Snapshot.HealthScore;
        HealthScoreGrade = HealthScore >= 80 ? "AIHub_HealthScoreGrade_Good".GetLocalizedOrFallback("Good")
            : HealthScore >= 60 ? "AIHub_HealthScoreGrade_Warning".GetLocalizedOrFallback("Warning")
            : "AIHub_HealthScoreGrade_Critical".GetLocalizedOrFallback("Critical");
        HighCount = view.Snapshot.HighCount;
        MediumCount = view.Snapshot.MediumCount;
        LowCount = view.Snapshot.LowCount;
        UpdateHealthSummary(HealthScore, HighCount, MediumCount, LowCount);
        if (!view.HasEvidence)
        {
            HealthVerdict = PreferChinese() ? "所选时段尚无可核验数据" : "No verified data for this period";
            HealthSummaryText = view.Coverage;
        }
        WindowText = $"{view.Snapshot.ScanStart?.ToLocalTime():g} – {view.Snapshot.ScanEnd?.ToLocalTime():g}";
        EventCountText = "AIHub_EventCountFormat".FormatLocalized("{0} events", view.Snapshot.EventsScanned);
        ActivityFindingsText = AllIssues.Count.ToString(CultureInfo.InvariantCulture);
        FinishedText = view.Snapshot.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        SelectedAuditLabel = (view.Snapshot.ScanEnd ?? view.Snapshot.Timestamp).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _suppressAuditDateLoad = true;
        try
        {
            SelectedAuditDate = (view.Snapshot.ScanEnd ?? view.Snapshot.Timestamp).ToLocalTime();
        }
        finally
        {
            _suppressAuditDateLoad = false;
        }
        _persistCurrentAudit = false;
        SetAuditRangeCoverage(view.Coverage);
        RebuildSections();
        RestorePersistedAiReport(view.Snapshot);
        if (!HasAiReport && _auditCompleteReports.TryGetValue((SelectedAuditRangeDays, view.Snapshot.CollectionProfile ?? string.Empty), out var previousReport))
        {
            RestorePersistedAiReport(previousReport);
            _auditReportNeedsRefresh = true;
        }

        var presentation = DescribeAuditOutcome(view.Snapshot.AnalysisOutcome, view.Snapshot.SkippedChannels?.Count > 0);
        AuditStatusText = presentation.ResourceKey.GetLocalizedOrFallback(presentation.Fallback)
            + "AIHub_FinishAudit_ScoreAndFindingsFormat".FormatLocalized(" Health score {0}/100; {1} finding(s).", HealthScore, AllIssues.Count);
        if (HasAiReport)
        {
            AiAnalysisRanText = AiStatusText = AiReportSummaryText;
        }
        else if (_auditIncomplete)
        {
            AiAnalysisRanText = AiStatusText = presentation.ResourceKey.GetLocalizedOrFallback(presentation.Fallback);
        }
        ScanTypeText = _auditHistoricalSelection
            ? "AIHub_ScanType_Historical".GetLocalizedOrFallback("Historical snapshot")
            : PreferChinese() ? "复用已采集事件" : "Reused collected events";
        AuditTimestampText = "AIHub_AuditTimestamp_LastScannedFormat".FormatLocalized("Last scanned: {0:yyyy-MM-dd HH:mm:ss} ({1} findings)",
            view.Snapshot.Timestamp.ToLocalTime(), view.Snapshot.Findings.Count);
        if (!scanCompleted)
        {
            _auditIsRangeNavigation = true;
            _auditWasDeepAnalysis = false;
            _auditClock.Reset();
            _auditProgress.Reset();
            _auditDetailedPercent = null;
            AuditPhase = ScanPhase.Idle;
            OnPropertyChanged(nameof(AuditElapsedText));
            PublishScanProgress();
            ScanTypeText = "AIHub_AuditPeriod_Cached".GetLocalizedOrFallback("Cached period");
            AuditStatusText = "AIHub_AuditPeriod_CachedFormat".FormatLocalized("Showing cached evidence for {0} days ({1} events).", SelectedAuditRangeDays, view.Snapshot.EventsScanned);
        }
        RefreshAuditDetailText();
        RefreshAuditActions();
    }

    private void RefreshAuditReportIdentity(string currentScope)
    {
        _knownAuditAnalysisScope = currentScope;
        _auditReportNeedsRefresh = HasAiReport && (!HasPersistedAiAnalysis(_auditOverviewSnapshot)
            || string.IsNullOrEmpty(_auditOverviewSnapshot?.AiAnalysisScope)
            || !string.Equals(_auditOverviewSnapshot.AiAnalysisScope, currentScope, StringComparison.Ordinal));
        RefreshAuditActions();
    }
}
