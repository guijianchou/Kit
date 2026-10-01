// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetMapLib;

public static class RouteProbe
{
    public static async Task TraceAsync(string target, Action<IReadOnlyList<HopResult>> progress, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(target, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return;
        }

        using var ping = new Ping();
        byte[] payload = new byte[24];
        async Task<(string Ip, double? Elapsed, bool Reached)> SendAsync(int ttl, CancellationToken token)
        {
            try
            {
                long started = Stopwatch.GetTimestamp();
                var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(800), payload, new PingOptions(ttl, true), token).ConfigureAwait(false);
                if (reply.Status is IPStatus.Success or IPStatus.TtlExpired)
                {
                    return (reply.Address.ToString(), Stopwatch.GetElapsedTime(started).TotalMilliseconds, reply.Status == IPStatus.Success);
                }
            }
            catch (PingException)
            {
                // An ICMP send failure is not evidence of application packet loss.
            }

            return (string.Empty, null, false);
        }

        await SampleAsync(progress, SendAsync, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SampleAsync(Action<IReadOnlyList<HopResult>> progress, Func<int, CancellationToken, Task<(string Ip, double? Elapsed, bool Reached)>> send, TimeSpan interval, CancellationToken token)
    {
        var rows = new List<HopResult>();
        int terminalTtl = 24;
        while (!token.IsCancellationRequested)
        {
            for (int ttl = 1; ttl <= terminalTtl; ttl++)
            {
                token.ThrowIfCancellationRequested();
                var (ip, elapsed, reached) = await send(ttl, token).ConfigureAwait(false);
                if (reached)
                {
                    terminalTtl = ttl;
                }
                else if (ttl == terminalTtl && ip.Length > 0)
                {
                    // A router now answers at the old destination TTL: discover the longer route.
                    terminalTtl = 24;
                }

                var previous = rows.Count >= ttl ? rows[ttl - 1] : new HopResult(ttl, string.Empty, 0, 0, null, null, false);

                // A changed router at a TTL starts fresh latency statistics for that router.
                if (ip.Length > 0 && previous.Ip.Length > 0 && ip != previous.Ip)
                {
                    previous = new(ttl, ip, 0, 0, null, null, false);
                }

                int received = previous.Received + (elapsed.HasValue ? 1 : 0);
                double? average = received == 0 ? null : (((previous.AverageMs ?? 0) * previous.Received) + (elapsed ?? 0)) / received;
                double? best = elapsed.HasValue ? Math.Min(previous.BestMs ?? elapsed.Value, elapsed.Value) : previous.BestMs;
                double? worst = elapsed.HasValue ? Math.Max(previous.WorstMs ?? elapsed.Value, elapsed.Value) : previous.WorstMs;
                int failures = elapsed.HasValue ? 0 : Math.Min(3, previous.ConsecutiveFailures + 1);
                var row = new HopResult(ttl, ip.Length > 0 ? ip : previous.Ip, previous.Sent + 1, received, elapsed, average, reached, BestMs: best, WorstMs: worst, ConsecutiveFailures: failures, LastKnownMs: failures < 3 ? elapsed ?? previous.LastMs ?? previous.LastKnownMs : null);
                if (rows.Count < ttl)
                {
                    rows.Add(row);
                }
                else
                {
                    rows[ttl - 1] = row;
                }

                if (reached && rows.Count > ttl)
                {
                    rows.RemoveRange(ttl, rows.Count - ttl);
                }

                progress(rows.ToArray());
                if (reached)
                {
                    break;
                }
            }

            await Task.Delay(interval, token).ConfigureAwait(false);
        }
    }
}
