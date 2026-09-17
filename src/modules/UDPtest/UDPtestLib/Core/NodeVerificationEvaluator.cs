using System.Net;
using System.Net.Sockets;

namespace UDPtestLib.Core
{
    public readonly record struct StunEgressEvidence(
        bool IsEnabled,
        string? EgressIpv4);

    public static class NodeVerificationEvaluator
    {
        public const int MinimumEnabledStunLines = 3;

        public static bool IsVerified(
            string? publicIpv4,
            string? location,
            IEnumerable<StunEgressEvidence> evidence)
        {
            ArgumentNullException.ThrowIfNull(evidence);

            if (!TryParseIpv4(publicIpv4, out IPAddress? publicAddress)
                || !IsForeignLocation(location))
            {
                return false;
            }

            StunEgressEvidence[] enabled = evidence
                .Where(static item => item.IsEnabled)
                .ToArray();
            if (enabled.Length < MinimumEnabledStunLines)
            {
                return false;
            }

            int requiredMatches = (enabled.Length * 2 + 2) / 3;
            int matchingEgresses = enabled.Count(item =>
                TryParseIpv4(item.EgressIpv4, out IPAddress? egressAddress)
                && egressAddress?.Equals(publicAddress) == true);
            return matchingEgresses >= requiredMatches;
        }

        private static bool IsForeignLocation(string? location)
        {
            string normalized = location?.Trim() ?? string.Empty;
            return normalized.Length == 2
                && normalized.All(static character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                && !normalized.Equals("CN", StringComparison.OrdinalIgnoreCase)
                && !normalized.Equals("XX", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseIpv4(string? value, out IPAddress? address)
        {
            bool parsed = IPAddress.TryParse(value, out address)
                && address is { AddressFamily: AddressFamily.InterNetwork };
            if (!parsed)
            {
                address = null;
            }

            return parsed;
        }
    }
}
