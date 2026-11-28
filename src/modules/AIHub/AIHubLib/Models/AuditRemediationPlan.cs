using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace Kit.AIHubLib.Models;

/// <summary>A human-readable diagnostic plan. Its text never authorizes execution.</summary>
public sealed class AuditRemediationPlan
{
    [JsonPropertyName("checks")]
    public List<AuditRemediationCheck> Checks { get; set; } = new();

    [JsonPropertyName("conditionalAction")]
    public string ConditionalAction { get; set; } = string.Empty;

    [JsonPropertyName("conditionalActionZh")]
    public string ConditionalActionZh { get; set; } = string.Empty;

    [JsonPropertyName("riskAndRecovery")]
    public string RiskAndRecovery { get; set; } = string.Empty;

    [JsonPropertyName("riskAndRecoveryZh")]
    public string RiskAndRecoveryZh { get; set; } = string.Empty;

    [JsonPropertyName("verification")]
    public string Verification { get; set; } = string.Empty;

    [JsonPropertyName("verificationZh")]
    public string VerificationZh { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasContent => Checks?.Any(check => check is not null &&
        (!string.IsNullOrWhiteSpace(check.Check) || !string.IsNullOrWhiteSpace(check.CheckZh))) == true
        || !string.IsNullOrWhiteSpace(ConditionalAction) || !string.IsNullOrWhiteSpace(ConditionalActionZh)
        || !string.IsNullOrWhiteSpace(RiskAndRecovery) || !string.IsNullOrWhiteSpace(RiskAndRecoveryZh)
        || !string.IsNullOrWhiteSpace(Verification) || !string.IsNullOrWhiteSpace(VerificationZh);

    [JsonIgnore]
    public bool IsValid => Checks is { Count: > 0 and <= 6 } && Checks.All(check => check is not null)
        && TextFields().All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 1200)
        && TextFields().Sum(value => (long)(value?.Length ?? 0)) <= 12000;

    internal IEnumerable<string> TextFields() => (Checks ?? []).Where(check => check is not null)
        .SelectMany(check => new[] { check.Check, check.CheckZh, check.ExpectedResult, check.ExpectedResultZh })
        .Concat([ConditionalAction, ConditionalActionZh, RiskAndRecovery, RiskAndRecoveryZh, Verification, VerificationZh]);

    public string Format(bool chinese)
    {
        var text = new StringBuilder();
        int index = 0;
        foreach (var check in Checks ?? [])
        {
            if (check is null) continue;
            string action = Localize(check.Check, check.CheckZh, chinese);
            if (string.IsNullOrWhiteSpace(action)) continue;
            text.AppendLine((chinese ? "检查 " : "Check ") + (++index).ToString(System.Globalization.CultureInfo.CurrentCulture) + ": " + action);
            Append(chinese ? "判断标准：" : "Decision criteria: ", Localize(check.ExpectedResult, check.ExpectedResultZh, chinese));
        }
        Append(chinese ? "条件处理：" : "Conditional action: ", Localize(ConditionalAction, ConditionalActionZh, chinese));
        Append(chinese ? "风险与恢复：" : "Risk and recovery: ", Localize(RiskAndRecovery, RiskAndRecoveryZh, chinese));
        Append(chinese ? "恢复验证：" : "Verification: ", Localize(Verification, VerificationZh, chinese));
        return text.ToString().TrimEnd();

        void Append(string label, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) text.AppendLine(label + value);
        }
    }

    public AuditRemediationPlan Clone() => new()
    {
        Checks = (Checks ?? []).Where(check => check is not null).Select(check => new AuditRemediationCheck
        {
            Check = check.Check, CheckZh = check.CheckZh,
            ExpectedResult = check.ExpectedResult, ExpectedResultZh = check.ExpectedResultZh,
        }).ToList(),
        ConditionalAction = ConditionalAction, ConditionalActionZh = ConditionalActionZh,
        RiskAndRecovery = RiskAndRecovery, RiskAndRecoveryZh = RiskAndRecoveryZh,
        Verification = Verification, VerificationZh = VerificationZh,
    };

    internal static string Localize(string? english, string? chinese, bool preferChinese) =>
        (preferChinese ? First(chinese, english) : First(english, chinese)) ?? string.Empty;

    private static string? First(string? preferred, string? fallback) => !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback;
}

public sealed class AuditRemediationCheck
{
    [JsonPropertyName("check")]
    public string Check { get; set; } = string.Empty;

    [JsonPropertyName("checkZh")]
    public string CheckZh { get; set; } = string.Empty;

    [JsonPropertyName("expectedResult")]
    public string ExpectedResult { get; set; } = string.Empty;

    [JsonPropertyName("expectedResultZh")]
    public string ExpectedResultZh { get; set; } = string.Empty;
}
