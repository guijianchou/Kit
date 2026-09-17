using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace UDPtestLib.Core
{
    public sealed record UdpEndpointSetting(
        string Name,
        [property: JsonPropertyName("Url")] string Endpoint,
        ProbeKind Kind = ProbeKind.StunBinding);

    public static class UdpTargetParser
    {
        public static bool TryParse(
            string? value,
            ProbeKind kind,
            out string host,
            out int port,
            out string canonicalEndpoint,
            out string error)
        {
            host = string.Empty;
            port = 0;
            canonicalEndpoint = string.Empty;
            error = string.Empty;

            string text = value?.Trim() ?? string.Empty;
            string scheme = kind switch
            {
                ProbeKind.StunBinding => "stun",
                ProbeKind.UdpEcho => "echo",
                _ => "udp",
            };
            int examplePort = kind == ProbeKind.UdpEcho ? 7 : 3478;
            if (text.Length == 0)
            {
                error = $"Enter a UDP host and port, for example {scheme}.example.org:{examplePort}.";
                return false;
            }

            string candidate = text.Contains("://", StringComparison.Ordinal)
                ? text
                : $"{scheme}://{text}";
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
                || !uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Use a {kind.ToDisplayName()} host and port such as {scheme}.example.org:{examplePort}.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || (uri.AbsolutePath.Length > 1 && uri.AbsolutePath != "/"))
            {
                error = "Only a UDP host and port are supported; paths, credentials, query strings, and fragments are not allowed.";
                return false;
            }

            if (uri.Port is < 1 or > 65535)
            {
                error = "The UDP port must be between 1 and 65535.";
                return false;
            }

            host = uri.DnsSafeHost;
            string literalHost = host.Trim('[', ']');
            if (IPAddress.TryParse(literalHost, out IPAddress? literalAddress)
                && literalAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                error = "UDP Test currently supports IPv4 UDP endpoints only.";
                return false;
            }

            port = uri.Port;
            canonicalEndpoint = Format(host, port);
            return true;
        }

        public static string Format(string host, int port) =>
            host.Contains(':')
                ? $"[{host}]:{port}"
                : $"{host}:{port}";

        public static string ToDisplayName(this ProbeKind kind) => kind switch
        {
            ProbeKind.StunBinding => "STUN",
            ProbeKind.UdpEcho => "ECHO",
            ProbeKind.Https204 => "HTTP",
            _ => kind.ToString(),
        };
    }
}
