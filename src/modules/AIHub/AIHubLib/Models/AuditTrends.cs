using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kit.AIHubLib.Models;

/// <summary>
/// One day of audit activity, used by the trends view.
/// </summary>
public sealed class AuditTrendDay
{
    public DateTime Date { get; init; }

    public int AuditCount { get; init; }

    public int High { get; init; }

    public int Medium { get; init; }

    public int Low { get; init; }

    public int Total => High + Medium + Low;

    /// <summary>Average health score for the day, or null when no audit ran.</summary>
    public double? HealthScore { get; init; }

    public bool HasData => AuditCount > 0;

    /// <summary>Compact label for the heatmap, e.g. "01-15".</summary>
    public string Label => Date.ToString("MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Darker buckets mean more findings; 0 means no activity.</summary>
    public int IntensityLevel => Total switch
    {
        0 => 0,
        <= 2 => 1,
        <= 5 => 2,
        <= 12 => 3,
        _ => 4,
    };
}

/// <summary>Finding count for one category across the requested window.</summary>
public sealed class AuditCategoryTotal
{
    public required string Name { get; init; }

    public required int Count { get; init; }
}

/// <summary>
/// Aggregation for the trends surface, derived from the stored audit history.
/// </summary>
/// <remarks>
/// Mirrors the original LocalSecurityAudit trends intent: a day-by-day activity series,
/// category totals and a windowed view, all computed from persisted audits so the view
/// cannot show numbers the history does not support.
/// </remarks>
public static class AuditTrends
{
    /// <summary>Supported window sizes, in days.</summary>
    public static readonly IReadOnlyList<int> SupportedWindows = new[] { 1, 7, 30 };

    /// <summary>Clamps a requested window to the supported set (defaulting to 7 days).</summary>
    public static int NormalizeWindowDays(int windowDays)
    {
        return SupportedWindows.Contains(windowDays) ? windowDays : 7;
    }

    /// <summary>
    /// Builds a dense per-day series for the window ending today, oldest first. Days with
    /// no audit are present with <see cref="AuditTrendDay.HasData"/> false, so the chart
    /// keeps a stable scale instead of collapsing empty days.
    /// </summary>
    public static IReadOnlyList<AuditTrendDay> BuildDailySeries(
        IReadOnlyList<AuditResult>? history,
        DateTime nowLocal,
        int windowDays)
    {
        int days = NormalizeWindowDays(windowDays);
        DateTime end = nowLocal.Date;
        DateTime start = end.AddDays(-(days - 1));

        var byDay = (history ?? Array.Empty<AuditResult>())
            .Where(entry => entry is not null)
            .GroupBy(entry => entry.Timestamp.ToLocalTime().Date)
            .ToDictionary(group => group.Key, group => group.ToList());

        var series = new List<AuditTrendDay>(days);
        for (DateTime day = start; day <= end; day = day.AddDays(1))
        {
            if (!byDay.TryGetValue(day, out List<AuditResult>? audits))
            {
                series.Add(new AuditTrendDay { Date = day, AuditCount = 0 });
                continue;
            }

            series.Add(new AuditTrendDay
            {
                Date = day,
                AuditCount = audits.Count,
                High = audits.Sum(a => a.HighCount),
                Medium = audits.Sum(a => a.MediumCount),
                Low = audits.Sum(a => a.LowCount),
                HealthScore = Math.Round(audits.Average(a => a.HealthScore), 1, MidpointRounding.AwayFromZero),
            });
        }

        return series;
    }

    /// <summary>
    /// Category totals across the window, most frequent first. Findings without a
    /// category are grouped under "Other" so nothing is silently dropped.
    /// </summary>
    public static IReadOnlyList<AuditCategoryTotal> BuildCategoryTotals(
        IReadOnlyList<AuditResult>? history,
        DateTime nowLocal,
        int windowDays)
    {
        int days = NormalizeWindowDays(windowDays);
        DateTime start = nowLocal.Date.AddDays(-(days - 1));

        return (history ?? Array.Empty<AuditResult>())
            .Where(entry => entry is not null && entry.Timestamp.ToLocalTime().Date >= start)
            .SelectMany(entry => entry.Findings ?? new List<AuditIssueEnhanced>())
            .Where(finding => finding is not null)
            .GroupBy(finding => string.IsNullOrWhiteSpace(finding.Category) ? "Other" : finding.Category, StringComparer.Ordinal)
            .Select(group => new AuditCategoryTotal { Name = group.Key, Count = group.Count() })
            .OrderByDescending(total => total.Count)
            .ThenBy(total => total.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Findings recorded inside the window, newest first.</summary>
    public static IReadOnlyList<(AuditIssueEnhanced Issue, DateTime ScanTimestamp)> BuildWindowedFindings(
        IReadOnlyList<AuditResult>? history,
        DateTime nowLocal,
        int windowDays)
    {
        int days = NormalizeWindowDays(windowDays);
        DateTime start = nowLocal.Date.AddDays(-(days - 1));

        return (history ?? Array.Empty<AuditResult>())
            .Where(entry => entry is not null && entry.Timestamp.ToLocalTime().Date >= start)
            .SelectMany(entry => (entry.Findings ?? new List<AuditIssueEnhanced>())
                .Where(finding => finding is not null)
                .Select(finding => (Issue: finding, ScanTimestamp: entry.Timestamp)))
            .OrderByDescending(pair => pair.ScanTimestamp)
            .ToList();
    }

    /// <summary>Number of days in the window that recorded at least one audit.</summary>
    public static int CountActiveDays(IReadOnlyList<AuditTrendDay> series)
    {
        return series.Count(day => day.HasData);
    }
}
