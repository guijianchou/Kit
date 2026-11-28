using System;
using System.Collections.Generic;
using System.Linq;

namespace Kit.AIHubLib.Models;

// Report-only evidence: one copy of an identical finding body, referenced by each scan.
// Raw event payloads are never added to this archive.
public sealed class AuditReportArchive
{
    public int Version { get; set; } = 3;
    public DateOnly? PrunedThrough { get; set; }
    public List<AuditArchiveDay> Days { get; set; } = new();
}

public sealed class AuditArchiveDay
{
    public DateOnly Date { get; set; }
    public int Audits { get; set; }
    public DateTime? OmittedThrough { get; set; }
    public bool EvidenceIncomplete { get; set; }
    public List<AuditIssueEnhanced> Findings { get; set; } = new();
    public List<AuditArchiveScan> Scans { get; set; } = new();

    public IEnumerable<AuditResult> RestoreScans() => Scans.Select(scan => new AuditResult
    {
        Timestamp = scan.Timestamp,
        HealthScore = scan.HealthScore,
        AnalysisOutcome = scan.AnalysisOutcome,
        UsedModel = scan.UsedModel,
        ScanStart = scan.ScanStart,
        ScanEnd = scan.ScanEnd,
        EventsScanned = scan.EventsScanned,
        CollectionProfile = scan.CollectionProfile,
        SkippedChannels = scan.SkippedChannels?.ToList() ?? (scan.IncompleteLogs && scan.LimitedChannels is null && !scan.EvidenceIncomplete ? ["archive"] : null),
        LimitedChannels = scan.LimitedChannels?.ToList(),
        EvidenceIncomplete = scan.EvidenceIncomplete,
        AiReportSummary = scan.AiReportSummary,
        AiReportMeta = scan.AiReportMeta,
        AiAnalysisScope = scan.AiAnalysisScope,
        AiEvidenceFingerprint = scan.AiEvidenceFingerprint,
        AiAnalyzedEvents = scan.AiAnalyzedEvents,
        AiReusedEvents = scan.AiReusedEvents,
        Findings = scan.Findings.Select(index => Findings[index]).ToList(),
    });
}

public sealed class AuditArchiveScan
{
    public DateTime Timestamp { get; set; }
    public int HealthScore { get; set; }
    public string? AnalysisOutcome { get; set; }
    public string UsedModel { get; set; } = string.Empty;
    public bool IncompleteLogs { get; set; }
    public bool EvidenceIncomplete { get; set; }
    public DateTime? ScanStart { get; set; }
    public DateTime? ScanEnd { get; set; }
    public int EventsScanned { get; set; }
    public string? CollectionProfile { get; set; }
    public List<string>? SkippedChannels { get; set; }
    public List<string>? LimitedChannels { get; set; }
    public string? AiReportSummary { get; set; }
    public string? AiReportMeta { get; set; }
    public string? AiAnalysisScope { get; set; }
    public string? AiEvidenceFingerprint { get; set; }
    public int AiAnalyzedEvents { get; set; }
    public int AiReusedEvents { get; set; }
    public int[] Findings { get; set; } = Array.Empty<int>();
}
