// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;
using ManagedCommon;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AIHubPageViewModel
{
    private async Task<AuditResult> RestoreHistoricalRiskAsync(AuditResult history)
    {
        if (history is null)
        {
            return null;
        }

        WeeklyScanSnapshot saved = null;
        if (history.ScanStart is { } start && history.ScanEnd is { } end && end - start == TimeSpan.FromDays(7))
        {
            try
            {
                // Storage only: never rescan or call a model to restore the risk floor.
                await _weeklyScanWriteTask.ConfigureAwait(false);
                saved = await _weeklyScanStore.LoadAsync(new DateTimeOffset(end.ToUniversalTime())).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Logger.LogWarning($"Saved audit risk evidence is unavailable ({exception.GetType().Name}).");
            }
        }

        return ReconcileHistoricalRisk(history, saved);
    }

    internal static AuditResult ReconcileHistoricalRisk(AuditResult history, WeeklyScanSnapshot saved = null)
    {
        // A saved seven-day file may belong to a later scan on the same day, or may
        // omit channels. Never apply its item-N references to another evidence set.
        bool exactEvidence = saved is { RawEventsAvailable: true, RawEventsTruncated: false }
            && saved.Audit.Timestamp == history.Timestamp
            && saved.Audit.ScanStart == history.ScanStart && saved.Audit.ScanEnd == history.ScanEnd
            && saved.Events.Count == history.EventsScanned
            && !string.IsNullOrEmpty(history.AiEvidenceFingerprint)
            && (string.Equals(history.AiEvidenceFingerprint, AiHubAuditAnalysisService.CreateEvidenceFingerprint(saved.Events, "en-US"), StringComparison.Ordinal)
                || string.Equals(history.AiEvidenceFingerprint, AiHubAuditAnalysisService.CreateEvidenceFingerprint(saved.Events, "zh-CN"), StringComparison.Ordinal));
        var copy = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(history, AIHubLibJsonContext.Default.AuditResult), AIHubLibJsonContext.Default.AuditResult);
        bool hasObservedLevels = copy.Findings.Any(finding => finding.ObservedEventLevel.HasValue);
        if (exactEvidence)
        {
            var rules = AuditRuleEngine.Analyze(saved.Events);
            copy.Findings = HasPersistedAiAnalysis(history)
                ? MergeIssues(rules, history.Findings.Where(IsPersistedAiFinding).Cast<AuditIssue>().ToList(), saved.Events,
                    replaceReviewedEvidence: string.Equals(history.AnalysisOutcome, "ok", StringComparison.OrdinalIgnoreCase))
                : rules;
        }
        else if (hasObservedLevels)
        {
            foreach (var finding in copy.Findings.Where(finding => finding.ObservedEventLevel is 1 or 2))
            {
                finding.Severity = "High";
            }
        }

        if (exactEvidence || hasObservedLevels)
        {
            copy.HealthScore = HealthScoreCalculator.Calculate(copy.Findings);
            if (HasPersistedAiAnalysis(history))
            {
                copy.AiReportSummary = BuildAuditNarrative(copy.Findings, copy.EventsScanned,
                    Math.Min(copy.EventsScanned, copy.AiAnalyzedEvents + copy.AiReusedEvents), PreferChinese());
            }
        }

        return copy;
    }
}
