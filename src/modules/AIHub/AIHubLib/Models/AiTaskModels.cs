using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Kit.AIHubLib.Models;

public sealed class EventAnalysisInput
{
    public int EventId { get; set; }
    public string LogName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public int? Level { get; set; }
    public string TimeUtc { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public sealed class OptimizationItemInput
{
    public int FileCount { get; set; } = 1;
    public string LocalRisk { get; set; } = "low";
    public int MinimumAgeMinutes { get; set; }
    public string Evidence { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeInBytes { get; set; }
    public string LastModifiedUtc { get; set; } = string.Empty;
    public string CategoryHint { get; set; } = string.Empty;
    public string AllowedAction { get; set; } = string.Empty;
}

public sealed class OptimizationRecommendation
{
    [JsonPropertyName("itemId")]
    [JsonRequired]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    [JsonRequired]
    public string Action { get; set; } = "skip"; // "move" or "delete" or "skip"

    [JsonPropertyName("targetRelative")]
    [JsonRequired]
    public string? TargetRelative { get; set; }

    [JsonPropertyName("risk")]
    [JsonRequired]
    public string Risk { get; set; } = "low"; // "low", "medium", "high"

    [JsonPropertyName("reasonEn")]
    [JsonRequired]
    public string ReasonEn { get; set; } = string.Empty;

    [JsonPropertyName("reasonZh")]
    [JsonRequired]
    public string ReasonZh { get; set; } = string.Empty;
}

public sealed class OptimizationRecommendationContainer
{
    [JsonPropertyName("recommendations")]
    public List<OptimizationRecommendation> Recommendations { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EventAnalysisInput))]
[JsonSerializable(typeof(List<EventAnalysisInput>))]
[JsonSerializable(typeof(AuditIssue))]
[JsonSerializable(typeof(AuditIssueContainer))]
[JsonSerializable(typeof(AuditResult))]
[JsonSerializable(typeof(List<AuditResult>))]
[JsonSerializable(typeof(AuditIssueEnhanced))]
[JsonSerializable(typeof(List<AuditIssueEnhanced>))]
[JsonSerializable(typeof(OptimizationItemInput))]
[JsonSerializable(typeof(List<OptimizationItemInput>))]
[JsonSerializable(typeof(OptimizationRecommendation))]
[JsonSerializable(typeof(OptimizationRecommendationContainer))]
public partial class AIHubLibJsonContext : JsonSerializerContext
{
}
