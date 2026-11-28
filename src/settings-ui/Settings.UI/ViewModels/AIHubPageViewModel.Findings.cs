// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AIHubPageViewModel
{
    /// <summary>
    /// Reconciles findings against the evidence actually reviewed. Only a complete,
    /// scope-stable AI result may dismiss or downgrade a local interpretation.
    /// </summary>
    internal static List<AuditIssueEnhanced> MergeIssues(
        List<AuditIssueEnhanced> ruleIssues,
        IReadOnlyList<AuditIssue> aiIssues,
        IReadOnlyList<SecurityEvent> events,
        bool replaceReviewedEvidence = false)
    {
        var selected = AiHubAuditAnalysisService.SelectEvidence(events);
        var eventGroups = events.ToLookup(item => (item.EventId.ToString(CultureInfo.InvariantCulture), (item.LogName ?? string.Empty).ToUpperInvariant()));
        var ruleEvidence = ruleIssues.Distinct().ToDictionary(issue => issue, issue =>
            eventGroups[(issue.EventId, (issue.LogName ?? string.Empty).ToUpperInvariant())].Where(item => MatchesRule(item, issue)).ToArray());
        var reviewedCounts = selected.GroupBy(item => item).ToDictionary(group => group.Key, group => group.Count());
        var unreviewed = new List<SecurityEvent>();
        foreach (var item in events)
        {
            if (replaceReviewedEvidence && reviewedCounts.TryGetValue(item, out int count) && count > 0)
            {
                reviewedCounts[item] = count - 1;
            }
            else
            {
                unreviewed.Add(item);
            }
        }
        var merged = replaceReviewedEvidence ? AuditRuleEngine.Analyze(unreviewed) : new List<AuditIssueEnhanced>(ruleIssues);
        if (replaceReviewedEvidence)
        {
            // Recollection may no longer contain an older channel or event. Its prior
            // finding cannot be dismissed without seeing that evidence again.
            merged.AddRange(ruleIssues.Where(issue => ruleEvidence[issue].Length == 0));
        }
        var enhanced = new List<AuditIssueEnhanced>();
        var references = aiIssues.Where(issue => issue is not null).SelectMany(issue => (issue.RelatedEventRefs ?? []).Append(issue.EventRef))
            .Where(value => value is not null && value.StartsWith("item-", StringComparison.Ordinal))
            .Select(value => int.TryParse(value.AsSpan(5), out int index) ? index - 1 : -1)
            .Where(index => index >= 0 && index < selected.Count).Distinct().Select(index => selected[index])
            .GroupBy(item => item).ToDictionary(group => group.Key, group => group.Count());
        var enrichedRules = ruleEvidence.Where(entry => entry.Value.Length > 0 && entry.Value.GroupBy(item => item)
            .All(group => references.TryGetValue(group.Key, out int count) && count >= group.Count())).Select(entry => entry.Key).ToHashSet();
        foreach (var ai in AiHubAuditAnalysisService.DeduplicateIssues(aiIssues))
        {
            if (ai is null || string.IsNullOrWhiteSpace(ai.Title))
            {
                continue;
            }

            var finding = ToEnhanced(ai);

            // The reference identifies evidence; model-authored channel/event labels do not.
            if (ai.EventRef.StartsWith("item-", StringComparison.Ordinal)
                && int.TryParse(ai.EventRef.AsSpan(5), out int reference) && reference > 0 && reference <= selected.Count)
            {
                var origin = selected[reference - 1];
                finding.LogName = origin.LogName ?? string.Empty;
                finding.EventId = origin.EventId.ToString(CultureInfo.InvariantCulture);
                finding.Source = origin.ProviderName ?? string.Empty;
            }
            var matchingRules = ruleIssues.Where(issue => issue.EventId == finding.EventId &&
                string.Equals(issue.LogName, finding.LogName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!replaceReviewedEvidence && matchingRules.Any(issue => issue.IsHigh))
            {
                finding.Severity = "High";
            }
            else if (!replaceReviewedEvidence && finding.Severity == "Low" && matchingRules.Any(issue => issue.IsMedium))
            {
                finding.Severity = "Medium";
            }
            if (!replaceReviewedEvidence)
            {
                // Partial findings enrich evidence but cannot eliminate unreviewed
                // local observations sharing a channel and event ID.
                merged.RemoveAll(issue => issue.EventId == finding.EventId &&
                    string.Equals(issue.LogName, finding.LogName, StringComparison.OrdinalIgnoreCase)
                    && enrichedRules.Contains(issue));
            }
            enhanced.Add(finding);
        }
        merged.AddRange(enhanced);
        return merged;
    }

    private static bool MatchesRule(SecurityEvent item, AuditIssue issue) =>
        item.EventId.ToString(CultureInfo.InvariantCulture) == issue.EventId
        && string.Equals(item.LogName, issue.LogName, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrEmpty(issue.Source) || string.Equals(item.ProviderName, issue.Source, StringComparison.OrdinalIgnoreCase));

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
            EventTimestamp = ai.EventTimestamp,
            LogName = ai.LogName,
            Category = string.IsNullOrWhiteSpace(ai.Category) ? "Other" : ai.Category,
            Severity = ai.Severity,
            Confidence = ai.Confidence,
            Affected = ai.Affected ?? string.Empty,
            Title = ai.Title,
            TitleZh = ai.TitleZh,
            Description = ai.Description,
            DescriptionZh = ai.DescriptionZh,
            RootCause = ai.RootCause,
            RootCauseZh = ai.RootCauseZh,
            Recommendation = ai.Recommendation,
            RecommendationZh = ai.RecommendationZh,
            RemediationPlan = ai.RemediationPlan?.Clone(),
            Occurrences = ai.Occurrences,
            RelatedEventRefs = ai.RelatedEventRefs?.ToList() ?? new List<string>(),
            Source = string.IsNullOrWhiteSpace(ai.Source) ? "AI" : ai.Source,
        };
}
