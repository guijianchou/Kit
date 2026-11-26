namespace Kit.AiHub.Models;

using System;
using System.Collections.Generic;
using Kit.AiHub.Contract;

/// <summary>Safe metadata reported by a native Codex or Pi route; no model content or credentials.</summary>
public sealed record AiRouteDiagnostics
{
    public string Phase { get; init; } = string.Empty;
    public int? HttpStatusCode { get; init; }
    public string Reason { get; init; } = string.Empty;
    public int NativeRetryCount { get; init; }
    public int? NativeRetryLimit { get; init; }
    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? CacheWriteTokens { get; init; }
    public long? OutputTokens { get; init; }
    public int ContextWindowTokens { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public int? ExitCode { get; init; }
    public string ProcessFailure { get; init; } = string.Empty;
    public string CancellationSource { get; init; } = string.Empty;
    public string CodexThreadId { get; init; } = string.Empty;
    public string ReportedModel { get; init; } = string.Empty;
}

/// <summary>A completed task or route. Task records do not contribute route/token totals.</summary>
public sealed record AiDiagnosticRecord
{
    public string Kind { get; init; } = "task";
    public Guid RequestId { get; init; }
    public Guid AgentId { get; init; }
    public Guid ParentAgentId { get; init; }
    public Guid AttemptId { get; init; }
    public string Role { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string PluginId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Kernel { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string ConfiguredModel { get; init; } = string.Empty;
    public string ReportedModel { get; init; } = string.Empty;
    public string ModelSource { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public string Effort { get; init; } = string.Empty;
    public string ApiMode { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public AiErrorCode ErrorCode { get; init; }
    /// <summary>One-based route batch index; zero for a task summary or older records.</summary>
    public int BatchIndex { get; init; }
    public int CompletedBatches { get; init; }
    public int TotalBatches { get; init; }
    public int InputItemCount { get; init; }
    public int PromptBytes { get; init; }
    public int OutputCharacters { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public long QueueMilliseconds { get; init; }
    public AiRouteDiagnostics? Diagnostics { get; init; }
}

/// <summary>Statistics for this engine session. Missing native usage is not treated as reported zero.</summary>
public sealed record AiDiagnosticsSnapshot
{
    public DateTimeOffset SessionStartedUtc { get; init; }
    public DateTimeOffset CapturedUtc { get; init; }
    public bool PersistenceEnabled { get; init; }
    public long PersistenceFailures { get; init; }
    public long RequestCount { get; init; }
    public long Succeeded { get; init; }
    public long Failed { get; init; }
    public long Cancelled { get; init; }
    public long Timeout { get; init; }
    public long Partial { get; init; }
    public long RouteAttempts { get; init; }
    public long MainAttempts { get; init; }
    public long FallbackAttempts { get; init; }
    public long NativeRetryCount { get; init; }
    public long KnownUsageRoutes { get; init; }
    public long ReportedInputRoutes { get; init; }
    public long ReportedCachedInputRoutes { get; init; }
    public long ReportedCacheWriteRoutes { get; init; }
    public long ReportedOutputRoutes { get; init; }
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public IReadOnlyList<AiDiagnosticRecord> RecentRecords { get; init; } = Array.Empty<AiDiagnosticRecord>();
}
