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
            config.EnableSubagents = false;
            config.MaxAgentsPerTask = 4;
            config.MaxConcurrentAnalysis = 3;
            store.Save(config);
            var reloadedConfig = new AiHubSettingsStore(root).Load();
            if (reloadedConfig.Targets[0].ApiKey != config.Targets[0].ApiKey
                || File.ReadAllText(store.SettingsFilePath).Contains(config.Targets[0].ApiKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The encrypted configuration round trip failed.");
            }

            if (reloadedConfig.EnableSubagents || reloadedConfig.MaxAgentsPerTask != 4 || reloadedConfig.MaxConcurrentAnalysis != 3)
            {
                throw new InvalidOperationException("The worker preferences round trip failed.");
            }

            ValidateWorkerMetadata();

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

            Console.WriteLine("PASS Native AOT: plugin metadata, JSON schema, DPAPI, worker preferences and metadata, shared worker, sanitization, disabled task, and IPC status.");
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

    private static void ValidateWorkerMetadata()
    {
        Guid runId = Guid.NewGuid();
        Guid agentId = Guid.NewGuid();
        Guid parentAgentId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        var agent = new AiAgentResult
        {
            RunId = runId,
            AgentId = agentId,
            ParentAgentId = parentAgentId,
            AttemptId = attemptId,
            Role = "smoke-worker",
            BatchIndex = 2,
            InputItemCount = 12,
            AttemptCount = 2,
            State = "succeeded",
            IsValidated = true,
            UsedRoute = "main",
        };
        List<AiAgentResult> agents = [agent, agent with
        {
            AgentId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            BatchIndex = 3,
            State = "failed",
            IsValidated = false,
            ErrorCode = AiErrorCode.Timeout,
            UsedRoute = "fallback",
        }];
        var reloadedAgents = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(agents, AiHubJsonContext.Default.ListAiAgentResult),
            AiHubJsonContext.Default.ListAiAgentResult);
        if (reloadedAgents is null || !agents.SequenceEqual(reloadedAgents))
        {
            throw new InvalidOperationException("The worker result metadata round trip failed.");
        }

        var progress = new AiTaskProgress("analysis", "Running worker", 2, 8)
        {
            RunId = runId,
            AgentId = agentId,
            ParentAgentId = parentAgentId,
            AttemptId = attemptId,
            Role = "smoke-worker",
            FailedBatches = 1,
            ActiveBatches = 2,
            QueuedBatches = 3,
            BatchIndex = 4,
            Route = "main",
            Activity = "retrying",
            RetryCount = 1,
            ElapsedMilliseconds = 1500,
            QueueElapsedMilliseconds = 250,
        };
        if (JsonSerializer.Deserialize(
            JsonSerializer.Serialize(progress, AiHubJsonContext.Default.AiTaskProgress),
            AiHubJsonContext.Default.AiTaskProgress) != progress)
        {
            throw new InvalidOperationException("The worker progress metadata round trip failed.");
        }

        List<AiDiagnosticRecord> diagnostics = [new AiDiagnosticRecord
        {
            Kind = "route",
            RequestId = runId,
            AgentId = agentId,
            ParentAgentId = parentAgentId,
            AttemptId = attemptId,
            Role = "smoke-worker",
            PluginId = "Smoke",
            TaskId = "diagnostic",
            Kernel = "codex",
            Route = "main",
            Outcome = "timeout",
            ErrorCode = AiErrorCode.Timeout,
            BatchIndex = 4,
            CompletedBatches = 2,
            TotalBatches = 8,
            ElapsedMilliseconds = 1500,
            Diagnostics = new AiRouteDiagnostics { Phase = "execution", NativeRetryCount = 1, CancellationSource = "route-deadline" },
        }
        ];
        var reloadedDiagnostics = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(diagnostics, AiHubJsonContext.Default.ListAiDiagnosticRecord),
            AiHubJsonContext.Default.ListAiDiagnosticRecord);
        if (reloadedDiagnostics is null || !diagnostics.SequenceEqual(reloadedDiagnostics))
        {
            throw new InvalidOperationException("The worker diagnostic identity round trip failed.");
        }
    }
}

internal sealed record SmokeInput(string State, string ApiKey);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SmokeInput))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext
{
}
