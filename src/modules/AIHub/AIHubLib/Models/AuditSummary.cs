using System;
using System.Collections.Generic;
using System.Linq;

namespace Kit.AIHubLib.Models;

public sealed record AuditCategorySummary(string Category, int Findings, long Occurrences, int High, int Medium, int Low, AuditIssueEnhanced Priority);

public sealed record AuditSummary(int Findings, long Occurrences, int High, int Medium, int Low, IReadOnlyList<AuditCategorySummary> Categories)
{
    public DateTime? ScanStart { get; init; }
    public DateTime? ScanEnd { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public int? RangeDays { get; init; }
    public int HealthScore { get; init; }
    public int EventsScanned { get; init; }

    public static AuditSummary From(AuditResult snapshot)
    {
        DateTime? start = snapshot.ScanStart is { } from ? AsUtc(from) : null;
        DateTime? end = snapshot.ScanEnd is { } to ? AsUtc(to) : null;
        int? rangeDays = start.HasValue && end.HasValue
            ? AuditSchedule.SupportedRangeDays.Where(days => end.Value - start.Value == TimeSpan.FromDays(days)).Select(days => (int?)days).FirstOrDefault()
            : null;
        return From(snapshot.Findings) with
        {
            ScanStart = start, ScanEnd = end, CompletedUtc = AsUtc(snapshot.Timestamp), RangeDays = rangeDays,
            HealthScore = snapshot.HealthScore, EventsScanned = snapshot.EventsScanned,
        };
    }

    private static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static AuditSummary From(IEnumerable<AuditIssueEnhanced> findings)
    {
        var issues = findings.ToArray();
        var categories = issues.GroupBy(issue => issue.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AuditCategorySummary(group.Key, group.Count(), group.Sum(issue => (long)Math.Max(1, issue.Occurrences)),
                group.Count(issue => issue.IsHigh), group.Count(issue => issue.IsMedium), group.Count(issue => issue.IsLow),
                group.OrderByDescending(issue => issue.IsHigh).ThenByDescending(issue => issue.IsMedium)
                    .ThenByDescending(issue => issue.Occurrences).ThenBy(issue => issue.Key, StringComparer.Ordinal).First()))
            .OrderByDescending(group => group.High).ThenByDescending(group => group.Medium)
            .ThenByDescending(group => group.Occurrences).ThenBy(group => group.Category, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(issues.Length, categories.Sum(group => group.Occurrences), issues.Count(issue => issue.IsHigh),
            issues.Count(issue => issue.IsMedium), issues.Count(issue => issue.IsLow), categories);
    }
}
