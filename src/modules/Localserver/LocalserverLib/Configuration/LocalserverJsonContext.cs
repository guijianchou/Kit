using System.Text.Json.Serialization;
using LocalServerHub.Windows;

namespace LocalServerHub.Core.Configuration;

// Persisted properties with initializer defaults use ordinary setters. The source
// generator synthesizes constructor arguments for init-only properties and fills
// omitted JSON members with default(T), bypassing those initializer defaults.
[JsonSerializable(typeof(ServiceCatalog))]
[JsonSerializable(typeof(HubSettings))]
[JsonSerializable(typeof(ServiceOwnershipRecord))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LocalserverJsonContext : JsonSerializerContext
{
}
