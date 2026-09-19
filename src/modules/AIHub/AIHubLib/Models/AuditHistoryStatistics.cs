using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kit.AIHubLib.Models;

/// <summary>
/// Aggregated view over the retained audit history. Used by the dashboard activity
/// counters, which must reflect real stored audits rather than fixed placeholders.
/// </summary>
public sealed class AuditHistoryStatistics
{
    public int AuditCount { get; init; }

    public int ActiveDayCount { get; init; }

    public int TotalFindings { get; init; }

    public int LatestFindingCount { get; init; }

    public double AverageHealthScore { get; init; }

    public DateTime? LatestAuditUtc { get; init; }

    /// <summary>Averages over nothing are meaningless, so callers must check this.</summary>
    public bool HasHistory => AuditCount > 0;

    public static AuditHistoryStatistics From(IReadOnlyList<AuditResult>? history)
    {
        if (history is null || history.Count == 0)
        {
            return new AuditHistoryStatistics
            {
                AuditCount = 0,
                ActiveDayCount = 0,
                TotalFindings = 0,
                LatestFindingCount = 0,
                AverageHealthScore = 0,
                LatestAuditUtc = null,
            };
        }

        var ordered = history.OrderByDescending(entry => entry.Timestamp).ToList();

        return new AuditHistoryStatistics
        {
            AuditCount = history.Count,
            ActiveDayCount = history
                .Select(entry => entry.Timestamp.ToLocalTime().Date)
                .Distinct()
                .Count(),
            TotalFindings = history.Sum(entry => entry.Findings?.Count ?? 0),
            LatestFindingCount = ordered[0].Findings?.Count ?? 0,
            AverageHealthScore = Math.Round(history.Average(entry => entry.HealthScore), 1, MidpointRounding.AwayFromZero),
            LatestAuditUtc = ordered[0].Timestamp,
        };
    }

    public string AuditCountText => AuditCount.ToString(CultureInfo.InvariantCulture);

    public string ActiveDayCountText => ActiveDayCount.ToString(CultureInfo.InvariantCulture);

    public string AverageHealthScoreText => AuditCount == 0
        ? "—"
        : AverageHealthScore.ToString("0.#", CultureInfo.InvariantCulture);
}
