using System.Net;
using System.Net.Sockets;

namespace UDPtestLib.Core
{
    public sealed record TcpEndpointSetting(
        string Name,
        string Endpoint,
        ProbeKind Kind = ProbeKind.Https204);

    public static class TcpTargetParser
    {
        public static bool TryParse(
            string? value,
            out string canonicalEndpoint,
            out int port,
            out string error)
        {
            canonicalEndpoint = string.Empty;
            port = 0;
            error = string.Empty;

            string text = value?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                error = "Enter an HTTPS endpoint, for example https://example.org/generate_204.";
                return false;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
                || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                error = "Only an absolute HTTPS endpoint is supported.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "The HTTPS endpoint cannot contain credentials or a fragment.";
                return false;
            }

            string literalHost = uri.DnsSafeHost.Trim('[', ']');
            if (IPAddress.TryParse(literalHost, out IPAddress? literalAddress)
                && literalAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                error = "UDP Test currently supports IPv4 TCP endpoints only.";
                return false;
            }

            if (uri.Port is < 1 or > 65535)
            {
                error = "The TCP port must be between 1 and 65535.";
                return false;
            }

            canonicalEndpoint = uri.AbsoluteUri;
            port = uri.Port;
            return true;
        }
    }
}
