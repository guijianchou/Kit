using System.Text.Json.Serialization;

namespace UDPtestLib.Storage;

[JsonSerializable(typeof(TargetSettings))]
internal sealed partial class TargetSettingsJsonContext : JsonSerializerContext
{
}
