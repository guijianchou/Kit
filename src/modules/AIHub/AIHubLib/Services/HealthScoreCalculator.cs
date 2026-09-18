using System;
using System.Collections.Generic;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

public static class HealthScoreCalculator
{
    public const int HighPenalty = 15;
    public const int HighMaxPenalty = 60;

    public const int MediumPenalty = 6;
    public const int MediumMaxPenalty = 25;

    public const double LowPenalty = 1.5;
    public const int LowMaxPenalty = 15;

    public static int Calculate(IReadOnlyList<AuditIssueEnhanced>? issues)
    {
        if (issues == null || issues.Count == 0)
        {
            return 100;
        }

        int highCount = 0;
        int mediumCount = 0;
        int lowCount = 0;

        foreach (var issue in issues)
        {
            if (issue.IsHigh)
            {
                highCount++;
            }
            else if (issue.IsMedium)
            {
                mediumCount++;
            }
            else if (issue.IsLow)
            {
                lowCount++;
            }
        }

        double totalDeduction =
            Math.Min(highCount * HighPenalty, HighMaxPenalty) +
            Math.Min(mediumCount * MediumPenalty, MediumMaxPenalty) +
            Math.Min(Math.Ceiling(lowCount * LowPenalty), LowMaxPenalty);

        return Math.Clamp((int)Math.Round(100.0 - totalDeduction), 0, 100);
    }
}
