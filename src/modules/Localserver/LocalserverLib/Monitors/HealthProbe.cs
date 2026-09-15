using System.Net;
using System.Net.Sockets;
using LocalServerHub.Core.Commands;
using LocalServerHub.Core.Configuration;
using LocalServerHub.Core.Models;

namespace LocalServerHub.Windows;

public static class HealthProbe
{
    public static async Task<bool> CheckAsync(ServiceDefinition definition, int? port,
        SecretStore? secrets, HttpClient client, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (definition.Health.Kind == HealthCheckKind.Tcp && port is { } tcpPort)
            {
                foreach (IPAddress address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
                {
                    if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) continue;
                    try
                    {
                        using TcpClient socket = new(address.AddressFamily);
                        await socket.ConnectAsync(address, tcpPort, timeout.Token).ConfigureAwait(false);
                        return true;
                    }
                    catch (SocketException) { }
                }
                return false;
            }
            if (definition.Health.Kind != HealthCheckKind.Http) return false;

            string url = VariableExpander.ForService(definition, port, secrets).ExpandRequired(definition.Health.Url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")) return false;
            using HttpResponseMessage response = await client.GetAsync(uri,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return definition.Health.ExpectStatus.Contains((int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or InvalidOperationException or ArgumentException)
        {
            // URLs may contain secrets; transport exception messages must not escape to logs.
            return false;
        }
    }
}
