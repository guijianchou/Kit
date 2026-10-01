// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace NetMap.UnitTests;

[TestClass]
public sealed class SessionTests
{
    [TestMethod]
    public async Task WebsiteProgressPublishesBeforeTheWholeBatchCompletes()
    {
        var proxy = Channel.CreateUnbounded<IdentityResult>();
        var pendingBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new NetMapSession(
            async (isProxy, _, token) =>
            {
                if (isProxy)
                {
                    return await proxy.Reader.ReadAsync(token);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            },
            async (_, progress, token) =>
            {
                var result = new ServiceResult("ChatGPT", true, ProbeError.None, 403, DateTimeOffset.Now, "BrowserCheck", 15);
                progress(result);
                await pendingBatch.Task.WaitAsync(token);
                return new[] { result };
            },
            (_, _, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(2));
        session.Start(new());
        await proxy.Writer.WriteAsync(Success("203.0.113.2", "JP"));
        await proxy.Writer.WriteAsync(Success("203.0.113.2", "JP"));
        await WaitFor(session, snapshot => snapshot.Services.Count == 1);
        Assert.IsFalse(pendingBatch.Task.IsCompleted);
        Assert.AreEqual("BrowserCheck", session.Current.Services[0].Outcome);
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ProxyFailureRetainsBothIndependentLastSuccesses()
    {
        var direct = Channel.CreateUnbounded<IdentityResult>();
        var proxy = Channel.CreateUnbounded<IdentityResult>();
        using var session = Create(direct, proxy);
        session.Start(new());
        await direct.Writer.WriteAsync(Success("203.0.113.1", "CN"));
        await proxy.Writer.WriteAsync(Success("203.0.113.2", "JP"));
        await WaitFor(session, s => s.Direct.LastSuccess != null && s.Proxy.LastSuccess != null);
        await proxy.Writer.WriteAsync(new(null, ProbeError.Timeout, DateTimeOffset.Now, "Explicit"));
        await WaitFor(session, s => s.Proxy.Latest?.Error == ProbeError.Timeout);
        Assert.AreEqual("203.0.113.1", session.Current.Direct.LastSuccess!.Identity!.Ip);
        Assert.AreEqual("203.0.113.2", session.Current.Proxy.LastSuccess!.Identity!.Ip);
        Assert.IsTrue(session.Current.Direct.Latest!.Success);
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task NewEgressIsImmediateAndOldDiagnosticsCannotOverwriteIt()
    {
        var direct = Channel.CreateUnbounded<IdentityResult>();
        var proxy = Channel.CreateUnbounded<IdentityResult>();
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        using var session = Create(direct, proxy, async (_, _) =>
        {
            if (Interlocked.Increment(ref call) == 1)
            {
                oldStarted.SetResult();
                await releaseOld.Task;
                oldFinished.SetResult();
                return new[] { new ServiceResult("old", true, ProbeError.None, 200, DateTimeOffset.Now) };
            }

            return new[] { new ServiceResult("new", true, ProbeError.None, 401, DateTimeOffset.Now) };
        });
        session.Start(new());
        await proxy.Writer.WriteAsync(Success("203.0.113.2", "JP"));
        await WaitFor(session, s => s.Proxy.Latest?.Identity?.Ip == "203.0.113.2");
        Assert.IsTrue(session.Current.Stabilizing);
        Assert.AreEqual(0, call);
        await proxy.Writer.WriteAsync(Success("203.0.113.2", "JP"));
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await proxy.Writer.WriteAsync(Success("203.0.113.3", "US"));
        await WaitFor(session, s => s.Proxy.Latest?.Identity?.Ip == "203.0.113.3");
        Assert.IsTrue(session.Current.Stabilizing);
        Assert.AreEqual(0, session.Current.Services.Count);
        await proxy.Writer.WriteAsync(Success("203.0.113.3", "US"));
        await WaitFor(session, s => s.Services.Any(item => item.Name == "new"));
        releaseOld.SetResult();
        await oldFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("new", session.Current.Services.Single().Name);
        Assert.AreEqual("203.0.113.3", session.Current.Proxy.LastSuccess!.Identity!.Ip);
    }

    [TestMethod]
    public async Task LateIdentityCannotRepopulateAfterStopAndRestart()
    {
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource<IdentityResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var incoming = Channel.CreateUnbounded<IdentityResult>();
        int calls = 0;
        using var session = new NetMapSession(
            async (isProxy, _, token) =>
            {
                if (isProxy && Interlocked.Increment(ref calls) == 1)
                {
                    oldStarted.SetResult();
                    return await releaseOld.Task;
                }

                return await incoming.Reader.ReadAsync(token);
            },
            (_, _, _) => Task.FromResult<IReadOnlyList<ServiceResult>>([]),
            (_, _, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(2));
        session.Start(new());
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Stop();
        Task oldRun = session.Completion;
        var stopped = session.Current;
        Assert.IsFalse(stopped.Running);
        session.Start(new());
        releaseOld.SetResult(Success("203.0.113.99", "JP"));
        await oldRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(session.Current.Proxy.LastSuccess);
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task SameEgressIsNotAutomaticallyAProxyFailure()
    {
        var direct = Channel.CreateUnbounded<IdentityResult>();
        var proxy = Channel.CreateUnbounded<IdentityResult>();
        using var session = Create(direct, proxy);
        session.Start(new());
        await direct.Writer.WriteAsync(Success("203.0.113.1", "CN"));
        await proxy.Writer.WriteAsync(Success("203.0.113.1", "CN"));
        await WaitFor(session, s => s.Direct.Latest != null && s.Proxy.Latest != null);
        Assert.IsTrue(session.Current.Proxy.Latest!.Success);
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task RestartPreservesLastSuccessButMarksItAsNotYetObserved()
    {
        var direct = Channel.CreateUnbounded<IdentityResult>();
        var proxy = Channel.CreateUnbounded<IdentityResult>();
        using var session = Create(direct, proxy);
        session.Start(new());
        await proxy.Writer.WriteAsync(Success("203.0.113.2", "JP"));
        await WaitFor(session, s => s.Proxy.LastSuccess != null);
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        session.Start(new());
        Assert.IsNull(session.Current.Proxy.Latest);
        Assert.IsNotNull(session.Current.Proxy.LastSuccess);
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task UnexpectedProbeFailureStopsBothLoopsWithoutLeakingTask()
    {
        using var session = new NetMapSession(
            (_, _, _) => throw new InvalidOperationException("Synthetic transport failure"),
            (_, _, _) => Task.FromResult<IReadOnlyList<ServiceResult>>([]),
            (_, _, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(2));
        session.Start(new());
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(session.Current.Running);
        Assert.IsTrue(session.Current.Faulted);
    }

    [TestMethod]
    public async Task ThreeFailuresClearOnlyTheFailingEgressAndSuccessRestoresIt()
    {
        var direct = Channel.CreateUnbounded<IdentityResult>();
        var proxy = Channel.CreateUnbounded<IdentityResult>();
        using var session = Create(direct, proxy);
        session.Start(new(OnlineLookup: false));
        await direct.Writer.WriteAsync(Success("192.0.2.1", "CN"));
        await proxy.Writer.WriteAsync(Success("192.0.2.2", "JP"));
        await WaitFor(session, s => s.Direct.LastSuccess != null && s.Proxy.LastSuccess != null);
        for (int failures = 1; failures <= 3; failures++)
        {
            await proxy.Writer.WriteAsync(new(null, ProbeError.Timeout, DateTimeOffset.Now, "Explicit"));
            await WaitFor(session, s => s.Proxy.ConsecutiveFailures == failures);
            Assert.AreEqual(failures < 3, session.Current.Proxy.LastSuccess != null);
            Assert.IsNotNull(session.Current.Direct.LastSuccess);
        }

        Assert.IsNull(session.Current.Proxy.Geography);
        await proxy.Writer.WriteAsync(Success("192.0.2.3", "SG"));
        await WaitFor(session, s => s.Proxy.ConsecutiveFailures == 0 && s.Proxy.LastSuccess?.Identity?.Ip == "192.0.2.3");
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task RepeatedServiceChecksCountProgressAndBatchOnceAndRecover()
    {
        var proxy = Channel.CreateUnbounded<IdentityResult>();
        var replies = Channel.CreateUnbounded<ServiceResult>();
        int active = 0;
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
            async (_, report, token) =>
            {
                Assert.AreEqual(1, Interlocked.Increment(ref active), "Service rounds must not overlap.");
                try
                {
                    var result = await replies.Reader.ReadAsync(token);
                    report(result);
                    return new[] { result };
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            (_, _, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(20));
        session.Start(new(OnlineLookup: false));
        for (int i = 0; i < 8; i++)
        {
            await proxy.Writer.WriteAsync(Success("192.0.2.2", "JP"));
        }

        var success = new ServiceResult("ChatGPT", true, ProbeError.None, 200, DateTimeOffset.Now, "TraceResponded", 25, Checkpoint: new("192.0.2.4", "JP", "JP"));
        await replies.Writer.WriteAsync(success);
        await WaitFor(session, s => s.Services.Count == 1);
        for (int failures = 1; failures <= 3; failures++)
        {
            await replies.Writer.WriteAsync(new("ChatGPT", true, ProbeError.Timeout, null, DateTimeOffset.Now));
            await WaitFor(session, s => s.Services[0].ConsecutiveFailures == failures);
            await Task.Delay(30);
            Assert.AreEqual(failures, session.Current.Services[0].ConsecutiveFailures, "The completed batch must not count the same failure twice.");
            Assert.AreEqual(failures < 3, session.Current.Services[0].LastKnownCheckpoint != null);
        }

        Assert.IsNull(session.Current.Services[0].LastKnownMs);
        await replies.Writer.WriteAsync(success with { Time = DateTimeOffset.Now, ElapsedMs = 76 });
        await WaitFor(session, s => s.Services[0].ConsecutiveFailures == 0 && s.Services[0].Health == ProbeHealth.Warning);
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static IdentityResult Success(string ip, string country) => new(new(ip, country, country), ProbeError.None, DateTimeOffset.Now, "Explicit");

    private static NetMapSession Create(Channel<IdentityResult> direct, Channel<IdentityResult> proxy, Func<NetMapOptions, CancellationToken, Task<IReadOnlyList<ServiceResult>>>? services = null) => new(
        async (isProxy, _, token) => await (isProxy ? proxy : direct).Reader.ReadAsync(token),
        (options, _, token) => services?.Invoke(options, token) ?? Task.FromResult<IReadOnlyList<ServiceResult>>([]),
        (_, _, _) => Task.CompletedTask,
        TimeSpan.FromMilliseconds(2),
        TimeSpan.FromMilliseconds(2));

    private static async Task WaitFor(NetMapSession session, Func<NetMapSnapshot, bool> predicate)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(NetMapSnapshot snapshot)
        {
            if (predicate(snapshot))
            {
                ready.TrySetResult();
            }
        }

        session.Changed += Check;
        try
        {
            Check(session.Current);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            session.Changed -= Check;
        }
    }
}
