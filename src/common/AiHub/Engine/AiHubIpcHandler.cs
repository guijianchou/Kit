namespace Kit.AiHub.Engine;

using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Serialization;

/// <summary>
/// Correlates native requests and cancellations without introducing another pipe or configuration store.
/// </summary>
public sealed partial class AiHubIpcHandler(IAiTaskEngine engine) : IDisposable
{
    public const int MaximumMessageLength = 524_288;
    private readonly IAiTaskEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _requests = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleLock = new();
    private volatile bool _disposed;

    public static bool IsAiHubMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > MaximumMessageLength || !message.Contains("AiHub", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String && target.GetString() == "AiHub";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<string?> HandleMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!IsAiHubMessage(message))
        {
            return null;
        }

        AiHubIpcRequest? request;
        Guid correlationId = Guid.Empty;
        try
        {
            using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String)
            {
                _ = id.TryGetGuid(out correlationId);
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.EnumerateObject().Any(property => !names.Add(property.Name)))
            {
                return SerializeFailure(correlationId, AiErrorCode.InvalidPayload);
            }

            request = JsonSerializer.Deserialize(message, AiHubJsonContext.Default.AiHubIpcRequest);
        }
        catch (JsonException)
        {
            return SerializeFailure(correlationId, AiErrorCode.InvalidPayload);
        }

        if (request is null || request.RequestId == Guid.Empty || request.Version != 1)
        {
            return SerializeFailure(request?.RequestId ?? Guid.Empty, AiErrorCode.InvalidPayload);
        }

        if (_disposed)
        {
            return SerializeFailure(request.RequestId, AiErrorCode.HostUnavailable);
        }

        if (request.Action == "get_status")
        {
            // Reports the cached verdict by default. Pass refresh=true to force a real
            // probe; a status query must not spawn a kernel process on every call.
            AiReadiness readiness = request.RefreshStatus
                ? await _engine.ProbeReadinessAsync(cancellationToken).ConfigureAwait(false)
                : _engine.GetReadiness();

            string response = JsonSerializer.Serialize(new AiHubIpcResponse
            {
                RequestId = request.RequestId,
                IsSuccess = true,
                IsEnabled = _engine.IsEnabled,
                ActiveKernel = _engine.ActiveKernel,
                Readiness = readiness,
            }, AiHubJsonContext.Default.AiHubIpcResponse);
            return response;
        }

        if (request.Action == "self_test")
        {
            // Runs the same probe an operator would use to attribute a failure. Exposed over
            // IPC so any plugin can confirm the shared service before depending on it.
            SelfTestResult outcome = await AiServiceSelfTest
                .RunAsync(_engine, cancellationToken)
                .ConfigureAwait(false);

            string diagnostic = JsonSerializer.Serialize(new AiHubIpcResponse
            {
                RequestId = request.RequestId,
                IsSuccess = outcome.Success,
                ErrorCode = outcome.Success ? AiErrorCode.None : MapSelfTestFailure(outcome),
                IsEnabled = _engine.IsEnabled,
                ActiveKernel = _engine.ActiveKernel,
                UsedModel = outcome.UsedModel,
                UsedRoute = outcome.UsedRoute,
                ElapsedMilliseconds = outcome.Elapsed.TotalMilliseconds,
                Readiness = outcome.Readiness,
                ErrorMessage = outcome.ErrorMessage,
            }, AiHubJsonContext.Default.AiHubIpcResponse);
            return diagnostic;
        }

        if (request.Action == "ai_task_cancel")
        {
            if (_requests.TryGetValue(request.RequestId, out var pending))
            {
                try
                {
                    await pending.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Cancellation raced with normal completion.
                }
            }

            return null;
        }

        if (request.Action != "ai_task_request" || request.Items is null || request.Items.Length > 2_000 || _requests.Count >= 16)
        {
            return SerializeFailure(request.RequestId, AiErrorCode.InvalidPayload);
        }

        CancellationTokenSource linked;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return SerializeFailure(request.RequestId, AiErrorCode.HostUnavailable);
            }

            if (_requests.Count >= 16 || _requests.ContainsKey(request.RequestId))
            {
                return SerializeFailure(request.RequestId, AiErrorCode.InvalidPayload);
            }

            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            _requests.TryAdd(request.RequestId, linked);
        }

        using var requestLifetime = linked;
        try
        {
            AiTaskResult<AiTaskReport> result = await _engine.ExecuteTaskAsync(
                request.PluginId,
                request.TaskId,
                request.Items,
                AiTaskReport.CreateSchema(AiHubJsonContext.Default.JsonElement),
                new AiTaskOptions { Language = request.Language, TimeoutSeconds = request.TimeoutSeconds },
                linked.Token).ConfigureAwait(false);
            string response = JsonSerializer.Serialize(new AiHubIpcResponse
            {
                RequestId = request.RequestId,
                IsSuccess = result.IsSuccess,
                ErrorCode = result.ErrorCode,
                Payload = result.Payload,
                UsedModel = result.UsedModel,
                UsedRoute = result.UsedRoute,
                ElapsedMilliseconds = result.Elapsed.TotalMilliseconds,
                IsEnabled = _engine.IsEnabled,
                ActiveKernel = _engine.ActiveKernel,
            }, AiHubJsonContext.Default.AiHubIpcResponse);
            return response.Length <= 1_048_576 ? response : SerializeFailure(request.RequestId, AiErrorCode.InvalidPayload);
        }
        catch (OperationCanceledException)
        {
            return SerializeFailure(request.RequestId, _disposed ? AiErrorCode.HostUnavailable : AiErrorCode.Cancelled);
        }
        catch (Exception)
        {
            return SerializeFailure(request.RequestId, AiErrorCode.ExecutionFailed);
        }
        finally
        {
            _requests.TryRemove(request.RequestId, out _);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Classifies a self-test failure so a caller can act on it without parsing prose.
    /// </summary>
    private static AiErrorCode MapSelfTestFailure(SelfTestResult outcome) => outcome.ErrorMessage switch
    {
        null => AiErrorCode.ExecutionFailed,
        var message when message.Contains("timed out", StringComparison.OrdinalIgnoreCase) => AiErrorCode.Timeout,
        var message when message.Contains("not available", StringComparison.OrdinalIgnoreCase) => AiErrorCode.HubDisabled,
        var message when message.Contains("instructions were not followed", StringComparison.OrdinalIgnoreCase) => AiErrorCode.InvalidPayload,
        var message when message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) => AiErrorCode.Cancelled,
        _ => AiErrorCode.ExecutionFailed,
    };

    private static string SerializeFailure(Guid requestId, AiErrorCode code) => JsonSerializer.Serialize(
        new AiHubIpcResponse { RequestId = requestId, ErrorCode = code }, AiHubJsonContext.Default.AiHubIpcResponse);
}
