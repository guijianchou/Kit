// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO.Pipes;
using System.Text;
using ManagedCommon.ServiceHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class ServiceHostReviewTests
{
    [TestMethod]
    public async Task PipeDisposalCancelsAndDrainsTheHandlerAndIsRepeatable()
    {
        string key = "ReviewPipe_" + Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new KitServicePipeServer(key, async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return KitPipeResponse.Ok();
        });
        server.Start();
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var wire = new NamedPipeClientStream(".", KitServicePipeUtils.GetPipeName(key), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await wire.ConnectAsync(timeout.Token);
            await wire.WriteAsync(Encoding.UTF8.GetBytes("{\"Command\":\"wait\"}\n"), timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            Task disposing = server.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(timeout.Token);
            Assert.IsFalse(disposing.IsCompleted, "Disposal must retain ownership until the handler has stopped.");
            release.TrySetResult();
            await disposing.WaitAsync(timeout.Token);
            await server.DisposeAsync();
            Assert.ThrowsExactly<ObjectDisposedException>(server.Start);
        }
        finally
        {
            release.TrySetResult();
            await server.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task OversizedPipeInputIsRejectedAndTheListenerStillAcceptsRequests()
    {
        string key = "ReviewBounded_" + Guid.NewGuid().ToString("N");
        int handlerCalls = 0;
        await using var server = new KitServicePipeServer(key, (_, _) =>
        {
            Interlocked.Increment(ref handlerCalls);
            return Task.FromResult(KitPipeResponse.Ok("ack"));
        });
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var wire = new NamedPipeClientStream(".", KitServicePipeUtils.GetPipeName(key), PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await wire.ConnectAsync(timeout.Token);
            try
            {
                await wire.WriteAsync(new byte[(1024 * 1024) + 1], timeout.Token);
                byte[] response = new byte[1];
                Assert.AreEqual(0, await wire.ReadAsync(response, timeout.Token));
            }
            catch (IOException)
            {
                // Windows can report the peer's rejection as a broken pipe.
            }
        }

        Assert.AreEqual(0, handlerCalls);
        var client = new KitServicePipeClient(key, Environment.ProcessPath!, TimeSpan.FromSeconds(3));
        Assert.IsTrue((await client.SendCommandAsync("status", ct: timeout.Token)).Success);
        Assert.AreEqual(1, handlerCalls);
    }

    [TestMethod]
    public async Task DisposingARunningHostStopsItWithoutOverwritingInvalidSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KitHostReview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        const string original = "{ invalid existing settings";
        File.WriteAllText(path, original);
        using var host = new TestHost(directory);
        try
        {
            Task<int> running = host.RunAsync([]);
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.Dispose();
            Assert.AreEqual(0, await running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(host.Stopped);
            Assert.AreEqual(original, File.ReadAllText(path));
        }
        finally
        {
            host.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestSettings
    {
        public TestSettings()
        {
        }
    }

    private sealed class TestHost(string directory) : KitModuleServiceBase<TestSettings>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }
        public override string ModuleKey { get; } = "ReviewHost_" + Guid.NewGuid().ToString("N");
        public override string ServiceExeName => "ReviewHost.exe";
        public override string ExitEventName => string.Empty;
        public override string RefreshSettingsEventName => string.Empty;

        protected override string GetSettingsFolderPath() => directory;

        protected override Task StartServiceAsync(TestSettings initialSettings, CancellationToken ct)
        {
            Started.TrySetResult();
            return Task.CompletedTask;
        }

        protected override Task StopServiceAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        protected override TestSettings DeserializeSettings(string json) => null!;

        protected override string SerializeSettings(TestSettings settings) => "{}";
    }
}
