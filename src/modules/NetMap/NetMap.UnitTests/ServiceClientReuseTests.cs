// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Http;
using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace NetMap.UnitTests;

[TestClass]
public sealed class ServiceClientReuseTests
{
    [TestMethod]
    public async Task TraceClientsAreReusedWithinOneEgressGeneration()
    {
        var created = new List<string>();
        using var probe = new NetworkProbe((name, _) => Create(created, name));

        await probe.CheckServicesAsync(new(), 7, _ => { }, CancellationToken.None);
        await probe.CheckServicesAsync(new(), 7, _ => { }, CancellationToken.None);

        Assert.AreEqual(1, created.Count(name => name == "Claude"), "A trace checkpoint must reuse one handler within a generation.");
        Assert.AreEqual(1, created.Count(name => name == "ChatGPT"), "A trace checkpoint must reuse one handler within a generation.");
        Assert.AreEqual(2, created.Count(name => name == "Gemini"), "A webpage check must keep a fresh handler per request.");
        Assert.AreEqual(2, created.Count(name => name == "Google"), "A webpage check must keep a fresh handler per request.");
    }

    [TestMethod]
    public async Task NewEgressGenerationDropsPooledTraceClients()
    {
        var created = new List<string>();
        using var probe = new NetworkProbe((name, _) => Create(created, name));

        await probe.CheckServicesAsync(new(), 7, _ => { }, CancellationToken.None);
        await probe.CheckServicesAsync(new(), 8, _ => { }, CancellationToken.None);

        Assert.AreEqual(2, created.Count(name => name == "Claude"), "A new egress must not reuse a connection that may point at the previous node.");
        Assert.AreEqual(2, created.Count(name => name == "ChatGPT"), "A new egress must not reuse a connection that may point at the previous node.");
    }

    [TestMethod]
    public async Task FailedPooledConnectionIsRebuiltForTheNextRound()
    {
        var created = new List<string>();
        using var probe = new NetworkProbe((name, _) => Create(created, name, failing: name == "Claude"));

        await probe.CheckServicesAsync(new(), 7, _ => { }, CancellationToken.None);
        await probe.CheckServicesAsync(new(), 7, _ => { }, CancellationToken.None);

        Assert.AreEqual(2, created.Count(name => name == "Claude"), "A failed pooled connection must reconnect on the next round.");
        Assert.AreEqual(1, created.Count(name => name == "ChatGPT"), "A sibling failure must not discard a healthy pooled connection.");
    }

    [TestMethod]
    public async Task SessionForwardsTheEgressGenerationToServiceChecks()
    {
        var proxy = Channel.CreateUnbounded<IdentityResult>();
        long first = -1;
        long second = -1;
        using var session = new NetMapSession(
            async (isProxy, _, token) =>
            {
                if (isProxy)
                {
                    return await proxy.Reader.ReadAsync(token);
                }

                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException();
            },
            (_, generation, _, _) =>
            {
                if (first < 0)
                {
                    first = generation;
                }
                else if (generation != first)
                {
                    second = generation;
                }

                return Task.FromResult<IReadOnlyList<ServiceResult>>([]);
            },
            (_, _, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(20));
        session.Start(new(OnlineLookup: false));
        await proxy.Writer.WriteAsync(Success("192.0.2.7", "JP"));
        await proxy.Writer.WriteAsync(Success("192.0.2.7", "JP"));
        Assert.IsTrue(await Until(() => first > 0), "The service loop must report its egress generation.");
        await proxy.Writer.WriteAsync(Success("192.0.2.8", "SG"));
        await proxy.Writer.WriteAsync(Success("192.0.2.8", "SG"));
        Assert.IsTrue(await Until(() => second > first), "A new egress must report a new generation so pooled connections are dropped.");
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static IdentityResult Success(string ip, string country) => new(new(ip, country, country), ProbeError.None, DateTimeOffset.Now, "Explicit");

    private static HttpClient Create(List<string> created, string name, bool failing = false)
    {
        created.Add(name);
        string body = name is "Gemini" or "Google" ? "<html>Page</html>" : "ip=198.51.100.8\nloc=JP\n";
        return new HttpClient(new Handler(body, failing));
    }

    private static async Task<bool> Until(Func<bool> condition)
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    private sealed class Handler(string body, bool failing) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (failing)
            {
                throw new HttpRequestException("Synthetic connection failure");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
