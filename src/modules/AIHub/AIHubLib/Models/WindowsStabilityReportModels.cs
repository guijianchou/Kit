using System;
using System.Collections.Generic;

namespace Kit.AIHubLib.Models;

public sealed record WindowsStabilityMetric(string Key, string Label, int? Current, int? Previous, string Unit, string Qualification);
public sealed record WindowsStabilityDaily(DateTimeOffset Start, int? Critical, int? Error, int? Warning, int? Information, int? Crashes, bool Current);
public sealed record WindowsStabilityTimeline(DateTimeOffset Time, string Kind, string Label, string Detail, bool Current);
public sealed record WindowsStabilityCrashCluster(string Process, string Module, string ExceptionCode, string Offset, int Current, int Previous, bool CompleteSignature)
{
    public bool CurrentKnown { get; init; } = true;
    public bool PreviousKnown { get; init; } = true;
}
public sealed record WindowsStabilityHeatCell(DateTimeOffset Day, int Hour, int? Count);
public sealed record WindowsStabilityNoise(string Provider, int EventId, int Current, int Previous, string Classification)
{
    public bool CurrentKnown { get; init; } = true;
    public bool PreviousKnown { get; init; } = true;
}
public sealed record WindowsStabilityDimension(string Name, string Status, string Evidence);
public sealed record WindowsStabilityAction(string Priority, string Title, string Reason, string Step, string Verification);
public sealed record WindowsStabilityCoverage(string Channel, DateTimeOffset RequestedStart, DateTimeOffset RequestedEnd,
    DateTimeOffset? OldestAvailable, bool ReadSucceeded, bool Capped, bool RolledOver, int Records, string Limitation);
public sealed record WindowsStabilityReportData(
    string Assessment, string Grade, string GradeBasis, string Machine,
    DateTimeOffset Start, DateTimeOffset End, DateTimeOffset PreviousStart, DateTimeOffset PreviousEnd,
    IReadOnlyList<WindowsStabilityMetric> Metrics, IReadOnlyList<WindowsStabilityDaily> Daily,
    IReadOnlyList<WindowsStabilityTimeline> Timeline, IReadOnlyList<WindowsStabilityCrashCluster> CrashClusters,
    IReadOnlyList<WindowsStabilityHeatCell> Heatmap, IReadOnlyList<WindowsStabilityNoise> Noise,
    IReadOnlyList<WindowsStabilityDimension> Dimensions, IReadOnlyList<WindowsStabilityAction> Actions,
    IReadOnlyList<string> BlindSpots, IReadOnlyList<WindowsStabilityCoverage> Coverage)
{
    public string Language { get; init; } = "zh-CN";
    public bool CurrentComplete { get; init; }
    public bool PreviousComplete { get; init; }
    public WeeklyReportSource? WeeklySource { get; init; }
    public WeeklyReportReview? WeeklyReview { get; init; }
}

public sealed record WindowsStabilityReportDocument(string Id, DateTime GeneratedUtc, DateTime StartUtc, DateTime EndUtc,
    bool Monthly, string Markdown, string SourcePath, string PngPath, WindowsStabilityReportData Data)
{
    public string Language => Data.Language;
    public bool RefreshFailed { get; init; }
}

public sealed record WindowsStabilityEvent(string Channel, string Provider, int EventId, long? RecordId, DateTimeOffset Time,
    int Level, string Message, IReadOnlyDictionary<string, string> Fields, IReadOnlyList<string> Properties);
public sealed record WindowsStabilityCollection(DateTimeOffset RequestedStart, DateTimeOffset End,
    IReadOnlyList<WindowsStabilityEvent> Events, IReadOnlyList<WindowsStabilityCoverage> Coverage,
    IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> BlindSpots);
