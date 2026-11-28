using System;
using System.Collections.Generic;
using System.Linq;

namespace Kit.AIHubLib.Models;

public sealed record AuditReportMeasure(string Label, int Count, string Detail = "");
public sealed record AuditReportPriority(AuditReportIssue Issue, bool NeedsAttention, string Title, string Context, string Evidence, string Check, string Action, string Verification);

/// <summary>Bounded presentation of saved observations, without inferred outage counts or repair status.</summary>
public sealed record AuditReportHighlights(
    string Assessment, string AssessmentDetail, string CoverageSummary, string CoverageDetail,
    IReadOnlyList<AuditReportPriority> Priorities, IReadOnlyList<AuditReportMeasure> Components,
    IReadOnlyList<AuditReportMeasure> Categories, IReadOnlyList<AuditReportMeasure> Severities,
    string Changes, string NextReview)
{
    internal static AuditReportHighlights From(AuditPeriodReport report, bool chinese)
    {
        string L(string en, string zh) => chinese ? zh : en;
        int totalDays = report.EndDate.DayNumber - report.StartDate.DayNumber + 1;
        var stability = report.Issues.Where(issue => AuditPeriodReport.IsStabilityFinding(issue.Latest)).ToArray();
        int high = stability.Count(issue => issue.Latest.IsHigh);
        int recurring = stability.Count(issue => issue.ObservedDays > 1 || issue.SeenPreviously);
        bool limited = report.Days.Count < totalDays || report.ArchiveUnavailable || report.CurrentHistoryIncomplete || report.IncompleteDays > 0
            || report.CurrentCoverage.UnknownWindowScans > 0 || report.CurrentCoverage.UnknownCollectionScans > 0;
        string assessment = report.Days.Count == 0 ? L("Stability cannot be assessed", "暂无证据判断系统稳定性")
            : high > 0 ? L("Priority stability signals need verification", "发现需优先核实的稳定性异常")
            : stability.Length > 0 ? L("Stability signals need follow-up", "发现需持续跟进的稳定性异常")
            : limited ? L("Evidence is insufficient to assess stability", "观察证据不足，无法确认系统稳定性")
            : L("No explicit stability signals in saved observations", "已保存观察未见明确的稳定性异常");
        string assessmentDetail = report.Days.Count == 0 ? L("No saved audits in this period.", "此时段没有保存的审计记录。")
            : stability.Length > 0 ? L($"{stability.Length} stability-related issue groups · {high} high priority · {recurring} observed repeatedly. Verify the recorded symptoms and affected components first.",
                $"稳定性相关问题 {stability.Length} 组 · 高优先级 {high} 组 · 重复观察 {recurring} 组。优先核实记录中的异常表现与受影响组件。")
            : L("Other issue categories remain in the priority list. A health score alone cannot establish system stability.",
                "其他分类问题仍纳入重点事项。健康分本身不能证明系统稳定。");
        string coverageSummary = L($"Recorded {report.Days.Count}/{totalDays} days · {report.IncompleteDays} days with limited evidence or unfinished analysis",
            $"有扫描记录 {report.Days.Count}/{totalDays} 天 · {report.IncompleteDays} 天存在证据限制或分析未完成");
        if (report.ArchiveUnavailable || report.CurrentHistoryIncomplete)
        {
            coverageSummary += L(" · History incomplete", " · 历史证据不完整");
        }
        if (report.CurrentCoverage.UnknownWindowScans > 0 || report.CurrentCoverage.UnknownCollectionScans > 0)
        {
            coverageSummary += L(" · Collection coverage unknown", " · 采集范围不明");
        }
        var coverage = new List<string>
        {
            L("Recorded dates are scan dates, not continuous event coverage. Missing dates are not counted as healthy days.",
                "记录日期是扫描保存日期，不等于连续事件覆盖；无记录日期不按健康日计算。"),
            L($"Completed AI audits: {report.CompletedAiAudits}; incomplete AI audits: {report.IncompleteAiAudits}.",
                $"已完成 AI 审计 {report.CompletedAiAudits} 次；未完成 AI 审计 {report.IncompleteAiAudits} 次。"),
        };
        if (report.ArchiveUnavailable)
        {
            coverage.Add(L("The daily archive could not be read; this report uses current audit history only. Existing exports were preserved.",
                "历史摘要存档无法读取，本报告仅使用当前审计历史；已有存档未覆盖。"));
        }
        if (report.CurrentHistoryIncomplete)
        {
            coverage.Add(L("Retained history cannot reconstruct all observations. Stability cannot be assured from this report.",
                "保留的历史不能重建全部观察，不能由此保证系统稳定。"));
        }
        if (report.CurrentCoverage.SkippedChannels.Count > 0)
        {
            coverage.Add(L("Unreadable channels: ", "未读取渠道：") + string.Join(", ", report.CurrentCoverage.SkippedChannels.Take(8)));
        }
        if (report.CurrentCoverage.LimitedChannels.Count > 0)
        {
            coverage.Add(L("Truncated channels: ", "达到数量上限的渠道：") + string.Join(", ", report.CurrentCoverage.LimitedChannels.Take(8)));
        }
        var orderedPriorities = report.Issues.OrderByDescending(issue => Severity(issue.Latest))
            .ThenByDescending(issue => AuditPeriodReport.IsStabilityFinding(issue.Latest))
            .ThenByDescending(issue => issue.ObservedDays).ThenByDescending(issue => issue.SeenPreviously).ToArray();
        var selectedPriorities = orderedPriorities.Take(3).ToList();
        if (stability.Length > 0 && !selectedPriorities.Any(issue => AuditPeriodReport.IsStabilityFinding(issue.Latest)))
        {
            // Keep the two highest-priority items and reserve the final slot for
            // the leading stability signal, without promoting it above those items.
            selectedPriorities[^1] = orderedPriorities.First(issue => AuditPeriodReport.IsStabilityFinding(issue.Latest));
        }
        var priorities = selectedPriorities.Select(issue => Priority(issue, chinese)).ToArray();
        var components = stability.GroupBy(issue => Component(issue.Latest, chinese), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count(issue => issue.Latest.IsHigh)).ThenByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase).Take(5)
            .Select(group => new AuditReportMeasure(Preview(group.Key, 120), group.Count(),
                L($"{group.Count(issue => issue.Latest.IsHigh)} high · event IDs: ", $"高优先级 {group.Count(issue => issue.Latest.IsHigh)} 组 · 事件 ID：")
                + string.Join(", ", group.Select(issue => issue.Latest.EventId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(4))))
            .ToArray();
        var categoryGroups = report.Issues.GroupBy(issue => issue.Latest.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var categories = categoryGroups.Take(5).Select(group => new AuditReportMeasure(Category(group.Key, chinese), group.Count())).ToList();
        if (categoryGroups.Length > 5)
        {
            categories.Add(new(L("Other categories", "其余分类"), categoryGroups.Skip(5).Sum(group => group.Count())));
        }
        AuditReportMeasure[] severities =
        [
            new(L("High", "高优先级"), report.Issues.Count(issue => issue.Latest.IsHigh)),
            new(L("Medium", "中优先级"), report.Issues.Count(issue => issue.Latest.IsMedium)),
            new(L("Low", "低优先级"), report.Issues.Count(issue => issue.Latest.IsLow)),
        ];
        int again = report.Issues.Count(issue => issue.SeenPreviously);
        int raised = report.Issues.Count(issue => issue.SeenPreviously && Severity(issue.Latest) > issue.PreviousPeakSeverity);
        string changes = report.PreviousDays.Count == 0 ? L("No recorded baseline; change cannot be determined.", "对比时段没有记录，无法判断变化。")
            : L($"{raised} groups have a higher recorded severity; {again} were observed again; {report.NotObservedAgain} were not observed again. These are changes in saved observations, not proof of worsening or repair.",
                $"{raised} 组记录严重程度升高；{again} 组再次观察；{report.NotObservedAgain} 组本期未再次观察。这些是保存观察的变化，不能单独证明恶化或修复。");
        string nextReview = L("Check the priority items, apply changes only when their conditions are confirmed, then repeat the affected scenario and save verification evidence. Compare the same collection scope next time.",
            "先完成重点事项的检查，只在条件确认后整改；随后复现原场景并保存验证证据。下次复查保持相同采集范围。");
        return new(assessment, assessmentDetail, coverageSummary, string.Join(Environment.NewLine, coverage), priorities, components, categories, severities, changes, nextReview);
    }

    private static AuditReportPriority Priority(AuditReportIssue issue, bool chinese)
    {
        string L(string en, string zh) => chinese ? zh : en;
        var finding = issue.Latest;
        var plan = finding.EffectiveRemediationPlan;
        var check = plan.Checks?.FirstOrDefault(item => item is not null && !string.IsNullOrWhiteSpace(Local(item.Check, item.CheckZh, chinese)));
        string context = Category(finding.Category, chinese) + L($" · observed on {issue.ObservedDays} scan dates", $" · 在 {issue.ObservedDays} 个扫描日期观察");
        if (!string.IsNullOrWhiteSpace(finding.Source) || !string.IsNullOrWhiteSpace(finding.EventId))
        {
            context += " · " + Preview(finding.Source, 80) + " / " + finding.EventId;
        }
        if (issue.LastObservedAt is { } saved)
        {
            context += L($" · latest observation {saved.ToLocalTime():MM-dd HH:mm}", $" · 最近观察 {saved.ToLocalTime():MM-dd HH:mm}");
        }
        return new(issue, finding.IsHigh || (AuditPeriodReport.IsStabilityFinding(finding) && finding.IsMedium),
            Preview(Local(finding.Title, finding.TitleZh, chinese), 160), context,
            Preview(Local(finding.Description, finding.DescriptionZh, chinese), 280),
            Preview(check is null ? Local(finding.Recommendation, finding.RecommendationZh, chinese) : Local(check.Check, check.CheckZh, chinese), 240),
            Preview(Local(plan.ConditionalAction, plan.ConditionalActionZh, chinese), 240),
            Preview(Local(plan.Verification, plan.VerificationZh, chinese), 240));
    }

    internal static string Category(string? category, bool chinese) => !chinese ? string.IsNullOrWhiteSpace(category) ? "Other" : category : (category ?? string.Empty).ToLowerInvariant() switch
    {
        "stability" => "崩溃与异常重启", "reliability" => "系统可靠性", "application" => "应用", "system" => "系统",
        "network" or "networksecurity" => "网络", "configuration" or "setup" => "配置", "security" => "安全",
        "login" or "authentication" => "登录与认证", "privilege" or "authorization" => "权限", "firewall" => "防火墙",
        "audit" or "auditfailure" => "审计", "encryption" => "加密", "policy" => "策略", "other" or "" => "其他", _ => category ?? "其他",
    };

    private static string Component(AuditIssue issue, bool chinese) => !string.IsNullOrWhiteSpace(issue.Affected) ? issue.Affected.Trim()
        : !string.IsNullOrWhiteSpace(issue.Source) ? issue.Source.Trim() : chinese ? "未记录组件" : "Component not recorded";
    private static int Severity(AuditIssueEnhanced issue) => issue.IsHigh ? 3 : issue.IsMedium ? 2 : 1;
    private static string Local(string? en, string? zh, bool chinese) => AuditRemediationPlan.Localize(en, zh, chinese);
    private static string Preview(string value, int limit) => value.Length <= limit ? value : value[..limit] + "…";
}
