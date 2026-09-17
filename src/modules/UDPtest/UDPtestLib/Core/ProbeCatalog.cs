namespace UDPtestLib.Core
{
    public static class ProbeCatalog
    {
        public static IReadOnlyList<ProbeLineDefinition> CreateDefaults() =>
        [
            new("tcp_1", "Google", ProbeProtocol.Tcp, ProbeKind.Https204, "https://www.google.com/generate_204", 443, ExpectedHttpStatus: 204, TargetLabel: "Google"),
            new("tcp_2", "Cloudflare", ProbeProtocol.Tcp, ProbeKind.Https204, "https://cp.cloudflare.com/generate_204", 443, ExpectedHttpStatus: 204, TargetLabel: "Cloudflare"),
            new("udp_1", "Singapore", ProbeProtocol.Udp, ProbeKind.UdpEcho, "150.242.146.124", 7, TargetLabel: "Singapore"),
            new("udp_2", "Cloudflare", ProbeProtocol.Udp, ProbeKind.StunBinding, "stun.cloudflare.com", 3478, TargetLabel: "Cloudflare"),
            new("udp_3", "Twilio", ProbeProtocol.Udp, ProbeKind.StunBinding, "global.stun.twilio.com", 3478, TargetLabel: "Twilio"),
            new("udp_4", "NextCloud", ProbeProtocol.Udp, ProbeKind.StunBinding, "stun.nextcloud.com", 3478, TargetLabel: "NextCloud"),
        ];

        public static IReadOnlyList<TcpEndpointSetting> CreateDefaultTcpSettings() =>
            CreateDefaults()
                .Where(static line => line.Protocol == ProbeProtocol.Tcp)
                .Select(static line => new TcpEndpointSetting(
                    line.DisplayTarget,
                    line.Target,
                    line.Kind))
                .ToArray();

        public static IReadOnlyList<UdpEndpointSetting> CreateDefaultUdpSettings() =>
            CreateDefaults()
                .Where(static line => line.Protocol == ProbeProtocol.Udp)
                .Select(static line => new UdpEndpointSetting(
                    line.DisplayTarget,
                    UdpTargetParser.Format(line.Target, line.Port),
                    line.Kind))
                .ToArray();
    }
}
