using System.Collections.Generic;
using System.Linq;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

/// <summary>Risk floors derived only from observed Windows event levels.</summary>
public static class AuditRiskPolicy
{
    public static bool RequiresHighRisk(SecurityEvent evidence) => evidence.Level is 1 or 2;

    public static string ApplySeverityFloor(string severity, IEnumerable<SecurityEvent> evidence) =>
        evidence.Any(RequiresHighRisk) ? "High" : severity;

    public static int? MostSevereLevel(IEnumerable<SecurityEvent> evidence) =>
        evidence.Select(item => item.Level).Where(level => level is >= 1 and <= 5).Min();

    public static IOrderedEnumerable<AuditIssueEnhanced> OrderFindings(IEnumerable<AuditIssueEnhanced> findings) =>
        findings.OrderByDescending(issue => issue.IsHigh).ThenByDescending(issue => issue.IsMedium)
            .ThenByDescending(issue => issue.ObservedEventLevel == 1).ThenByDescending(issue => issue.Occurrences);
}
