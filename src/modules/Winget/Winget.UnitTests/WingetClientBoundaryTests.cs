// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Kit.WingetLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class WingetClientBoundaryTests
{
    [TestMethod]
    public async Task PreCanceledDetectionDoesNotTouchWinGet()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => new WingetClient().DetectAsync(canceled.Token));
    }

    [TestMethod]
    public async Task PreCancelledSearchAndUpgradeDoNotTouchWinGet()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var client = new WingetClient();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => client.SearchAsync("Example", cancelled.Token));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => client.ListInstalledAsync(cancelled.Token));
        var package = new WingetPackageUpdate("Example.App", "App", "1", "2", "winget", null, null, WingetInstallEligibility.RequiresElevation, string.Empty, string.Empty);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => client.UpgradeAsync(package, cancellationToken: cancelled.Token));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => client.UninstallAsync(package, cancellationToken: cancelled.Token));
    }

    [TestMethod]
    public async Task ForgedEligibilityCannotEnableInstallationFromAnotherSource()
    {
        var package = new WingetPackageUpdate("Vendor.App", "App", "1", "2", "UntrustedSource", null, null, WingetInstallEligibility.UserWithoutElevation, "Forged", "unused");
        await Assert.ThrowsExactlyAsync<WingetOperationException>(() => new WingetClient().InstallAsync(package));
        await Assert.ThrowsExactlyAsync<WingetOperationException>(() => new WingetClient().UninstallAsync(package with { CanUninstall = true }));
    }

    [TestMethod]
    public async Task UnresolvedDiscoveryCannotBypassReviewByForgingEligibility()
    {
        var package = WingetClient.CreateSearchSummary("Example.App", "Example") with
        {
            InstallEligibility = WingetInstallEligibility.UserWithoutElevation,
        };
        var client = new WingetClient();
        await Assert.ThrowsExactlyAsync<WingetOperationException>(() => client.InstallAsync(package));
        await Assert.ThrowsExactlyAsync<WingetOperationException>(() => client.UpgradeAsync(package));
        await Assert.ThrowsExactlyAsync<WingetOperationException>(() => client.DownloadAsync(package));
    }
}
