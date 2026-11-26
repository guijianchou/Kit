// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace ViewModelTests;

[TestClass]
public class DashboardUpdatingTests
{
    private static readonly string[] ExpectedDeliveredVersions = ["3.6.0"];

    [TestMethod]
    [DataRow(UpdatingSettings.UpdatingState.ReadyToDownload)]
    [DataRow(UpdatingSettings.UpdatingState.ReadyToInstall)]
    [DataRow(UpdatingSettings.UpdatingState.Downloading)]
    [DataRow(UpdatingSettings.UpdatingState.Verifying)]
    [DataRow(UpdatingSettings.UpdatingState.Installing)]
    public void AlreadyInstalledReleaseDoesNotAdvertiseAnUpdate(UpdatingSettings.UpdatingState state)
    {
        Assert.AreEqual(string.Empty, DashboardUpdateNotice.ResourceKey(State(state, "3.5.11"), "v3.6.0"));
        Assert.AreEqual(string.Empty, DashboardUpdateNotice.ResourceKey(State(state, "3.6.0"), "3.6.0.0"));
        Assert.AreEqual(string.Empty, DashboardUpdateNotice.ResourceKey(State(state, "3.6.0.0"), "v3.6.0"));
    }

    [TestMethod]
    public void UnknownAndFreshStatesClearThePreviousNotice()
    {
        Assert.AreEqual(string.Empty, DashboardUpdateNotice.ResourceKey(null, "3.6.0"));
        Assert.AreEqual(string.Empty, DashboardUpdateNotice.ResourceKey(new UpdatingSettings(), "3.6.0"));
        Assert.AreEqual(string.Empty, DashboardUpdateNotice.ResourceKey(State(UpdatingSettings.UpdatingState.ReadyToDownload, "invalid"), "3.6.0"));
        Assert.AreEqual(string.Empty, DashboardUpdateNotice.ResourceKey(State(UpdatingSettings.UpdatingState.UpToDate, "3.7.0"), "3.6.0"));
        Assert.AreEqual("Dashboard_UpdateCheckFailed", DashboardUpdateNotice.ResourceKey(new UpdatingSettings { State = UpdatingSettings.UpdatingState.NetworkError }, "3.6.0"));
        Assert.AreEqual("Dashboard_UpdateRecovery", DashboardUpdateNotice.ResourceKey(new UpdatingSettings { ErrorCode = "recoveryRequired" }, "3.6.0"));
    }

    [TestMethod]
    [DataRow(UpdatingSettings.UpdatingState.ReadyToDownload, "Dashboard_UpdateAvailable")]
    [DataRow(UpdatingSettings.UpdatingState.ReadyToInstall, "Dashboard_UpdateReady")]
    [DataRow(UpdatingSettings.UpdatingState.Downloading, "Dashboard_UpdateDownloading")]
    [DataRow(UpdatingSettings.UpdatingState.Verifying, "Dashboard_UpdateVerifying")]
    [DataRow(UpdatingSettings.UpdatingState.Installing, "Dashboard_UpdateInstalling")]
    [DataRow(UpdatingSettings.UpdatingState.ErrorDownloading, "Dashboard_UpdateFailed")]
    public void NewerReleaseUsesItsActualState(UpdatingSettings.UpdatingState state, string expected) =>
        Assert.AreEqual(expected, DashboardUpdateNotice.ResourceKey(State(state, "3.7.0"), "3.6.0"));

    [TestMethod]
    public async Task SharedObserverIgnoresAnOlderReadAndReleasesItsWatcher()
    {
        var observer = new Mock<IFileSystemWatcher>();
        int watcherCount = 0;
        int reads = 0;
        using var releaseFirstRead = new ManualResetEventSlim();
        var firstReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<UpdatingSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<string>();
        var source = new UpdateStateSource(
            () =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    firstReadStarted.TrySetResult();
                    if (!releaseFirstRead.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new InvalidOperationException("The test did not release its state read.");
                    }

                    return State(UpdatingSettings.UpdatingState.ReadyToDownload, "3.7.0");
                }

                return State(UpdatingSettings.UpdatingState.UpToDate, "3.6.0");
            },
            _ =>
            {
                Interlocked.Increment(ref watcherCount);
                return observer.Object;
            });

        using IDisposable first = source.Subscribe(settings => delivered.Enqueue(settings!.NewVersion));
        try
        {
            await firstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using IDisposable second = source.Subscribe(settings => received.TrySetResult(settings!));
            source.Refresh();
            releaseFirstRead.Set();
            UpdatingSettings latest = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(UpdatingSettings.UpdatingState.UpToDate, latest.State);
            CollectionAssert.AreEqual(ExpectedDeliveredVersions, delivered.ToArray(), "A pending older read cannot restore a stale update badge.");
            Assert.AreEqual(1, watcherCount, "Home and General share one observer.");
            observer.Verify(item => item.Dispose(), Times.Never);
            first.Dispose();
            observer.Verify(item => item.Dispose(), Times.Never, "The second page still owns its subscription.");
        }
        finally
        {
            releaseFirstRead.Set();
            first.Dispose();
        }

        observer.Verify(item => item.Dispose(), Times.Once);
        Assert.AreEqual("3.6.0", source.Snapshot!.NewVersion);
    }

    private static UpdatingSettings State(UpdatingSettings.UpdatingState state, string version) => new()
    {
        State = state,
        ReleasePageLink = "https://github.com/guijianchou/Kit/releases/tag/" + version,
    };
}
