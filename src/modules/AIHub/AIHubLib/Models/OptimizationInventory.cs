using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kit.AIHubLib.Models;

/// <summary>Bounded metadata inventory. These records never authorize a filesystem action.</summary>
public sealed record OptimizationInventoryReport
{
    public DateTimeOffset CompletedUtc { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public IReadOnlyList<OptimizationInventoryScope> Scopes { get; init; } = Array.Empty<OptimizationInventoryScope>();
    public int ExaminedEntries { get; init; }
    public bool IsPartial { get; init; }

    public IReadOnlyDictionary<string, long> ChangesSince(OptimizationInventoryReport? previous)
    {
        var changes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (previous is null || CompletedUtc <= previous.CompletedUtc)
        {
            return changes;
        }

        static HashSet<string> NestedRoots(OptimizationInventoryReport report, string root)
        {
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return report.Scopes.Select(scope => scope.RootPath)
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var baseline = previous.Scopes.ToDictionary(scope => scope.RootPath, StringComparer.OrdinalIgnoreCase);
        foreach (var scope in Scopes)
        {
            if (scope.State == "complete" && scope.ObservedBytes is long current && current >= 0 &&
                baseline.TryGetValue(scope.RootPath, out var old) && old.State == "complete" && old.ObservedBytes is long before && before >= 0 &&
                NestedRoots(this, scope.RootPath).SetEquals(NestedRoots(previous, scope.RootPath)))
            {
                changes.Add(scope.RootPath, current - before);
            }
        }

        return changes;
    }
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
