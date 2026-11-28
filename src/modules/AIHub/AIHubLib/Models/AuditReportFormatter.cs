using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Kit.AIHubLib.Models;

/// <summary>Deterministic report prose grounded in saved observations, without inferred repairs.</summary>
internal static class AuditReportFormatter
{
    public static string Overview(AuditPeriodReport report, bool chinese) => string.Join(Environment.NewLine + Environment.NewLine,
        Sections(report, chinese).Select(section => section.Title + Environment.NewLine + section.Text)) + Environment.NewLine;
    public static System.Collections.Generic.IReadOnlyList<AuditReportSection> Sections(AuditPeriodReport report, bool chinese)
    {
        var sections = new System.Collections.Generic.List<AuditReportSection>();
        string heading = chinese ? "报告范围" : "Report scope";
        var text = new StringBuilder().AppendLine(CultureInfo.CurrentCulture, $"{report.StartDate:yyyy-MM-dd} – {report.EndDate:yyyy-MM-dd}");
        if (report.ArchiveUnavailable)
            text.AppendLine(chinese ? "历史摘要存档无法读取，本报告仅使用当前审计历史；已有存档未覆盖。" : "The daily archive could not be read; this report uses current audit history only. Existing exports were preserved.");
        if (report.HistoryIncomplete)
        {
            text.AppendLine(report.CurrentHistoryIncomplete
                ? chinese ? "本期历史证据存在保留期限或摘要缺口；本期判断仅覆盖现存证据。" : "Current-period history has retention or summary gaps; the assessment covers retained evidence only."
                : chinese ? "对比期历史证据不完整；不据此推断本期改善或修复。" : "Baseline history is incomplete; it cannot establish current-period improvement or recovery.");
        }
        if (report.Days.Count == 0)
        {
            text.AppendLine(chinese ? "此时段没有保存的审计记录。" : "No saved audits in this period.");
            sections.Add(new(heading, text.ToString().Trim()));
            return sections;
        }

        var highlights = report.Highlights(chinese);
        sections.Add(new(chinese ? "系统稳定性评估" : "System stability assessment",
            highlights.Assessment + Environment.NewLine + highlights.AssessmentDetail + Environment.NewLine + highlights.CoverageSummary));

        text.AppendLine(chinese ? $"已完成 AI 审计 {report.CompletedAiAudits} 次；未完成 AI 审计 {report.IncompleteAiAudits} 次。"
            : $"Completed AI audits: {report.CompletedAiAudits}; incomplete AI audits: {report.IncompleteAiAudits}.");
        if (report.Models.Count > 0) text.AppendLine((chinese ? "完成审计的模型：" : "Completed audit models: ") + string.Join(", ", report.Models));

        BeginSection(chinese ? "本期总评" : "Overall assessment");
        int high = report.Issues.Count(issue => issue.Latest.IsHigh);
        int medium = report.Issues.Count(issue => issue.Latest.IsMedium);
        text.AppendLine(report.Issues.Count == 0
            ? chinese ? "已保存的观察中没有问题；这不证明未覆盖的时段或渠道没有风险。" : "No issues appear in the saved observations. This does not establish that unobserved periods or channels are free of risk."
            : chinese ? $"本期记录 {report.Issues.Count} 类问题，其中 {high} 类高优先级、{medium} 类中优先级。优先核实高风险及反复出现、影响日常使用的问题。"
                : $"Saved observations contain {report.Issues.Count} issue groups: {high} high and {medium} medium priority. Verify high-priority and recurring problems affecting normal use first.");
        var areas = report.Issues.GroupBy(issue => issue.Latest.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(4).Select(group => $"{Category(group.Key, chinese)} ({group.Count()})").ToArray();
        if (areas.Length > 0) text.AppendLine((chinese ? "主要涉及：" : "Main affected areas: ") + string.Join(" · ", areas));
        text.AppendLine(chinese ? "本报告依据保存的本地与 AI 观察整理；严重程度是待核实的优先级，不代表已确认攻击或故障根因。"
            : "This report summarizes saved local and AI observations. Severity guides verification; it does not establish an attack or a confirmed root cause.");

        BeginSection(chinese ? "本期变化" : "Changes during the period");
        if (report.PreviousDays.Count == 0)
        {
            text.AppendLine(chinese ? "对比时段没有记录，无法判断变化。" : "No recorded baseline; change cannot be determined.");
        }
        else
        {
            int recurring = report.Issues.Count(issue => issue.SeenPreviously);
            int raised = report.Issues.Count(issue => issue.SeenPreviously && Severity(issue.Latest) > issue.PreviousPeakSeverity);
            text.AppendLine(chinese
                ? $"相对 {report.PreviousStartDate:MM-dd} – {report.PreviousEndDate:MM-dd} 的已保存观察：{report.Issues.Count - recurring} 类本期首次观察，{recurring} 类再次观察，其中 {raised} 类本期最高严重程度更高。首次观察不等于首次发生。"
                : $"Against saved observations from {report.PreviousStartDate:MM-dd} – {report.PreviousEndDate:MM-dd}: {report.Issues.Count - recurring} groups newly observed, {recurring} observed again, and {raised} of those have a higher recorded peak severity. Newly observed does not mean newly occurring.");
        }
        if (report.NotObservedAgain > 0)
            text.AppendLine(chinese ? $"此前 {report.NotObservedAgain} 类问题未在本期快照中出现，不能据此判定已解决。" : $"{report.NotObservedAgain} previous issue groups were not observed again; this does not establish resolution.");
        double score = report.Days.Average(day => day.HealthScore);
        text.AppendLine(chinese ? $"平均健康分：{score:0.#}/100（仅作扫描记录摘要）。" : $"Average health score: {score:0.#}/100 (a summary of recorded scans only).");
        if (report.CanCompareScores)
        {
            text.AppendLine(chinese ? $"同口径扫描较对比期平均分：{score - report.PreviousDays.Average(day => day.HealthScore):+0.#;-0.#;0} 分；分数变化不证明问题已修复。"
                : $"Change across comparable scans: {score - report.PreviousDays.Average(day => day.HealthScore):+0.#;-0.#;0} points. A score change does not establish recovery.");
        }
        else if (report.PreviousDays.Count > 0)
        {
            text.AppendLine(chinese ? "未比较分数趋势：扫描范围、采集完整性或分析口径不一致或未知，不能据此判断系统改善。"
                : "Score trend withheld: scan windows, collection completeness or analysis methods differ or are unknown. These scores cannot establish system improvement.");
        }

        BeginSection(chinese ? "重点行动（最多 3 项）" : "Priority actions (up to 3)");
        if (report.Issues.Count == 0) text.AppendLine(chinese ? "当前没有可从保存证据提出的处理项；按覆盖情况决定是否补充扫描。" : "No action is supported by the saved findings. Use coverage information to decide whether another scan is needed.");
        int index = 0;
        foreach (var priority in highlights.Priorities)
        {
            var issue = priority.Issue;
            var finding = issue.Latest;
            text.AppendLine(CultureInfo.CurrentCulture, $"{++index}. [{Risk(finding, chinese)}] {Preview(Title(finding, chinese), 180)}");
            text.AppendLine("   " + priority.Context);
            if (!string.IsNullOrWhiteSpace(priority.Evidence)) text.AppendLine((chinese ? "   依据：" : "   Evidence: ") + priority.Evidence);
            text.AppendLine((chinese ? "   检查：" : "   Check: ") + OrUnknown(priority.Check, chinese));
            text.AppendLine((chinese ? "   条件整改：" : "   Conditional action: ") + OrUnknown(priority.Action, chinese));
            text.AppendLine((chinese ? "   验证：" : "   Verification: ") + OrUnknown(priority.Verification, chinese));
        }
        if (report.Issues.Count > 0) text.AppendLine(chinese ? "以上为行动摘要；执行前请阅读完整问题明细中的条件处理、风险与验证步骤。" : "These are action summaries. Read the full finding's conditions, risks and verification steps before making changes.");

        BeginSection(chinese ? "处理与验证" : "Actions and verification");
        text.AppendLine(chinese ? "审计记录没有可核验的修复执行收据，本报告不将任何问题标记为已修复。建议步骤不代表已经执行。"
            : "Audit records contain no verified repair receipts, so this report does not mark issues as repaired. Recommended steps are not completed actions.");
        text.AppendLine(chinese ? "完成处理后，按对应方案复现原场景并收集新的验证证据。问题未再次出现、日志缺失或分数回升，都不能单独证明恢复。"
            : "After a change, repeat the affected scenario and collect the plan's verification evidence. Absence of recurrence, missing logs or a higher score alone cannot establish recovery.");

        BeginSection(chinese ? "证据覆盖与统计口径" : "Coverage and interpretation");
        int totalDays = report.EndDate.DayNumber - report.StartDate.DayNumber + 1;
        text.AppendLine(chinese
            ? $"已记录 {report.Days.Count}/{totalDays} 天，共 {report.Days.Sum(day => day.Audits)} 次审计。健康分取每日最后一次扫描，问题汇总当天所有观察；分数回升不代表此前问题已解决。"
            : $"Recorded {report.Days.Count}/{totalDays} days, {report.Days.Sum(day => day.Audits)} audits. Health scores use each day's last scan; issues include all observations that day. A higher score does not establish resolution of earlier issues.");
        text.AppendLine(chinese ? "日期表示保存扫描的日期，不代表事件仅发生在该日。发生次数取单次快照的最高值，不累加重叠扫描；不能将其当作期间唯一事件总数。"
            : "Dates identify when scans were saved, not when every event occurred. Occurrences use the highest single-snapshot count; overlapping scans are not added. This is not a count of unique events in the period.");
        if (report.IncompleteDays > 0)
            text.AppendLine(chinese ? $"{report.IncompleteDays} 天有日志缺失、数量上限或未完成的 AI 分析。" : $"{report.IncompleteDays} days have missing/truncated logs or incomplete AI analysis.");
        if (report.Coverage.IncompleteEvidenceScans > 0)
        {
            text.AppendLine(chinese ? $"本期及对比期有 {report.Coverage.IncompleteEvidenceScans} 次扫描只保留了部分时段证据；AI 分析完成不代表这些缺失区间已补齐。"
                : $"{report.Coverage.IncompleteEvidenceScans} current/baseline scans retain only part of their event-window evidence. Completed AI analysis does not fill those evidence gaps.");
        }
        if (report.Coverage.UnknownWindowScans > 0)
        {
            text.AppendLine(chinese ? $"本期及对比期有 {report.Coverage.UnknownWindowScans} 次扫描缺少有效的范围元数据，无法重建完整时间覆盖。"
                : $"{report.Coverage.UnknownWindowScans} current/baseline scans lack valid window metadata; complete time coverage cannot be reconstructed.");
        }
        if (report.Coverage.MixedWindows)
        {
            text.AppendLine(chinese ? "记录包含不同长度的扫描窗口；更长窗口内的旧问题可能再次被观察，不应解释为本期新增。"
                : "Records contain different scan-window lengths. Older events found by a longer scan must not be interpreted as new incidents.");
        }
        if (report.Coverage.UnknownCollectionScans > 0)
        {
            text.AppendLine(chinese ? $"本期及对比期有 {report.Coverage.UnknownCollectionScans} 次扫描未记录采集渠道与规则版本，不能确认其与当前扫描口径一致。"
                : $"{report.Coverage.UnknownCollectionScans} current/baseline scans have no recorded channel and rule profile; their collection scope cannot be assumed to match current scans.");
        }
        if (report.Coverage.MixedCollectionProfiles)
        {
            text.AppendLine(chinese ? "记录包含不同采集渠道或规则版本，分数变化可能来自采集范围变化。"
                : "Records use different channel sets or rule versions; score differences may reflect a change in collection scope.");
        }
        AppendChannels(text, chinese ? "未读取渠道：" : "Unreadable channels: ", report.Coverage.SkippedChannels);
        AppendChannels(text, chinese ? "达到数量上限的渠道：" : "Truncated channels: ", report.Coverage.LimitedChannels);
        sections.Add(new(heading, text.ToString().Trim()));
        text.Clear().AppendLine(chinese ? "每个扫描保存日内的问题组去重并取最高严重程度；重叠窗口不累加为事件次数。缺少扫描记录的日期不补零。"
            : "Issue groups are deduplicated across all saved scans on each date at their highest severity. Overlapping windows are not event counts. Missing scan dates are not filled with zero.");
        foreach (var day in report.TrendDays)
        {
            text.AppendLine(day.Scans == 0 ? $"{day.Date:MM-dd} · " + (chinese ? "无保存扫描" : "No saved scan")
                : chinese ? $"{day.Date:MM-dd} · 稳定性 {day.Stability} 组 · 高/中/低 {day.High}/{day.Medium}/{day.Low} 组"
                    : $"{day.Date:MM-dd} · stability {day.Stability} groups · high/medium/low {day.High}/{day.Medium}/{day.Low} groups");
            if (day.EvidenceLimited || day.AiIncomplete || day.MetadataUnknown)
            {
                text.AppendLine(chinese ? "  证据受限、分析未完成或采集范围未知，不能作为完整覆盖。" : "  Evidence, analysis or collection metadata is incomplete; coverage is not assured.");
            }
        }
        sections.Add(new(chinese ? "每日异常观察" : "Daily issue observations", text.ToString().Trim()));
        text.Clear().AppendLine(chinese ? "仅依据 Stability / Reliability 分类；受影响组件或事件来源按去重问题组排行，不是崩溃次数。"
            : "Stability / Reliability classifications only. Affected components or event sources are ranked by deduplicated issue groups, not crash counts.");
        foreach (var component in highlights.Components)
        {
            text.AppendLine(CultureInfo.CurrentCulture, $"{component.Label} · {component.Count} · {component.Detail}");
        }
        if (highlights.Components.Count == 0) text.AppendLine(chinese ? "未记录稳定性分类组件。" : "No stability-classified components recorded.");
        sections.Add(new(chinese ? "主要受影响组件" : "Leading affected components", text.ToString().Trim()));
        return sections;

        void BeginSection(string title)
        {
            sections.Add(new(heading, text.ToString().Trim()));
            text.Clear();
            heading = title;
        }
    }

    public static string Details(AuditPeriodReport report, bool chinese)
    {
        if (report.Issues.Count == 0) return string.Empty;
        var text = new StringBuilder();
        Heading(text, chinese ? "完整问题明细" : "Complete findings");
        foreach (var issue in report.Issues)
        {
            var finding = issue.Latest;
            text.AppendLine().AppendLine(CultureInfo.CurrentCulture, $"[{Risk(finding, chinese)}] {Title(finding, chinese)}");
            text.AppendLine(chinese ? $"在 {issue.ObservedDays} 个扫描日期观察 · 单次最多 {issue.PeakOccurrences} 次 · {finding.LogName}/{finding.EventId}"
                : $"Observed on {issue.ObservedDays} scan dates · peak {issue.PeakOccurrences} per snapshot · {finding.LogName}/{finding.EventId}");
            text.AppendLine((chinese ? "事实：" : "Evidence: ") + OrUnknown(Local(finding.Description, finding.DescriptionZh, chinese), chinese));
            text.AppendLine((chinese ? "原因判断：" : "Cause assessment: ") + OrUnknown(Local(finding.RootCause, finding.RootCauseZh, chinese), chinese));
            string remedy = finding.EffectiveRemediationPlan.Format(chinese);
            text.AppendLine((chinese ? "下一步：" : "Next step: ") + OrUnknown(remedy, chinese));
            if (finding.RemediationPlan is null && !string.IsNullOrWhiteSpace(Local(finding.Recommendation, finding.RecommendationZh, chinese)))
                text.AppendLine((chinese ? "原记录建议（需按上述步骤核实）：" : "Previously recorded advice (verify using the checks above): ") + Local(finding.Recommendation, finding.RecommendationZh, chinese));
        }
        return text.ToString();
    }

    private static void Heading(StringBuilder text, string title) => text.AppendLine().AppendLine(title);
    private static string Local(string? english, string? chinese, bool preferChinese) => AuditRemediationPlan.Localize(english, chinese, preferChinese);
    private static string OrUnknown(string value, bool chinese) => string.IsNullOrWhiteSpace(value) ? chinese ? "未记录；需要补充证据后判断。" : "Not recorded; additional evidence is needed." : value;
    private static string Title(AuditIssue issue, bool chinese) => OrUnknown(Local(issue.Title, issue.TitleZh, chinese), chinese);
    private static string Risk(AuditIssueEnhanced issue, bool chinese) => chinese ? issue.IsHigh ? "高" : issue.IsMedium ? "中" : "低" : issue.Severity;
    private static int Severity(AuditIssueEnhanced issue) => issue.IsHigh ? 3 : issue.IsMedium ? 2 : 1;
    private static string Preview(string value, int limit) => value.Length <= limit ? value : value[..limit] + "…";
    private static void AppendChannels(StringBuilder text, string label, System.Collections.Generic.IReadOnlyList<string> channels)
    {
        if (channels.Count > 0) text.AppendLine(label + string.Join(", ", channels.Take(12)) + (channels.Count > 12 ? "…" : string.Empty));
    }

    private static string Category(string category, bool chinese) => !chinese ? category : category.ToLowerInvariant() switch
    {
        "application" => "应用", "system" => "系统", "stability" or "reliability" => "稳定性",
        "network" or "networksecurity" => "网络", "configuration" or "setup" => "配置",
        "security" => "安全", "login" or "authentication" => "登录与认证", "privilege" or "authorization" => "权限",
        "firewall" => "防火墙", "audit" or "auditfailure" => "审计", "encryption" => "加密", "policy" => "策略", "other" => "其他",
        _ => category,
    };
}
