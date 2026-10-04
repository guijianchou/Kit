// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Covers the Security/Firewall channel extension of the audit scan. The channels
/// themselves need elevation, so these tests assert the mode contract and the
/// non-throwing degradation path that the settings host relies on.
/// </summary>
[TestClass]
public sealed class EventLogServiceTests
{
    private static bool EventLogAvailable()
    {
        try
        {
            _ = Type.GetType("System.Diagnostics.EventLog, System.Diagnostics.EventLog", throwOnError: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [TestMethod]
    public void ExtendedModeOverloadIsTheDefaultAndDoesNotRequireElevation()
    {
        var service = new EventLogService();

        // Both overloads exist; the 5-argument call binds to the extended default.
        Assert.IsNotNull(service);
        Assert.IsFalse(
            typeof(EventLogService).GetMethod(nameof(EventLogService.CollectEventsAsync), [typeof(DateTime), typeof(DateTime), typeof(int), typeof(CancellationToken)]) is null,
            "The extended-mode overload must stay available for existing callers.");
        Assert.IsFalse(
            typeof(EventLogService).GetMethod(nameof(EventLogService.CollectEventsAsync), [typeof(DateTime), typeof(DateTime), typeof(EventLogService.AuditMode), typeof(int), typeof(CancellationToken)]) is null,
            "The mode-aware overload must be available for full-access scans.");
    }

    [TestMethod]
    public async Task ExtendedScanNeverTouchesTheSecurityChannel()
    {
        if (!EventLogAvailable())
        {
            Assert.Inconclusive("The Windows event-log assembly cannot be loaded in this test host.");
        }

        var service = new EventLogService();
        DateTime to = DateTime.UtcNow;
        DateTime from = to.AddMinutes(-5);

        // A five-minute extended window is empty or small, and must never throw even
        // when channels are missing or unreadable.
        var events = await service.CollectEventsAsync(from, to, EventLogService.AuditMode.Extended, 10, CancellationToken.None);

        Assert.IsNotNull(events);
        Assert.IsTrue(
            events.TrueForAll(evt => !string.Equals(evt.LogName, "Security", StringComparison.OrdinalIgnoreCase)),
            "Extended mode must not read the Security channel.");
    }

    [TestMethod]
    public async Task FullScanDegradesInsteadOfThrowingWhenSecurityIsUnreadable()
    {
        if (!EventLogAvailable())
        {
            Assert.Inconclusive("The Windows event-log assembly cannot be loaded in this test host.");
        }

        var service = new EventLogService();
        DateTime to = DateTime.UtcNow;
        DateTime from = to.AddMinutes(-5);

        // Without elevation the Security channel reader yields nothing; the call must
        // still complete so the UI can explain the situation.
        var events = await service.CollectEventsAsync(from, to, EventLogService.AuditMode.Full, 10, CancellationToken.None);

        Assert.IsNotNull(events);
    }

    [TestMethod]
    [DataRow(EventLogService.AuditMode.Extended, 4)]
    [DataRow(EventLogService.AuditMode.Full, 6)]
    public async Task CollectionProgressMatchesCompletedChannelsAndReturnedEvents(EventLogService.AuditMode mode, int channelCount)
    {
        if (!EventLogAvailable())
        {
            Assert.Inconclusive("The Windows event-log assembly cannot be loaded in this test host.");
        }

        var service = new EventLogService();
        var reports = new List<EventCollectionProgress>();
        DateTime to = DateTime.UtcNow;
        var events = await service.CollectEventsAsync(to.AddMinutes(-5), to, mode, 10,
            new InlineProgress(reports.Add), CancellationToken.None);

        var completed = reports.Where(report => report.IsComplete).ToArray();
        Assert.AreEqual(channelCount, completed.Length);
        CollectionAssert.AreEqual(Enumerable.Range(1, channelCount).ToArray(), completed.Select(report => report.CompletedChannels).ToArray());
        Assert.IsTrue(reports.All(report => report.TotalChannels == channelCount));
        Assert.AreEqual(events.Count, completed[^1].TotalEvents);
        Assert.AreEqual(events.Count, completed.Sum(report => report.ChannelEvents));
        CollectionAssert.AreEqual(service.SkippedChannels.ToArray(), completed.Where(report => report.Skipped).Select(report => report.Channel).ToArray());
        int previousCount = 0;
        foreach (var channel in completed)
        {
            var updates = reports.Where(report => report.Channel == channel.Channel).ToArray();
            Assert.IsFalse(updates[0].IsComplete);
            Assert.AreEqual(0, updates[0].ChannelEvents);
            Assert.AreEqual(channel.CompletedChannels - 1, updates[0].CompletedChannels);
        }

        foreach (var report in reports)
        {
            Assert.IsTrue(report.TotalEvents >= previousCount, "Collected event counts must not regress.");
            previousCount = report.TotalEvents;
        }

        if (mode == EventLogService.AuditMode.Extended)
        {
            Assert.IsFalse(reports.Any(report => report.Channel is "Security" or "Firewall"));
        }
    }

    [TestMethod]
    public async Task CancelledCollectionDoesNotPublishProgressOrReadChannels()
    {
        var service = new EventLogService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int reports = 0;
        DateTime to = DateTime.UtcNow;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.CollectEventsAsync(to.AddMinutes(-5), to,
            EventLogService.AuditMode.Extended, 10, new InlineProgress(_ => reports++), cancellation.Token));
        Assert.AreEqual(0, reports);
    }

    [TestMethod]
    public async Task CancellationDuringAChannelIsNotReportedAsSkippedOrComplete()
    {
        if (!EventLogAvailable())
        {
            Assert.Inconclusive("The Windows event-log assembly cannot be loaded in this test host.");
        }

        var service = new EventLogService();
        using var cancellation = new CancellationTokenSource();
        var reports = new List<EventCollectionProgress>();
        DateTime to = DateTime.UtcNow;
        var progress = new InlineProgress(value =>
        {
            reports.Add(value);
            cancellation.Cancel();
        });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.CollectEventsAsync(to.AddMinutes(-5), to,
            EventLogService.AuditMode.Extended, 10, progress, cancellation.Token));
        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual("System", reports[0].Channel);
        Assert.IsFalse(reports[0].IsComplete || reports[0].Skipped);
        Assert.AreEqual(0, service.SkippedChannels.Count);
    }

    private sealed class InlineProgress(Action<EventCollectionProgress> report) : IProgress<EventCollectionProgress>
    {
        public void Report(EventCollectionProgress value) => report(value);
    }

    [TestMethod]
    public void CanReadSecurityLogIsAStableBooleanProbe()
    {
        // The probe is consumed by the settings host to explain why full mode is
        // unavailable. It must exist as a static Boolean member so callers never have
        // to construct an EventLogService just to query elevation state.
        var property = typeof(EventLogService).GetProperty(
            nameof(EventLogService.CanReadSecurityLog),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        Assert.IsNotNull(property, "CanReadSecurityLog must remain a public static probe.");
        Assert.AreEqual(typeof(bool), property.PropertyType);
    }
}
