namespace Kit.AiHub.Serialization;

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;

/// <summary>
/// Source-generated JSON context ensuring high performance and Native AOT compatibility.
/// Replaces all runtime reflection serialization from the legacy .NET 8 codebase.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AiHubConfig))]
[JsonSerializable(typeof(AiTargetSettings))]
[JsonSerializable(typeof(List<AiTargetSettings>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(AiTaskReport))]
[JsonSerializable(typeof(AiHubIpcRequest))]
[JsonSerializable(typeof(AiHubIpcResponse))]
[JsonSerializable(typeof(SelfTestInput))]
public sealed partial class AiHubJsonContext : JsonSerializerContext
{
}
