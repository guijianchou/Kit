// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Services;
using ManagedCommon;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AIHubPageViewModel
{
    // Both entry points use the same shared engine. Delegates allow the UI harness to
    // exercise collection-to-analysis transitions without a real model or credentials.
    private Func<bool> _auditAiAvailable = () => AiHubAuditAnalysisService.IsAvailable;
    private Func<IReadOnlyList<SecurityEvent>, IProgress<AiTaskProgress>, IProgress<AiHubAuditAnalysisService.AuditAnalysisResult>,
        AuditAnalysisCache, AiHubAuditAnalysisService.AuditAnalysisRetryState, CancellationToken, Task<AiHubAuditAnalysisService.AuditAnalysisResult>> _analyzeAuditEvents =
        (events, progress, partial, cache, retry, token) => AiHubAuditAnalysisService.AnalyzeEventsAsync(events, progress, partial, cache, false, retry, token);
    private string _fullScanLogContext;

    private void ContinueFullScanAnalysis(string logContext)
    {
        _fullScanLogContext = logContext;
        Logger.LogInfo($"AI Hub audit continuing: {logContext}, phase=ai, events={GetCachedAuditEventCount()}");
        StartDeepAnalysis(continueScan: true);
    }

    private void CompleteFullScanWithoutAnalysis(AuditResult snapshot, bool noEvents)
    {
        string message = noEvents
            ? "AIHub_FullScan_NoEvents".GetLocalizedOrFallback("Log collection finished with no events to analyze. AI did not run.")
            : "AIHub_FullScan_AiUnavailable".GetLocalizedOrFallback("Log collection finished, but AI did not run because the AI service is unavailable. Configure or enable it, then retry the AI report.");
        if (_retainingAiReport)
        {
            message += "AIHub_FullScan_PreviousReport".GetLocalizedOrFallback(" The previous complete AI report remains available.");
        }

        _retainingAiReport = false;
        _auditIncomplete = true;
        _lastAuditAnalysisAttempt = "failed";
        snapshot.AnalysisOutcome = noEvents ? "skipped" : "failed";
        AiAnalysisRanText = AuditStatusText = AiStatusText = message;
        AuditPhase = ScanPhase.Failed;
        FinishedText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        ShowStatus(message, noEvents ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
        FinishFullScanAnalysis(snapshot, noEvents ? "no-events" : "ai-unavailable");
        IsAiAnalyzing = false;
        RefreshAuditCoverage();
        RefreshAuditActions();
    }

    private void FinishFullScanAnalysis(AuditResult snapshot, string outcome)
    {
        if (_fullScanLogContext is not { } context)
        {
            return;
        }

        _fullScanLogContext = null;
        if (snapshot.AnalysisOutcome == "pending")
        {
            snapshot.AnalysisOutcome = outcome;
        }

        if (outcome == "ok")
        {
            _lastCompletedAuditUtc = DateTime.UtcNow;
        }

        Logger.LogInfo($"AI Hub audit finished: {context}, outcome={outcome}, phase=ai, elapsedMs={_auditClock.ElapsedMilliseconds}");

        // The pending write consumed its permission. The final revision must replace
        // that same timestamp, including failures which retain an earlier AI report.
        _persistCurrentAudit = !_auditHistoricalSelection && !snapshot.EvidenceIncomplete
            && ReferenceEquals(snapshot, _auditOverviewSnapshot) && ReferenceEquals(snapshot, _auditSourceSnapshot);
        PersistCurrentAudit();
    }
}
