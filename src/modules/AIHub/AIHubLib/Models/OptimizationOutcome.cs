using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kit.AIHubLib.Models;

/// <summary>
/// Outcome of a batch optimization run: per-item results plus the aggregate counts the
/// dashboard reports. Failures keep their reason so the UI can explain what happened
/// instead of collapsing everything into an anonymous "failed" count.
/// </summary>
public sealed class OptimizationOutcome
{
    private readonly List<OptimizationItemResult> _results = new();

    public IReadOnlyList<OptimizationItemResult> Results => _results;

    public int MovedCount => _results.Count(r => r.Succeeded && r.Action == "move");

    public int RecycledCount => _results.Count(r => r.Succeeded && r.Action == "delete");

    public int FailedCount => _results.Count(r => !r.Succeeded);

    public int TotalCount => _results.Count;

    public bool HasFailures => FailedCount > 0;

    public void Add(OptimizationItemResult result) => _results.Add(result);

    /// <summary>Human readable failure summary, or null when everything succeeded.</summary>
    public string? DescribeFailures(bool chinese, int maxItems = 3)
    {
        var failures = _results.Where(r => !r.Succeeded).ToList();
        if (failures.Count == 0)
        {
            return null;
        }

        IEnumerable<string> details = failures
            .Take(maxItems)
            .Select(r => string.IsNullOrWhiteSpace(r.Reason)
                ? r.FileName
                : $"{r.FileName}: {r.Reason}");

        string joined = string.Join("; ", details);
        int remaining = failures.Count - Math.Min(failures.Count, maxItems);
        string suffix = remaining > 0
            ? (chinese ? $" 等 {failures.Count} 项" : $" and {remaining} more")
            : string.Empty;

        return joined + suffix;
    }

    /// <summary>Bilingual one-line summary of the run.</summary>
    public string Describe(bool chinese)
    {
        return chinese
            ? $"优化完成：已整理 {MovedCount} 个文件，已将 {RecycledCount} 个文件移至回收站，{FailedCount} 个失败"
            : $"Optimization completed: {MovedCount} file(s) organized, {RecycledCount} file(s) recycled, {FailedCount} failed";
    }
}

/// <summary>Result of attempting one optimization item.</summary>
public sealed class OptimizationItemResult
{
    public required string FileName { get; init; }

    public required string Action { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>Failure reason, or null on success.</summary>
    public string? Reason { get; init; }

    public static OptimizationItemResult Success(string fileName, string action) =>
        new() { FileName = fileName, Action = action, Succeeded = true };

    public static OptimizationItemResult Failure(string fileName, string action, string? reason) =>
        new() { FileName = fileName, Action = action, Succeeded = false, Reason = reason };
}
