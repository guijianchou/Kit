using System;
using System.Globalization;
using System.Text.Json.Serialization;
using Kit.AIHubLib.Services;

namespace Kit.AIHubLib.Models;

public sealed class AuditIssueEnhanced : AuditIssue
{
    private static bool IsChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>The most severe level in the referenced raw events, never supplied by the AI.</summary>
    [JsonPropertyName("observedEventLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ObservedEventLevel { get; set; }

    public bool IsHigh => string.Equals(Severity, "High", StringComparison.OrdinalIgnoreCase);
    public bool IsMedium => string.Equals(Severity, "Medium", StringComparison.OrdinalIgnoreCase);
    public bool IsLow => string.Equals(Severity, "Low", StringComparison.OrdinalIgnoreCase);

    public string SeverityText
    {
        get
        {
            if (IsChinese)
            {
                if (IsHigh) return "高";
                if (IsMedium) return "中";
                return "低";
            }

            return Severity;
        }
    }

    public string RiskBadgeText
    {
        get
        {
            if (IsChinese)
            {
                if (IsHigh) return "高风险";
                if (IsMedium) return "中风险";
                return "低风险";
            }

            if (IsHigh) return "High";
            if (IsMedium) return "Medium";
            return "Low";
        }
    }

    public string SeverityGlyph => IsHigh ? "\uEA39" : IsMedium ? "\uE7BA" : "\uE946";

    public string CompactMetaText
    {
        get
        {
            string category = CategoryLabel;
            string eventPart = string.IsNullOrWhiteSpace(EventId)
                ? string.Empty
                : (IsChinese ? $"事件 {EventId}" : $"Event {EventId}");
            string countPart = Occurrences <= 1
                ? string.Empty
                : (IsChinese ? $"{Occurrences} 次" : $"{Occurrences} occurrences");

            if (!string.IsNullOrEmpty(eventPart) && !string.IsNullOrEmpty(countPart))
            {
                return $"{category} · {eventPart} · {countPart}";
            }

            if (!string.IsNullOrEmpty(eventPart))
            {
                return $"{category} · {eventPart}";
            }

            if (!string.IsNullOrEmpty(countPart))
            {
                return $"{category} · {countPart}";
            }

            return category;
        }
    }

    public string EvidenceChipText
    {
        get
        {
            string id = !string.IsNullOrWhiteSpace(EventId) ? EventId : CategoryLabel;
            if (Occurrences > 1)
            {
                return IsChinese ? $"{id} · {Occurrences} 次" : $"{id} · {Occurrences}x";
            }

            return id;
        }
    }

    public string DisplayTitle
    {
        get
        {
            if (IsChinese)
            {
                if (!string.IsNullOrWhiteSpace(TitleZh)) return TitleZh;
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                if (!string.IsNullOrWhiteSpace(DescriptionZh)) return DescriptionZh;
                return Description;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                if (!string.IsNullOrWhiteSpace(TitleZh)) return TitleZh;
                if (!string.IsNullOrWhiteSpace(Description)) return Description;
                return DescriptionZh;
            }
        }
    }

    public string DisplayDescription
    {
        get
        {
            if (IsChinese)
            {
                return !string.IsNullOrWhiteSpace(DescriptionZh) ? DescriptionZh : Description;
            }
            else
            {
                return !string.IsNullOrWhiteSpace(Description) ? Description : DescriptionZh;
            }
        }
    }

    [JsonIgnore]
    public AuditRemediationPlan EffectiveRemediationPlan => AuditRemediationCatalog.Resolve(this);

    public string PriorityActionText => EffectiveRemediationPlan.Format(IsChinese);

    public string PriorityReasonText
    {
        get
        {
            if (IsChinese)
            {
                if (!string.IsNullOrWhiteSpace(RootCauseZh)) return RootCauseZh;
                if (!string.IsNullOrWhiteSpace(RootCause)) return RootCause;
                if (!string.IsNullOrWhiteSpace(DescriptionZh)) return DescriptionZh;
                return Description;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(RootCause)) return RootCause;
                if (!string.IsNullOrWhiteSpace(RootCauseZh)) return RootCauseZh;
                if (!string.IsNullOrWhiteSpace(Description)) return Description;
                return DescriptionZh;
            }
        }
    }

    public string CategoryLabel
    {
        get
        {
            if (IsChinese)
            {
                return Category switch
                {
                    "Application" => "应用日志",
                    "Security" => "安全",
                    "System" => "系统",
                    "Setup" => "设置",
                    "ForwardedEvents" => "转发事件",
                    "Reliability" => "系统稳定性",
                    "Stability" => "系统崩溃与异常重启",
                    "Network" => "网络连接",
                    "Configuration" => "系统配置",
                    "Authentication" => "登录与认证",
                    "Authorization" => "权限与特权",
                    "AuditFailure" => "审核与日志",
                    "NetworkSecurity" => "网络与防火墙",
                    _ => Category,
                };
            }

            return Category;
        }
    }

    public string ModelLabel => "AI Hub";

    public string ActionBadgeText => IsChinese ? "操作建议" : "Suggested Action";

    public string ConfidenceLabel => IsChinese ? $"置信度: {Confidence}" : $"Confidence: {Confidence}";

    public string EvidenceSummaryText => DisplayDescription;

    public string OccurrencesLabel
    {
        get
        {
            if (Occurrences <= 1) return string.Empty;
            return IsChinese ? $"{Occurrences} 次发生" : $"{Occurrences} occurrences";
        }
    }

    public bool HasOccurrences => Occurrences > 1;

    public string EventTimestampText => EventTimestamp;

    public bool NeedsTranslation => string.IsNullOrWhiteSpace(TitleZh);

    public AuditIssueEnhanced()
    {
    }

    public AuditIssueEnhanced(AuditIssue baseIssue)
    {
        Key = baseIssue.Key;
        EventRef = baseIssue.EventRef;
        EventId = baseIssue.EventId;
        EventTimestamp = baseIssue.EventTimestamp;
        Source = baseIssue.Source;
        LogName = baseIssue.LogName;
        Title = baseIssue.Title;
        Description = baseIssue.Description;
        Severity = baseIssue.Severity;
        ObservedEventLevel = (baseIssue as AuditIssueEnhanced)?.ObservedEventLevel;
        Confidence = baseIssue.Confidence;
        Category = baseIssue.Category;
        Affected = baseIssue.Affected;
        RootCause = baseIssue.RootCause;
        Recommendation = baseIssue.Recommendation;
        RemediationPlan = baseIssue.RemediationPlan?.Clone();
        TitleZh = baseIssue.TitleZh;
        DescriptionZh = baseIssue.DescriptionZh;
        RootCauseZh = baseIssue.RootCauseZh;
        RecommendationZh = baseIssue.RecommendationZh;
        Occurrences = baseIssue.Occurrences;
        RelatedEventRefs = baseIssue.RelatedEventRefs;
    }
}
