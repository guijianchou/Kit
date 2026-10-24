// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ManagedCommon.ServiceHost
{
    public sealed record KitPipeRequest
    {
        public int Version { get; init; } = 1;
        public string Command { get; init; } = string.Empty;
        public string? PayloadJson { get; init; }
    }

    public sealed record KitPipeResponse
    {
        public int Version { get; init; } = 1;
        public bool Success { get; init; } = true;
        public string? DataJson { get; init; }
        public KitPipeError? Error { get; init; }

        public static KitPipeResponse Ok(string? dataJson = null)
        {
            return new KitPipeResponse { Version = 1, Success = true, DataJson = dataJson, Error = null };
        }

        public static KitPipeResponse Fail(string code, string message)
        {
            return new KitPipeResponse { Version = 1, Success = false, DataJson = null, Error = new KitPipeError { Code = code, Message = message } };
        }
    }

    public sealed record KitPipeError
    {
        public string Code { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }

    [JsonSerializable(typeof(KitPipeRequest))]
    [JsonSerializable(typeof(KitPipeResponse))]
    [JsonSerializable(typeof(KitPipeError))]
    internal sealed partial class ServicePipeJsonContext : JsonSerializerContext
    {
    }

    public static class KitServicePipeUtils
    {
        public static string GetPipeName(string moduleKey)
        {
            using var proc = Process.GetCurrentProcess();
            return $"Kit_{moduleKey}_Service_{proc.SessionId}";
        }

        public static string GetPipeName(string moduleKey, int sessionId)
        {
            return $"Kit_{moduleKey}_Service_{sessionId}";
        }
    }

    public sealed class KitServicePipeServer : IAsyncDisposable
    {
        private readonly string _pipeName;
        private readonly Func<KitPipeRequest, CancellationToken, Task<KitPipeResponse>> _handler;
        private readonly CancellationTokenSource _cts = new();
        private Task? _listenerTask;

        public KitServicePipeServer(string moduleKey, Func<KitPipeRequest, CancellationToken, Task<KitPipeResponse>> handler)
        {
            _pipeName = KitServicePipeUtils.GetPipeName(moduleKey);
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Start()
        {
            _listenerTask = Task.Run(ListenLoopAsync);
        }

        private async Task ListenLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    _ = HandleConnectionAsync(server, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    Logger.LogWarning($"[KitServicePipeServer] Error accepting pipe connection: {ex.Message}");
                    try
                    {
                        await Task.Delay(200, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true))
            using (var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
            {
                try
                {
                    while (pipe.IsConnected && !ct.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (line == null)
                        {
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        KitPipeResponse response;
                        try
                        {
                            var request = JsonSerializer.Deserialize(line, ServicePipeJsonContext.Default.KitPipeRequest);
                            if (request == null)
                            {
                                response = KitPipeResponse.Fail("INVALID_REQUEST", "Failed to deserialize request");
                            }
                            else
                            {
                                response = await _handler(request, ct).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            response = KitPipeResponse.Fail("REQUEST_ERROR", ex.Message);
                        }

                        string responseJson = JsonSerializer.Serialize(response, ServicePipeJsonContext.Default.KitPipeResponse);
                        await writer.WriteLineAsync(responseJson.AsMemory(), ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogDebug($"[KitServicePipeServer] Pipe client disconnected: {ex.Message}");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_listenerTask != null)
            {
                try
                {
                    await _listenerTask.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _cts.Dispose();
        }
    }

    public sealed class KitServicePipeClient
    {
        private readonly string _pipeName;
        private readonly string _expectedExeName;
        private readonly TimeSpan _timeout;

        public KitServicePipeClient(string moduleKey, string expectedExeName, TimeSpan? timeout = null)
        {
            _pipeName = KitServicePipeUtils.GetPipeName(moduleKey);
            _expectedExeName = expectedExeName;
            _timeout = timeout ?? TimeSpan.FromSeconds(3);
        }

        public async Task<KitPipeResponse> SendCommandAsync(string command, string? payloadJson = null, CancellationToken ct = default)
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(_timeout);

            await client.ConnectAsync(linkedCts.Token).ConfigureAwait(false);

            if (!PipeServerIdentity.IsTrustedServer(client, _expectedExeName))
            {
                throw new InvalidOperationException($"Untrusted named pipe server: failed identity check for {_expectedExeName}");
            }

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            var request = new KitPipeRequest
            {
                Version = 1,
                Command = command,
                PayloadJson = payloadJson,
            };

            string requestJson = JsonSerializer.Serialize(request, ServicePipeJsonContext.Default.KitPipeRequest);
            await writer.WriteLineAsync(requestJson.AsMemory(), linkedCts.Token).ConfigureAwait(false);

            string? responseLine = await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                return KitPipeResponse.Fail("EMPTY_RESPONSE", "Received empty response from service.");
            }

            var response = JsonSerializer.Deserialize(responseLine, ServicePipeJsonContext.Default.KitPipeResponse);
            return response ?? KitPipeResponse.Fail("DESERIALIZE_ERROR", "Failed to deserialize service response.");
        }
    }
}
