using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using UDPtestLib.Core;

namespace UDPtestLib.Probes
{
    public sealed class UdpEchoProbe : IProbe
    {
        private const uint PayloadMagic = 0x4E4D4531; // "NME1"
        private const int PayloadLength = 32;
        private readonly ProbeLineDefinition _line;
        private readonly UdpEndpointResolver _endpointResolver = new();
        private Socket? _socket;
        private string? _lastRemoteIp;

        public UdpEchoProbe(ProbeLineDefinition line)
        {
            _line = line;
            if (IPAddress.TryParse(line.Target, out IPAddress? directIp) && directIp.AddressFamily == AddressFamily.InterNetwork)
            {
                _lastRemoteIp = directIp.ToString();
            }
        }

        public async ValueTask<ProbeResult> ExecuteAsync(
            long sequenceNumber,
            DateTimeOffset startedAtUtc,
            long sessionElapsedMilliseconds,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource = new(timeout);
            using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            Stopwatch stopwatch = new();
            bool sawSourceMismatch = false;
            bool sawPayloadMismatch = false;
            try
            {
                UdpEndpointResolution resolution = await _endpointResolver
                    .ResolveAsync(_line.Target, _line.Port, linkedSource.Token)
                    .ConfigureAwait(false);
                if (resolution.Changed)
                {
                    ResetSocket();
                }

                IPEndPoint endpoint = resolution.EndPoint;
                _lastRemoteIp = endpoint.Address.ToString();
                Socket socket = GetSocket();
                DrainPendingDatagrams(socket);

                byte[] request = CreatePayload(sequenceNumber);
                stopwatch.Start();
                await socket.SendToAsync(request, SocketFlags.None, endpoint, linkedSource.Token).ConfigureAwait(false);

                byte[] response = new byte[PayloadLength + 1];
                while (true)
                {
                    EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    SocketReceiveFromResult received = await socket.ReceiveFromAsync(
                        response,
                        SocketFlags.None,
                        source,
                        linkedSource.Token).ConfigureAwait(false);

                    if (received.RemoteEndPoint is not IPEndPoint sourceEndpoint
                        || !sourceEndpoint.Address.Equals(endpoint.Address)
                        || sourceEndpoint.Port != endpoint.Port)
                    {
                        sawSourceMismatch = true;
                        continue;
                    }

                    if (received.ReceivedBytes != request.Length
                        || !response.AsSpan(0, received.ReceivedBytes).SequenceEqual(request))
                    {
                        sawPayloadMismatch = true;
                        continue;
                    }

                    stopwatch.Stop();
                    return CreateResult(
                        sequenceNumber,
                        startedAtUtc,
                        sessionElapsedMilliseconds,
                        stopwatch.Elapsed.TotalMilliseconds,
                        ProbeOutcome.Success,
                        null,
                        remoteIp: endpoint.Address.ToString());
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return CreateResult(
                    sequenceNumber,
                    startedAtUtc,
                    sessionElapsedMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ProbeOutcome.Cancelled,
                    "cancelled");
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                string failureCode = sawPayloadMismatch
                    ? "udp_echo_payload_mismatch"
                    : sawSourceMismatch
                        ? "udp_echo_source_mismatch"
                        : "udp_echo_timeout";
                return CreateResult(
                    sequenceNumber,
                    startedAtUtc,
                    sessionElapsedMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ProbeOutcome.Failure,
                    failureCode);
            }
            catch (SocketException exception)
            {
                stopwatch.Stop();
                ResetState();
                string code = exception.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => "udp_dns",
                    SocketError.TimedOut => "udp_echo_timeout",
                    _ => "udp_socket",
                };

                return CreateResult(
                    sequenceNumber,
                    startedAtUtc,
                    sessionElapsedMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ProbeOutcome.Failure,
                    code,
                    (int)exception.SocketErrorCode);
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                ResetState();
                return CreateResult(
                    sequenceNumber,
                    startedAtUtc,
                    sessionElapsedMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ProbeOutcome.InternalError,
                    exception.GetType().Name,
                    exception.HResult);
            }
        }

        public ValueTask DisposeAsync()
        {
            ResetState();
            return ValueTask.CompletedTask;
        }

        private Socket GetSocket() =>
            _socket ??= new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        private void ResetSocket()
        {
            _socket?.Dispose();
            _socket = null;
        }

        private void ResetState()
        {
            ResetSocket();
            _endpointResolver.Reset();
        }

        private static void DrainPendingDatagrams(Socket socket)
        {
            byte[] drainBuffer = new byte[2048];
            while (socket.Available > 0)
            {
                EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                _ = socket.ReceiveFrom(drainBuffer, ref source);
            }
        }

        private static byte[] CreatePayload(long sequenceNumber)
        {
            byte[] payload = new byte[PayloadLength];
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), PayloadMagic);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(4, 8), sequenceNumber);
            RandomNumberGenerator.Fill(payload.AsSpan(12));
            return payload;
        }

        private ProbeResult CreateResult(
            long sequenceNumber,
            DateTimeOffset startedAtUtc,
            long sessionElapsedMilliseconds,
            double durationMilliseconds,
            ProbeOutcome outcome,
            string? failureCode,
            int? nativeErrorCode = null,
            string? remoteIp = null) =>
            new(
                _line.Id,
                _line.Protocol,
                sequenceNumber,
                startedAtUtc,
                sessionElapsedMilliseconds,
                durationMilliseconds,
                outcome,
                failureCode,
                nativeErrorCode,
                RemoteIp: remoteIp ?? _lastRemoteIp);
    }
}
