using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using UDPtestLib.Core;

namespace UDPtestLib.Probes
{
    public sealed record NatStunTarget(
        string Name,
        string Host,
        int Port,
        bool IsFallback = false);

    public sealed class NatTypeProbe
    {
        public const int RequiredLineCount = 3;
        private const int RequestAttemptCount = 3;

        public static IReadOnlyList<NatStunTarget> FixedTargets { get; } =
        [
            new("Cloudflare", "stun.cloudflare.com", 3478),
            new("Twilio", "global.stun.twilio.com", 3478),
            new("NextCloud", "stun.nextcloud.com", 3478),
            new("Google", "stun.l.google.com", 19302, IsFallback: true),
        ];

        public Task<NatAssessment> EvaluateAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            EvaluateAsync(FixedTargets, timeout, cancellationToken);

        public async Task<NatAssessment> EvaluateAsync(
            IReadOnlyList<NatStunTarget> targets,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(targets);
            NatStunTarget[] primaryTargets = targets
                .Where(static target => !target.IsFallback)
                .ToArray();
            if (primaryTargets.Length < RequiredLineCount)
            {
                return Unavailable("The fixed NAT catalog requires three primary STUN targets");
            }

            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            List<BaselineSample> baseline = new(RequiredLineCount);
            foreach (NatStunTarget target in primaryTargets)
            {
                await TryAddBaselineAsync(socket, target, baseline, timeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            bool usedFallback = false;
            if (baseline.Count < RequiredLineCount)
            {
                foreach (NatStunTarget target in targets.Where(static target => target.IsFallback))
                {
                    usedFallback = true;
                    await TryAddBaselineAsync(socket, target, baseline, timeout, cancellationToken)
                        .ConfigureAwait(false);
                    if (baseline.Count >= RequiredLineCount)
                    {
                        break;
                    }
                }
            }

            if (baseline.Count < RequiredLineCount)
            {
                string fallbackDetail = usedFallback ? " Google fallback was attempted." : string.Empty;
                return Unavailable(
                    $"Only {baseline.Count} of 3 fixed STUN targets returned a valid mapping.{fallbackDetail}");
            }

            baseline = baseline.Take(RequiredLineCount).ToList();
            string targetNames = string.Join(", ", baseline.Select(static sample => sample.Target.Name));
            IPEndPoint firstMapping = baseline[0].Response.MappedAddress!;
            if (baseline.Skip(1).Any(sample => !sample.Response.MappedAddress!.Equals(firstMapping)))
            {
                return Result(
                    "Symmetric",
                    $"RFC 5389/8489 mappings differ across {targetNames}; mapping behavior is endpoint-dependent.");
            }

            BaselineSample? behaviorSample = baseline.FirstOrDefault(
                static sample => sample.Response.AlternateAddress is IPEndPoint alternate
                    && HasChangedIpAndPort(alternate, sample.Endpoint));
            behaviorSample ??= baseline.FirstOrDefault(
                static sample => sample.Response.AlternateAddress is IPEndPoint alternate
                    && HasChangedEndpoint(alternate, sample.Endpoint));

            if (behaviorSample is null)
            {
                return DowngradedPortRestricted(
                    targetNames,
                    "the servers did not expose an RFC 5780 OTHER-ADDRESS");
            }

            IPEndPoint primaryEndpoint = behaviorSample.Endpoint;
            NatResponse primaryResponse = behaviorSample.Response;
            IPEndPoint alternateEndpoint = primaryResponse.AlternateAddress!;
            bool hasChangedIpAndPort = HasChangedIpAndPort(alternateEndpoint, primaryEndpoint);

            NatResponse changeIpAndPort = await SendChangeRequestAsync(
                    socket,
                    primaryEndpoint,
                    StunChangeRequest.IpAndPort,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (RespondedFromChangedIpAndPort(changeIpAndPort, primaryEndpoint))
            {
                return Result(
                    "Full Cone",
                    $"RFC 5780 change-IP-and-port response succeeded after matching mappings across {targetNames}.");
            }

            NatResponse alternateBaseline = await SendBaselineAsync(
                    socket,
                    alternateEndpoint,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!alternateBaseline.IsSuccess || alternateBaseline.MappedAddress is null)
            {
                return DowngradedPortRestricted(
                    targetNames,
                    "the advertised RFC 5780 alternate address did not return a mapping");
            }

            if (!alternateBaseline.MappedAddress.Equals(primaryResponse.MappedAddress))
            {
                return Result(
                    "Symmetric",
                    "The mapped UDP endpoint changed at the RFC 5780 alternate address.");
            }

            NatResponse changePort = await SendChangeRequestAsync(
                    socket,
                    primaryEndpoint,
                    StunChangeRequest.Port,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (RespondedFromChangedPort(changePort, primaryEndpoint))
            {
                string evidence = hasChangedIpAndPort && !changeIpAndPort.Received
                    ? "RFC 5780 Test II timed out and Test III returned from a changed source port."
                    : "Downgraded RFC 5780 evidence: a changed-port response succeeded, but changed-IP filtering could not be verified.";
                return Result(
                    "Address Restricted",
                    $"{evidence} Mappings matched across {targetNames}; an ICE connectivity check is still authoritative.");
            }

            string portEvidence = hasChangedIpAndPort && !changeIpAndPort.Received && !changePort.Received
                ? "RFC 5780 change-IP-and-port and change-port requests both timed out."
                : "Downgraded RFC 5780 evidence: the server did not provide a valid changed-source response.";
            return Result(
                "Port Restricted",
                $"{portEvidence} Mappings matched across {targetNames}; classified conservatively for ICE/P2P expectations.");
        }

        private static async Task TryAddBaselineAsync(
            Socket socket,
            NatStunTarget target,
            List<BaselineSample> baseline,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            IPEndPoint? endpoint = await ResolveEndpointAsync(target, cancellationToken).ConfigureAwait(false);
            if (endpoint is null || baseline.Any(sample => sample.Endpoint.Equals(endpoint)))
            {
                return;
            }

            NatResponse response = await SendBaselineAsync(socket, endpoint, timeout, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccess && response.MappedAddress is not null)
            {
                baseline.Add(new BaselineSample(target, endpoint, response));
            }
        }

        private static async Task<IPEndPoint?> ResolveEndpointAsync(
            NatStunTarget target,
            CancellationToken cancellationToken)
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(
                        target.Host,
                        cancellationToken)
                    .ConfigureAwait(false);
                IPAddress? address = addresses.FirstOrDefault(
                    static candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
                return address is null ? null : new IPEndPoint(address, target.Port);
            }
            catch (SocketException)
            {
                return null;
            }
        }

        private static async Task<NatResponse> SendBaselineAsync(
            Socket socket,
            IPEndPoint endpoint,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            NatResponse response = NatResponse.NoResponse;
            for (int attempt = 0; attempt < RequestAttemptCount; attempt++)
            {
                response = await SendBindingAsync(
                        socket,
                        endpoint,
                        StunChangeRequest.None,
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccess && response.MappedAddress is not null)
                {
                    return response;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            return response;
        }

        private static async Task<NatResponse> SendChangeRequestAsync(
            Socket socket,
            IPEndPoint endpoint,
            StunChangeRequest changeRequest,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < RequestAttemptCount; attempt++)
            {
                NatResponse response = await SendBindingAsync(
                        socket,
                        endpoint,
                        changeRequest,
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (response.Received)
                {
                    return response;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            return NatResponse.NoResponse;
        }

        private static async Task<NatResponse> SendBindingAsync(
            Socket socket,
            IPEndPoint endpoint,
            StunChangeRequest changeRequest,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource = new(timeout);
            using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            byte[] transactionId = RandomNumberGenerator.GetBytes(12);
            byte[] request = StunMessageCodec.CreateBindingRequest(transactionId, changeRequest);
            try
            {
                await socket.SendToAsync(
                        request,
                        SocketFlags.None,
                        endpoint,
                        linkedSource.Token)
                    .ConfigureAwait(false);

                byte[] responseBuffer = new byte[2048];
                EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    SocketReceiveFromResult received = await socket.ReceiveFromAsync(
                            responseBuffer,
                            SocketFlags.None,
                            source,
                            linkedSource.Token)
                        .ConfigureAwait(false);
                    StunParseStatus parseStatus = StunMessageCodec.ParseBindingResponse(
                        responseBuffer.AsSpan(0, received.ReceivedBytes),
                        transactionId,
                        received.RemoteEndPoint as IPEndPoint,
                        out StunBindingResponse parsed);
                    if (parseStatus is StunParseStatus.Success or StunParseStatus.ErrorResponse)
                    {
                        return new NatResponse(
                            true,
                            parseStatus == StunParseStatus.Success,
                            parsed.MappedAddress,
                            parsed.AlternateAddress,
                            parsed.SourceAddress);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return NatResponse.NoResponse;
            }
            catch (SocketException)
            {
                return NatResponse.NoResponse;
            }
        }

        private static bool RespondedFromChangedIpAndPort(
            NatResponse response,
            IPEndPoint originalEndpoint) =>
            response.IsSuccess
            && response.SourceAddress is IPEndPoint source
            && HasChangedIpAndPort(source, originalEndpoint);

        private static bool HasChangedIpAndPort(
            IPEndPoint candidate,
            IPEndPoint originalEndpoint) =>
            !candidate.Address.Equals(originalEndpoint.Address)
            && candidate.Port != originalEndpoint.Port;

        private static bool HasChangedEndpoint(
            IPEndPoint candidate,
            IPEndPoint originalEndpoint) =>
            !candidate.Equals(originalEndpoint);

        private static bool RespondedFromChangedPort(
            NatResponse response,
            IPEndPoint originalEndpoint) =>
            response.IsSuccess
            && response.SourceAddress is IPEndPoint source
            && source.Address.Equals(originalEndpoint.Address)
            && source.Port != originalEndpoint.Port;

        private static NatAssessment Result(string type, string detail) =>
            new(type, detail, DateTimeOffset.UtcNow);

        private static NatAssessment DowngradedPortRestricted(
            string targetNames,
            string reason) =>
            Result(
                "Port Restricted",
                $"Downgraded evidence: mappings matched across {targetNames}, but {reason}; classified conservatively for ICE/P2P expectations.");

        private static NatAssessment Unavailable(string detail) =>
            new("N/A", detail, DateTimeOffset.UtcNow);

        private sealed record NatResponse(
            bool Received,
            bool IsSuccess,
            IPEndPoint? MappedAddress,
            IPEndPoint? AlternateAddress,
            IPEndPoint? SourceAddress)
        {
            public static NatResponse NoResponse { get; } = new(false, false, null, null, null);
        }

        private sealed record BaselineSample(
            NatStunTarget Target,
            IPEndPoint Endpoint,
            NatResponse Response);
    }
}
