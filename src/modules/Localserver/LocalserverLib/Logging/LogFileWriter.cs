using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace LocalServerHub.Core.Logging;

/// <summary>
/// Spills log lines to <c>&lt;root&gt;/&lt;serviceId&gt;/&lt;date&gt;.log</c> with size
/// rotation and a retention cap (plan.md §11.1).
/// </summary>
/// <remarks>
/// <para>
/// Writes happen on one background task draining a channel, never on the caller's
/// thread: the caller is the process's stdout reader, and blocking it on file IO
/// would apply backpressure to the child process itself.
/// </para>
/// <para>
/// Lines arrive already redacted - <see cref="LogRingBuffer"/> is fed from the
/// same string - so no secret reaches disk that was not already on screen.
/// </para>
/// <para>
/// Nothing here throws into the caller. A log sink that can take down a service
/// because a directory turned read-only is worse than a log sink that quietly
/// stops; failures are counted in <see cref="FailureCount"/> and the last one is
/// kept in <see cref="LastError"/> so the UI can say so.
/// </para>
/// </remarks>
public sealed class LogFileWriter : IAsyncDisposable
{
    private const int QueueCapacity = 8_192;

    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Channel<QueuedLine> _queue;
    private readonly Task _pump;
    private sealed record WriterSettings(string Root, long RotationSizeBytes, int RetentionCopies);
    private WriterSettings _settings;
    private long _droppedCount;
    private long _failureCount;

    public LogFileWriter(string rootDirectory, int rotationSizeMB = 10, int retentionCopies = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        _settings = new(Path.GetFullPath(rootDirectory), Math.Max(1, rotationSizeMB) * 1024L * 1024L, Math.Max(1, retentionCopies));

        // Dropping the oldest line beats blocking the reader or growing without
        // bound; the drop is counted so it can be reported rather than hidden.
        _queue = Channel.CreateBounded<QueuedLine>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        }, _ => Interlocked.Increment(ref _droppedCount));

        _pump = Task.Run(PumpAsync);
    }

    public string RootDirectory => Volatile.Read(ref _settings).Root;

    public void UpdateSettings(string rootDirectory, int rotationSizeMB, int retentionCopies)
    {
        string root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
        Volatile.Write(ref _settings, new(root, Math.Max(1, rotationSizeMB) * 1024L * 1024L, Math.Max(1, retentionCopies)));
    }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>Lines that could not be written. Non-zero means logs on disk are incomplete.</summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>Most recent write failure, for display next to the log folder.</summary>
    public string? LastError { get; private set; }

    /// <summary>Directory this service's files go in, whether or not it exists yet.</summary>
    public string GetServiceDirectory(string serviceId) =>
        Path.Combine(RootDirectory, SanitizeSegment(serviceId));

    /// <summary>
    /// Queues one line. Returns immediately and never throws; a full queue drops
    /// its oldest entry.
    /// </summary>
    public void Enqueue(string serviceId, LogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return;
        }

        if (!_queue.Writer.TryWrite(new QueuedLine(serviceId, line, Volatile.Read(ref _settings))))
            Interlocked.Increment(ref _droppedCount);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Dispose must not throw; the pump already records its own failures.
        }
    }

    /// <summary>
    /// Drains the channel in batches: everything already queued for one file is
    /// written with a single open, because a chatty service produces thousands of
    /// lines a second and one handle per line is most of the cost.
    /// </summary>
    private async Task PumpAsync()
    {
        Dictionary<string, (StringBuilder Text, int Count, WriterSettings Settings)> batches = new(StringComparer.OrdinalIgnoreCase);

        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            int lines = 0;
            int characters = 0;
            while (lines < 2048 && characters < 1_048_576 && _queue.Reader.TryRead(out QueuedLine queued))
            {
                WriterSettings settings = queued.Settings;
                string path = Path.Combine(settings.Root, SanitizeSegment(queued.ServiceId), $"{queued.Line.TimestampUtc.ToLocalTime():yyyy-MM-dd}.log");
                if (!batches.TryGetValue(path, out var batch)) batch = (new StringBuilder(), 0, settings);
                string formatted = Format(queued.Line);
                batch.Text.Append(formatted);
                batches[path] = (batch.Text, batch.Count + 1, settings);
                lines++;
                characters += formatted.Length;
            }

            foreach (var (path, batch) in batches)
            {
                TryFlush(path, batch.Text.ToString(), batch.Count, batch.Settings);
            }
            batches.Clear();
        }
    }

    private void TryFlush(string path, string payload, int lineCount, WriterSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? settings.Root);
            RotateIfOversized(path, settings);

            // No BOM: these files get appended to, tailed and grepped, and a
            // preamble in the middle of a stream is noise.
            using StreamWriter writer = new(path, append: true, NoBomUtf8);
            writer.Write(payload);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            Interlocked.Add(ref _failureCount, lineCount);
            LastError = exception.Message;
        }
    }

    /// <summary>
    /// One line per record, timestamp first so files stay greppable and sortable:
    /// <c>2026-08-31 14:02:11.123 [out] text</c>.
    /// </summary>
    private static string Format(LogLine line)
    {
        string stamp = line.TimestampUtc.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string stream = line.Stream switch
        {
            LogStream.StdOut => "out",
            LogStream.StdErr => "err",
            _ => "hub",
        };

        return $"{stamp} [{stream}] {line.Text}{Environment.NewLine}";
    }

    private void RotateIfOversized(string path, WriterSettings settings)
    {
        FileInfo current = new(path);
        if (!current.Exists || current.Length < settings.RotationSizeBytes)
        {
            return;
        }

        string directory = current.DirectoryName ?? settings.Root;
        string stem = Path.GetFileNameWithoutExtension(path);
        PruneRotated(directory, stem, settings.RetentionCopies);
        for (int index = 1; index <= settings.RetentionCopies; index++)
        {
            string candidate = Path.Combine(directory, $"{stem}.{index}.log");
            if (File.Exists(candidate))
            {
                continue;
            }

            File.Move(path, candidate);
            return;
        }

        // Every slot is taken: drop the oldest and reuse its name.
        string oldest = Path.Combine(directory, $"{stem}.1.log");
        File.Delete(oldest);
        for (int index = 2; index <= settings.RetentionCopies; index++)
        {
            string from = Path.Combine(directory, $"{stem}.{index}.log");
            if (File.Exists(from))
            {
                File.Move(from, Path.Combine(directory, $"{stem}.{index - 1}.log"), overwrite: true);
            }
        }

        File.Move(path, Path.Combine(directory, $"{stem}.{settings.RetentionCopies}.log"), overwrite: true);
    }

    private static void PruneRotated(string directory, string stem, int retentionCopies)
    {
        string[] rotated = [.. Directory
            .EnumerateFiles(directory, $"{stem}.*.log")
            .Where(path => int.TryParse(Path.GetFileNameWithoutExtension(path)[(stem.Length + 1)..], out int index) && index > retentionCopies)];

        foreach (string stale in rotated)
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // A locked rotated file is not worth failing the current write over.
            }
        }
    }

    /// <summary>
    /// Reduces a service id to one safe path segment. Ids come from a JSON file a
    /// user hand-edits, so "../../etc" must not become a directory traversal.
    /// </summary>
    private static string SanitizeSegment(string serviceId)
    {
        StringBuilder builder = new(serviceId.Length);
        foreach (char character in serviceId)
        {
            builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ? '_' : character);
        }

        string sanitized = builder.ToString().Trim(' ', '.');
        return sanitized.Length == 0 ? "unnamed" : sanitized;
    }

    private readonly record struct QueuedLine(string ServiceId, LogLine Line, WriterSettings Settings);
}
