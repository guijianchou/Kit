// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The scheduler owns the cadence loop for headless auditing. These tests use a real
/// pipeline with a short poll interval so the loop is exercised without waiting hours.
/// </summary>
[TestClass]
public sealed class AuditSchedulerTests
{
    private string _dataDirectory = string.Empty;

    [TestInitialize]
    public void CreateIsolatedDataDirectory()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "KitAuditSchedulerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
    }

    [TestCleanup]
    public void RemoveIsolatedDataDirectory()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    private AuditPipeline CreatePipeline() => new(historyStorage: new AuditHistoryStorage(_dataDirectory));

    [TestMethod]
    public void DisabledCadenceKeepsTheRoundedInterval()
    {
        using var scheduler = new AuditScheduler(CreatePipeline(), intervalHours: 0);

        Assert.AreEqual(0, scheduler.IntervalHours);
        Assert.IsFalse(scheduler.IsDue(), "A zero interval never becomes due.");
    }

    [TestMethod]
    public void OversizedIntervalIsClamped()
    {
        using var scheduler = new AuditScheduler(CreatePipeline(), intervalHours: 10_000);

        Assert.AreEqual(AuditSchedule.MaximumIntervalHours, scheduler.IntervalHours);
    }

    [TestMethod]
    public void FirstCheckIsDueBecauseNoAuditHasRun()
    {
        using var scheduler = new AuditScheduler(CreatePipeline(), intervalHours: 24);

        Assert.IsTrue(scheduler.IsDue(), "A fresh schedule should not wait a whole interval.");
    }

    [TestMethod]
    public void DisabledSwitchSuppressesDueChecks()
    {
        using var scheduler = new AuditScheduler(CreatePipeline(), intervalHours: 1, isEnabled: () => false);

        Assert.IsFalse(scheduler.IsDue(), "A disabled module must not schedule work.");
    }

    [TestMethod]
    public async Task RunOnceProducesAndStoresAnAudit()
    {
        var storage = new AuditHistoryStorage(_dataDirectory);
        var scheduler = new AuditScheduler(new AuditPipeline(historyStorage: storage), intervalHours: 24);

        AuditResult? result = await scheduler.RunOnceAsync();

        Assert.IsNotNull(result);
        Assert.IsTrue(result.HealthScore is >= 0 and <= 100);
        Assert.AreEqual(1, storage.LoadHistory().Count, "A scheduled run persists its result.");
        Assert.IsNotNull(scheduler.LastCompletedUtc, "The completion time drives the next due check.");
    }

    [TestMethod]
    public async Task RunOnceIsNotDueAgainImmediately()
    {
        var scheduler = new AuditScheduler(CreatePipeline(), intervalHours: 24);

        await scheduler.RunOnceAsync();

        Assert.IsFalse(scheduler.IsDue(), "After a run the next one waits for the interval.");
    }

    [TestMethod]
    public async Task DisabledModuleSkipsTheRunEntirely()
    {
        var storage = new AuditHistoryStorage(_dataDirectory);
        var scheduler = new AuditScheduler(new AuditPipeline(historyStorage: storage), intervalHours: 24, isEnabled: () => false);

        AuditResult? result = await scheduler.RunOnceAsync();

        Assert.IsNull(result, "A disabled module must not audit, even when asked directly.");
        Assert.AreEqual(0, storage.LoadHistory().Count);
    }

    [TestMethod]
    public async Task StartRunsDueWorkAndStopEndsTheLoop()
    {
        var storage = new AuditHistoryStorage(_dataDirectory);
        var scheduler = new AuditScheduler(
            new AuditPipeline(historyStorage: storage),
            intervalHours: 24,
            pollInterval: TimeSpan.FromMilliseconds(50));

        scheduler.Start();
        Assert.IsTrue(scheduler.IsRunning);

        // Wait for the first due run to land.
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (storage.LoadHistory().Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await scheduler.StopAsync();
        Assert.IsFalse(scheduler.IsRunning, "StopAsync must end the loop.");
        Assert.AreEqual(1, storage.LoadHistory().Count, "The loop ran the due audit exactly once.");
    }

    [TestMethod]
    public async Task StartingTwiceDoesNotCreateASecondLoop()
    {
        using var scheduler = new AuditScheduler(
            CreatePipeline(),
            intervalHours: 24,
            pollInterval: TimeSpan.FromMilliseconds(50));

        scheduler.Start();
        scheduler.Start();

        await scheduler.StopAsync();
        Assert.IsFalse(scheduler.IsRunning);
    }

    [TestMethod]
    public async Task StopWithoutStartIsSafe()
    {
        using var scheduler = new AuditScheduler(CreatePipeline(), intervalHours: 24);

        await scheduler.StopAsync();
    }

    [TestMethod]
    public void UpdateIntervalTakesEffectForTheNextCheck()
    {
        using var scheduler = new AuditScheduler(CreatePipeline(), intervalHours: 24);

        scheduler.UpdateInterval(48);
        Assert.AreEqual(48, scheduler.IntervalHours);

        scheduler.UpdateInterval(0);
        Assert.AreEqual(0, scheduler.IntervalHours);
        Assert.IsFalse(scheduler.IsDue(), "Turning the schedule off stops due checks immediately.");
    }
}
