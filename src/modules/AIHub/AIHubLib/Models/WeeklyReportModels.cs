using System;
using System.Collections.Generic;

namespace Kit.AIHubLib.Models;

public enum WeeklyReportMode
{
    Fast,
    Full,
}

public sealed record WeeklyReportProgress(string Stage, string Message);

/// <summary>A retained seven-day audit; only the three stability channels retain raw evidence.</summary>
public sealed record WeeklyScanSnapshot(int Version, string Id, DateTime SavedUtc, AuditResult Audit, IReadOnlyList<SecurityEvent> Events)
{
    public bool RawEventsAvailable { get; init; } = true;
    public bool RawEventsTruncated { get; init; }
}

/// <summary>Report provenance is separate from presentation language and audit completion.</summary>
public sealed record WeeklyReportSource(WeeklyReportMode Mode, string CacheIdentity, string EvidenceFingerprint,
    string ScanId, DateTime? ScanStartUtc, DateTime? ScanEndUtc, bool RawEventsAvailable, bool RawEventsTruncated,
    string UsedModel, string UsedRoute, string AnalysisIdentity)
{
    public string TemplateVersion { get; init; } = "weekly-v1";
}
