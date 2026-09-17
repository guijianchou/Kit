using System.Text.Json.Serialization;

namespace UDPtestLib.Core
{
    public enum ProbeProtocol
    {
        Tcp,
        Udp,
    }

    [JsonConverter(typeof(JsonStringEnumConverter<ProbeKind>))]
    public enum ProbeKind
    {
        Unknown,
        Https204,
        StunBinding,
        UdpEcho,
    }

    public enum ProbeOutcome
    {
        Success,
        Failure,
        Cancelled,
        InternalError,
    }

    public enum MonitorRunState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
    }

    public sealed record ProbeLineDefinition(
        string Id,
        string Name,
        ProbeProtocol Protocol,
        ProbeKind Kind,
        string Target,
        int Port,
        bool IsEnabled = true,
        int? ExpectedHttpStatus = null,
        string? TargetLabel = null)
    {
        public string DisplayTarget => TargetLabel ?? Target;

        public string DisplayPort => Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public bool SupportsUdpEgress => Protocol == ProbeProtocol.Udp && Kind == ProbeKind.StunBinding;

        public bool SupportsNatAssessment => Protocol == ProbeProtocol.Udp && Kind == ProbeKind.StunBinding;
    }

    public sealed record ProbeResult(
        string LineId,
        ProbeProtocol Protocol,
        long SequenceNumber,
        DateTimeOffset StartedAtUtc,
        long SessionElapsedMilliseconds,
        double DurationMilliseconds,
        ProbeOutcome Outcome,
        string? FailureCode = null,
        int? NativeErrorCode = null,
        int? HttpStatus = null,
        string? TcpEgressIpv4 = null,
        string? TcpRemoteIpv4 = null,
        string? UdpEgressIpv4 = null,
        string? RemoteIp = null)
    {
        public bool CountsTowardQuality => Outcome is ProbeOutcome.Success or ProbeOutcome.Failure;

        public bool ChangesFailureStreak => CountsTowardQuality;
    }

    public sealed record LineMetricSnapshot(
        string LineId,
        string State,
        int SuccessCount,
        int FailureCount,
        int EligibleWindowCount,
        int WindowCapacity,
        int ConsecutiveFailures,
        int QualityPercent,
        int? AverageRttMilliseconds,
        int? JitterMilliseconds,
        string? EgressIpv4,
        string? RemoteIpv4,
        string? LastError,
        bool HasSegmentSuccess,
        bool IsEnabled,
        ProbeOutcome? LastOutcome,
        DateTimeOffset? LastStartedAtUtc,
        double? LastDurationMilliseconds);

    public sealed record UdpAssessment(string State, string Detail);

    public sealed record NatAssessment(string Type, string Detail, DateTimeOffset EvaluatedAtUtc);
}
