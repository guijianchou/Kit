using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Kit.AIHubLib.Models;

public class AuditIssue
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("eventRef")]
    public string EventRef { get; set; } = string.Empty;

    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("eventTimestamp")]
    public string EventTimestamp { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("logName")]
    public string LogName { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Low";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "Medium";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "Other";

    [JsonPropertyName("affected")]
    public string Affected { get; set; } = string.Empty;

    [JsonPropertyName("rootCause")]
    public string RootCause { get; set; } = string.Empty;

    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = string.Empty;

    [JsonPropertyName("titleZh")]
    public string TitleZh { get; set; } = string.Empty;

    [JsonPropertyName("descriptionZh")]
    public string DescriptionZh { get; set; } = string.Empty;

    [JsonPropertyName("rootCauseZh")]
    public string RootCauseZh { get; set; } = string.Empty;

    [JsonPropertyName("recommendationZh")]
    public string RecommendationZh { get; set; } = string.Empty;

    [JsonPropertyName("occurrences")]
    public int Occurrences { get; set; } = 1;

    [JsonPropertyName("relatedEventRefs")]
    public List<string> RelatedEventRefs { get; set; } = new();
}

public sealed class AuditIssueContainer
{
    [JsonPropertyName("issues")]
    public List<AuditIssue> Issues { get; set; } = new();
}
