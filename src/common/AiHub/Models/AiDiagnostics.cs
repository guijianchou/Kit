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
}

/// <summary>A completed task or route. Task records do not contribute route/token totals.</summary>
public sealed record AiDiagnosticRecord
{
    public string Kind { get; init; } = "task";
    public Guid RequestId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string PluginId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Kernel { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public AiErrorCode ErrorCode { get; init; }
    public int CompletedBatches { get; init; }
    public int TotalBatches { get; init; }
    public long ElapsedMilliseconds { get; init; }
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
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public IReadOnlyList<AiDiagnosticRecord> RecentRecords { get; init; } = Array.Empty<AiDiagnosticRecord>();
}
