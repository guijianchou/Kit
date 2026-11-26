using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Kit.AIHubLib.Models;

public sealed record AuditReportIssue(AuditIssueEnhanced Latest, int ObservedDays, int PeakOccurrences, bool SeenPreviously);
public sealed record AuditDailySnapshot(DateOnly Date, int Audits, AuditResult Snapshot);

/// <summary>Calendar summaries of saved evidence. Repeated, overlapping scans are not added as new events.</summary>
public sealed record AuditPeriodReport(
    DateOnly StartDate, DateOnly EndDate, DateOnly PreviousStartDate, DateOnly PreviousEndDate,
    IReadOnlyList<AuditReportDay> Days, IReadOnlyList<AuditReportDay> PreviousDays,
    IReadOnlyList<AuditReportIssue> Issues, int NotObservedAgain, int IncompleteDays)
{
    public bool ArchiveUnavailable { get; init; }
    public bool HistoryIncomplete { get; init; }
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
        var previousKeys = previous.SelectMany(day => day.Scans).SelectMany(scan => scan.Findings).Select(IssueKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = current.SelectMany(day => day.Scans.SelectMany(scan => scan.Findings.Select(issue => (day.Date, Scan: scan, Issue: issue))))
            .GroupBy(item => IssueKey(item.Issue), StringComparer.OrdinalIgnoreCase).ToArray();
        // At equal severity, retain reviewed AI remedies instead of later generic rule text.
        var issues = groups.Select(group => new AuditReportIssue(
                group.OrderByDescending(item => Severity(item.Issue))
                    .ThenByDescending(item => item.Scan.AnalysisOutcome is "ok" or "partial" || !string.IsNullOrEmpty(item.Scan.UsedModel))
                    .ThenByDescending(item => Utc(item.Scan.Timestamp)).ThenByDescending(item => Severity(item.Issue)).First().Issue,
                group.Select(item => item.Date).Distinct().Count(), group.Max(item => item.Issue.Occurrences), previousKeys.Contains(group.Key)))
            .OrderByDescending(item => Severity(item.Latest)).ThenByDescending(item => item.ObservedDays).ToArray();
        return new(start, endDate, previousStart, previousEnd,
            current.Select(day => new AuditReportDay(day.Date, day.Count, day.Latest.HealthScore, day.Latest.HighCount, day.Latest.MediumCount, day.Latest.LowCount)).ToArray(),
            previous.Select(day => new AuditReportDay(day.Date, day.Count, day.Latest.HealthScore, day.Latest.HighCount, day.Latest.MediumCount, day.Latest.LowCount)).ToArray(),
            issues, previousKeys.Except(groups.Select(group => group.Key), StringComparer.OrdinalIgnoreCase).Count(),
            current.Count(day => day.Scans.Any(scan => scan.AnalysisOutcome is "pending" or "partial" or "failed" or "cancelled"
                || scan.SkippedChannels?.Count > 0 || scan.LimitedChannels?.Count > 0)));
    }

    public string Format(bool chinese)
    {
        var text = new StringBuilder().AppendLine(CultureInfo.CurrentCulture, $"{StartDate:yyyy-MM-dd} – {EndDate:yyyy-MM-dd}");
        int totalDays = EndDate.DayNumber - StartDate.DayNumber + 1;
        if (ArchiveUnavailable)
            text.AppendLine(chinese ? "历史摘要存档无法读取，本报告仅使用当前审计历史；已有存档未覆盖。" : "The daily archive could not be read; this report uses current audit history only. Existing exports were preserved.");
        if (HistoryIncomplete)
            text.AppendLine(chinese ? "部分历史证据已超出保留范围或来自旧版摘要，当前结果不完整；已有导出未覆盖。" : "Some historical evidence was trimmed or comes from older daily summaries. This report is incomplete; existing exports were preserved.");
        if (Days.Count == 0)
        {
            return text.AppendLine(chinese ? "此时段没有保存的审计记录。" : "No saved audits in this period.").ToString();
        }
        text.AppendLine(chinese
            ? $"已记录 {Days.Count}/{totalDays} 天，共 {Days.Sum(day => day.Audits)} 次审计。健康分取每日最后一次扫描，问题汇总当天所有观察；分数回升不代表此前问题已解决。"
            : $"Recorded {Days.Count}/{totalDays} days, {Days.Sum(day => day.Audits)} audits. Health scores use each day's last scan; issues include all observations that day. A higher score does not establish resolution of earlier issues.");
        double score = Days.Average(day => day.HealthScore);
        text.AppendLine(chinese ? $"平均健康分：{score:0.#}/100。" : $"Average health score: {score:0.#}/100.");
        text.AppendLine(PreviousDays.Count == 0
            ? chinese ? "对比时段没有记录，无法判断变化。" : "No recorded baseline; change cannot be determined."
            : chinese
                ? $"较 {PreviousStartDate:MM-dd} – {PreviousEndDate:MM-dd}（记录 {PreviousDays.Count} 天）：{score - PreviousDays.Average(day => day.HealthScore):+0.#;-0.#;0} 分。"
                : $"Change from {PreviousStartDate:MM-dd} – {PreviousEndDate:MM-dd} ({PreviousDays.Count} days recorded): {score - PreviousDays.Average(day => day.HealthScore):+0.#;-0.#;0} points.");
        text.AppendLine(chinese
            ? $"共 {Issues.Count} 类问题，{Issues.Count(issue => issue.SeenPreviously)} 类曾在对比时段出现。"
            : $"{Issues.Count} issue groups; {Issues.Count(issue => issue.SeenPreviously)} also appeared in the baseline.");
        if (IncompleteDays > 0)
            text.AppendLine(chinese ? $"{IncompleteDays} 天有日志缺失、数量上限或未完成的 AI 分析。" : $"{IncompleteDays} days have missing/truncated logs or incomplete AI analysis.");
        if (NotObservedAgain > 0)
            text.AppendLine(chinese ? $"此前 {NotObservedAgain} 类问题未在本期快照中出现，不能据此判定已解决。" : $"{NotObservedAgain} previous issue groups were not observed again; this does not establish resolution.");
        text.AppendLine(chinese ? "发生次数取单次快照的最高值，不累加重叠扫描。" : "Occurrences use the highest single-snapshot count; overlapping scans are not added.");
        foreach (var issue in Issues)
        {
            var finding = issue.Latest;
            text.AppendLine().AppendLine(CultureInfo.CurrentCulture, $"[{finding.Severity}] {(chinese ? finding.TitleZh : finding.Title)}");
            text.AppendLine(chinese ? $"记录 {issue.ObservedDays} 天 · 单次最多 {issue.PeakOccurrences} 次 · {finding.LogName}/{finding.EventId}"
                : $"Observed on {issue.ObservedDays} days · peak {issue.PeakOccurrences} per snapshot · {finding.LogName}/{finding.EventId}");
            text.AppendLine((chinese ? "事实：" : "Evidence: ") + (chinese ? finding.DescriptionZh : finding.Description));
            text.AppendLine((chinese ? "原因判断：" : "Cause assessment: ") + (chinese ? finding.RootCauseZh : finding.RootCause));
            text.AppendLine((chinese ? "下一步：" : "Next step: ") + (chinese ? finding.RecommendationZh : finding.Recommendation));
        }
        return text.ToString();
    }

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
