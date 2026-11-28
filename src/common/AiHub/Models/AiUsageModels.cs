namespace Kit.AiHub.Models;

using System;
using System.Collections.Generic;
using Kit.AiHub.Contract;

/// <summary>Time range filter for persisted Codex usage queries.</summary>
public enum AiUsageTimeRange
{
    AllTime = 0,
    Session = 1,
    Today = 2,
    Last7Days = 3,
    Last30Days = 4,
}

/// <summary>Aggregated Codex usage overview loaded from SQLite storage.</summary>
public sealed record AiUsageOverview
{
    public AiUsageTimeRange Range { get; init; } = AiUsageTimeRange.Today;
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SessionId { get; init; } = string.Empty;
    public string DatabasePath { get; init; } = string.Empty;
    public bool PersistenceAvailable { get; init; } = true;

    public long TaskCount { get; init; }
    public long SucceededTasks { get; init; }
    public long FailedTasks { get; init; }
    public long CancelledTasks { get; init; }
    public long TimeoutTasks { get; init; }
    public long PartialTasks { get; init; }
    public long TaskElapsedMilliseconds { get; init; }

    public long RouteCalls { get; init; }
    public long MainCalls { get; init; }
    public long FallbackCalls { get; init; }
    public long SucceededCalls { get; init; }
    public long FailedCalls { get; init; }
    public long CancelledCalls { get; init; }
    public long TimeoutCalls { get; init; }
    public long NativeRetries { get; init; }
    public long TotalRouteElapsedMilliseconds { get; init; }
    public long TotalQueueMilliseconds { get; init; }

    public long KnownUsageCalls { get; init; }
    public long ReportedInputCalls { get; init; }
    public long ReportedCachedInputCalls { get; init; }
    public long ReportedOutputCalls { get; init; }

    /// <summary>Codex native input_tokens total (includes cached_input_tokens).</summary>
    public long InputTokens { get; init; }

    /// <summary>Codex native cached_input_tokens total.</summary>
    public long CachedInputTokens { get; init; }

    /// <summary>Non-cached input tokens: sum(max(0, input_tokens - coalesce(cached_input_tokens, 0))).</summary>
    public long UncachedInputTokens { get; init; }

    /// <summary>Codex native output_tokens total.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Total tokens across input and output.</summary>
    public long TotalTokens { get; init; }

    /// <summary>Cache hit ratio computed across calls that reported both input_tokens &gt; 0 and cached_input_tokens.</summary>
    public double? CacheHitRate { get; init; }

    public double? AverageRouteElapsedMilliseconds { get; init; }
    public double? AverageQueueMilliseconds { get; init; }

    public int DistinctModelCount { get; init; }
    public long SessionCount { get; init; }
    public DateTimeOffset? FirstEventUtc { get; init; }
    public DateTimeOffset? LastEventUtc { get; init; }

    /// <summary>Attributed Codex models (records that captured an explicit model identifier).</summary>
    public IReadOnlyList<AiModelUsageSummary> Models { get; init; } = Array.Empty<AiModelUsageSummary>();

    /// <summary>Pre-SQLite legacy records imported without a recorded model name, kept separate from real models.</summary>
    public AiModelUsageSummary? LegacyUnattributedModel { get; init; }

    public IReadOnlyList<AiPluginTaskUsageSummary> Tasks { get; init; } = Array.Empty<AiPluginTaskUsageSummary>();
    public IReadOnlyList<AiUsageCallRecord> RecentCalls { get; init; } = Array.Empty<AiUsageCallRecord>();
}

/// <summary>Per-model Codex token and execution breakdown.</summary>
public sealed record AiModelUsageSummary
{
    public string Model { get; init; } = string.Empty;
    public bool IsLegacyUnattributed { get; init; }
    public long Calls { get; init; }
    public long SucceededCalls { get; init; }
    public long FailedCalls { get; init; }
    public long CancelledCalls { get; init; }
    public long TimeoutCalls { get; init; }
    public long MainCalls { get; init; }
    public long FallbackCalls { get; init; }
    public long CodexReportedCalls { get; init; }
    public long NativeRetries { get; init; }
    public long ReportedInputCalls { get; init; }
    public long ReportedCachedInputCalls { get; init; }
    public long ReportedOutputCalls { get; init; }
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long UncachedInputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens { get; init; }
    public double? CacheHitRate { get; init; }
    public double TokenSharePercent { get; init; }
    public long TotalElapsedMilliseconds { get; init; }
    public double? AverageElapsedMilliseconds { get; init; }
    public double? AverageQueueMilliseconds { get; init; }
    public long TotalInputItems { get; init; }
    public int MaxContextWindowTokens { get; init; }
    public string DistinctEfforts { get; init; } = string.Empty;
    public string DistinctApiModes { get; init; } = string.Empty;
    public DateTimeOffset LastUsedUtc { get; init; }
}

/// <summary>Per-plugin and task Codex usage breakdown.</summary>
public sealed record AiPluginTaskUsageSummary
{
    public string PluginId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public long TaskCount { get; init; }
    public long SucceededTasks { get; init; }
    public long FailedTasks { get; init; }
    public long RouteCalls { get; init; }
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens { get; init; }
    public double? AverageElapsedMilliseconds { get; init; }
    public DateTimeOffset LastUsedUtc { get; init; }
}

/// <summary>One persisted Codex route call entry.</summary>
public sealed record AiUsageCallRecord
{
    public long Id { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public Guid RequestId { get; init; }
    public string PluginId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string ConfiguredModel { get; init; } = string.Empty;
    public string ReportedModel { get; init; } = string.Empty;
    public string ModelSource { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public string Effort { get; init; } = string.Empty;
    public string ApiMode { get; init; } = string.Empty;
    public string CodexThreadId { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public AiErrorCode ErrorCode { get; init; }
    public string Reason { get; init; } = string.Empty;
    public int? HttpStatusCode { get; init; }
    public int BatchIndex { get; init; }
    public int TotalBatches { get; init; }
    public int InputItemCount { get; init; }
    public int PromptBytes { get; init; }
    public int OutputCharacters { get; init; }
    public int NativeRetryCount { get; init; }
    public int? NativeRetryLimit { get; init; }
    public int? ExitCode { get; init; }
    public string ProcessFailure { get; init; } = string.Empty;
    public string CancellationSource { get; init; } = string.Empty;
    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? UncachedInputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? TotalTokens { get; init; }
    public int ContextWindowTokens { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public long QueueMilliseconds { get; init; }
}
