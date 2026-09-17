using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using UDPtestLib.Core;

namespace UDPtestLib.Probes
{
    public sealed class StunProbe : IProbe
    {
        private readonly ProbeLineDefinition _line;
        private readonly UdpEndpointResolver _endpointResolver = new();
        private Socket? _socket;
        private string? _lastRemoteIp;

        public StunProbe(ProbeLineDefinition line)
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
            bool sawTransactionMismatch = false;
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

                byte[] transactionId = RandomNumberGenerator.GetBytes(12);
                byte[] request = StunMessageCodec.CreateBindingRequest(transactionId);
                stopwatch.Start();
                await socket.SendToAsync(request, SocketFlags.None, endpoint, linkedSource.Token).ConfigureAwait(false);

                byte[] response = new byte[2048];
                EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    SocketReceiveFromResult received = await socket.ReceiveFromAsync(
                        response,
                        SocketFlags.None,
                        source,
                        linkedSource.Token).ConfigureAwait(false);

                    StunParseStatus validation = StunMessageCodec.ParseBindingResponse(
                        response.AsSpan(0, received.ReceivedBytes),
                        transactionId,
                        received.RemoteEndPoint as IPEndPoint,
                        out StunBindingResponse parsed);
                    if (validation == StunParseStatus.TransactionMismatch)
                    {
                        sawTransactionMismatch = true;
                        continue;
                    }

                    stopwatch.Stop();
                    if (validation == StunParseStatus.Success && parsed.MappedAddress is not null)
                    {
                        return CreateResult(
                            sequenceNumber,
                            startedAtUtc,
                            sessionElapsedMilliseconds,
                            stopwatch.Elapsed.TotalMilliseconds,
                            ProbeOutcome.Success,
                            null,
                            egressIpv4: parsed.MappedAddress.Address.ToString(),
                            remoteIp: endpoint.Address.ToString());
                    }

                    return CreateResult(
                        sequenceNumber,
                        startedAtUtc,
                        sessionElapsedMilliseconds,
                        stopwatch.Elapsed.TotalMilliseconds,
                        ProbeOutcome.Failure,
                        validation == StunParseStatus.ErrorResponse
                            ? "stun_error_response"
                            : "stun_invalid_response",
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
                return CreateResult(
                    sequenceNumber,
                    startedAtUtc,
                    sessionElapsedMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ProbeOutcome.Failure,
                    sawTransactionMismatch ? "stun_no_matching_response" : "stun_timeout");
            }
            catch (SocketException exception)
            {
                stopwatch.Stop();
                ResetState();
                string code = exception.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => "udp_dns",
                    SocketError.TimedOut => "stun_timeout",
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

        private Socket GetSocket() => _socket ??= new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

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

        private ProbeResult CreateResult(
            long sequenceNumber,
            DateTimeOffset startedAtUtc,
            long sessionElapsedMilliseconds,
            double durationMilliseconds,
            ProbeOutcome outcome,
            string? failureCode,
            int? nativeErrorCode = null,
            string? egressIpv4 = null,
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
                UdpEgressIpv4: egressIpv4,
                RemoteIp: remoteIp ?? _lastRemoteIp);
    }
}
