// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.Settings.UI.Helpers;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Serialization;

/// <summary>
/// Connects native module messages to the shared engine on the existing Kit pipe.
/// </summary>
public static class AiHubIpcBridge
{
    private static readonly Lazy<AiHubIpcHandler> Handler = new(() => new AiHubIpcHandler(AiHubEngine.Current));

    public static bool TryDispatchMessage(string message, Action<string> sendResponse)
    {
        if (!AiHubIpcHandler.IsAiHubMessage(message))
        {
            return false;
        }

        _ = SendResponseAsync(message, sendResponse);
        return true;
    }

    public static async Task<string?> HandleMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!AiHubIpcHandler.IsAiHubMessage(message))
        {
            return null;
        }

        try
        {
            return await Handler.Value.HandleMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            using var document = JsonDocument.Parse(message);
            Guid id = Guid.Empty;
            if (document.RootElement.TryGetProperty("requestId", out var value) && value.ValueKind == JsonValueKind.String)
            {
                _ = value.TryGetGuid(out id);
            }

            return JsonSerializer.Serialize(new AiHubIpcResponse
            {
                RequestId = id,
                ErrorCode = AiErrorCode.InvalidConfiguration,
            }, AiHubJsonContext.Default.AiHubIpcResponse);
        }
    }

    public static void Shutdown()
    {
        if (Handler.IsValueCreated)
        {
            Handler.Value.Dispose();
        }

        AiHubEngine.Shutdown();
    }

    private static async Task SendResponseAsync(string message, Action<string> sendResponse)
    {
        try
        {
            string? response = await HandleMessageAsync(message).ConfigureAwait(false);
            if (response is not null)
            {
                sendResponse(response);
            }
        }
        catch (Exception)
        {
            // Closing the host may close the pipe before a response can be delivered.
        }
    }
}
