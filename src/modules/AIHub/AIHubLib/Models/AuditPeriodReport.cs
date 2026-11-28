using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kit.AIHubLib.Models;

public sealed record AuditReportIssue(AuditIssueEnhanced Latest, int ObservedDays, int PeakOccurrences, bool SeenPreviously)
{
    public int PreviousPeakSeverity { get; init; }
    public DateTime? LastObservedAt { get; init; }
}
public sealed record AuditDailySnapshot(DateOnly Date, int Audits, AuditResult Snapshot);
public sealed record AuditReportSection(string Title, string Text);
public sealed record AuditReportTrendDay(DateOnly Date, int Scans, int? Stability, int? High, int? Medium, int? Low, bool EvidenceLimited, bool AiIncomplete, bool MetadataUnknown);

/// <summary>Calendar summaries of saved evidence. Repeated, overlapping scans are not added as new events.</summary>
public sealed record AuditPeriodReport(
    DateOnly StartDate, DateOnly EndDate, DateOnly PreviousStartDate, DateOnly PreviousEndDate,
    IReadOnlyList<AuditReportDay> Days, IReadOnlyList<AuditReportDay> PreviousDays,
    IReadOnlyList<AuditReportIssue> Issues, int NotObservedAgain, int IncompleteDays)
{
    public bool ArchiveUnavailable { get; init; }
    public bool HistoryIncomplete { get; init; }
    public bool CurrentHistoryIncomplete { get; init; }
    public bool CurrentRetainedHistoryIncomplete { get; init; }
    public AuditPeriodCoverage Coverage { get; init; } = new();
    public AuditPeriodCoverage CurrentCoverage { get; init; } = new();
    public IReadOnlyList<AuditReportTrendDay> TrendDays { get; init; } = Array.Empty<AuditReportTrendDay>();
    public DateTime? LatestSavedAt { get; init; }
    public int CompletedAiAudits { get; init; }
    public int IncompleteAiAudits { get; init; }
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
    public bool CanCompareScores => Coverage.ScoreComparisonReason == "comparable" && !HistoryIncomplete && !ArchiveUnavailable;
    public static AuditPeriodReport From(IReadOnlyList<AuditResult> history, DateOnly endDate, TimeZoneInfo zone, bool monthly, IReadOnlyDictionary<DateOnly, int>? auditCounts = null)
    {
        var start = monthly ? new DateOnly(endDate.Year, endDate.Month, 1) : endDate.AddDays(-6);
        var previousStart = monthly ? start.AddMonths(-1) : start.AddDays(-7);
        var previousEnd = monthly
            ? previousStart.AddDays(Math.Min(endDate.Day, DateTime.DaysInMonth(previousStart.Year, previousStart.Month)) - 1)
            : start.AddDays(-1);
        var daily = history.GroupBy(audit => Utc(audit.Timestamp)).Select(group => group.Last())
            .GroupBy(audit => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Utc(audit.Timestamp), zone)))
            .Where(group => group.Key >= previousStart && group.Key <= endDate)
            .Select(group => (Date: group.Key, Count: Math.Max(group.Count(), auditCounts?.GetValueOrDefault(group.Key) ?? 0),
                Latest: group.MaxBy(audit => Utc(audit.Timestamp))!, Scans: group.ToArray()))
            .OrderByDescending(day => day.Date).ToArray();
        var current = daily.Where(day => day.Date >= start).ToArray();
        var previous = daily.Where(day => day.Date >= previousStart && day.Date <= previousEnd).ToArray();
        var previousIssues = previous.SelectMany(day => day.Scans).SelectMany(scan => scan.Findings)
            .GroupBy(IssueKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(Severity), StringComparer.OrdinalIgnoreCase);
        var groups = current.SelectMany(day => day.Scans.SelectMany(scan => scan.Findings.Select(issue => (day.Date, Scan: scan, Issue: issue))))
            .GroupBy(item => IssueKey(item.Issue), StringComparer.OrdinalIgnoreCase).ToArray();
        // At equal severity, retain reviewed AI remedies instead of later generic rule text.
        var issues = groups.Select(group => new AuditReportIssue(
                group.OrderByDescending(item => Severity(item.Issue))
                    .ThenByDescending(item => item.Scan.AnalysisOutcome is "ok" or "partial" || !string.IsNullOrEmpty(item.Scan.UsedModel))
                    .ThenByDescending(item => Utc(item.Scan.Timestamp)).ThenByDescending(item => Severity(item.Issue)).First().Issue,
                group.Select(item => item.Date).Distinct().Count(), group.Max(item => item.Issue.Occurrences), previousIssues.ContainsKey(group.Key))
            { PreviousPeakSeverity = previousIssues.GetValueOrDefault(group.Key), LastObservedAt = group.Max(item => Utc(item.Scan.Timestamp)) })
            .OrderByDescending(item => Severity(item.Latest)).ThenByDescending(item => item.ObservedDays).ToArray();
        return new(start, endDate, previousStart, previousEnd,
            current.Select(day => new AuditReportDay(day.Date, day.Count, day.Latest.HealthScore, day.Latest.HighCount, day.Latest.MediumCount, day.Latest.LowCount)).ToArray(),
            previous.Select(day => new AuditReportDay(day.Date, day.Count, day.Latest.HealthScore, day.Latest.HighCount, day.Latest.MediumCount, day.Latest.LowCount)).ToArray(),
            issues, previousIssues.Keys.Except(groups.Select(group => group.Key), StringComparer.OrdinalIgnoreCase).Count(),
            current.Count(day => day.Scans.Any(scan => scan.AnalysisOutcome is "pending" or "partial" or "failed" or "cancelled"
                || scan.EvidenceIncomplete || scan.SkippedChannels?.Count > 0 || scan.LimitedChannels?.Count > 0)))
        {
            Coverage = AuditPeriodCoverage.From(current.SelectMany(day => day.Scans), previous.SelectMany(day => day.Scans)),
            CurrentCoverage = AuditPeriodCoverage.From(current.SelectMany(day => day.Scans), []),
            TrendDays = Enumerable.Range(0, endDate.DayNumber - start.DayNumber + 1).Select(offset =>
            {
                var date = start.AddDays(offset);
                var day = current.FirstOrDefault(item => item.Date == date);
                if (day.Scans is null)
                {
                    return new AuditReportTrendDay(date, 0, null, null, null, null, false, false, false);
                }
                // Count issue groups across every saved scan on this date. A later empty
                // incremental scan cannot erase an earlier observation; windows may overlap.
                var findings = day.Scans.SelectMany(scan => scan.Findings).GroupBy(IssueKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.MaxBy(Severity)!).ToArray();
                return new AuditReportTrendDay(date, day.Count, findings.Count(IsStabilityFinding),
                    findings.Count(issue => issue.IsHigh), findings.Count(issue => issue.IsMedium), findings.Count(issue => issue.IsLow),
                    day.Scans.Any(scan => scan.EvidenceIncomplete || scan.SkippedChannels?.Count > 0 || scan.LimitedChannels?.Count > 0),
                    day.Scans.Any(scan => scan.AnalysisOutcome is "pending" or "partial" or "failed" or "cancelled"),
                    day.Scans.Any(scan => string.IsNullOrWhiteSpace(scan.CollectionProfile) || scan.ScanStart is null || scan.ScanEnd is null || scan.ScanEnd <= scan.ScanStart));
            }).ToArray(),
            LatestSavedAt = current.SelectMany(day => day.Scans).Select(scan => (DateTime?)Utc(scan.Timestamp)).Max(),
            CompletedAiAudits = current.SelectMany(day => day.Scans).Count(scan => scan.AnalysisOutcome == "ok"),
            IncompleteAiAudits = current.SelectMany(day => day.Scans).Count(scan => scan.AnalysisOutcome is "pending" or "partial" or "failed" or "cancelled"),
            Models = current.SelectMany(day => day.Scans).Where(scan => scan.AnalysisOutcome == "ok" && !string.IsNullOrWhiteSpace(scan.UsedModel))
                .Select(scan => scan.UsedModel).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        };
    }

    public string FormatOverview(bool chinese) => AuditReportFormatter.Overview(this, chinese);
    public IReadOnlyList<AuditReportSection> OverviewSections(bool chinese) => AuditReportFormatter.Sections(this, chinese);
    public AuditReportHighlights Highlights(bool chinese) => AuditReportHighlights.From(this, chinese);
    public string FormatDetails(bool chinese) => AuditReportFormatter.Details(this, chinese);
    public string Format(bool chinese) => FormatOverview(chinese) + Environment.NewLine + FormatDetails(chinese);

    public static bool IsStabilityFinding(AuditIssue issue) => string.Equals(issue.Category, "Stability", StringComparison.OrdinalIgnoreCase)
        || string.Equals(issue.Category, "Reliability", StringComparison.OrdinalIgnoreCase);

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static int Severity(AuditIssue issue) => string.Equals(issue.Severity, "High", StringComparison.OrdinalIgnoreCase) ? 3
        : string.Equals(issue.Severity, "Medium", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    private static string IssueKey(AuditIssue issue)
    {
        // Local rule sequence numbers change when earlier groups enter or leave a scan.
        string key = issue.Key?.StartsWith("issue-", StringComparison.Ordinal) == true
            && int.TryParse(issue.Key.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out _) ? string.Empty : issue.Key ?? string.Empty;
        return string.Join("\0", issue.Category, key, issue.LogName, issue.Source, issue.EventId, issue.Affected);
    }
}
