namespace Kit.AiHub.Contract;

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Version-one messages travel over the existing Runner/Settings two-way pipe.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AiHubIpcRequest
{
    public string Target { get; init; } = "AiHub";
    public int Version { get; init; } = 1;
    public required string Action { get; init; }
    public required Guid RequestId { get; init; }
    public string PluginId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public JsonElement[] Items { get; init; } = [];
    public string Language { get; init; } = "en-US";
    public int TimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// Set on a <c>get_status</c> request to force a live probe instead of the cached
    /// verdict. Optional, so existing callers are unaffected.
    /// </summary>
    public bool RefreshStatus { get; init; }
}

public sealed class AiHubIpcResponse
{
    public string Target { get; init; } = "AiHub";
    public string Action { get; init; } = "ai_task_response";
    public int Version { get; init; } = 1;
    public Guid RequestId { get; init; }
    public bool IsSuccess { get; init; }
    public AiErrorCode ErrorCode { get; init; }
    public AiTaskReport? Payload { get; init; }
    public bool IsEnabled { get; init; }
    public string ActiveKernel { get; init; } = string.Empty;
    public string UsedModel { get; init; } = string.Empty;
    public string UsedRoute { get; init; } = string.Empty;
    public double ElapsedMilliseconds { get; init; }

    /// <summary>
    /// Whether a call can be expected to succeed, reported on <c>get_status</c> so a caller
    /// can check before doing work that depends on the AI service.
    /// </summary>
    /// <remarks>
    /// Null on task responses, which already carry their own outcome. IsEnabled and
    /// ActiveKernel are retained for callers written against version one.
    /// </remarks>
    public AiReadiness? Readiness { get; init; }

    /// <summary>
    /// Failure detail for the diagnostic actions. Null on success, and never contains a
    /// configured value such as an API key.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
