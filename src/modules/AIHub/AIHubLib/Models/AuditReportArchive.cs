using System;
using System.Collections.Generic;
using System.Linq;

namespace Kit.AIHubLib.Models;

// Report-only evidence: one copy of an identical finding body, referenced by each scan.
// Raw event payloads are never added to this archive.
public sealed class AuditReportArchive
{
    public int Version { get; set; } = 2;
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
        SkippedChannels = scan.IncompleteLogs ? ["archive"] : null,
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
    public int[] Findings { get; set; } = Array.Empty<int>();
}
