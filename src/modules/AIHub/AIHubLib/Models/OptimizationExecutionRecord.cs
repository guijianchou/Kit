using System;
using System.Collections.Generic;

namespace Kit.AIHubLib.Models;

/// <summary>Bounded local evidence of actions already attempted. It is not a rollback plan.</summary>
public sealed class OptimizationExecutionRecord
{
    public Guid RunId { get; set; } = Guid.NewGuid();
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public string State { get; set; } = "running";
    public int TotalCount { get; set; }
    public int ProcessedCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public int MovedCount { get; set; }
    public int DeletedCount { get; set; }
    public long FreedBytes { get; set; }
    public int RemainingCount => Math.Max(0, TotalCount - ProcessedCount);
    public bool DetailsTruncated => ProcessedCount > Items.Count;
    public string? FailureReason { get; set; }
    public string? HistoryError { get; set; }
    public List<OptimizationExecutionItemRecord> Items { get; set; } = new();
}

public sealed class OptimizationExecutionItemRecord
{
    public string ItemId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string? DestinationPath { get; set; }
    public string Action { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    /// <summary>Snapshot bytes affected by a successful action; zero for a failed action.</summary>
    public long Bytes { get; set; }
    public string? Reason { get; set; }
    public DateTime CompletedUtc { get; set; }
}

public sealed record OptimizationExecutionFailure(TempFileInfo Item, string Reason);

public sealed record OptimizationExecutionProgress(
    int ProcessedCount, int TotalCount, int SucceededCount, int FailedCount, string CurrentFileName);

/// <summary>Full in-memory identities are kept separate from the bounded persisted details.</summary>
public sealed record OptimizationExecutionResult(
    OptimizationExecutionRecord Record,
    IReadOnlyList<TempFileInfo> SucceededItems,
    IReadOnlyList<OptimizationExecutionFailure> FailedItems);
