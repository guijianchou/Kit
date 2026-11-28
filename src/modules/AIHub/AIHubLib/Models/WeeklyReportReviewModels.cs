using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Kit.AIHubLib.Models;

public sealed record WeeklyReportReviewInput(WeeklyReportMode Mode, WindowsStabilityReportData Data,
    IReadOnlyList<WindowsStabilityEvent> Events, WeeklyScanSnapshot? Scan);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WeeklyReportFinding
{
    [JsonRequired]
    public string Priority { get; init; } = string.Empty;
    [JsonRequired]
    public string TitleZh { get; init; } = string.Empty;
    [JsonRequired]
    public string TitleEn { get; init; } = string.Empty;
    [JsonRequired]
    public string FactsZh { get; init; } = string.Empty;
    [JsonRequired]
    public string FactsEn { get; init; } = string.Empty;
    [JsonRequired]
    public string HypothesisZh { get; init; } = string.Empty;
    [JsonRequired]
    public string HypothesisEn { get; init; } = string.Empty;
    [JsonRequired]
    public string StepsZh { get; init; } = string.Empty;
    [JsonRequired]
    public string StepsEn { get; init; } = string.Empty;
    [JsonRequired]
    public string VerificationZh { get; init; } = string.Empty;
    [JsonRequired]
    public string VerificationEn { get; init; } = string.Empty;
    [JsonRequired]
    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();
}

public sealed record WeeklyReportReview
{
    public string AssessmentZh { get; init; } = string.Empty;
    public string AssessmentEn { get; init; } = string.Empty;
    public IReadOnlyList<WeeklyReportFinding> Findings { get; init; } = Array.Empty<WeeklyReportFinding>();
    public string UsedModel { get; init; } = string.Empty;
    public string UsedRoute { get; init; } = string.Empty;
    public IReadOnlyList<WeeklyReportEvidence> EvidenceIndex { get; init; } = Array.Empty<WeeklyReportEvidence>();
}

/// <summary>Host-owned, sanitized evidence index; never accepted from model output.</summary>
public sealed record WeeklyReportEvidence(string EvidenceRef, string Kind, string Channel, string Provider,
    int? EventId, int? Count, DateTimeOffset? First, DateTimeOffset? Last, string FirstSample, string LastSample);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WeeklyReportAiOutput
{
    [JsonRequired]
    public string InputRef { get; init; } = string.Empty;
    [JsonRequired]
    public string AssessmentZh { get; init; } = string.Empty;
    [JsonRequired]
    public string AssessmentEn { get; init; } = string.Empty;
    [JsonRequired]
    public IReadOnlyList<WeeklyReportFinding> Findings { get; init; } = Array.Empty<WeeklyReportFinding>();
}

internal sealed record WeeklyEvidenceCount(string Channel, int Level, bool Current, int Count);
internal sealed record WeeklyEvidenceGroup(string EvidenceRef, string Channel, string Provider, int EventId,
    int Level, bool Current, int Count, DateTimeOffset First, DateTimeOffset Last, string FirstSample, string LastSample,
    IReadOnlyDictionary<string, string> Fields);
internal sealed record WeeklyEvidenceSelection(int ProvidedEvents, int TotalGroups, int IncludedGroups,
    int IncludedOccurrences, int OmittedGroups, string Method);
internal sealed record WeeklyScanReference(string EvidenceRef, string Id, DateTime? StartUtc, DateTime? EndUtc,
    string Profile, string AnalysisOutcome, string UsedModel, bool EvidenceIncomplete, bool RawEventsAvailable,
    bool RawEventsTruncated, IReadOnlyList<string> SkippedChannels, IReadOnlyList<string> LimitedChannels,
    int TotalFindings, int IncludedFindings, string Summary, IReadOnlyList<WeeklyScanFinding> Findings);
internal sealed record WeeklyScanFinding(string EvidenceRef, string Severity, string TitleZh, string TitleEn,
    string FactsZh, string FactsEn, string RecommendationZh, string RecommendationEn);
internal sealed record WeeklyReportAiInput(string Mode, string StatisticsRef, DateTimeOffset Start,
    DateTimeOffset End, DateTimeOffset PreviousStart, DateTimeOffset PreviousEnd, bool CurrentComplete,
    bool PreviousComplete, IReadOnlyList<WindowsStabilityMetric> Metrics, IReadOnlyList<WindowsStabilityDaily> Daily,
    IReadOnlyList<WindowsStabilityCoverage> Coverage, IReadOnlyList<string> BlindSpots,
    IReadOnlyList<WeeklyEvidenceCount> ObservedCounts, WeeklyEvidenceSelection Selection,
    IReadOnlyList<WeeklyEvidenceGroup> Evidence, WeeklyScanReference? ScanReference);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WeeklyReportAiInput))]
[JsonSerializable(typeof(WeeklyReportAiOutput))]
[JsonSerializable(typeof(WeeklyReportReview))]
[JsonSerializable(typeof(List<WeeklyReportEvidence>))]
internal sealed partial class WeeklyReportReviewJsonContext : JsonSerializerContext
{
}
