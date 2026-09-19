// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The audit pipeline is the headless entry point that the scheduled audit (and a future
/// worker) rely on, so its persistence contract and window handling are pinned here. The
/// event-log read itself is not asserted: it depends on the machine's channels.
/// </summary>
[TestClass]
public sealed class AuditPipelineTests
{
    private string _dataDirectory = string.Empty;

    [TestInitialize]
    public void CreateIsolatedDataDirectory()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "KitAuditPipelineTests", Guid.NewGuid().ToString("N"));
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

    [TestMethod]
    public async Task RunProducesAResultWithAScoreInsideTheValidRange()
    {
        var pipeline = new AuditPipeline(historyStorage: new AuditHistoryStorage(_dataDirectory));
        DateTime to = DateTime.UtcNow;

        AuditResult result = await pipeline.RunAsync(to.AddMinutes(-5), to, persist: false);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.HealthScore is >= 0 and <= 100);
        Assert.IsNotNull(result.Findings);
        Assert.IsTrue(result.ScanStart <= result.ScanEnd);
    }

    [TestMethod]
    public async Task PersistingStoresTheAuditInTheHistory()
    {
        var storage = new AuditHistoryStorage(_dataDirectory);
        var pipeline = new AuditPipeline(historyStorage: storage);
        DateTime to = DateTime.UtcNow;

        await pipeline.RunAsync(to.AddMinutes(-5), to, persist: true);

        Assert.AreEqual(1, storage.LoadHistory().Count, "A persisted audit must appear in the history.");
    }

    [TestMethod]
    public async Task NotPersistingLeavesTheHistoryUntouched()
    {
        var storage = new AuditHistoryStorage(_dataDirectory);
        var pipeline = new AuditPipeline(historyStorage: storage);
        DateTime to = DateTime.UtcNow;

        await pipeline.RunAsync(to.AddMinutes(-5), to, persist: false);

        Assert.AreEqual(0, storage.LoadHistory().Count, "An exploratory run must not write history.");
    }

    [TestMethod]
    public async Task ReversedWindowsAreNormalisedInsteadOfFailing()
    {
        var pipeline = new AuditPipeline(historyStorage: new AuditHistoryStorage(_dataDirectory));
        DateTime now = DateTime.UtcNow;

        AuditResult result = await pipeline.RunAsync(now, now.AddMinutes(-5), persist: false);

        Assert.IsTrue(result.ScanStart <= result.ScanEnd, "The pipeline must order the window itself.");
    }

    [TestMethod]
    public async Task RangeOverloadUsesTheRequestedLookBack()
    {
        var pipeline = new AuditPipeline(historyStorage: new AuditHistoryStorage(_dataDirectory));

        AuditResult result = await pipeline.RunForRangeAsync(TimeSpan.FromMinutes(5), persist: false);

        Assert.IsNotNull(result.ScanEnd);
        Assert.IsNotNull(result.ScanStart);
        Assert.IsTrue(result.ScanEnd - result.ScanStart <= TimeSpan.FromMinutes(6));
    }

    [TestMethod]
    public async Task StatisticsComeFromTheSameHistoryThePipelineWrites()
    {
        var storage = new AuditHistoryStorage(_dataDirectory);
        var pipeline = new AuditPipeline(historyStorage: storage);
        DateTime to = DateTime.UtcNow;

        await pipeline.RunAsync(to.AddMinutes(-5), to, persist: true);
        AuditHistoryStatistics statistics = await pipeline.GetStatisticsAsync();

        Assert.AreEqual(1, statistics.AuditCount);
        Assert.IsTrue(statistics.HasHistory);
    }

    [TestMethod]
    public async Task CancellationIsHonoured()
    {
        var pipeline = new AuditPipeline(historyStorage: new AuditHistoryStorage(_dataDirectory));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        DateTime to = DateTime.UtcNow;

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => pipeline.RunAsync(to.AddMinutes(-5), to, persist: false, cancellationToken: cts.Token));
    }

    [TestMethod]
    public void PipelineConstructsWithoutAWindowOrAView()
    {
        // A headless worker constructs it exactly like this: no UI, no repository, no page.
        var pipeline = new AuditPipeline();

        Assert.IsNotNull(pipeline);
    }
}
