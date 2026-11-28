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
    /// Reconciles findings against the evidence actually reviewed. Complete AI reviews
    /// may correct local interpretations, but cannot hide Critical or Error evidence.
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
        var enhanced = new List<AuditIssueEnhanced>();
        var coveredIndices = new HashSet<int>();
        var validIssues = aiIssues.Where(issue => issue is not null && !string.IsNullOrWhiteSpace(issue.Title)
            && ResolveAuditEvidenceReferences(issue, selected.Count) is not null)
            .Select(issue => new AuditIssueEnhanced(issue) { RelatedEventRefs = issue.RelatedEventRefs?.ToList() ?? [] });
        foreach (var ai in AiHubAuditAnalysisService.DeduplicateIssues(validIssues))
        {
            var indices = ResolveAuditEvidenceReferences(ai, selected.Count);
            if (indices is null)
            {
                continue;
            }

            var finding = ToEnhanced(ai);

            // The reference identifies evidence; model-authored channel/event labels do not.
            var origin = selected[indices[0]];
            finding.LogName = origin.LogName ?? string.Empty;
            finding.EventId = origin.EventId.ToString(CultureInfo.InvariantCulture);
            finding.Source = origin.ProviderName ?? string.Empty;
            var evidence = indices.Select(index => selected[index]).ToArray();
            finding.ObservedEventLevel = AuditRiskPolicy.MostSevereLevel(evidence);
            finding.Severity = AuditRiskPolicy.ApplySeverityFloor(finding.Severity, evidence);
            var matchingRules = ruleIssues.Where(issue => evidence.Any(item => MatchesRule(item, issue))).ToList();
            if (!replaceReviewedEvidence && matchingRules.Any(issue => issue.IsHigh))
            {
                finding.Severity = "High";
            }
            else if (!replaceReviewedEvidence && finding.Severity == "Low" && matchingRules.Any(issue => issue.IsMedium))
            {
                finding.Severity = "Medium";
            }
            coveredIndices.UnionWith(indices);
            enhanced.Add(finding);
        }

        var references = coveredIndices.Select(index => selected[index]).GroupBy(item => item)
            .ToDictionary(group => group.Key, group => group.Count());
        List<AuditIssueEnhanced> merged;
        if (replaceReviewedEvidence)
        {
            var reviewedCounts = selected.GroupBy(item => item).ToDictionary(group => group.Key, group => group.Count());
            var retained = new List<SecurityEvent>();
            foreach (var item in events)
            {
                bool reviewed = ConsumeEvidenceCount(reviewedCounts, item);
                bool represented = reviewed && ConsumeEvidenceCount(references, item);

                // A completed batch does not prove that the AI reported each observed
                // failure. Rebuild only the remaining evidence so a partially cited
                // group neither disappears nor duplicates its represented occurrences.
                if (!reviewed || (AuditRiskPolicy.RequiresHighRisk(item) && !represented))
                {
                    retained.Add(item);
                }
            }
            merged = AuditRuleEngine.Analyze(retained);

            // A missing channel/event cannot be dismissed without seeing its evidence again.
            merged.AddRange(ruleIssues.Where(issue => ruleEvidence[issue].Length == 0));
        }
        else
        {
            // Partial output still preserves whole local groups until every one of their
            // exact observations has a validated reference, including repeated values.
            var enrichedRules = ruleEvidence.Where(entry => entry.Value.Length > 0 && entry.Value.GroupBy(item => item)
                .All(group => references.TryGetValue(group.Key, out int count) && count >= group.Count())).Select(entry => entry.Key).ToHashSet();
            merged = ruleIssues.Where(issue => !enrichedRules.Contains(issue)).ToList();
        }
        merged.AddRange(enhanced);
        return merged;
    }

    private static bool ConsumeEvidenceCount(Dictionary<SecurityEvent, int> counts, SecurityEvent item)
    {
        if (!counts.TryGetValue(item, out int count) || count == 0)
        {
            return false;
        }
        counts[item] = count - 1;
        return true;
    }

    private static int[] ResolveAuditEvidenceReferences(AuditIssue issue, int selectedCount)
    {
        var indices = new List<int>();
        foreach (string reference in new[] { issue.EventRef }.Concat(issue.RelatedEventRefs ?? []))
        {
            if (reference is null || reference.Length != 11 || !reference.StartsWith("item-", StringComparison.Ordinal)
                || !int.TryParse(reference.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                || number <= 0 || number > selectedCount)
            {
                return null;
            }
            int index = number - 1;
            if (!indices.Contains(index))
            {
                indices.Add(index);
            }
        }
        return indices.ToArray();
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
