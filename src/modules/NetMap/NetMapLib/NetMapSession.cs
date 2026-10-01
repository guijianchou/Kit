// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NetMap.UnitTests")]

namespace NetMapLib;

public sealed class NetMapSession : IDisposable
{
    private readonly object gate = new();
    private readonly Func<bool, NetMapOptions, CancellationToken, Task<IdentityResult>> observe;
    private readonly Func<NetMapOptions, Action<ServiceResult>, CancellationToken, Task<IReadOnlyList<ServiceResult>>> services;
    private readonly Func<string, Action<IReadOnlyList<HopResult>>, CancellationToken, Task> trace;
    private readonly TimeSpan directInterval;
    private readonly TimeSpan proxyInterval;
    private readonly TimeSpan serviceInterval;
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? diagnosticCancellation;
    private Task completion = Task.CompletedTask;
    private NetMapSnapshot current = NetMapSnapshot.Empty;
    private long sessionId;
    private bool disposed;

    public NetMapSession()
        : this(new NetworkProbe().ObserveAsync, new NetworkProbe().CheckServicesAsync, RouteProbe.TraceAsync, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5))
    {
    }

    internal NetMapSession(
        Func<bool, NetMapOptions, CancellationToken, Task<IdentityResult>> observe,
        Func<NetMapOptions, Action<ServiceResult>, CancellationToken, Task<IReadOnlyList<ServiceResult>>> services,
        Func<string, Action<IReadOnlyList<HopResult>>, CancellationToken, Task> trace,
        TimeSpan directInterval,
        TimeSpan proxyInterval,
        TimeSpan? serviceInterval = null)
    {
        this.observe = observe;
        this.services = services;
        this.trace = trace;
        this.directInterval = directInterval;
        this.proxyInterval = proxyInterval;
        this.serviceInterval = serviceInterval ?? TimeSpan.FromSeconds(10);
    }

    public event Action<NetMapSnapshot>? Changed;

    public NetMapSnapshot Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public Task Completion => completion;

    public void Start(NetMapOptions options)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current.Running)
            {
                return;
            }

            if (!options.IsValid)
            {
                throw new ArgumentException("Invalid NetMap options.", nameof(options));
            }

            var source = new CancellationTokenSource();
            cancellation = source;
            long id = ++sessionId;
            Publish(current with
            {
                Running = true,
                Generation = current.Generation + 1,
                Stabilizing = true,
                Services = [],
                Hops = [],
                Direct = current.Direct with { Latest = null, ConsecutiveFailures = 0 },
                Proxy = current.Proxy with { Latest = null, ConsecutiveFailures = 0 },
                RouteComplete = false,
                Faulted = false,
                DiagnosticFailed = false,
            });
            completion = Task.Run(() => RunAsync(id, options, source));
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!current.Running)
            {
                return;
            }

            ++sessionId;
            Publish(current with { Running = false, Stabilizing = false });
            cancellation?.Cancel();
            diagnosticCancellation?.Cancel();
            cancellation = null;
            diagnosticCancellation = null;
        }
    }

    public void Dispose()
    {
        Stop();
        lock (gate)
        {
            disposed = true;
            Changed = null;
        }
    }

    private async Task RunAsync(long id, NetMapOptions options, CancellationTokenSource source)
    {
        using (source)
        using (var geo = new GeoDatabase(options))
        {
            lock (gate)
            {
                if (id == sessionId && current.Running)
                {
                    Publish(current with { DatabaseStatus = geo.Status });
                }
            }

            await Task.WhenAll(ObserveLoopAsync(id, false, options, geo, source.Token), ObserveLoopAsync(id, true, options, geo, source.Token)).ConfigureAwait(false);
        }
    }

    private async Task ObserveLoopAsync(long id, bool isProxy, NetMapOptions options, GeoDatabase geo, CancellationToken token)
    {
        string? candidate = null;
        bool diagnosed = false;
        var tasks = new List<Task>();
        var locatedIps = new HashSet<string>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await observe(isProxy, options, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var geography = result.Success ? geo.Lookup(result.Identity!.Ip) : null;
                lock (gate)
                {
                    if (id != sessionId || !current.Running)
                    {
                        break;
                    }

                    var previous = isProxy ? current.Proxy : current.Direct;
                    int failures = result.Success ? 0 : Math.Min(3, previous.ConsecutiveFailures + 1);
                    var channel = new ChannelState(result, result.Success ? result : failures < 3 ? previous.LastSuccess : null, result.Success ? geography : failures < 3 ? previous.Geography : null, failures);
                    var next = isProxy ? current with { Proxy = channel } : current with { Direct = channel };
                    if (isProxy)
                    {
                        string? identityKey = result.Success ? $"{result.Identity!.Ip}|{result.Identity.CountryCode}|{result.Route}" : null;
                        if (identityKey == null || identityKey != candidate)
                        {
                            diagnosticCancellation?.Cancel();
                            diagnosticCancellation = null;
                            candidate = identityKey;
                            diagnosed = false;
                            next = next with { Generation = next.Generation + 1, Stabilizing = result.Success, Services = [], Hops = [], RouteComplete = false, DiagnosticFailed = false };
                        }
                        else if (!diagnosed)
                        {
                            diagnosed = true;
                            next = next with { Stabilizing = false };
                            var source = CancellationTokenSource.CreateLinkedTokenSource(token);
                            diagnosticCancellation = source;
                            long generation = next.Generation;
                            tasks.RemoveAll(task => task.IsCompleted);
                            tasks.Add(Task.Run(() => DiagnoseAsync(id, generation, result.Identity!.Ip, options, geo, source), CancellationToken.None));
                        }
                    }

                    Publish(next);
                }

                if (result.Success && geography?.LookupStatus == "Pending" && locatedIps.Add(result.Identity!.Ip))
                {
                    tasks.RemoveAll(task => task.IsCompleted);
                    tasks.Add(EnrichEgressAsync(id, isProxy, result.Identity.Ip, geo, token));
                }

                await Task.Delay(isProxy ? proxyInterval : directInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped sessions cannot publish.
        }
        catch (Exception)
        {
            lock (gate)
            {
                if (id == sessionId)
                {
                    Stop();
                    Publish(current with { Faulted = true });
                }
            }
        }
        finally
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task EnrichEgressAsync(long id, bool isProxy, string ip, GeoDatabase geo, CancellationToken token)
    {
        try
        {
            var geography = await geo.LookupAsync(ip, token).ConfigureAwait(false);
            lock (gate)
            {
                var channel = isProxy ? current.Proxy : current.Direct;
                if (id == sessionId && current.Running && channel.LastSuccess?.Identity?.Ip == ip)
                {
                    channel = channel with { Geography = geography };
                    Publish(isProxy ? current with { Proxy = channel } : current with { Direct = channel });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Enrichment shares the session lifetime, but never delays the new IP card.
        }
    }

    private async Task DiagnoseAsync(long id, long generation, string ip, NetMapOptions options, GeoDatabase geo, CancellationTokenSource source)
    {
        using (source)
        {
            var token = source.Token;
            async Task TraceAsync()
            {
                var locatedIps = new HashSet<string>();
                var lookups = new List<Task>();
                async Task LocateHopAsync(string address)
                {
                    try
                    {
                        var location = await geo.LookupAsync(address, token).ConfigureAwait(false);
                        CommitDiagnostic(id, generation, snapshot => snapshot with { Hops = snapshot.Hops.Select(row => row.Ip == address ? row with { Geography = location } : row).ToArray() });
                    }
                    catch (OperationCanceledException)
                    {
                        // The user stopped sampling or selected another egress.
                    }
                }

                try
                {
                    await trace(
                        ip,
                        rows =>
                        {
                            var located = rows.Select(row => row.Ip.Length == 0 ? row : row with { Geography = geo.Lookup(row.Ip) }).ToArray();
                            CommitDiagnostic(id, generation, snapshot => snapshot with { Hops = located });
                            foreach (var row in located)
                            {
                                if (row.Geography?.LookupStatus == "Pending" && locatedIps.Add(row.Ip))
                                {
                                    lookups.RemoveAll(task => task.IsCompleted);
                                    lookups.Add(LocateHopAsync(row.Ip));
                                }
                            }
                        },
                        token).ConfigureAwait(false);
                    CommitDiagnostic(id, generation, snapshot => snapshot with { RouteComplete = true });
                }
                catch (OperationCanceledException)
                {
                    // MTR sampling continues until the egress or page lifetime changes.
                }
                catch (Exception)
                {
                    CommitDiagnostic(id, generation, snapshot => snapshot with { DiagnosticFailed = true });
                }
                finally
                {
                    await Task.WhenAll(lookups).ConfigureAwait(false);
                }
            }

            async Task ServicesAsync()
            {
                try
                {
                    using var timer = new PeriodicTimer(serviceInterval);
                    do
                    {
                        var reported = new HashSet<(string Name, bool IsProxy)>();
                        void Report(ServiceResult result) => CommitDiagnostic(id, generation, snapshot =>
                        {
                            // Progress and the completed batch must count each observation only once.
                            if (!reported.Add((result.Name, result.IsProxy)))
                            {
                                return snapshot;
                            }

                            var previous = snapshot.Services.FirstOrDefault(item => item.Name == result.Name && item.IsProxy == result.IsProxy);
                            int failures = result.Failed ? Math.Min(3, (previous?.ConsecutiveFailures ?? 0) + 1) : 0;
                            var measured = result with
                            {
                                ConsecutiveFailures = failures,
                                LastKnownMs = failures is > 0 and < 3 ? previous?.ElapsedMs ?? previous?.LastKnownMs : null,
                                LastKnownCheckpoint = failures is > 0 and < 3 ? previous?.Checkpoint ?? previous?.LastKnownCheckpoint : null,
                            };
                            return snapshot with { Services = snapshot.Services.Where(item => item.Name != result.Name || item.IsProxy != result.IsProxy).Append(measured).ToArray() };
                        });
                        var results = await services(options, Report, token).ConfigureAwait(false);
                        foreach (var result in results)
                        {
                            Report(result);
                        }
                    }
                    while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    // The selected egress or page lifetime changed.
                }
                catch (Exception)
                {
                    CommitDiagnostic(id, generation, snapshot => snapshot with { DiagnosticFailed = true });
                }
            }

            await Task.WhenAll(TraceAsync(), ServicesAsync()).ConfigureAwait(false);
            lock (gate)
            {
                if (ReferenceEquals(diagnosticCancellation, source))
                {
                    diagnosticCancellation = null;
                }
            }
        }
    }

    private void CommitDiagnostic(long id, long generation, Func<NetMapSnapshot, NetMapSnapshot> update)
    {
        lock (gate)
        {
            if (id == sessionId && current.Running && generation == current.Generation)
            {
                var updated = update(current);
                if (!ReferenceEquals(updated, current))
                {
                    Publish(updated);
                }
            }
        }
    }

    private void Publish(NetMapSnapshot snapshot)
    {
        current = snapshot;
        Changed?.Invoke(snapshot);
    }
}
