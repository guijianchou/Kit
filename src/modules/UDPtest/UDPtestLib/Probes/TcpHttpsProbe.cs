using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Sockets;
using UDPtestLib.Core;

namespace UDPtestLib.Probes
{
    public sealed class TcpHttpsProbe : IProbe
    {
        private const int MaximumDrainBytes = 64 * 1024;

        private readonly ProbeLineDefinition _line;
        private readonly Uri _targetUri;
        private readonly HttpClient _client;
        private readonly int _expectedHttpStatus;
        private string? _tcpEgressIpv4;
        private string? _tcpRemoteIpv4;

        public TcpHttpsProbe(ProbeLineDefinition line)
        {
            _line = line;
            _expectedHttpStatus = line.ExpectedHttpStatus
                ?? throw new ArgumentException("HTTPS probes require an expected HTTP status.", nameof(line));
            _targetUri = new Uri(line.Target, UriKind.Absolute);

            SocketsHttpHandler handler = new()
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                Proxy = null,
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                ConnectCallback = ConnectSocketAsync,
            };

            _client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
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

            if (_tcpRemoteIpv4 is null)
            {
                if (IPAddress.TryParse(_targetUri.Host, out IPAddress? parsedIp) && parsedIp.AddressFamily == AddressFamily.InterNetwork)
                {
                    _tcpRemoteIpv4 = parsedIp.ToString();
                }
                else
                {
                    try
                    {
                        IPAddress[] addresses = await Dns.GetHostAddressesAsync(_targetUri.Host, AddressFamily.InterNetwork, linkedSource.Token).ConfigureAwait(false);
                        if (addresses.Length > 0)
                        {
                            _tcpRemoteIpv4 = addresses[0].ToString();
                        }
                    }
                    catch
                    {
                        // Ignore DNS fallback failure; will be caught by SendAsync
                    }
                }
            }

            using HttpRequestMessage request = new(HttpMethod.Get, _line.Target);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                using HttpResponseMessage response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedSource.Token).ConfigureAwait(false);
                stopwatch.Stop();

                bool drained = await DrainResponseAsync(response, linkedSource.Token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                bool success = status == _expectedHttpStatus && drained;
                return CreateResult(
                    sequenceNumber,
                    startedAtUtc,
                    sessionElapsedMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds,
                    success ? ProbeOutcome.Success : ProbeOutcome.Failure,
                    success ? null : drained ? $"http_status_{status}" : "http_body_too_large",
                    httpStatus: status,
                    tcpEgressIpv4: _tcpEgressIpv4,
                    tcpRemoteIpv4: _tcpRemoteIpv4);
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
                    "cancelled",
                    tcpEgressIpv4: _tcpEgressIpv4,
                    tcpRemoteIpv4: _tcpRemoteIpv4);
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
                    "tcp_timeout",
                    tcpEgressIpv4: _tcpEgressIpv4,
                    tcpRemoteIpv4: _tcpRemoteIpv4);
            }
            catch (HttpRequestException exception)
            {
                stopwatch.Stop();
                string code = exception.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError => "tcp_dns",
                    HttpRequestError.ConnectionError => "tcp_connect",
                    HttpRequestError.SecureConnectionError => "tcp_tls",
                    _ => "tcp_request",
                };

                return CreateResult(
                    sequenceNumber,
                    startedAtUtc,
                    sessionElapsedMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ProbeOutcome.Failure,
                    code,
                    exception.HResult,
                    tcpEgressIpv4: _tcpEgressIpv4,
                    tcpRemoteIpv4: _tcpRemoteIpv4);
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                return CreateResult(
                    sequenceNumber,
                    startedAtUtc,
                    sessionElapsedMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ProbeOutcome.InternalError,
                    exception.GetType().Name,
                    exception.HResult,
                    tcpEgressIpv4: _tcpEgressIpv4,
                    tcpRemoteIpv4: _tcpRemoteIpv4);
            }
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }

        private ProbeResult CreateResult(
            long sequenceNumber,
            DateTimeOffset startedAtUtc,
            long sessionElapsedMilliseconds,
            double durationMilliseconds,
            ProbeOutcome outcome,
            string? failureCode,
            int? nativeErrorCode = null,
            int? httpStatus = null,
            string? tcpEgressIpv4 = null,
            string? tcpRemoteIpv4 = null) =>
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
                httpStatus,
                TcpEgressIpv4: tcpEgressIpv4,
                TcpRemoteIpv4: tcpRemoteIpv4,
                RemoteIp: tcpRemoteIpv4);

        private async ValueTask<Stream> ConnectSocketAsync(
            SocketsHttpConnectionContext context,
            CancellationToken cancellationToken)
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                DnsEndPoint endpoint = new(
                    context.DnsEndPoint.Host,
                    context.DnsEndPoint.Port,
                    AddressFamily.InterNetwork);
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

                if (socket.LocalEndPoint is IPEndPoint localEndPoint
                    && localEndPoint.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    _tcpEgressIpv4 = localEndPoint.Address.ToString();
                }

                if (socket.RemoteEndPoint is IPEndPoint remoteEndPoint
                    && remoteEndPoint.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    _tcpRemoteIpv4 = remoteEndPoint.Address.ToString();
                }

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static async Task<bool> DrainResponseAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            if (response.Content.Headers.ContentLength is > MaximumDrainBytes)
            {
                return false;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[4096];
            int total = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return true;
                }

                total += read;
                if (total > MaximumDrainBytes)
                {
                    return false;
                }
            }
        }
    }
}
