using System;

namespace Kit.AIHubLib.Models;

/// <summary>
/// Scheduling and window arithmetic for audits. Extracted from the ViewModel so the
/// rules that decide "should this run" and "what period does it cover" are testable
/// without a UI thread or an event log.
/// </summary>
/// <remarks>
/// Mirrors the original LocalSecurityAudit scheduler intent: a configurable interval
/// (0 disables scheduling) plus incremental windows that continue from the previous
/// scan instead of always rescanning the whole selected range.
/// </remarks>
public static class AuditSchedule
{
    /// <summary>
    /// Overlap applied when continuing from a previous scan. Event log writes can lag
    /// the reported time slightly, so re-reading a short tail avoids gaps at the seam.
    /// </summary>
    public static readonly TimeSpan IncrementalOverlap = TimeSpan.FromMinutes(2);

    /// <summary>Shortest supported scheduled interval, in hours.</summary>
    public const int MinimumIntervalHours = 1;

    /// <summary>Longest supported scheduled interval, in hours (one week).</summary>
    public const int MaximumIntervalHours = 168;

    /// <summary>True when an interval value enables scheduling at all.</summary>
    public static bool IsSchedulingEnabled(int intervalHours) => intervalHours >= MinimumIntervalHours;

    /// <summary>
    /// Clamps a configured interval into the supported range. Values below the minimum
    /// (including zero, which means "off") are returned unchanged.
    /// </summary>
    public static int NormalizeIntervalHours(int intervalHours)
    {
        if (!IsSchedulingEnabled(intervalHours))
        {
            return intervalHours <= 0 ? 0 : MinimumIntervalHours;
        }

        return Math.Min(intervalHours, MaximumIntervalHours);
    }

    /// <summary>
    /// True when a scheduled audit is due. A missing previous scan is always due, so a
    /// freshly enabled schedule does not wait a full interval before its first run.
    /// </summary>
    public static bool IsDue(DateTime? lastScanUtc, DateTime nowUtc, int intervalHours)
    {
        if (!IsSchedulingEnabled(intervalHours))
        {
            return false;
        }

        if (lastScanUtc is null)
        {
            return true;
        }

        DateTime due = lastScanUtc.Value.AddHours(NormalizeIntervalHours(intervalHours));
        return nowUtc >= due;
    }

    /// <summary>
    /// When the next scheduled audit becomes due, or null when scheduling is off.
    /// </summary>
    public static DateTime? NextRunUtc(DateTime? lastScanUtc, DateTime nowUtc, int intervalHours)
    {
        if (!IsSchedulingEnabled(intervalHours))
        {
            return null;
        }

        DateTime anchor = lastScanUtc ?? nowUtc;
        DateTime due = anchor.AddHours(NormalizeIntervalHours(intervalHours));

        // An overdue schedule should run now rather than in the past.
        return due < nowUtc ? nowUtc : due;
    }

    /// <summary>
    /// Window to scan. Without a usable previous scan, or when the previous scan is
    /// older than the selected range, the full range is used; otherwise the window
    /// continues from the previous scan with a small overlap.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtc) ComputeWindow(
        DateTime? lastScanUtc,
        DateTime nowUtc,
        TimeSpan fullRange)
    {
        DateTime fullStart = nowUtc - fullRange;

        if (lastScanUtc is null || lastScanUtc.Value <= fullStart || lastScanUtc.Value > nowUtc)
        {
            return (fullStart, nowUtc);
        }

        DateTime incrementalStart = lastScanUtc.Value - IncrementalOverlap;
        return incrementalStart < fullStart ? (fullStart, nowUtc) : (incrementalStart, nowUtc);
    }

    /// <summary>
    /// True when the computed window is shorter than the configured range, i.e. the scan
    /// is continuing from a previous run rather than covering the full period.
    /// </summary>
    public static bool IsIncremental(DateTime? lastScanUtc, DateTime nowUtc, TimeSpan fullRange)
    {
        (DateTime from, _) = ComputeWindow(lastScanUtc, nowUtc, fullRange);
        return from > nowUtc - fullRange;
    }
}
