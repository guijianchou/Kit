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
using Kit.Settings.UI.Services;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AIHubPageViewModel
{
    private void RunDeepAnalysis()
    {
        if (!CanRunDeepAnalysis)
        {
            return;
        }
        if (CanViewCurrentAiReport)
        {
            var snapshot = _auditOverviewSnapshot;

            // Legacy reports can be opened without recollecting evidence or an AI route.
            if (string.IsNullOrEmpty(snapshot?.AiAnalysisScope) || !AiHubAuditAnalysisService.IsAvailable)
            {
                RequestJumpToAiReport?.Invoke(this, EventArgs.Empty);
                return;
            }
            long generation = ++_auditIdentityGeneration;
            long readinessGeneration = _readinessRefreshGeneration;
            _checkingAuditIdentity = true;
            RefreshAuditActions();
            _ = Task.Run(async () =>
            {
                try
                {
                    string scope = await AiHubAuditAnalysisService.ReadAnalysisScopeAsync(CancellationToken.None).ConfigureAwait(false);
                    EnqueueOnUI(() =>
                    {
                        if (generation != _auditIdentityGeneration)
                        {
                            return;
                        }
                        _checkingAuditIdentity = false;
                        if (readinessGeneration != _readinessRefreshGeneration)
                        {
                            RefreshAuditActions();
                            RefreshAiReadinessFromCache();
                            return;
                        }
                        if (!ReferenceEquals(snapshot, _auditOverviewSnapshot) || IsAuditing || IsAiAnalyzing)
                        {
                            RefreshAuditActions();
                            return;
                        }
                        RefreshAuditReportIdentity(scope);
                        if (_auditReportNeedsRefresh)
                        {
                            ShowStatus(PreferChinese() ? "模型或分析策略已变化。点击“更新 AI 报告”生成新结论。"
                                : "The model or analysis policy changed. Choose Update AI report to generate a new assessment.", InfoBarSeverity.Informational);
                        }
                        else
                        {
                            RequestJumpToAiReport?.Invoke(this, EventArgs.Empty);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Could not check audit report identity: {ex.GetType().Name}");
                    EnqueueOnUI(() =>
                    {
                        if (generation != _auditIdentityGeneration)
                        {
                            return;
                        }
                        _checkingAuditIdentity = false;
                        RefreshAuditActions();
                        if (ReferenceEquals(snapshot, _auditOverviewSnapshot))
                        {
                            RequestJumpToAiReport?.Invoke(this, EventArgs.Empty);
                        }
                    });
                }
            });
            return;
        }
        StartDeepAnalysis();
    }

    private void StartDeepAnalysis(bool continueScan = false)
    {
        if (continueScan ? !IsEnabled || _disposed || !IsAuditing || IsAiAnalyzing || _auditCts?.IsCancellationRequested != false : !CanStartDeepAnalysis)
        {
            Logger.LogInfo(
                $"AI deep analysis skipped: enabled={IsEnabled}, auditing={_isAuditing}, " +
                $"analyzing={_isAiAnalyzing}, hasAuditData={HasAuditData}, cachedEvents={GetCachedAuditEventCount()}, " +
                $"aiAvailable={_auditAiAvailable()}");
            return;
        }

        List<SecurityEvent> events;
        lock (_lastAuditEvents)
        {
            events = _lastAuditEvents.ToList();
        }

        bool needsEventCollection = events.Count == 0 && !continueScan;
        var snapshot = _auditOverviewSnapshot;
        List<AuditIssueEnhanced> initialRuleIssues = needsEventCollection ? [] : RunRuleBasedAuditAnalysis(events);

        Logger.LogInfo(
            $"AI deep analysis started: mode={(needsEventCollection ? "recollect-snapshot" : "cached-events")}, " +
            $"cachedEvents={events.Count}, hasSnapshot={snapshot is not null}");

        if (!continueScan)
        {
            _auditProgress.Reset();
        }
        _auditWasDeepAnalysis = true;
        _retainingAiReport = HasAiReport && !_fluentAiReportPartial;
        _auditIncomplete = false;
        _lastAuditAnalysisAttempt = string.Empty;
        if (!_retainingAiReport)
        {
            _fluentAiReportPartial = false;
            _aiReportFindings.Clear();
            HasAiReport = false;
            AiReportSummaryText = AiReportMetaText = string.Empty;
        }
        _auditMergedSummary = string.Empty;
        AiAnalysisRanText = string.Empty;
        _persistCurrentAudit = !_auditHistoricalSelection && snapshot?.EvidenceIncomplete != true && ReferenceEquals(snapshot, _auditSourceSnapshot);
        IsAiAnalyzing = true;
        if (continueScan)
        {
            IsAuditing = false;
        }
        AuditPhase = ScanPhase.Scanning;
        if (needsEventCollection)
        {
            BeginStage("Collect event logs", audit: true);
            AiStatusText = "AIHub_DeepAnalysis_Recollecting".GetLocalizedOrFallback("Collecting Windows events from this audit window for AI deep analysis...");
        }
        else
        {
            EndStage("Collect event logs", audit: true);
            BeginStage("Analyze findings", audit: true);
            if (continueScan)
            {
                _auditDetailedPercent = 30;
                PublishScanProgress();
            }

            AiStatusText = "AIHub_DeepAnalysis_Requesting".GetLocalizedOrFallback("Requesting AI deep analysis...");
        }

        AuditStatusText = AiStatusText;
        FinishedText = string.Empty;
        if (snapshot is not null && (continueScan || !_retainingAiReport))
        {
            snapshot.AnalysisOutcome = "pending";
        }
        RefreshAuditCoverage();
        FinishAuditStatus(HealthScore, AllIssues.Count, "pending", snapshot?.SkippedChannels ?? []);

        if (continueScan)
        {
            if (events.Count == 0 || !_auditAiAvailable())
            {
                CompleteFullScanWithoutAnalysis(snapshot, noEvents: events.Count == 0);
                return;
            }
            PersistCurrentAudit();
            _persistCurrentAudit = !_auditHistoricalSelection && !snapshot.EvidenceIncomplete;
        }
        else
        {
            _auditCts?.Cancel();
            _auditCts?.Dispose();
            _auditCts = new CancellationTokenSource();
        }
        CancellationToken token = _auditCts.Token;
        var auditMode = IsFullAuditMode ? EventLogService.AuditMode.Full : EventLogService.AuditMode.Extended;
        int rangeDays = _selectedScopeIndex switch { 1 => 7, 2 => 30, _ => 2 };
        int publishedBatches = 0;
        bool IsCurrent() => _auditCts?.Token == token && ReferenceEquals(snapshot, _auditOverviewSnapshot);
        var collectionProgress = new Progress<EventCollectionProgress>(update => EnqueueOnUI(() =>
        {
            if (IsCurrent() && IsAiAnalyzing && _auditProgress.CurrentStage == "Collect event logs")
            {
                ApplyAuditCollectionProgress(update);
                AiStatusText = AuditStatusText;
            }
        }, token));
        var progress = new Progress<AiTaskProgress>(update => EnqueueOnUI(() =>
        {
            if (IsCurrent() && IsAiAnalyzing && AuditPhase == ScanPhase.Scanning)
            {
                ApplyAuditAiProgress(update);
            }
        }, token));
        List<AuditIssueEnhanced> ruleIssues = initialRuleIssues;
        List<string> recollectedSkippedChannels = null;
        List<string> recollectedLimitedChannels = null;

        _ = Task.Run(async () =>
        {
            try
            {
                if (needsEventCollection)
                {
                    DateTime windowTo = snapshot?.ScanEnd?.ToUniversalTime() ?? DateTime.UtcNow;
                    DateTime windowFrom = snapshot?.ScanStart?.ToUniversalTime() ?? windowTo.AddDays(-rangeDays);
                    if (windowFrom >= windowTo)
                    {
                        windowTo = DateTime.UtcNow;
                        windowFrom = windowTo.AddDays(-rangeDays);
                    }

                    var eventLogService = new EventLogService();
                    events = await eventLogService.CollectEventsAsync(windowFrom, windowTo, auditMode, 5000, collectionProgress, token).ConfigureAwait(false);
                    recollectedSkippedChannels = eventLogService.SkippedChannels.ToList();
                    recollectedLimitedChannels = eventLogService.LimitedChannels.ToList();
                    token.ThrowIfCancellationRequested();

                    if (events.Count == 0)
                    {
                        Logger.LogInfo("AI deep analysis re-collection found 0 events in the snapshot window.");
                        EnqueueOnUI(() =>
                        {
                            if (!IsCurrent())
                            {
                                return;
                            }

                            string message = "AIHub_DeepAnalysis_NoEventsInWindow".GetLocalizedOrFallback("No Windows events remain in this audit window. Run a new scan first.");
                            AiStatusText = AuditStatusText = AiAnalysisRanText = message;
                            _auditIncomplete = false;
                            EndStage("Collect event logs", audit: true);
                            EndStage("Analyze findings", audit: true);
                            EndStage("Finish", audit: true);
                            AuditPhase = ScanPhase.SelectingTargets;
                            FinishedText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                            if (snapshot is not null && !_retainingAiReport)
                            {
                                snapshot.AnalysisOutcome = "skipped";
                            }
                            _lastAuditAnalysisAttempt = "failed";
                            _retainingAiReport = false;
                            IsAiAnalyzing = false;
                            RefreshAuditCoverage();
                            ShowStatus(message, InfoBarSeverity.Warning);
                        }, token);
                        return;
                    }

                    ruleIssues = RunRuleBasedAuditAnalysis(events);
                    var collectedEvents = events;
                    EnqueueOnUI(() =>
                    {
                        if (!IsCurrent() || !IsAiAnalyzing)
                        {
                            return;
                        }

                        lock (_lastAuditEvents)
                        {
                            _lastAuditEvents.Clear();
                            _lastAuditEvents.AddRange(collectedEvents);
                        }

                        if (snapshot is not null && !_retainingAiReport)
                        {
                            snapshot.EventsScanned = collectedEvents.Count;
                        }

                        if (!_retainingAiReport)
                        {
                            EventCountText = "AIHub_EventCountFormat".FormatLocalized("{0} events", collectedEvents.Count);
                        }
                        EndStage("Collect event logs", audit: true);
                        BeginStage("Analyze findings", audit: true);
                        AiStatusText = AuditStatusText = "AIHub_DeepAnalysis_Requesting".GetLocalizedOrFallback("Requesting AI deep analysis...");
                    }, token);
                }

                var partialResults = new Progress<AiHubAuditAnalysisService.AuditAnalysisResult>(result => EnqueueOnUI(() =>
                {
                    if (IsCurrent() && IsAiAnalyzing && result.ValidatedBatchCount > publishedBatches)
                    {
                        publishedBatches = result.ValidatedBatchCount;
                        ApplyAiAnalysis(result, ruleIssues, events, snapshot, interim: true);
                    }
                }, token));

                var result = await _analyzeAuditEvents(events, progress, partialResults, _incrementalAiCache, _auditRetryState, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                EnqueueOnUI(() =>
                {
                    if (!IsCurrent())
                    {
                        return;
                    }
                    ApplyAiAnalysis(result, ruleIssues, events, snapshot, interim: false);
                    FinishFullScanAnalysis(snapshot, _lastAuditAnalysisAttempt is "failed" or "partial" ? _lastAuditAnalysisAttempt : "ok");
                    if (needsEventCollection && result.Issues is not null && !result.IsPartial && snapshot is not null)
                    {
                        snapshot.CollectionProfile = AuditCollectionProfile.ForMode(auditMode);
                        snapshot.SkippedChannels = recollectedSkippedChannels;
                        snapshot.LimitedChannels = recollectedLimitedChannels;
                        RefreshAuditCoverage();
                    }
                    IsAiAnalyzing = false;
                    PersistCurrentAudit();
                }, token);
            }
            catch (OperationCanceledException)
            {
                EnqueueOnUI(() =>
                {
                    if (!IsCurrent())
                    {
                        return;
                    }
                    CompleteAiInterruption(snapshot, cancelled: true);
                });
            }
            catch (Exception ex)
            {
                Logger.LogError($"AI analysis failed: type={ex.GetType().Name}, hresult=0x{ex.HResult:X8}");
                EnqueueOnUI(() =>
                {
                    if (!IsCurrent())
                    {
                        return;
                    }
                    CompleteAiInterruption(snapshot, cancelled: false);
                });
            }
            finally
            {
                EnqueueOnUI(() =>
                {
                    if (!IsCurrent())
                    {
                        return;
                    }
                    if (token.IsCancellationRequested && IsAiAnalyzing)
                    {
                        CompleteAiInterruption(snapshot, cancelled: true);
                    }
                    IsAiAnalyzing = false;
                });
            }
        });
    }

    private void RunAudit(bool fast) => StartAuditRangeLoad(fast, refresh: true);

    private void FinishAuditStatus(int score, int findingCount, string aiOutcome, IReadOnlyList<string> skippedChannels)
    {
        var presentation = DescribeAuditOutcome(aiOutcome, skippedChannels.Count > 0);
        string message = presentation.ResourceKey.GetLocalizedOrFallback(presentation.Fallback);
        message += "AIHub_FinishAudit_ScoreAndFindingsFormat".FormatLocalized(" Health score {0}/100; {1} finding(s).", score, findingCount);
        if (skippedChannels.Count > 0)
        {
            message += "AIHub_FinishAudit_SkippedChannelsFormat".FormatLocalized(" Could not read: {0}.", string.Join(", ", skippedChannels));
        }

        AuditStatusText = message;
        ShowStatus(message, presentation.Severity);
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

    private static List<AuditIssueEnhanced> RunRuleBasedAuditAnalysis(List<SecurityEvent> events)
        => AuditRuleEngine.Analyze(events);

    private void RebuildSections(bool expandMatches = false)
    {
        RefreshAuditSummary();
        var previousState = FindingSections.ToDictionary(section => section.Name, section => section.IsExpanded);

        // Category and event-log source describe different dimensions of a finding.
        var sourceIssues = AllIssues.Where(issue =>
            (_findingCategory == null || string.Equals(issue.Category ?? string.Empty, _findingCategory, StringComparison.OrdinalIgnoreCase)) &&
            (_sourceFilter == "All" || string.Equals(issue.LogName, _sourceFilter, StringComparison.OrdinalIgnoreCase))).ToList();

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

        // 5. Priority findings (top 3)
        PriorityFindings.Clear();
        foreach (var item in visibleIssues.OrderByDescending(i => i.IsHigh).ThenByDescending(i => i.IsMedium).ThenByDescending(i => i.Occurrences).Take(3))
        {
            PriorityFindings.Add(item);
        }

        // 6. Filtered issues (for backward compatibility)
        FilteredIssues.Clear();
        foreach (var item in visibleIssues)
        {
            FilteredIssues.Add(item);
        }

        RebuildOverviewFindings();

        // Notify properties
        OnPropertyChanged(nameof(CanRunDeepAnalysis));
        OnPropertyChanged(nameof(DeepAnalyzeHint));
        ((RelayCommand)DeepAnalyzeCommand).OnCanExecuteChanged();
        RefreshAuditActions();
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
        long generation = Interlocked.Increment(ref _auditHistoryGeneration);
        var auditAtLoad = _auditCts;
        var snapshotAtLoad = _auditOverviewSnapshot;
        Task pendingWrites = _auditHistoryWriteTask;
        Task.Run(async () =>
        {
            try
            {
                var historyList = await ReadAuditHistoryAfterWritesAsync(pendingWrites, _auditHistoryStorage.LoadHistoryAsync).ConfigureAwait(false);
                AuditHistoryStatistics statistics = AuditHistoryStatistics.From(historyList);
                EnqueueOnUI(() =>
                {
                    if (generation == _auditHistoryGeneration && !IsAuditing && !IsAiAnalyzing && _auditCts == auditAtLoad && ReferenceEquals(snapshotAtLoad, _auditOverviewSnapshot))
                    {
                        ApplyStatistics(statistics);
                    }
                });

                var history = historyList.OrderByDescending(result => result.Timestamp).FirstOrDefault();
                EnqueueOnUI(() =>
                {
                    if (generation != _auditHistoryGeneration || IsAuditing || IsAiAnalyzing || _auditCts != auditAtLoad || !ReferenceEquals(snapshotAtLoad, _auditOverviewSnapshot))
                    {
                        return;
                    }
                    if (history != null)
                    {
                        ApplyHistoricalAudit(history);
                        _auditHistoricalSelection = false;
                        RefreshAuditActions();
                    }
                    else
                    {
                        ClearAuditSnapshot();
                        SelectedAuditLabel = LatestAuditLabel;
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to load the most recent AI Hub audit snapshot", ex);
            }
        });
    }

    private void ApplyHistoricalAudit(AuditResult history)
    {
        ClearAuditSnapshot();
        _auditHistoricalSelection = true;
        _auditOverviewSnapshot = history;
        RegisterAuditEvidence(history, null);
        _auditIncomplete = IsIncompleteAuditOutcome(history.AnalysisOutcome);
        if (_auditIncomplete)
        {
            AiAnalysisRanText = AuditStatusText = "AIHub_AuditOverviewPartialAi".GetLocalizedOrFallback(
                "AI analysis was incomplete; this overview retains local findings and any validated AI findings.");
            _auditDetailedPercent = null;
            AuditPhase = ScanPhase.SelectingTargets;
        }
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
        WindowText = history.ScanStart.HasValue && history.ScanEnd.HasValue
            ? $"{history.ScanStart.Value.ToLocalTime():g} – {history.ScanEnd.Value.ToLocalTime():g}"
            : "AIHub_Window_AllTimes".GetLocalizedOrFallback("All times");
        EventCountText = history.EventsScanned > 0 ? "AIHub_EventCountFormat".FormatLocalized("{0} events", history.EventsScanned)
            : "AIHub_EventCount_HistoricalFormat".FormatLocalized("{0} findings", history.Findings.Count);
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
        RestorePersistedAiReport(history);
        int historicalDays = AuditSummary.From(history).RangeDays ?? 0;
        if (historicalDays is 2 or 7 or 30)
        {
            _selectedScopeIndex = historicalDays switch { 7 => 1, 30 => 2, _ => 0 };
            _auditRangeViews[historicalDays] = new(history, [], true, string.Empty);
            OnPropertyChanged(nameof(FullScanRangeIndex));
            OnPropertyChanged(nameof(ScanRangeDescription));
        }
        else
        {
            SetAuditRangeCoverage(PreferChinese() ? "当前显示历史记录的原始范围。选择时段后将自动读取对应日期的事件，不修改已保存的历史报告。"
                : "Showing the original historical snapshot. Selecting a period reads its historical events without changing saved reports.");
        }
        RefreshCommands();
    }

    private void ClearAuditSnapshot()
    {
        CancelAuditForNavigation();
        _auditRangeAnchorUtc = null;
        _auditCompleteReports.Clear();
        _auditRangeProfile = string.Empty;
        _auditHistoricalSelection = false;
        _auditClock.Reset();
        OnPropertyChanged(nameof(AuditElapsedText));
        _auditOverviewSnapshot = null;
        _auditSourceSnapshot = null;
        _auditSourceEvents = null;
        _auditRangeViews.Clear();
        _auditIdentityGeneration++;
        _checkingAuditIdentity = false;
        _lastAuditAnalysisAttempt = string.Empty;
        _retainingAiReport = false;
        SetAuditRangeCoverage(string.Empty);
        _auditMergedSummary = string.Empty;
        AllIssues.Clear();
        _hasAuditResult = false;
        _auditIncomplete = false;
        _auditWasDeepAnalysis = false;
        _fluentAiReportPartial = false;
        _aiReportFindings.Clear();
        HasAiReport = false;
        AiReportSummaryText = string.Empty;
        AiReportMetaText = string.Empty;
        AiStatusText = string.Empty;
        AiAnalysisRanText = string.Empty;
        lock (_lastAuditEvents)
        {
            _lastAuditEvents.Clear();
        }
        HealthScore = 100;
        HighCount = MediumCount = LowCount = 0;
        HealthScoreGrade = "AIHub_HealthScoreGrade_Good".GetLocalizedOrFallback("Good");
        HealthVerdict = "AIHub_HealthVerdict_NotScanned".GetLocalizedOrFallback("Not scanned yet");
        HealthSummaryText = "AIHub_HealthSummary_NotScanned".GetLocalizedOrFallback("No audit snapshot is available for this date.");
        AuditTimestampText = "AIHub_AuditTimestamp_NotScanned".GetLocalizedOrFallback("Not scanned yet");
        AuditStatusText = "AIHub_AuditStatus_Ready".GetLocalizedOrFallback("Ready");
        ScanTypeText = FinishedText = WindowText = EventCountText = string.Empty;
        _auditProgress.Reset();
        AuditPhase = ScanPhase.Idle;
        RebuildSections();
        RefreshCommands();
    }

    private async Task LoadAuditForDateAsync(DateTime localDate)
    {
        long generation = Interlocked.Increment(ref _auditHistoryGeneration);
        var auditAtLoad = _auditCts;
        try
        {
            var historyList = await ReadAuditHistoryAfterWritesAsync(_auditHistoryWriteTask, _auditHistoryStorage.LoadHistoryAsync).ConfigureAwait(false);
            var match = historyList
                .Where(h => h.Timestamp.ToLocalTime().Date == localDate)
                .OrderByDescending(h => h.Timestamp)
                .FirstOrDefault();

            EnqueueOnUI(() =>
            {
                if (generation != _auditHistoryGeneration || IsAuditing || IsAiAnalyzing || _auditCts != auditAtLoad)
                {
                    return;
                }

                if (match != null)
                {
                    ApplyHistoricalAudit(match);
                }
                else
                {
                    ClearAuditSnapshot();
                    _auditHistoricalSelection = true;
                    _auditRangeAnchorUtc = localDate.Date.AddDays(1).ToUniversalTime() < DateTime.UtcNow
                        ? localDate.Date.AddDays(1).ToUniversalTime() : DateTime.UtcNow;
                    SelectedAuditLabel = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    RefreshAuditActions();
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
}
