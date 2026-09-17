using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace UDPtestLib.Core
{
    public sealed class MetricsEngine
    {
        private const int WindowMilliseconds = 20_000;
        private const int MinimumSamples = 5;
        private const int RecentAverageCount = 6;
        private const int MaxFailureCount = 3;
        private const int GreenQualityThreshold = 97;

        private readonly ConcurrentDictionary<string, LineState> _states = new(StringComparer.Ordinal);
        private int _windowCapacity = 20;

        public void Reset(IEnumerable<ProbeLineDefinition> lines, int refreshIntervalMilliseconds)
        {
            _windowCapacity = Math.Max(1, WindowMilliseconds / refreshIntervalMilliseconds);
            _states.Clear();

            foreach (ProbeLineDefinition line in lines)
            {
                _states[line.Id] = new LineState(line, _windowCapacity);
            }
        }

        public LineMetricSnapshot Apply(ProbeResult result)
        {
            if (!_states.TryGetValue(result.LineId, out LineState? state))
            {
                throw new InvalidOperationException($"Unknown probe line '{result.LineId}'.");
            }

            state.Apply(result);
            return state.CreateSnapshot();
        }

        public LineMetricSnapshot SetLineEnabled(string lineId, bool isEnabled)
        {
            if (!_states.TryGetValue(lineId, out LineState? state))
            {
                throw new InvalidOperationException($"Unknown probe line '{lineId}'.");
            }

            return state.SetEnabled(isEnabled);
        }

        public IReadOnlyList<LineMetricSnapshot> ResetWindows() =>
            _states.Values
                .OrderBy(static state => state.Line.Id, StringComparer.Ordinal)
                .Select(static state => state.ResetWindow())
                .ToArray();

        public IReadOnlyList<LineMetricSnapshot> SnapshotAll() =>
            _states.Values
                .OrderBy(static state => state.Line.Id, StringComparer.Ordinal)
                .Select(static state => state.CreateSnapshot())
                .ToArray();

        public UdpAssessment AssessUdp()
        {
            int udpTargetCount = _states.Values.Count(static state => state.Protocol == ProbeProtocol.Udp);
            LineMetricSnapshot[] udp = _states.Values
                .Where(static state => state.Protocol == ProbeProtocol.Udp && state.IsEnabled)
                .Select(static state => state.CreateSnapshot())
                .ToArray();

            if (udp.Length == 0)
            {
                return new("Not Monitored", $"0/{udpTargetCount} enabled");
            }

            int stableCount = udp.Count(static line =>
                line.State == "OK" && line.QualityPercent >= 97 && line.HasSegmentSuccess);
            if (stableCount >= 2)
            {
                return new("Supported / Stable", $"{udp.Length}/{udpTargetCount} enabled");
            }

            if (udp.Length == 1 && stableCount == 1)
            {
                return new("Supported / Single-Target Stable", $"1/{udpTargetCount} enabled");
            }

            if (udp.Any(static line => line.HasSegmentSuccess))
            {
                return new("Supported / Unstable", $"{udp.Length}/{udpTargetCount} enabled");
            }

            if (udp.Any(static line => line.State == "Internal Error"))
            {
                return new("Unknown / Internal Error", $"{udp.Length}/{udpTargetCount} enabled");
            }

            bool fullNoSuccess = udp.All(line =>
                line.EligibleWindowCount >= line.WindowCapacity && !line.HasSegmentSuccess);
            if (!fullNoSuccess)
            {
                return new("Warming", $"{udp.Length}/{udpTargetCount} enabled");
            }

            bool tcpAvailable = _states.Values
                .Where(static state => state.Protocol == ProbeProtocol.Tcp && state.IsEnabled)
                .Select(static state => state.CreateSnapshot())
                .Any(static line => line.HasSegmentSuccess && line.State == "OK");

            if (!tcpAvailable)
            {
                return new("Unknown", "TCP baseline unavailable");
            }

            return udp.Length == udpTargetCount
                ? new(
                    "Unsupported",
                    $"{udpTargetCount}/{udpTargetCount} targets completed a full window without a response")
                : new("No Response / Insufficient Targets", $"{udp.Length}/{udpTargetCount} enabled");
        }

        private sealed class LineState
        {
            private readonly object _sync = new();
            private readonly Queue<ProbeResult> _eligibleWindow = new();
            private readonly int _capacity;
            private bool _isEnabled;
            private DateTimeOffset _enabledSinceUtc = DateTimeOffset.MinValue;
            private int _successCount;
            private int _failureCount;
            private int _consecutiveFailures;
            private bool _hasInternalError;
            private bool _hasSegmentSuccess;
            private string? _egressIpv4;
            private string? _remoteIpv4;
            private string? _lastError;
            private ProbeResult? _lastResult;

            public LineState(ProbeLineDefinition line, int capacity)
            {
                Line = line;
                _capacity = capacity;
                _isEnabled = line.IsEnabled;
            }

            public ProbeLineDefinition Line { get; }

            public ProbeProtocol Protocol => Line.Protocol;

            public bool IsEnabled
            {
                get
                {
                    lock (_sync)
                    {
                        return _isEnabled;
                    }
                }
            }

            public void Apply(ProbeResult result)
            {
                lock (_sync)
                {
                    bool appliesToActiveWindow = _isEnabled && result.StartedAtUtc >= _enabledSinceUtc;
                    if (!string.IsNullOrEmpty(result.RemoteIp))
                    {
                        _remoteIpv4 = result.RemoteIp;
                    }

                    if (appliesToActiveWindow)
                    {
                        _egressIpv4 = Line.Kind switch
                        {
                            ProbeKind.Https204 => result.TcpEgressIpv4 ?? _egressIpv4,
                            ProbeKind.StunBinding => result.UdpEgressIpv4 ?? _egressIpv4,
                            _ => _egressIpv4,
                        };
                    }

                    switch (result.Outcome)
                    {
                        case ProbeOutcome.Success:
                            _successCount++;
                            if (appliesToActiveWindow)
                            {
                                _lastResult = result;
                                _consecutiveFailures = 0;
                                _hasSegmentSuccess = true;
                                _lastError = null;
                                AddEligible(result);
                            }
                            break;
                        case ProbeOutcome.Failure:
                            _failureCount++;
                            if (appliesToActiveWindow)
                            {
                                _lastResult = result;
                                _consecutiveFailures++;
                                _lastError = result.FailureCode;
                                AddEligible(result);
                            }
                            break;
                        case ProbeOutcome.InternalError:
                            if (appliesToActiveWindow)
                            {
                                _lastResult = result;
                                _hasInternalError = true;
                                _lastError = result.FailureCode;
                            }
                            break;
                        case ProbeOutcome.Cancelled:
                            if (appliesToActiveWindow)
                            {
                                _lastResult = result;
                                _lastError = "cancelled";
                            }
                            break;
                    }
                }
            }

            public LineMetricSnapshot CreateSnapshot()
            {
                lock (_sync)
                {
                    return CreateSnapshotCore();
                }
            }

            public LineMetricSnapshot SetEnabled(bool isEnabled)
            {
                lock (_sync)
                {
                    if (_isEnabled == isEnabled)
                    {
                        return CreateSnapshotCore();
                    }

                    _isEnabled = isEnabled;
                    _lastResult = null;
                    if (isEnabled)
                    {
                        ClearRealtimeState();
                    }

                    return CreateSnapshotCore();
                }
            }

            public LineMetricSnapshot ResetWindow()
            {
                lock (_sync)
                {
                    ClearRealtimeState();
                    return CreateSnapshotCore();
                }
            }

            private void ClearRealtimeState()
            {
                _enabledSinceUtc = DateTimeOffset.UtcNow;
                _eligibleWindow.Clear();
                _consecutiveFailures = 0;
                _hasInternalError = false;
                _hasSegmentSuccess = false;
                _egressIpv4 = null;
                _remoteIpv4 = null;
                _lastError = null;
                _lastResult = null;
            }

            private LineMetricSnapshot CreateSnapshotCore()
            {
                int eligible = _eligibleWindow.Count;
                int successes = _eligibleWindow.Count(static result => result.Outcome == ProbeOutcome.Success);
                int quality = eligible == 0 ? 0 : (int)Math.Floor(successes * 100d / eligible);
                double[] recentRtts = _eligibleWindow
                    .Where(static result => result.Outcome == ProbeOutcome.Success)
                    .Select(static result => result.DurationMilliseconds)
                    .TakeLast(RecentAverageCount)
                    .ToArray();

                int? average = recentRtts.Length == 0
                    ? null
                    : (int)Math.Round(recentRtts.Average(), MidpointRounding.AwayFromZero);
                int? jitter = recentRtts.Length < 2
                    ? null
                    : (int)Math.Round(
                        recentRtts.Zip(recentRtts.Skip(1), static (left, right) => Math.Abs(right - left)).Average(),
                        MidpointRounding.AwayFromZero);

                int required = Math.Min(MinimumSamples, _capacity);
                string state = !_isEnabled ? "Disabled"
                    : _hasInternalError ? "Internal Error"
                    : _consecutiveFailures >= MaxFailureCount ? "DOWN"
                    : eligible < required ? "Warming"
                    : quality < GreenQualityThreshold ? "DEGRADED"
                    : "OK";

                return new(
                    Line.Id,
                    state,
                    _successCount,
                    _failureCount,
                    eligible,
                    _capacity,
                    _consecutiveFailures,
                    quality,
                    average,
                    jitter,
                    _egressIpv4,
                    _remoteIpv4,
                    _lastError,
                    _hasSegmentSuccess,
                    _isEnabled,
                    _lastResult?.Outcome,
                    _lastResult?.StartedAtUtc,
                    _lastResult?.DurationMilliseconds);
            }

            private void AddEligible(ProbeResult result)
            {
                _eligibleWindow.Enqueue(result);
                while (_eligibleWindow.Count > _capacity)
                {
                    _eligibleWindow.Dequeue();
                }
            }
        }
    }
}
