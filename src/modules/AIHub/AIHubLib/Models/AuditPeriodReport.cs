using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kit.AIHubLib.Models;

public sealed record AuditReportIssue(AuditIssueEnhanced Latest, int ObservedDays, int PeakOccurrences, bool SeenPreviously)
{
    public int PreviousPeakSeverity { get; init; }
}
public sealed record AuditDailySnapshot(DateOnly Date, int Audits, AuditResult Snapshot);

/// <summary>Calendar summaries of saved evidence. Repeated, overlapping scans are not added as new events.</summary>
public sealed record AuditPeriodReport(
    DateOnly StartDate, DateOnly EndDate, DateOnly PreviousStartDate, DateOnly PreviousEndDate,
    IReadOnlyList<AuditReportDay> Days, IReadOnlyList<AuditReportDay> PreviousDays,
    IReadOnlyList<AuditReportIssue> Issues, int NotObservedAgain, int IncompleteDays)
{
    public bool ArchiveUnavailable { get; init; }
    public bool HistoryIncomplete { get; init; }
    public AuditPeriodCoverage Coverage { get; init; } = new();
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
            { PreviousPeakSeverity = previousIssues.GetValueOrDefault(group.Key) })
            .OrderByDescending(item => Severity(item.Latest)).ThenByDescending(item => item.ObservedDays).ToArray();
        return new(start, endDate, previousStart, previousEnd,
            current.Select(day => new AuditReportDay(day.Date, day.Count, day.Latest.HealthScore, day.Latest.HighCount, day.Latest.MediumCount, day.Latest.LowCount)).ToArray(),
            previous.Select(day => new AuditReportDay(day.Date, day.Count, day.Latest.HealthScore, day.Latest.HighCount, day.Latest.MediumCount, day.Latest.LowCount)).ToArray(),
            issues, previousIssues.Keys.Except(groups.Select(group => group.Key), StringComparer.OrdinalIgnoreCase).Count(),
            current.Count(day => day.Scans.Any(scan => scan.AnalysisOutcome is "pending" or "partial" or "failed" or "cancelled"
                || scan.EvidenceIncomplete || scan.SkippedChannels?.Count > 0 || scan.LimitedChannels?.Count > 0)))
        {
            Coverage = AuditPeriodCoverage.From(current.SelectMany(day => day.Scans), previous.SelectMany(day => day.Scans)),
        };
    }

    public string FormatOverview(bool chinese) => AuditReportFormatter.Overview(this, chinese);
    public string FormatDetails(bool chinese) => AuditReportFormatter.Details(this, chinese);
    public string Format(bool chinese) => FormatOverview(chinese) + Environment.NewLine + FormatDetails(chinese);

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static int Severity(AuditIssue issue) => issue.Severity == "High" ? 3 : issue.Severity == "Medium" ? 2 : 1;
    private static string IssueKey(AuditIssue issue)
    {
        // Local rule sequence numbers change when earlier groups enter or leave a scan.
        string key = issue.Key?.StartsWith("issue-", StringComparison.Ordinal) == true
            && int.TryParse(issue.Key.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out _) ? string.Empty : issue.Key ?? string.Empty;
        return string.Join("\0", issue.Category, key, issue.LogName, issue.Source, issue.EventId, issue.Affected);
    }
}
