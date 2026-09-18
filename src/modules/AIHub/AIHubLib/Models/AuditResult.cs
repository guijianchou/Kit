using System;
using System.Collections.Generic;

namespace Kit.AIHubLib.Models;

public sealed class AuditResult
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int HealthScore { get; set; } = 100;
    public DateTime? ScanStart { get; set; }
    public DateTime? ScanEnd { get; set; }
    public int EventsScanned { get; set; }
    public string UsedModel { get; set; } = string.Empty;
    public List<AuditIssueEnhanced> Findings { get; set; } = new();

    public int HighCount => Findings.FindAll(f => f.IsHigh).Count;
    public int MediumCount => Findings.FindAll(f => f.IsMedium).Count;
    public int LowCount => Findings.FindAll(f => f.IsLow).Count;
    public int TotalFindings => Findings.Count;
}
