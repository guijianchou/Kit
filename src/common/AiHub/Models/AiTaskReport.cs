namespace Kit.AiHub.Contract;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Kit.AiHub.Serialization;

/// <summary>
/// Shared advisory report used by native IPC and plugins that do not need a custom output shape.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AiTaskReport
{
    public required List<AiFinding> Findings { get; init; }

    public required List<AiSuggestedAction> Actions { get; init; }

    public static AiTaskSchema<TInput, AiTaskReport> CreateSchema<TInput>(JsonTypeInfo<TInput> inputTypeInfo) => new()
    {
        InputTypeInfo = inputTypeInfo,
        OutputTypeInfo = AiHubJsonContext.Default.AiTaskReport,
        ValidateOutput = Validate,
        MergeBatches = reports => new AiTaskReport
        {
            Findings = reports.SelectMany(report => report.Findings).ToList(),
            Actions = reports.SelectMany(report => report.Actions).ToList(),
        },
    };

    private static bool Validate(AiTaskReport report, IReadOnlySet<string> itemIds)
    {
        if (report.Findings is null || report.Actions is null || report.Findings.Count > itemIds.Count * 8 || report.Actions.Count > itemIds.Count)
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in report.Findings)
        {
            if (finding is null || !itemIds.Contains(finding.ItemId) || string.IsNullOrWhiteSpace(finding.Key)
                || !keys.Add(finding.ItemId + ":" + finding.Key) || finding.Severity is not ("low" or "medium" or "high")
                || !HasText(finding.TitleEn, finding.TitleZh, finding.DescriptionEn, finding.DescriptionZh, finding.RecommendationEn, finding.RecommendationZh))
            {
                return false;
            }
        }

        keys.Clear();
        return report.Actions.All(action => action is not null && itemIds.Contains(action.ItemId)
            && keys.Add(action.ItemId) && action.ActionType == "skip" && string.IsNullOrEmpty(action.TargetRelativePath)
            && HasText(action.ReasonEn, action.ReasonZh));
    }

    private static bool HasText(params string[] values) => values.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 8_192);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AiFinding
{
    public required string ItemId { get; init; }
    public required string Key { get; init; }
    public required string Severity { get; init; }
    public required string TitleEn { get; init; }
    public required string TitleZh { get; init; }
    public required string DescriptionEn { get; init; }
    public required string DescriptionZh { get; init; }
    public required string RecommendationEn { get; init; }
    public required string RecommendationZh { get; init; }

    [JsonIgnore]
    public string DisplayTitle => IsChinese ? TitleZh : TitleEn;
    [JsonIgnore]
    public string DisplayDescription => IsChinese ? DescriptionZh : DescriptionEn;
    [JsonIgnore]
    public string DisplayRecommendation => IsChinese ? RecommendationZh : RecommendationEn;

    private static bool IsChinese => CultureInfo.CurrentUICulture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AiSuggestedAction
{
    public required string ItemId { get; init; }
    public required string ActionType { get; init; }
    public required string TargetRelativePath { get; init; }
    public required string ReasonEn { get; init; }
    public required string ReasonZh { get; init; }

    [JsonIgnore]
    public string DisplayReason => CultureInfo.CurrentUICulture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ? ReasonZh : ReasonEn;
}
