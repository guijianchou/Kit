using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.Security;
using Kit.AiHub.Serialization;
using Kit.AiHub.Storage;

internal static class Program
{
    private static async Task<int> Main()
    {
        if (RuntimeFeature.IsDynamicCodeSupported || JsonSerializer.IsReflectionEnabledByDefault)
        {
            Console.Error.WriteLine("FAIL: Run the published Native AOT executable with reflection serialization disabled.");
            return 1;
        }

        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KitAiHubAotSmoke"));
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new AiHubSettingsStore(root);
            var config = AiHubConfig.CreateDefault();
            config.Targets[0].ApiKey = "isolated-aot-fixture-secret";
            store.Save(config);
            if (new AiHubSettingsStore(root).Load().Targets[0].ApiKey != config.Targets[0].ApiKey
                || File.ReadAllText(store.SettingsFilePath).Contains(config.Targets[0].ApiKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The encrypted configuration round trip failed.");
            }

            using var worker = AiHubEngine.Create(root, Path.Combine(root, "modules"));
            var schema = AiTaskReport.CreateSchema(SmokeJsonContext.Default.SmokeInput);
            _ = schema.OutputTypeInfo.GetJsonSchemaAsNode().ToJsonString();
            var result = await worker.ExecuteTaskAsync("Smoke", "diagnostic", new[] { new SmokeInput("running", "redact-fixture") }, schema);
            if (result.ErrorCode != AiErrorCode.HubDisabled)
            {
                throw new InvalidOperationException("The disabled worker did not short circuit.");
            }

            JsonElement sanitized = new DataSanitizer().SanitizeJson(JsonSerializer.SerializeToElement(new SmokeInput("running", "redact-fixture"), SmokeJsonContext.Default.SmokeInput));
            if (sanitized.GetRawText().Contains("redact-fixture", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Input sanitization failed.");
            }

            using var handler = new AiHubIpcHandler(worker);
            Guid id = Guid.NewGuid();
            string request = JsonSerializer.Serialize(new AiHubIpcRequest { RequestId = id, Action = "get_status" }, AiHubJsonContext.Default.AiHubIpcRequest);
            var response = JsonSerializer.Deserialize((await handler.HandleMessageAsync(request))!, AiHubJsonContext.Default.AiHubIpcResponse);
            if (response is not { IsSuccess: true, IsEnabled: false } || response.RequestId != id)
            {
                throw new InvalidOperationException("The IPC serialization round trip failed.");
            }

            Console.WriteLine("PASS Native AOT: plugin metadata, JSON schema, DPAPI, shared worker, sanitization, disabled task, and IPC status.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL Native AOT smoke: " + ex.GetType().Name);
            return 1;
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) == parent && Guid.TryParseExact(Path.GetFileName(root), "N", out _))
            {
                Directory.Delete(root, true);
            }
        }
    }
}

internal sealed record SmokeInput(string State, string ApiKey);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SmokeInput))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext
{
}
