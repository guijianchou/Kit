namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AuditEvidenceSessionTests
{
    private static readonly DateTime End = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task ExpandingAndReturningRangesOnlyReadsMissingIntervals()
    {
        var calls = new List<AuditEvidenceWindow>();
        var source = new[] { Event(1), Event(5), Event(20) };
        await using var session = new AuditEvidenceSession((window, _, _, _, _) =>
        {
            calls.Add(window);
            return Task.FromResult(new AuditEvidenceBatch(source, [], []));
        });
        Assert.AreEqual(1, (await session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Extended, 5000)).Events.Count);
        Assert.AreEqual(2, (await session.LoadAsync(End.AddDays(-7), End, EventLogService.AuditMode.Extended, 5000)).Events.Count);
        Assert.AreEqual(new AuditEvidenceWindow(End.AddDays(-7), End.AddDays(-2)), calls[1]);
        Assert.AreEqual(3, (await session.LoadAsync(End.AddDays(-30), End, EventLogService.AuditMode.Extended, 5000)).Events.Count);
        Assert.AreEqual(new AuditEvidenceWindow(End.AddDays(-30), End.AddDays(-7)), calls[2]);
        var returned = await session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Extended, 5000);
        Assert.AreEqual(3, calls.Count);
        Assert.AreEqual(0, returned.CollectedIntervals);
        Assert.AreEqual(1, returned.ReusedEvents);
        Assert.IsFalse(returned.HasMissingIntervals);
    }

    [TestMethod]
    public async Task FullScanReplacesEveryCachedIntervalAndNavigationReusesTheNewEvidence()
    {
        var calls = new List<AuditEvidenceWindow>();
        var current = new[] { Event(1), Event(5), Event(20) };
        await using var session = new AuditEvidenceSession((window, _, _, _, _) =>
        {
            calls.Add(window);
            return Task.FromResult(new AuditEvidenceBatch(current, [], []));
        });
        await session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Extended, 5000);
        await session.LoadAsync(End.AddDays(-7), End, EventLogService.AuditMode.Extended, 5000);
        await session.LoadAsync(End.AddDays(-30), End, EventLogService.AuditMode.Extended, 5000);
        current = [Event(3) with { Message = "replacement evidence" }];
        var full = await session.LoadAsync(End.AddDays(-7), End, EventLogService.AuditMode.Extended, 5000,
            refreshEntireWindow: true);
        Assert.AreEqual(new AuditEvidenceWindow(End.AddDays(-7), End), calls[^1]);
        Assert.AreEqual(1, full.CollectedIntervals);
        Assert.AreEqual(1, full.ReadEvents);
        Assert.AreEqual(0, full.ReusedEvents);
        CollectionAssert.AreEqual(current, full.Events, "Full scan must remove stale cached events from successfully read channels.");
        var month = await session.LoadAsync(End.AddDays(-30), End, EventLogService.AuditMode.Extended, 5000);
        Assert.AreEqual(4, calls.Count);
        Assert.AreEqual(0, month.ReadEvents);
        Assert.AreEqual(2, month.ReusedEvents);
        CollectionAssert.Contains(month.Events, Event(20));
        CollectionAssert.Contains(month.Events, current[0]);
        Assert.IsTrue(calls.All(window => window.ToUtc <= End), "Refreshing historical evidence must not move its cutoff to the current time.");
    }

    [TestMethod]
    public async Task TailRefreshPreservesCompletedOlderIntervalsAndAvoidsBoundaryDuplicates()
    {
        var calls = new List<AuditEvidenceWindow>();
        var boundary = new SecurityEvent(41, "System", "Kernel-Power", 1, End, "boundary");
        await using var session = new AuditEvidenceSession((window, _, _, _, _) =>
        {
            calls.Add(window);
            return Task.FromResult(new AuditEvidenceBatch([Event(10), boundary], [], []));
        });
        await session.LoadAsync(End.AddDays(-30), End, EventLogService.AuditMode.Extended, 5000);
        var refreshed = await session.LoadAsync(End.AddMinutes(10).AddDays(-30), End.AddMinutes(10),
            EventLogService.AuditMode.Extended, 5000, End.AddMinutes(-2));
        Assert.AreEqual(2, calls.Count);
        Assert.AreEqual(new AuditEvidenceWindow(End.AddMinutes(-2), End.AddMinutes(10)), calls[1]);
        Assert.AreEqual(2, refreshed.Events.Count);
        Assert.AreEqual(1, refreshed.Events.Count(item => item == boundary));
        Assert.AreEqual(1, refreshed.ReadEvents);
        Assert.AreEqual(1, refreshed.ReusedEvents);
    }

    [TestMethod]
    public async Task ChangedCollectionModeInvalidatesEvidenceAndLimitFlagsRemainVisible()
    {
        int calls = 0;
        await using var session = new AuditEvidenceSession((_, _, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new AuditEvidenceBatch([Event(1)], ["Security"], ["System"]));
        });
        await session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Extended, 500);
        var full = await session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Full, 500);
        Assert.AreEqual(2, calls);
        CollectionAssert.Contains(full.SkippedChannels, "Security");
        CollectionAssert.Contains(full.LimitedChannels, "System");
        await session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Full, 5000);
        Assert.AreEqual(3, calls, "A higher event limit must revisit a previously truncated interval.");
    }

    [TestMethod]
    public async Task CancelledIntervalsDoNotEnterCacheAndMemoryEvictionDoesNotClaimCoverage()
    {
        int calls = 0;
        using var cancellation = new CancellationTokenSource();
        await using var session = new AuditEvidenceSession((_, _, _, _, _) =>
        {
            if (++calls == 1)
            {
                cancellation.Cancel();
            }

            return Task.FromResult(new AuditEvidenceBatch([Event(1), Event(5)], [], []));
        }, maximumEvents: 1);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => session.LoadAsync(End.AddDays(-7), End,
            EventLogService.AuditMode.Extended, 5000, cancellationToken: cancellation.Token));
        var result = await session.LoadAsync(End.AddDays(-7), End, EventLogService.AuditMode.Extended, 5000);
        Assert.AreEqual(2, calls);
        Assert.IsTrue(result.HasMissingIntervals);
        Assert.AreEqual(0, result.Events.Count);
    }

    [TestMethod]
    public async Task ManualRefreshRetriesPreviouslySkippedIntervalsBeyondTheTail()
    {
        var calls = new List<AuditEvidenceWindow>();
        await using var session = new AuditEvidenceSession((window, _, _, _, _) =>
        {
            calls.Add(window);
            return Task.FromResult(new AuditEvidenceBatch([Event(1)], calls.Count == 1 ? ["Security"] : [], []));
        });
        await session.LoadAsync(End.AddDays(-7), End, EventLogService.AuditMode.Extended, 5000);
        var cached = await session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Extended, 5000);
        Assert.AreEqual(1, calls.Count);
        CollectionAssert.Contains(cached.SkippedChannels, "Security");
        var refreshed = await session.LoadAsync(End.AddDays(-7), End, EventLogService.AuditMode.Extended, 5000,
            End.AddMinutes(-2), retrySkippedIntervals: true);
        Assert.AreEqual(2, calls.Count);
        Assert.AreEqual(new AuditEvidenceWindow(End.AddDays(-7), End), calls[1]);
        Assert.AreEqual(0, refreshed.SkippedChannels.Count);
    }

    [TestMethod]
    public async Task UnreadableChannelsRetainPreviousEvidenceUntilASuccessfulReadReplacesIt()
    {
        var system = Event(1);
        var security = new SecurityEvent(4625, "Security", "Audit", 2, End.AddHours(-1), "denied");
        var updated = security with { Message = "updated" };
        int calls = 0;
        await using var session = new AuditEvidenceSession((_, _, _, _, _) => Task.FromResult(++calls switch
        {
            1 => new AuditEvidenceBatch([system], ["Security"], []),
            2 => new AuditEvidenceBatch([security], ["system"], []),
            3 => new AuditEvidenceBatch([system, security], ["System"], []),
            _ => new AuditEvidenceBatch([updated], [], []),
        }));
        await session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Extended, 5000);
        var unreadable = await session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Extended, 5000, retrySkippedIntervals: true);
        Assert.AreEqual(2, unreadable.Events.Count);
        CollectionAssert.Contains(unreadable.Events, system);
        CollectionAssert.Contains(unreadable.SkippedChannels, "system");
        Assert.AreEqual(1, unreadable.ReusedEvents, "Evidence retained from an unreadable channel is reused, not freshly collected.");
        var overlap = await session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Extended, 5000, retrySkippedIntervals: true);
        Assert.AreEqual(2, overlap.Events.Count, "A partial read must not duplicate the same previously retained event.");
        var recovered = await session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Extended, 5000, retrySkippedIntervals: true);
        CollectionAssert.AreEqual(new[] { updated }, recovered.Events);
        Assert.AreEqual(0, recovered.SkippedChannels.Count);
        Assert.AreEqual(0, recovered.ReusedEvents, "A successfully read empty channel must remove its old retained evidence.");
    }

    [TestMethod]
    public async Task DisposalCancelsAndDrainsActiveAndQueuedLoadsBeforeReleasingResources()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new AuditEvidenceSession(async (_, _, _, _, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
            return new AuditEvidenceBatch([], [], []);
        });
        var active = session.LoadAsync(End.AddDays(-2), End, EventLogService.AuditMode.Extended, 5000);
        await entered.Task;
        var queued = session.LoadAsync(End.AddDays(-7), End, EventLogService.AuditMode.Extended, 5000);
        session.Dispose();
        var disposal = session.DisposeAsync().AsTask();
        await cancelled.Task;
        Assert.IsFalse(disposal.IsCompleted, "Disposal must wait for the active collector before disposing its gate.");
        release.TrySetResult();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => queued);
        await disposal;
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => session.LoadAsync(End.AddDays(-2), End,
            EventLogService.AuditMode.Extended, 5000));
    }

    private static SecurityEvent Event(int daysAgo) => new(41, "System", "Kernel-Power", 1, End.AddDays(-daysAgo), "interruption");
}
