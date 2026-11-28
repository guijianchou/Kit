using System;
using System.Collections.Generic;
using System.Linq;

namespace Kit.AIHubLib.Models;

/// <summary>Collection comparability, distinct from missing or pruned history.</summary>
public sealed record AuditPeriodCoverage
{
    public int UnknownWindowScans { get; init; }
    public bool MixedWindows { get; init; }
    public int UnknownCollectionScans { get; init; }
    public bool MixedCollectionProfiles { get; init; }
    public int IncompleteEvidenceScans { get; init; }
    public string ScoreComparisonReason { get; init; } = "unknown-window";
    public IReadOnlyList<string> SkippedChannels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LimitedChannels { get; init; } = Array.Empty<string>();

    internal static AuditPeriodCoverage From(IEnumerable<AuditResult> current, IEnumerable<AuditResult> previous)
    {
        var currentScans = current.ToArray();
        var previousScans = previous.ToArray();
        var scans = currentScans.Concat(previousScans).ToArray();
        int unknown = scans.Count(scan => !Window(scan).HasValue);
        bool mixed = scans.Select(Window).Where(window => window.HasValue).Distinct().Skip(1).Any();
        int unknownCollection = scans.Count(scan => string.IsNullOrWhiteSpace(scan.CollectionProfile));
        bool mixedCollection = scans.Where(scan => !string.IsNullOrWhiteSpace(scan.CollectionProfile))
            .Select(scan => scan.CollectionProfile).Distinct(StringComparer.Ordinal).Skip(1).Any();
        int incompleteEvidence = scans.Count(scan => scan.EvidenceIncomplete);
        bool incomplete = incompleteEvidence > 0 || scans.Any(scan => scan.SkippedChannels?.Count > 0 || scan.LimitedChannels?.Count > 0
            || scan.AnalysisOutcome is "pending" or "partial" or "failed" or "cancelled");
        // A local rule score and an AI-reviewed score do not represent the same assessment.
        bool analysisChanged = scans.Select(scan => !string.IsNullOrWhiteSpace(scan.UsedModel)
                ? scan.UsedModel + "\0" + scan.AiAnalysisScope
                : scan.AnalysisOutcome == "ok" ? "ai-unspecified" : "local")
            .Distinct(StringComparer.Ordinal).Skip(1).Any();
        return new()
        {
            UnknownWindowScans = unknown,
            MixedWindows = mixed,
            UnknownCollectionScans = unknownCollection,
            MixedCollectionProfiles = mixedCollection,
            IncompleteEvidenceScans = incompleteEvidence,
            ScoreComparisonReason = previousScans.Length == 0 ? "no-baseline"
                : unknown > 0 ? "unknown-window"
                : mixed ? "mixed-windows"
                : unknownCollection > 0 ? "unknown-collection"
                : mixedCollection ? "different-collection"
                : incomplete ? "incomplete-collection"
                : analysisChanged ? "different-analysis" : "comparable",
            SkippedChannels = currentScans.SelectMany(scan => scan.SkippedChannels ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            LimitedChannels = currentScans.SelectMany(scan => scan.LimitedChannels ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private static long? Window(AuditResult scan) => scan.ScanStart is { } start && scan.ScanEnd is { } end && Utc(end) > Utc(start)
        ? (Utc(end) - Utc(start)).Ticks : null;

    private static DateTime Utc(DateTime timestamp) => timestamp.Kind == DateTimeKind.Local
        ? timestamp.ToUniversalTime() : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
}
