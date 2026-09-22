namespace Kit.AiHub.Contract;

using System;
using System.Text.Json.Serialization;

/// <summary>
/// How usable the shared AI service is right now.
/// </summary>
/// <remarks>
/// A caller needs to know whether an AI call can succeed before it starts work that
/// depends on one. The engine previously exposed only a master switch, so a plugin could
/// spend minutes collecting data and only then discover that no endpoint was configured.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AiReadinessLevel>))]
public enum AiReadinessLevel
{
    /// <summary>The AI service is switched off in Kit settings.</summary>
    Disabled = 0,

    /// <summary>Switched on, but the kernel, endpoint, model or credentials are missing or invalid.</summary>
    NotConfigured,

    /// <summary>Configuration passes validation but no call has been attempted yet.</summary>
    Unverified,

    /// <summary>The most recent probe succeeded; calls can be expected to work.</summary>
    Ready,

    /// <summary>The main route failed but a usable fallback exists.</summary>
    Degraded,
}

/// <summary>
/// Point-in-time report of whether the shared AI service can serve a request.
/// </summary>
/// <param name="Level">Overall usability.</param>
/// <param name="ActiveKernel">Normalized kernel identifier, e.g. <c>codex</c>.</param>
/// <param name="ActiveModel">Model configured on the main route, when one is set.</param>
/// <param name="Detail">Human-readable explanation, or null when self-explanatory.</param>
/// <param name="VerifiedAtUtc">When the result was last confirmed by a real probe.</param>
/// <param name="ProbeLatency">Round-trip time of that probe, when it succeeded.</param>
/// <param name="FromCache">True when the answer came from a cached probe rather than fresh work.</param>
public sealed record AiReadiness(
    AiReadinessLevel Level,
    string ActiveKernel,
    string ActiveModel,
    string? Detail = null,
    DateTimeOffset? VerifiedAtUtc = null,
    TimeSpan? ProbeLatency = null,
    bool FromCache = false)
{
    /// <summary>True when a call can be attempted, including the optimistic unverified case.</summary>
    public bool CanAttempt => Level is AiReadinessLevel.Ready or AiReadinessLevel.Degraded or AiReadinessLevel.Unverified;

    /// <summary>True when the configuration is known to be good.</summary>
    public bool IsVerifiedReady => Level is AiReadinessLevel.Ready;
}
