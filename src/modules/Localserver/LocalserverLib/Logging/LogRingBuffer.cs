namespace LocalServerHub.Core.Logging;

public enum LogStream
{
    StdOut,
    StdErr,
    /// <summary>Emitted by the hub itself: state changes, preflight results, kills.</summary>
    Hub,
}

/// <summary>One line of output. Sequence numbers are per-service and monotonic.</summary>
public sealed record LogLine(long Sequence, DateTimeOffset TimestampUtc, LogStream Stream, string Text);

/// <summary>
/// Bounded in-memory scrollback for one service (plan.md §11.1).
/// </summary>
/// <remarks>
/// The buffer is the only thing the UI reads from, so a chatty service can cost
/// memory but can never grow without bound. Consumers reconnect by asking for
/// everything after the last sequence number they saw, which is what makes the
/// terminal survive a window close or a UI reload without losing its place.
/// </remarks>
public sealed class LogRingBuffer
{
    private readonly Lock _gate = new();
    private readonly Queue<LogLine> _lines;
    private int _capacity;
    private long _nextSequence = 1;

    public LogRingBuffer(int capacity = 5000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        _capacity = capacity;
        _lines = new Queue<LogLine>(Math.Min(capacity, 1024));
    }

    /// <summary>Sequence number the next appended line will receive.</summary>
    public long NextSequence
    {
        get
        {
            lock (_gate)
            {
                return _nextSequence;
            }
        }
    }

    /// <summary>
    /// Lines dropped because the buffer was full. Reported in the output panel's
    /// subtitle rather than hidden, since a silently truncated log is misleading.
    /// </summary>
    /// <remarks>
    /// Written under the lock and read without one. A 64-bit aligned read cannot
    /// tear on the platforms this targets, and a reader that is one line behind is
    /// of no consequence for a counter that only ever grows.
    /// </remarks>
    public long DroppedCount { get; private set; }

    public int Capacity { get { lock (_gate) { return _capacity; } } }

    public void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        lock (_gate)
        {
            _capacity = capacity;
            while (_lines.Count > _capacity) { _lines.Dequeue(); DroppedCount++; }
        }
    }

    public LogLine Append(LogStream stream, string text, DateTimeOffset? timestampUtc = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate)
        {
            LogLine line = new(
                _nextSequence++,
                timestampUtc ?? DateTimeOffset.UtcNow,
                stream,
                text);

            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
                DroppedCount++;
            }

            return line;
        }
    }

    /// <summary>All retained lines with a sequence number greater than <paramref name="afterSequence"/>.</summary>
    public IReadOnlyList<LogLine> Snapshot(long afterSequence = 0)
    {
        lock (_gate)
        {
            if (afterSequence <= 0)
            {
                return [.. _lines];
            }

            return [.. _lines.Where(line => line.Sequence > afterSequence)];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            DroppedCount = 0;
        }
    }
}
