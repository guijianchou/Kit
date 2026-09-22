namespace Kit.AiHub.Contract;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Status codes returned by AI Hub task execution.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AiErrorCode>))]
public enum AiErrorCode
{
    None = 0,
    HubDisabled,
    KernelNotInstalled,
    EndpointFailed,
    InvalidPayload,
    PolicyViolation,
    Timeout,
    Cancelled,
    ExecutionFailed,
    InvalidConfiguration,
    HostUnavailable
}

/// <summary>
/// Event arguments fired when AI Hub configuration or master switch state changes.
/// </summary>
public sealed class AiHubStateChangedEventArgs : EventArgs
{
    public bool IsEnabled { get; init; }
    public string ActiveKernel { get; init; } = string.Empty;
    public string ActiveModel { get; init; } = string.Empty;
}

/// <summary>
/// Execution options passed by plugins to the AI Hub engine.
/// </summary>
public sealed class AiTaskOptions
{
    /// <summary>
    /// Kit UI language ("en-US" or "zh-CN") for prompt hints.
    /// </summary>
    public string Language { get; set; } = "en-US";

    /// <summary>
    /// Optional progress callback for batching and analysis stages.
    /// </summary>
    public IProgress<AiTaskProgress>? Progress { get; set; }

    /// <summary>
    /// Custom timeout override in seconds (default 600s).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 600;
}

/// <summary>
/// Standard execution progress reported back to the plugin.
/// </summary>
public sealed record AiTaskProgress(string Stage, string StatusMessage, int CompletedBatches, int TotalBatches);

/// <summary>
/// Result envelope returned by the AI Hub engine.
/// </summary>
public sealed class AiTaskResult<TOutput> where TOutput : class
{
    public bool IsSuccess { get; init; }
    public AiErrorCode ErrorCode { get; init; } = AiErrorCode.None;
    public string? ErrorMessage { get; init; }
    public TOutput? Payload { get; init; }
    public string UsedModel { get; init; } = string.Empty;
    public string UsedRoute { get; init; } = string.Empty;
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Static factory methods for creating AiTaskResult instances.
/// </summary>
public static class AiTaskResult
{
    public static AiTaskResult<TOutput> Success<TOutput>(TOutput payload, string model, string route, TimeSpan elapsed) where TOutput : class =>
        new()
        {
            IsSuccess = true,
            Payload = payload,
            UsedModel = model,
            UsedRoute = route,
            Elapsed = elapsed
        };

    public static AiTaskResult<TOutput> Failed<TOutput>(AiErrorCode code, string message, TimeSpan elapsed = default) where TOutput : class =>
        new()
        {
            IsSuccess = false,
            ErrorCode = code,
            ErrorMessage = message,
            Elapsed = elapsed
        };
}

/// <summary>
/// Plugin-owned serialization metadata and output validation. No reflection fallback is used.
/// </summary>
public sealed class AiTaskSchema<TInput, TOutput> where TOutput : class
{
    public required JsonTypeInfo<TInput> InputTypeInfo { get; init; }

    public required JsonTypeInfo<TOutput> OutputTypeInfo { get; init; }

    /// <summary>
    /// Validates required fields and task semantics against the opaque IDs in the current batch.
    /// </summary>
    public required Func<TOutput, IReadOnlySet<string>, bool> ValidateOutput { get; init; }

    /// <summary>
    /// Combines validated batches. Required when the task exceeds one batch.
    /// </summary>
    public Func<IReadOnlyList<TOutput>, TOutput>? MergeBatches { get; init; }

    /// <summary>
    /// Permitted advisory actions. The engine never executes a suggested action.
    /// </summary>
    public IReadOnlySet<string> AllowedActions { get; init; } = new HashSet<string>(StringComparer.Ordinal) { "skip" };
}

/// <summary>
/// Core engine contract implemented by Kit.AiHub and consumed by plugins.
/// </summary>
public interface IAiTaskEngine : IDisposable
{
    /// <summary>
    /// Indicates whether the AI Hub subsystem is enabled in Kit Settings.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets the current normalized active kernel identifier (e.g., "codex" or "pi").
    /// </summary>
    string ActiveKernel { get; }

    /// <summary>
    /// Event raised when AI Hub is toggled on/off or when the active kernel changes.
    /// </summary>
    event EventHandler<AiHubStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Reports whether a call can be expected to succeed, without starting a kernel.
    /// </summary>
    /// <remarks>
    /// Answers from a cached probe when one is still fresh, so callers can ask cheaply on
    /// every scan. Use <see cref="ProbeReadinessAsync"/> to force a real check.
    /// </remarks>
    AiReadiness GetReadiness();

    /// <summary>
    /// Verifies the configured route by issuing a minimal probe request.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="AiReadinessLevel.Disabled"/> without touching the kernel when the
    /// AI service is switched off: an unused service must not wake a CLI process or open a
    /// network connection.
    /// </remarks>
    Task<AiReadiness> ProbeReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a sandboxed AI analysis task guided by the specified task chain.
    /// </summary>
    /// <typeparam name="TInput">Type of raw input items.</typeparam>
    /// <typeparam name="TOutput">Type of validated structured output.</typeparam>
    /// <param name="pluginId">Unique identifier of the calling plugin.</param>
    /// <param name="taskId">Name of the sub-folder under Chains containing AGENTS.md.</param>
    /// <param name="items">List of input records to analyze or organize.</param>
    /// <param name="schema">Source-generated metadata and plugin-specific validation.</param>
    /// <param name="options">Execution and language options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Strongly typed and security-audited task result.</returns>
    Task<AiTaskResult<TOutput>> ExecuteTaskAsync<TInput, TOutput>(
        string pluginId,
        string taskId,
        IReadOnlyList<TInput> items,
        AiTaskSchema<TInput, TOutput> schema,
        AiTaskOptions? options = null,
        CancellationToken cancellationToken = default)
        where TOutput : class;
}
