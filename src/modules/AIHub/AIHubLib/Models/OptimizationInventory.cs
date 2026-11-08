using System;
using System.Collections.Generic;

namespace Kit.AIHubLib.Models;

/// <summary>Bounded metadata inventory. These records never authorize a filesystem action.</summary>
public sealed record OptimizationInventoryReport
{
    public DateTimeOffset CompletedUtc { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public IReadOnlyList<OptimizationInventoryScope> Scopes { get; init; } = Array.Empty<OptimizationInventoryScope>();
    public int ExaminedEntries { get; init; }
    public bool IsPartial { get; init; }
}

/// <summary>Observed space in one location, kept separate from reviewed cleanup candidates.</summary>
public sealed record OptimizationInventoryScope
{
    public string Id { get; init; } = string.Empty;
    public string FamilyId { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string NameZh { get; init; } = string.Empty;
    public string RootPath { get; init; } = string.Empty;
    public string Disposition { get; init; } = "analysis-only";
    public string State { get; init; } = "missing";
    public long? ObservedBytes { get; init; }
    public int FileCount { get; init; }
    public long RecentBytes { get; init; }
    public long OlderBytes { get; init; }
    public int SkippedEntries { get; init; }
    public int AccessFailures { get; init; }
    public string ReasonEn { get; init; } = string.Empty;
    public string ReasonZh { get; init; } = string.Empty;
    public string GuidanceEn { get; init; } = string.Empty;
    public string GuidanceZh { get; init; } = string.Empty;
    public IReadOnlyList<OptimizationInventoryFile> TopFiles { get; init; } = Array.Empty<OptimizationInventoryFile>();
}

public sealed record OptimizationInventoryFile
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long SizeInBytes { get; init; }
    public DateTimeOffset LastModifiedUtc { get; init; }
}
