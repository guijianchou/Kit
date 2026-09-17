using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace UDPtestLib.Probes
{
    public sealed class UdpEndpointResolver
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

        private IPEndPoint? _current;
        private long _resolvedTimestamp;

        public async ValueTask<UdpEndpointResolution> ResolveAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            if (_current is not null
                && _current.Port == port
                && Stopwatch.GetElapsedTime(_resolvedTimestamp) < RefreshInterval)
            {
                return new UdpEndpointResolution(_current, false);
            }

            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException) when (_current is not null && _current.Port == port)
            {
                _resolvedTimestamp = Stopwatch.GetTimestamp();
                return new UdpEndpointResolution(_current, false);
            }

            IPAddress? address = _current is not null
                && _current.Port == port
                && addresses.Any(candidate => candidate.Equals(_current.Address))
                    ? _current.Address
                    : addresses.FirstOrDefault(static candidate =>
                        candidate.AddressFamily == AddressFamily.InterNetwork);
            if (address is null)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            IPEndPoint resolved = new(address, port);
            bool changed = _current is null || !_current.Equals(resolved);
            _current = resolved;
            _resolvedTimestamp = Stopwatch.GetTimestamp();
            return new UdpEndpointResolution(resolved, changed);
        }

        public void Reset()
        {
            _current = null;
            _resolvedTimestamp = 0;
        }
    }

    public readonly record struct UdpEndpointResolution(IPEndPoint EndPoint, bool Changed);
}
