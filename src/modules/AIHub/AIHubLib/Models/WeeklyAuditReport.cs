using System;
using System.Collections.Generic;
using System.Linq;

namespace Kit.AIHubLib.Models;

public sealed record AuditReportDay(DateOnly Date, int Audits, int HealthScore, int High, int Medium, int Low);

public sealed record WeeklyAuditReport(DateOnly EndDate, IReadOnlyList<AuditReportDay> Days, IReadOnlyList<AuditReportDay> PreviousDays)
{
    public static WeeklyAuditReport From(IReadOnlyList<AuditResult> history, DateOnly endDate, TimeZoneInfo zone)
    {
        static DateTime UtcTimestamp(AuditResult audit)
        {
            DateTime timestamp = audit.Timestamp;
            return timestamp.Kind == DateTimeKind.Local ? timestamp.ToUniversalTime() : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
        }

        int AgeInDays(DateOnly date) => endDate.DayNumber - date.DayNumber;

        var days = history.GroupBy(audit => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcTimestamp(audit), zone)))
            .Where(group => AgeInDays(group.Key) is >= 0 and < 14)
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                var latest = group.MaxBy(UtcTimestamp)!;
                return new AuditReportDay(group.Key, group.Count(), latest.HealthScore, latest.HighCount, latest.MediumCount, latest.LowCount);
            }).ToArray();

        return new(endDate, days.Where(day => AgeInDays(day.Date) < 7).ToArray(), days.Where(day => AgeInDays(day.Date) >= 7).ToArray());
    }
}
