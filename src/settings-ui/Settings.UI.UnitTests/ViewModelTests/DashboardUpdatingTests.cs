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

    [TestMethod]
    public void UpdateStatusPresentationFormatsRelativeCheckTimesAndStates()
    {
        DateTimeOffset now = new(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);

        DashboardUpdatePresentation checking = DashboardUpdateStatus.Resolve(null, "3.6.1", true, now);
        Assert.AreEqual(DashboardUpdateStatusKind.Normal, checking.Kind);
        Assert.AreEqual("Dashboard_UpdateChecking", checking.SubtitleResourceKey);

        DashboardUpdatePresentation unknown = DashboardUpdateStatus.Resolve(null, "3.6.1", false, now);
        Assert.AreEqual(DashboardUpdateStatusKind.Normal, unknown.Kind);
        Assert.AreEqual("Dashboard_UpdateCheckPrompt", unknown.SubtitleResourceKey);

        DashboardUpdatePresentation upToDateNoTimestamp = DashboardUpdateStatus.Resolve(new UpdatingSettings { State = UpdatingSettings.UpdatingState.UpToDate }, "3.6.1", false, now);
        Assert.AreEqual("Dashboard_UpdateCheckPrompt", upToDateNoTimestamp.SubtitleResourceKey);

        DashboardUpdatePresentation justNow = DashboardUpdateStatus.Resolve(UpToDateCheckedAt(now - TimeSpan.FromSeconds(25)), "3.6.1", false, now);
        Assert.AreEqual("Dashboard_UpdateLastCheckedJustNow", justNow.SubtitleResourceKey);
        Assert.AreEqual("Last checked: Just now", DashboardUpdateStatus.FormatSubtitle(justNow, TestResource));

        DashboardUpdatePresentation oneMinute = DashboardUpdateStatus.Resolve(UpToDateCheckedAt(now - TimeSpan.FromMinutes(1.5)), "3.6.1", false, now);
        Assert.AreEqual("Dashboard_UpdateLastCheckedMinute", oneMinute.SubtitleResourceKey);

        DashboardUpdatePresentation fifteenMinutes = DashboardUpdateStatus.Resolve(UpToDateCheckedAt(now - TimeSpan.FromMinutes(15)), "3.6.1", false, now);
        Assert.AreEqual("Dashboard_UpdateLastCheckedMinutes", fifteenMinutes.SubtitleResourceKey);
        Assert.AreEqual(15, fifteenMinutes.RelativeAmount);
        Assert.AreEqual("Last checked: 15 minutes ago", DashboardUpdateStatus.FormatSubtitle(fifteenMinutes, TestResource));

        DashboardUpdatePresentation oneHour = DashboardUpdateStatus.Resolve(UpToDateCheckedAt(now - TimeSpan.FromHours(1.2)), "3.6.1", false, now);
        Assert.AreEqual("Dashboard_UpdateLastCheckedHour", oneHour.SubtitleResourceKey);

        DashboardUpdatePresentation eightHours = DashboardUpdateStatus.Resolve(UpToDateCheckedAt(now - TimeSpan.FromHours(8)), "3.6.1", false, now);
        Assert.AreEqual("Dashboard_UpdateLastCheckedHours", eightHours.SubtitleResourceKey);
        Assert.AreEqual(8, eightHours.RelativeAmount);
        Assert.AreEqual("Last checked: 8 hours ago", DashboardUpdateStatus.FormatSubtitle(eightHours, TestResource));

        DashboardUpdatePresentation oneDay = DashboardUpdateStatus.Resolve(UpToDateCheckedAt(now - TimeSpan.FromDays(1.2)), "3.6.1", false, now);
        Assert.AreEqual("Dashboard_UpdateLastCheckedDay", oneDay.SubtitleResourceKey);

        DashboardUpdatePresentation threeDays = DashboardUpdateStatus.Resolve(UpToDateCheckedAt(now - TimeSpan.FromDays(3)), "3.6.1", false, now);
        Assert.AreEqual("Dashboard_UpdateLastCheckedDays", threeDays.SubtitleResourceKey);
        Assert.AreEqual(3, threeDays.RelativeAmount);

        DashboardUpdatePresentation available = DashboardUpdateStatus.Resolve(State(UpdatingSettings.UpdatingState.ReadyToDownload, "3.7.0"), "3.6.1", false, now);
        Assert.AreEqual(DashboardUpdateStatusKind.Attention, available.Kind);
        Assert.AreEqual("Dashboard_UpdateAvailable", available.SubtitleResourceKey);

        DashboardUpdatePresentation ready = DashboardUpdateStatus.Resolve(State(UpdatingSettings.UpdatingState.ReadyToInstall, "3.7.0"), "3.6.1", false, now);
        Assert.AreEqual(DashboardUpdateStatusKind.Attention, ready.Kind);
        Assert.AreEqual("Dashboard_UpdateReady", ready.SubtitleResourceKey);

        DashboardUpdatePresentation networkError = DashboardUpdateStatus.Resolve(new UpdatingSettings { State = UpdatingSettings.UpdatingState.NetworkError }, "3.6.1", false, now);
        Assert.AreEqual(DashboardUpdateStatusKind.Error, networkError.Kind);
        Assert.AreEqual("Dashboard_UpdateCheckFailed", networkError.SubtitleResourceKey);

        DashboardUpdatePresentation recovery = DashboardUpdateStatus.Resolve(new UpdatingSettings { ErrorCode = "recoveryRequired" }, "3.6.1", false, now);
        Assert.AreEqual(DashboardUpdateStatusKind.Error, recovery.Kind);
        Assert.AreEqual("Dashboard_UpdateRecovery", recovery.SubtitleResourceKey);
    }

    [TestMethod]
    public async Task ManualCheckFlagNotifiesActiveSubscribers()
    {
        var observer = new Mock<IFileSystemWatcher>();
        int notifications = 0;
        var initialReadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UpdateStateSource(
            () => new UpdatingSettings { State = UpdatingSettings.UpdatingState.UpToDate },
            _ => observer.Object);

        using IDisposable subscription = source.Subscribe(_ =>
        {
            if (Interlocked.Increment(ref notifications) == 1)
            {
                initialReadDone.TrySetResult();
            }
        });

        await initialReadDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.SetManualCheckInProgress(true);
        Assert.IsTrue(source.IsChecking);
        Assert.AreEqual(2, Volatile.Read(ref notifications));

        source.SetManualCheckInProgress(false);
        Assert.IsFalse(source.IsChecking);
        Assert.AreEqual(3, Volatile.Read(ref notifications));
    }

    private static UpdatingSettings UpToDateCheckedAt(DateTimeOffset checkedAt) => new()
    {
        State = UpdatingSettings.UpdatingState.UpToDate,
        LastCheckedDate = checkedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string TestResource(string key) => key switch
    {
        "Dashboard_UpdateLastCheckedJustNow" => "Last checked: Just now",
        "Dashboard_UpdateLastCheckedMinutes" => "Last checked: {0} minutes ago",
        "Dashboard_UpdateLastCheckedHours" => "Last checked: {0} hours ago",
        _ => key,
    };

    private static UpdatingSettings State(UpdatingSettings.UpdatingState state, string version) => new()
    {
        State = state,
        ReleasePageLink = "https://github.com/guijianchou/Kit/releases/tag/" + version,
    };
}
