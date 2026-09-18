using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

public sealed class EventLogService
{
    private const string FailureLevels = "(Level=1 or Level=2 or Level=3)";

    public async IAsyncEnumerable<SecurityEvent> ReadSystemEventsAsync(
        DateTime from,
        DateTime to,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string query = BuildQuery(from, to, FailureLevels);
        await foreach (var evt in ReadEventsFromLogAsync("System", query, cancellationToken))
        {
            yield return evt;
        }
    }

    public async IAsyncEnumerable<SecurityEvent> ReadApplicationEventsAsync(
        DateTime from,
        DateTime to,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string query = BuildQuery(from, to, FailureLevels);
        await foreach (var evt in ReadEventsFromLogAsync("Application", query, cancellationToken))
        {
            yield return evt;
        }
    }

    public async IAsyncEnumerable<SecurityEvent> ReadSetupEventsAsync(
        DateTime from,
        DateTime to,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string query = BuildQuery(from, to, FailureLevels);
        await foreach (var evt in ReadEventsFromLogAsync("Setup", query, cancellationToken))
        {
            yield return evt;
        }
    }

    public async IAsyncEnumerable<SecurityEvent> ReadForwardedEventsAsync(
        DateTime from,
        DateTime to,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string query = BuildQuery(from, to, FailureLevels);
        await foreach (var evt in ReadEventsFromLogAsync("ForwardedEvents", query, cancellationToken))
        {
            yield return evt;
        }
    }

    public async Task<List<SecurityEvent>> CollectEventsAsync(
        DateTime from,
        DateTime to,
        int maxEventsPerChannel = 200,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SecurityEvent>();

        async Task CollectChannelAsync(IAsyncEnumerable<SecurityEvent> stream)
        {
            int count = 0;
            try
            {
                await foreach (var evt in stream.WithCancellation(cancellationToken))
                {
                    results.Add(evt);
                    count++;
                    if (count >= maxEventsPerChannel)
                    {
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // Channels unavailable or empty
            }
        }

        // Extended mode: standard privileges, reads ordinary channels and skips Security
        await CollectChannelAsync(ReadSystemEventsAsync(from, to, cancellationToken));
        await CollectChannelAsync(ReadApplicationEventsAsync(from, to, cancellationToken));
        await CollectChannelAsync(ReadSetupEventsAsync(from, to, cancellationToken));
        await CollectChannelAsync(ReadForwardedEventsAsync(from, to, cancellationToken));

        return results;
    }

    private static async IAsyncEnumerable<SecurityEvent> ReadEventsFromLogAsync(
        string logName,
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EventLogQuery eventQuery;
        try
        {
            eventQuery = new EventLogQuery(logName, PathType.LogName, query)
            {
                ReverseDirection = true
            };
        }
        catch (Exception)
        {
            yield break;
        }

        using var reader = new EventLogReader(eventQuery);

        while (!cancellationToken.IsCancellationRequested)
        {
            EventRecord? record = null;
            try
            {
                record = reader.ReadEvent();
            }
            catch (Exception)
            {
                break;
            }

            if (record == null)
            {
                break;
            }

            using (record)
            {
                string? message = null;
                try
                {
                    message = record.FormatDescription();
                }
                catch
                {
                    message = null;
                }

                yield return new SecurityEvent(
                    record.Id,
                    record.LogName ?? logName,
                    record.ProviderName ?? string.Empty,
                    record.Level,
                    record.TimeCreated?.ToUniversalTime(),
                    message);
            }

            await Task.Yield();
        }
    }

    private static string BuildQuery(DateTime from, DateTime to, string levelFilter)
    {
        string fromUtc = from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string toUtc = to.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        return $"*[System[TimeCreated[@SystemTime >= '{fromUtc}' and @SystemTime <= '{toUtc}'] and {levelFilter}]]";
    }
}
