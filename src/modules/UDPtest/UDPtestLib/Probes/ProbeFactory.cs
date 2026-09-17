using UDPtestLib.Core;

namespace UDPtestLib.Probes
{
    public static class ProbeFactory
    {
        public static IProbe Create(ProbeLineDefinition line)
        {
            ArgumentNullException.ThrowIfNull(line);
            return (line.Protocol, line.Kind) switch
            {
                (ProbeProtocol.Tcp, ProbeKind.Https204) => new TcpHttpsProbe(line),
                (ProbeProtocol.Udp, ProbeKind.StunBinding) => new StunProbe(line),
                (ProbeProtocol.Udp, ProbeKind.UdpEcho) => new UdpEchoProbe(line),
                _ => throw new NotSupportedException(
                    $"Probe kind '{line.Kind}' is not valid for transport '{line.Protocol}'."),
            };
        }
    }
}
