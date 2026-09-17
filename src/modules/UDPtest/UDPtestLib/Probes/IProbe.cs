using UDPtestLib.Core;

namespace UDPtestLib.Probes
{
    public interface IProbe : IAsyncDisposable
    {
        ValueTask<ProbeResult> ExecuteAsync(
            long sequenceNumber,
            DateTimeOffset startedAtUtc,
            long sessionElapsedMilliseconds,
            TimeSpan timeout,
            CancellationToken cancellationToken);
    }
}
