using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using Kit.AiHub.Contract;

namespace Kit.AIHubLib.Models;

public sealed class OptimizationEvidenceSnapshot
{
    public DateTimeOffset CollectedUtc { get; init; }
    public long CollectionElapsedMilliseconds { get; init; }
    public List<OptimizationDiagnosticEvidence> Evidence { get; init; } = [];

    // Display names stay on this device and never enter the model input.
    [JsonIgnore]
    public Dictionary<string, string> LocalLabels { get; init; } = new(StringComparer.Ordinal);
}

public sealed class OptimizationDiagnosticEvidence
{
    public string Id { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Coverage { get; init; } = "unavailable";
    public string Observation { get; init; } = string.Empty;
}

/// <summary>Caller-supplied audit counters; no event messages, paths or identities are transmitted.</summary>
public sealed class OptimizationStabilityEvidence
{
    public int PeriodDays { get; init; }
    public int ScanCount { get; init; }
    public int CriticalCount { get; init; }
    public int WarningCount { get; init; }
    public bool IsPartial { get; init; }
    public DateTimeOffset? ObservedFromUtc { get; init; }
    public DateTimeOffset? ObservedToUtc { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class OptimizationDiagnosticReport
{
    [JsonRequired]
    public string Summary { get; set; } = string.Empty;
    [JsonRequired]
    public List<OptimizationDiagnosticFinding> Findings { get; set; } = [];
    [JsonRequired]
    public List<string> Limitations { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class OptimizationDiagnosticFinding
{
    [JsonRequired]
    public string Title { get; set; } = string.Empty;
    [JsonRequired]
    public string Priority { get; set; } = "review";
    [JsonRequired]
    public string Diagnosis { get; set; } = string.Empty;
    [JsonRequired]
    public string Recommendation { get; set; } = string.Empty;
    [JsonRequired]
    public string ExpectedBenefit { get; set; } = string.Empty;
    [JsonRequired]
    public string Tradeoff { get; set; } = string.Empty;
    [JsonRequired]
    public string Verification { get; set; } = string.Empty;
    [JsonRequired]
    public List<string> EvidenceIds { get; set; } = [];
}

public sealed record OptimizationDiagnosticResult(
    OptimizationEvidenceSnapshot Snapshot,
    OptimizationDiagnosticReport? Report,
    AiErrorCode ErrorCode,
    long AnalysisElapsedMilliseconds)
{
    public bool IsSuccess => ErrorCode == AiErrorCode.None && Report is not null;

    public string Format(bool chinese)
    {
        var text = new StringBuilder();
        text.AppendLine(chinese ? "系统优化诊断 · 只读建议" : "System optimization diagnosis · Advisory only");
        text.AppendLine(Snapshot.CollectedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));
        text.AppendLine(chinese
            ? $"采集 {Snapshot.CollectionElapsedMilliseconds / 1000.0:F1} 秒 · AI 分析 {AnalysisElapsedMilliseconds / 1000.0:F1} 秒"
            : $"Collection {Snapshot.CollectionElapsedMilliseconds / 1000.0:F1}s · AI analysis {AnalysisElapsedMilliseconds / 1000.0:F1}s");
        text.AppendLine();
        if (Report is null)
        {
            text.AppendLine(chinese ? $"AI 诊断未完成：{ErrorCode}。未生成任何可执行操作。" : $"AI diagnosis incomplete: {ErrorCode}. No executable actions were created.");
        }
        else
        {
            text.AppendLine(Report.Summary);
            foreach (var finding in Report.Findings)
            {
                text.AppendLine();
                string priority = chinese ? finding.Priority switch { "urgent" => "优先处理", "monitor" => "持续观察", _ => "建议检查" } : finding.Priority;
                text.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"[{priority}] {finding.Title}");
                text.AppendLine(finding.Diagnosis);
                text.AppendLine((chinese ? "建议：" : "Recommendation: ") + finding.Recommendation);
                text.AppendLine((chinese ? "预期收益：" : "Expected benefit: ") + finding.ExpectedBenefit);
                text.AppendLine((chinese ? "代价与风险：" : "Tradeoff: ") + finding.Tradeoff);
                text.AppendLine((chinese ? "验证方法：" : "Verification: ") + finding.Verification);
                text.AppendLine((chinese ? "依据：" : "Evidence: ") + string.Join("; ", finding.EvidenceIds.Select(id =>
                    Snapshot.LocalLabels.TryGetValue(id, out string? label) ? $"{id} ({label})" : id)));
            }

            if (Report.Limitations.Count > 0)
            {
                text.AppendLine();
                text.AppendLine(chinese ? "分析限制" : "Limitations");
                foreach (string limitation in Report.Limitations) text.AppendLine("• " + limitation);
            }
        }

        text.AppendLine();
        text.AppendLine(chinese ? "采集范围与原始依据" : "Coverage and collected evidence");
        foreach (var evidence in Snapshot.Evidence)
        {
            string label = Snapshot.LocalLabels.TryGetValue(evidence.Id, out string? local) ? local : evidence.Domain;
            text.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"{evidence.Id} · {label} · {evidence.Coverage}: {evidence.Observation}");
        }

        text.AppendLine();
        text.AppendLine(chinese
            ? "短时 CPU/内存样本不能代表长期负载或启动影响；释放缓存空间可能增加后续重建或下载成本。本报告不会执行清理或修改系统。"
            : "Brief CPU/memory samples do not establish sustained load or startup impact. Cache removal may increase later rebuild/download costs. This report never cleans files or changes system settings.");
        return text.ToString().TrimEnd();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OptimizationEvidenceSnapshot))]
[JsonSerializable(typeof(OptimizationDiagnosticEvidence))]
[JsonSerializable(typeof(OptimizationDiagnosticReport))]
public partial class OptimizationDiagnosticJsonContext : JsonSerializerContext
{
}
