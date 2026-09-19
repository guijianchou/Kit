// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace UDPtest.UnitTests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UDPtestLib.Core;
using UDPtestLib.Engine;

/// <summary>
/// The coordinator's lifecycle contract, exercised through its guard clauses. Starting a
/// real probe run needs network access, so these tests pin the validation and state rules
/// that protect callers from a mis-configured run.
/// </summary>
[TestClass]
public sealed class ProbeCoordinatorTests
{
    private static ProbeLineDefinition Line(string id, bool enabled = true, int port = 3478)
        => new(id, id, ProbeProtocol.Udp, ProbeKind.StunBinding, "127.0.0.1", port, enabled);

    [TestMethod]
    public void CoordinatorStartsInTheStoppedState()
    {
        var coordinator = new ProbeCoordinator();

        Assert.AreEqual(MonitorRunState.Stopped, coordinator.State);
    }

    [TestMethod]
    public async Task StartingWithoutAnyEnabledLineIsRejected()
    {
        var coordinator = new ProbeCoordinator();
        var lines = new List<ProbeLineDefinition> { Line("a", enabled: false) };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.StartAsync(lines, 1000));
    }

    [TestMethod]
    public async Task NullLineListIsRejected()
    {
        var coordinator = new ProbeCoordinator();

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => coordinator.StartAsync(null!, 1000));
    }

    [TestMethod]
    public async Task UnsupportedRefreshIntervalIsRejected()
    {
        var coordinator = new ProbeCoordinator();
        var lines = new List<ProbeLineDefinition> { Line("a") };

        // Only the four documented intervals are accepted; an arbitrary value must fail
        // loudly rather than silently running at a different cadence.
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => coordinator.StartAsync(lines, 750));
    }

    [TestMethod]
    public async Task StoppingAnIdleCoordinatorIsSafe()
    {
        var coordinator = new ProbeCoordinator();

        await coordinator.StopAsync();

        Assert.AreEqual(MonitorRunState.Stopped, coordinator.State);
    }

    [TestMethod]
    public async Task ClearingHistoryOnAnIdleCoordinatorIsSafe()
    {
        var coordinator = new ProbeCoordinator();

        await coordinator.ClearHistoryAsync();

        Assert.AreEqual(MonitorRunState.Stopped, coordinator.State);
    }

    [TestMethod]
    public async Task DisposingTheCoordinatorLeavesItStopped()
    {
        var coordinator = new ProbeCoordinator();

        await coordinator.DisposeAsync();

        Assert.AreEqual(MonitorRunState.Stopped, coordinator.State);
    }

    [TestMethod]
    public async Task TogglingALineWhileStoppedIsRejected()
    {
        var coordinator = new ProbeCoordinator();

        // Line toggles are only meaningful during a run; asking outside one must fail
        // loudly instead of silently doing nothing.
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => coordinator.SetLineEnabledAsync("does-not-exist", false));

        Assert.AreEqual(MonitorRunState.Stopped, coordinator.State);
    }

    [TestMethod]
    public async Task ApplyingAnUnsupportedIntervalWhileStoppedIsRejected()
    {
        var coordinator = new ProbeCoordinator();
        var lines = new List<ProbeLineDefinition> { Line("a") };

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => coordinator.StartAsync(lines, 1234, CancellationToken.None));
    }

    [TestMethod]
    public async Task DisposalIsIdempotent()
    {
        var coordinator = new ProbeCoordinator();

        // DisposeAsync twice must not throw; the coordinator is used from page teardown.
        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();
    }
}
