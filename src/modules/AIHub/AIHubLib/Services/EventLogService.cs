using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// Audit scan mode. Extended runs under standard privileges; Full additionally
    /// reads the Security and Windows Firewall channels and therefore requires elevation.
    /// </summary>
    public enum AuditMode
    {
        Extended,
        Full,
    }

    public async Task<List<SecurityEvent>> CollectEventsAsync(
        DateTime from,
        DateTime to,
        int maxEventsPerChannel = 200,
        CancellationToken cancellationToken = default)
        => await CollectEventsAsync(from, to, AuditMode.Extended, maxEventsPerChannel, cancellationToken).ConfigureAwait(false);

    public async Task<List<SecurityEvent>> CollectEventsAsync(
        DateTime from,
        DateTime to,
        AuditMode mode,
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

        await CollectChannelAsync(ReadSystemEventsAsync(from, to, cancellationToken));
        await CollectChannelAsync(ReadApplicationEventsAsync(from, to, cancellationToken));
        await CollectChannelAsync(ReadSetupEventsAsync(from, to, cancellationToken));
        await CollectChannelAsync(ReadForwardedEventsAsync(from, to, cancellationToken));

        // Full-access mode additionally reads Security and Firewall. The Security
        // channel requires elevation; when it is unreadable the channel reader yields
        // nothing and the result degrades to the extended set.
        if (mode == AuditMode.Full)
        {
            await CollectChannelAsync(ReadSecurityEventsAsync(from, to, cancellationToken));
            await CollectChannelAsync(ReadFirewallEventsAsync(from, to, cancellationToken));
        }

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


    /// <summary>
    /// Security channel event IDs worth auditing. An explicit allowlist keeps the
    /// Security log query bounded: the channel is large and every additional ID
    /// widens the scan.
    /// </summary>
    private static readonly int[] SecurityEventIds =
    [
        1102, // audit log cleared
        4624, // successful logon
        4625, // failed logon
        4634, 4647, // logoff
        4648, // explicit credential use
        4672, // special privileges assigned
        4688, // process creation
        4697, // service installed
        4719, // audit policy changed
        4720, 4722, 4723, 4724, 4726, // account lifecycle
        4728, 4732, 4756, // group membership changes
        4740, // account lockout
        4776, // credential validation
        4778, 4779, // session reconnect/disconnect
        4907, // audit settings changed
        4946, 4947, 4948, 4949, 4950, // firewall rule changes
        5024, 5025, // firewall service state
        5152, 5156, 5157, // WFP blocked connection
    ];

    /// <summary>
    /// Firewall events live in the Security channel too, but under a different ID set.
    /// </summary>
    private static readonly int[] FirewallEventIds =
    [
        4946, 4947, 4948, 4949, 4950,
        5024, 5025,
        5152, 5156, 5157,
    ];

    /// <summary>Maximum IDs per XPath disjunction; keeps each query string manageable.</summary>
    private const int EventIdChunkSize = 16;

    /// <summary>True when the current process can read the Security channel.</summary>
    public static bool CanReadSecurityLog
    {
        get
        {
            try
            {
                var query = new EventLogQuery("Security", PathType.LogName, "*[System[(EventID=1102)]]") { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                reader.ReadEvent()?.Dispose();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Full-access mode: reads the Security channel, restricted to the audited event
    /// IDs. Requires an elevated process; returns nothing when the channel is
    /// unreadable so the caller degrades to extended mode.
    /// </summary>
    public async IAsyncEnumerable<SecurityEvent> ReadSecurityEventsAsync(
        DateTime from,
        DateTime to,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (int[] chunk in Chunk(SecurityEventIds, EventIdChunkSize))
        {
            string query = BuildIdQuery(from, to, chunk);
            await foreach (var evt in ReadEventsFromLogAsync("Security", query, cancellationToken))
            {
                yield return evt;
            }
        }
    }

    /// <summary>
    /// Full-access mode: reads Windows Firewall / WFP events from the Security channel.
    /// </summary>
    public async IAsyncEnumerable<SecurityEvent> ReadFirewallEventsAsync(
        DateTime from,
        DateTime to,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (int[] chunk in Chunk(FirewallEventIds, EventIdChunkSize))
        {
            string query = BuildIdQuery(from, to, chunk);
            await foreach (var evt in ReadEventsFromLogAsync("Security", query, cancellationToken))
            {
                yield return evt;
            }
        }
    }

    private static IEnumerable<int[]> Chunk(int[] source, int size)
    {
        for (int i = 0; i < source.Length; i += size)
        {
            yield return source.Skip(i).Take(size).ToArray();
        }
    }

    /// <summary>
    /// Builds a query filtering on an explicit event ID set. Unlike the level-based
    /// query this does not filter on Level: several security events of interest are
    /// recorded as informational.
    /// </summary>
    private static string BuildIdQuery(DateTime from, DateTime to, int[] eventIds)
    {
        string fromUtc = from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string toUtc = to.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string ids = string.Join(" or ", eventIds.Select(id => $"EventID={id}"));

        return $"*[System[TimeCreated[@SystemTime >= '{fromUtc}' and @SystemTime <= '{toUtc}'] and ({ids})]]";
    }

    private static string BuildQuery(DateTime from, DateTime to, string levelFilter)
    {
        string fromUtc = from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string toUtc = to.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        return $"*[System[TimeCreated[@SystemTime >= '{fromUtc}' and @SystemTime <= '{toUtc}'] and {levelFilter}]]";
    }
}
