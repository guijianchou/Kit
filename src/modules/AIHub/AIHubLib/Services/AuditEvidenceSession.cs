using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

public sealed record AuditEvidenceWindow(DateTime FromUtc, DateTime ToUtc);

public sealed record AuditEvidenceBatch(
    IReadOnlyList<SecurityEvent> Events,
    IReadOnlyList<string> SkippedChannels,
    IReadOnlyList<string> LimitedChannels);

public sealed record AuditEvidenceSelection(
    List<SecurityEvent> Events,
    List<string> SkippedChannels,
    List<string> LimitedChannels,
    bool HasMissingIntervals,
    int CollectedIntervals,
    int ReusedEvents,
    int ReadEvents);

/// <summary>Bounded, in-memory event evidence. Completed historical intervals survive navigation and tail refreshes.</summary>
public sealed partial class AuditEvidenceSession : IDisposable, IAsyncDisposable
{
    private sealed record Segment(AuditEvidenceWindow Window, AuditEvidenceBatch Batch, int MaximumPerChannel);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Segment> _segments = [];
    private readonly Func<AuditEvidenceWindow, EventLogService.AuditMode, int, IProgress<EventCollectionProgress>?, CancellationToken, Task<AuditEvidenceBatch>> _collect;
    private readonly int _maximumEvents;
    private readonly long _maximumCharacters;
    private string _profile = string.Empty;
    private int _activeLoads;
    private bool _disposed;
    private Task? _disposeTask;

    public AuditEvidenceSession(int maximumEvents = 60000, long maximumCharacters = 32 * 1024 * 1024)
        : this(CollectAsync, maximumEvents, maximumCharacters)
    {
    }

    public AuditEvidenceSession(
        Func<AuditEvidenceWindow, EventLogService.AuditMode, int, IProgress<EventCollectionProgress>?, CancellationToken, Task<AuditEvidenceBatch>> collect,
        int maximumEvents = 60000,
        long maximumCharacters = 32 * 1024 * 1024)
    {
        _collect = collect ?? throw new ArgumentNullException(nameof(collect));
        _maximumEvents = Math.Max(1, maximumEvents);
        _maximumCharacters = Math.Max(1, maximumCharacters);
    }

    public async Task<AuditEvidenceSelection> LoadAsync(
        DateTime fromUtc,
        DateTime toUtc,
        EventLogService.AuditMode mode,
        int maximumPerChannel,
        DateTime? refreshFromUtc = null,
        IProgress<EventCollectionProgress>? progress = null,
        bool retrySkippedIntervals = false,
        bool refreshEntireWindow = false,
        CancellationToken cancellationToken = default)
    {
        if (fromUtc >= toUtc)
        {
            throw new ArgumentException("The evidence window must have a positive duration.");
        }
        CancellationTokenSource linkedCancellation;
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
            _activeLoads++;
        }
        using (linkedCancellation)
        {
            try
            {
                return await LoadCoreAsync(fromUtc, toUtc, mode, maximumPerChannel, refreshFromUtc, progress,
                    retrySkippedIntervals, refreshEntireWindow, linkedCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_lifetimeGate)
                {
                    if (--_activeLoads == 0 && _disposed)
                    {
                        _drained.TrySetResult();
                    }
                }
            }
        }
    }

    public void Dispose() => _ = BeginDisposal();

    public ValueTask DisposeAsync() => new(BeginDisposal());

    private Task BeginDisposal()
    {
        lock (_lifetimeGate)
        {
            if (_disposeTask is not null)
            {
                return _disposeTask;
            }
            _disposed = true;
            if (_activeLoads == 0)
            {
                _drained.TrySetResult();
            }
            _disposeTask = DrainAndDisposeAsync();
            return _disposeTask;
        }
    }

    private async Task DrainAndDisposeAsync()
    {
        _lifetimeCancellation.Cancel();
        await _drained.Task.ConfigureAwait(false);
        _gate.Dispose();
        _lifetimeCancellation.Dispose();
        _segments.Clear();
    }

    private async Task<AuditEvidenceSelection> LoadCoreAsync(DateTime fromUtc, DateTime toUtc, EventLogService.AuditMode mode,
        int maximumPerChannel, DateTime? refreshFromUtc, IProgress<EventCollectionProgress>? progress,
        bool retrySkippedIntervals, bool refreshEntireWindow, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string profile = AuditCollectionProfile.ForMode(mode);
            if (!string.Equals(profile, _profile, StringComparison.Ordinal))
            {
                _segments.Clear();
                _profile = profile;
            }
            var requested = new AuditEvidenceWindow(fromUtc, toUtc);
            var before = Select(requested);
            var work = Missing(requested, _segments.Select(segment => segment.Window));
            var retry = _segments.Where(segment => Overlaps(segment.Window, requested)
                && ((segment.MaximumPerChannel < maximumPerChannel && segment.Batch.LimitedChannels.Count > 0)
                    || (retrySkippedIntervals && segment.Batch.SkippedChannels.Count > 0)))
                .Select(segment => new AuditEvidenceWindow(
                    segment.Window.FromUtc > fromUtc ? segment.Window.FromUtc : fromUtc,
                    segment.Window.ToUtc < toUtc ? segment.Window.ToUtc : toUtc));
            work = Merge(work.Concat(retry));
            if (refreshFromUtc is DateTime refresh && refresh < toUtc)
            {
                var tail = new AuditEvidenceWindow(refresh > fromUtc ? refresh : fromUtc, toUtc);
                work = Merge(work.Append(tail));
            }
            if (refreshEntireWindow)
            {
                work = [requested];
            }

            int retainedUnreadableEvents = 0;
            int readEvents = 0;
            foreach (var interval in work)
            {
                var batch = await _collect(interval, mode, maximumPerChannel, progress, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var collected = InWindow(batch.Events, interval).ToList();
                readEvents += collected.Count;
                if (batch.SkippedChannels.Count > 0)
                {
                    var unreadable = batch.SkippedChannels.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var counts = collected.GroupBy(item => item).ToDictionary(group => group.Key, group => group.Count());
                    foreach (var previous in Select(interval).Where(item => unreadable.Contains(item.LogName)))
                    {
                        if (counts.TryGetValue(previous, out int count) && count > 0)
                        {
                            counts[previous] = count - 1;
                        }
                        else
                        {
                            collected.Add(previous);
                            retainedUnreadableEvents++;
                        }
                    }
                }
                var bounded = batch with { Events = collected };
                Replace(interval, bounded, maximumPerChannel);
                Trim();
            }
            cancellationToken.ThrowIfCancellationRequested();
            var selected = Select(requested);
            var overlapping = _segments.Where(segment => Overlaps(segment.Window, requested)).ToList();
            return new(selected,
                overlapping.SelectMany(segment => segment.Batch.SkippedChannels).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                overlapping.SelectMany(segment => segment.Batch.LimitedChannels).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Missing(requested, _segments.Select(segment => segment.Window)).Count > 0,
                work.Count,
                retainedUnreadableEvents + before.Count(item => item.TimeCreated is DateTime time && !work.Any(window => Contains(window, time.ToUniversalTime()))),
                readEvents);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static List<AuditEvidenceWindow> Missing(AuditEvidenceWindow requested, IEnumerable<AuditEvidenceWindow> available)
    {
        var result = new List<AuditEvidenceWindow>();
        DateTime cursor = requested.FromUtc;
        foreach (var interval in Merge(available).Where(window => Overlaps(window, requested)))
        {
            if (interval.FromUtc > cursor)
            {
                result.Add(new(cursor, interval.FromUtc < requested.ToUtc ? interval.FromUtc : requested.ToUtc));
            }
            if (interval.ToUtc > cursor)
            {
                cursor = interval.ToUtc;
            }
            if (cursor >= requested.ToUtc)
            {
                break;
            }
        }
        if (cursor < requested.ToUtc)
        {
            result.Add(new(cursor, requested.ToUtc));
        }
        return result;
    }

    private static List<AuditEvidenceWindow> Merge(IEnumerable<AuditEvidenceWindow> intervals)
    {
        var result = new List<AuditEvidenceWindow>();
        foreach (var interval in intervals.OrderBy(window => window.FromUtc))
        {
            if (result.Count == 0 || result[^1].ToUtc < interval.FromUtc)
            {
                result.Add(interval);
            }
            else if (interval.ToUtc > result[^1].ToUtc)
            {
                result[^1] = result[^1] with { ToUtc = interval.ToUtc };
            }
        }
        return result;
    }

    private static bool Overlaps(AuditEvidenceWindow first, AuditEvidenceWindow second) => first.FromUtc < second.ToUtc && second.FromUtc < first.ToUtc;
    private static bool Contains(AuditEvidenceWindow window, DateTime time) => time >= window.FromUtc && time < window.ToUtc;
    private static IEnumerable<SecurityEvent> InWindow(IEnumerable<SecurityEvent> events, AuditEvidenceWindow window) =>
        events.Where(item => item.TimeCreated is DateTime time && Contains(window, time.ToUniversalTime()));
    private List<SecurityEvent> Select(AuditEvidenceWindow window) => _segments.SelectMany(segment => InWindow(segment.Batch.Events, window)).ToList();

    private void Replace(AuditEvidenceWindow window, AuditEvidenceBatch batch, int maximumPerChannel)
    {
        foreach (var segment in _segments.Where(segment => Overlaps(segment.Window, window)).ToList())
        {
            _segments.Remove(segment);
            if (segment.Window.FromUtc < window.FromUtc)
            {
                AddSlice(segment, new(segment.Window.FromUtc, window.FromUtc));
            }
            if (segment.Window.ToUtc > window.ToUtc)
            {
                AddSlice(segment, new(window.ToUtc, segment.Window.ToUtc));
            }
        }
        _segments.Add(new(window, batch, maximumPerChannel));
    }

    private void AddSlice(Segment segment, AuditEvidenceWindow window) =>
        _segments.Add(new(window, segment.Batch with { Events = InWindow(segment.Batch.Events, window).ToList() }, segment.MaximumPerChannel));

    private void Trim()
    {
        while (_segments.Count > 128 || _segments.Sum(segment => segment.Batch.Events.Count) > _maximumEvents
            || _segments.Sum(segment => segment.Batch.Events.Sum(item => (long)(item.Message?.Length ?? 0) + (item.RawXml?.Length ?? 0) + 256)) > _maximumCharacters)
        {
            _segments.Remove(_segments.MinBy(segment => segment.Window.ToUtc)!);
        }
    }

    private static async Task<AuditEvidenceBatch> CollectAsync(AuditEvidenceWindow window, EventLogService.AuditMode mode, int maximumPerChannel,
        IProgress<EventCollectionProgress>? progress, CancellationToken cancellationToken)
    {
        var service = new EventLogService();
        var events = await service.CollectEventsAsync(window.FromUtc, window.ToUtc, mode, maximumPerChannel, progress, cancellationToken).ConfigureAwait(false);
        return new(events, service.SkippedChannels.ToList(), service.LimitedChannels.ToList());
    }
}
