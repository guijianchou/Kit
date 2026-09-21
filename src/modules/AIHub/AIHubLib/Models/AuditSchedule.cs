using System;
using System.Linq;

namespace Kit.AIHubLib.Models;

/// <summary>Why an audit is being started; it decides which window is scanned.</summary>
public enum ScanIntent
{
    /// <summary>Quick look over a short recent window.</summary>
    Fast,

    /// <summary>User asked for the full selected range; always rescans that range.</summary>
    ManualFull,

    /// <summary>User explicitly re-ran a range; always rescans that range.</summary>
    Reanalyze,

    /// <summary>Cadence-driven run; may continue from the previous scan.</summary>
    Scheduled,
}

/// <summary>
/// Scheduling and window arithmetic for audits. Extracted from the ViewModel so the
/// rules that decide "should this run" and "what period does it cover" are testable
/// without a UI thread or an event log.
/// </summary>
/// <remarks>
/// Mirrors the original LocalSecurityAudit <c>GetScanStart</c> semantics. A manual full
/// scan or an explicit reanalyze <em>always</em> covers the whole selected range; only a
/// cadence-driven run may resume from the previous scan. Collapsing the two is what made
/// repeated scans return nothing: the window shrank to the tail since the last run.
/// </remarks>
public static class AuditSchedule
{
    /// <summary>
    /// Overlap applied when a scheduled run continues from the previous one. Event log
    /// writes can lag the reported time slightly, so a short tail is re-read to avoid a
    /// gap at the seam.
    /// </summary>
    public static readonly TimeSpan IncrementalOverlap = TimeSpan.FromMinutes(2);

    /// <summary>Shortest supported scheduled interval, in hours.</summary>
    public const int MinimumIntervalHours = 1;

    /// <summary>Longest supported scheduled interval, in hours (one week).</summary>
    public const int MaximumIntervalHours = 168;

    /// <summary>Supported full-scan ranges, in days: 2 days, 1 week, 1 month.</summary>
    public static readonly int[] SupportedRangeDays = [2, 7, 30];

    /// <summary>Range used when a caller supplies an unsupported value.</summary>
    public static int DefaultRangeDays => SupportedRangeDays[0];

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
    /// Start of the window to scan, following the original GetScanStart rules:
    /// a scheduled run resumes from the previous scan when it is newer than the range
    /// start, while manual full scans and reanalyzes always cover the whole range.
    /// </summary>
    /// <param name="intent">Why the audit is starting.</param>
    /// <param name="lastScanUtc">End of the previous scan, or null when none is usable.</param>
    /// <param name="endUtc">End of the new window.</param>
    /// <param name="rangeDays">Selected range for a full scan, in days.</param>
    /// <param name="fastRangeHours">Window for a fast scan, in hours.</param>
    public static DateTime ComputeScanStart(
        ScanIntent intent,
        DateTime? lastScanUtc,
        DateTime endUtc,
        int rangeDays,
        double fastRangeHours = 1)
    {
        int days = SupportedRangeDays.Contains(rangeDays) ? rangeDays : DefaultRangeDays;
        DateTime rangeStart = endUtc.AddDays(-days);

        if (intent == ScanIntent.Fast)
        {
            // A quick scan covers the current day, matching the original app: it starts at
            // local midnight rather than a rolling window, so repeated quick scans see the
            // same period and their results stay comparable.
            DateTime localMidnight = endUtc.ToLocalTime().Date;
            DateTime todayStartUtc = DateTime.SpecifyKind(localMidnight, DateTimeKind.Local).ToUniversalTime();
            return todayStartUtc > endUtc ? endUtc.AddHours(-Math.Max(fastRangeHours, 0.1)) : todayStartUtc;
        }

        // Only a scheduled run may continue from the previous scan.
        if (intent == ScanIntent.Scheduled
            && lastScanUtc is DateTime last
            && last > rangeStart
            && last <= endUtc)
        {
            DateTime resume = last - IncrementalOverlap;
            return resume < rangeStart ? rangeStart : resume;
        }

        // Manual full scans and reanalyzes always cover the whole selected range.
        return rangeStart;
    }

    /// <summary>
    /// Window to scan, as a (from, to) pair. Kept for callers that want both ends.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtc) ComputeWindow(
        ScanIntent intent,
        DateTime? lastScanUtc,
        DateTime endUtc,
        int rangeDays,
        double fastRangeHours = 1)
        => (ComputeScanStart(intent, lastScanUtc, endUtc, rangeDays, fastRangeHours), endUtc);

    /// <summary>
    /// True when the computed window is shorter than the selected range, i.e. the run
    /// resumed from a previous scan instead of covering the full period.
    /// </summary>
    public static bool IsIncremental(ScanIntent intent, DateTime? lastScanUtc, DateTime endUtc, int rangeDays)
    {
        if (intent != ScanIntent.Scheduled)
        {
            return false;
        }

        DateTime from = ComputeScanStart(intent, lastScanUtc, endUtc, rangeDays);
        return from > endUtc.AddDays(-(SupportedRangeDays.Contains(rangeDays) ? rangeDays : DefaultRangeDays));
    }
}
