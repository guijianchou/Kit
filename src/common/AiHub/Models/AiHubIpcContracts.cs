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
}
