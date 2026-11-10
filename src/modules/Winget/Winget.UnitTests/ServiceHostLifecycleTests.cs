// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using ManagedCommon.ServiceHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class ServiceHostLifecycleTests
{
    [TestMethod]
    public void LateRunnerOrExitCallbackAfterDisposeIsHarmless()
    {
        var host = new TestServiceHost();
        host.Dispose();

        // RegisteredWaitHandle.Unregister(null) cannot retract a queued callback.
        // The runner watcher can also finish after host resources have been released.
        host.Stop();
        host.Stop();
        host.Dispose();
    }

    [TestMethod]
    public void StopStillNotifiesCancellationBeforeResourcesAreReleased()
    {
        var host = new TestServiceHost();
        int callbacks = 0;
        using var registration = host.CancellationToken.Register(() => Interlocked.Increment(ref callbacks));

        host.Stop();
        host.Stop();
        Assert.AreEqual(1, callbacks);
        host.Dispose();
        host.Stop();
    }

    private sealed class TestSettings
    {
        public TestSettings()
        {
        }
    }

    private sealed class TestServiceHost : KitModuleServiceBase<TestSettings>
    {
        public override string ModuleKey => "LifecycleTest";
        public override string ServiceExeName => "LifecycleTest.exe";
        public override string ExitEventName => string.Empty;
        public override string RefreshSettingsEventName => string.Empty;

        protected override Task StartServiceAsync(TestSettings initialSettings, CancellationToken ct) => Task.CompletedTask;
        protected override Task StopServiceAsync() => Task.CompletedTask;
        protected override TestSettings DeserializeSettings(string json) => new();
        protected override string SerializeSettings(TestSettings settings) => "{}";
    }
}
