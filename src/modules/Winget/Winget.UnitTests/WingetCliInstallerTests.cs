// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Kit.WingetLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class WingetCliInstallerTests
{
    private static readonly WingetPackageUpdate Package = new("Vendor.App", "App", string.Empty, "2.0", "winget", null, null, WingetInstallEligibility.RequiresElevation, "May request administrator approval", string.Empty);

    [TestMethod]
    public void NativeCommandUsesExactArgumentListAndRetainsWinGetVerificationAndDependencies()
    {
        var package = Package with { Id = "Vendor.App'$(calc); space" };
        var start = WingetCliInstaller.CreateStartInfo(WingetCliInstaller.ExecutablePath, package, upgrade: false, acceptPackageAgreements: false);
        Assert.IsFalse(start.UseShellExecute);
        Assert.IsTrue(start.CreateNoWindow);
        Assert.AreEqual("utf-8", start.StandardOutputEncoding?.WebName);
        Assert.AreEqual("utf-8", start.StandardErrorEncoding?.WebName);
        Assert.AreEqual(string.Empty, start.Verb);
        Assert.AreEqual(string.Empty, start.Arguments);
        Assert.AreEqual(WingetCliInstaller.ExecutablePath, start.FileName);
        CollectionAssert.AreEqual(
            new[]
            {
                "install", "--id", package.Id, "--exact", "--version", "2.0", "--source", "winget", "--interactive", "--disable-interactivity", "--no-upgrade",
            },
            start.ArgumentList.ToArray());
        foreach (string disallowed in new[] { "--force", "--ignore-security-hash", "--allow-reboot", "--skip-dependencies", "--location", "--accept-source-agreements", "--accept-package-agreements" })
        {
            Assert.IsFalse(start.ArgumentList.Contains(disallowed));
        }

        var upgrade = WingetCliInstaller.CreateStartInfo(WingetCliInstaller.ExecutablePath, Package, upgrade: true, acceptPackageAgreements: true);
        Assert.AreEqual("upgrade", upgrade.ArgumentList[0]);
        Assert.IsFalse(upgrade.ArgumentList.Contains("--no-upgrade"));
        Assert.IsTrue(upgrade.ArgumentList.Contains("--accept-package-agreements"));
    }

    [TestMethod]
    public void UntrustedExecutableOrSourceIsRejectedWithoutLaunching()
    {
        Assert.ThrowsExactly<WingetOperationException>(() => WingetCliInstaller.CreateStartInfo("winget.exe", Package, false, false));
        Assert.ThrowsExactly<WingetOperationException>(() => WingetCliInstaller.CreateStartInfo(Path.Combine(Path.GetTempPath(), "winget.exe"), Package, false, false));
        Assert.ThrowsExactly<WingetOperationException>(() => WingetCliInstaller.CreateStartInfo(WingetCliInstaller.ExecutablePath, Package with { Source = "third-party" }, false, false));
    }

    [TestMethod]
    public async Task PreCancelledRequestNeverCallsProcessRunner()
    {
        var runner = new FakeRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new WingetCliInstaller(runner).RunAsync(Package, false, false, null, cancellation.Token));
        Assert.AreEqual(0, runner.Calls);
    }

    [TestMethod]
    public async Task CancellationKeepsNativeOperationOccupiedUntilRealExitAndPreservesSuccess()
    {
        var runner = new FakeRunner();
        using var cancellation = new CancellationTokenSource();
        Task<WingetInstallResult> operation = new WingetCliInstaller(runner).RunAsync(Package, false, false, null, cancellation.Token);
        await runner.Started.Task;
        cancellation.Cancel();
        Assert.IsFalse(operation.IsCompleted);
        Assert.IsTrue(runner.CancellationObserved);
        runner.Exit.TrySetResult(0);
        var result = await operation;
        Assert.IsTrue(result.Succeeded, "A cancel request must not overwrite the native successful result.");
        Assert.IsTrue(result.CancellationRequested);
        Assert.IsFalse(result.Cancelled);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FakeInstallationUsesValidatedSilentScopeArchitectureAndUnknownOptions(bool upgrade)
    {
        var runner = new FakeRunner();
        var options = new WingetPackageOptions { Silent = true, Scope = "Machine", Architecture = "X86", IncludeUnknown = true };
        var operation = new WingetCliInstaller(runner).RunAsync(Package, upgrade, true, null, CancellationToken.None, options);
        await runner.Started.Task;
        var arguments = runner.StartInfo!.ArgumentList;
        Assert.IsTrue(arguments.Contains("--silent"));
        Assert.IsFalse(arguments.Contains("--interactive"));
        Assert.AreEqual("machine", arguments[arguments.IndexOf("--scope") + 1]);
        Assert.AreEqual("x86", arguments[arguments.IndexOf("--architecture") + 1]);
        Assert.AreEqual(upgrade, arguments.Contains("--include-unknown"));
        Assert.IsFalse(arguments.Contains("--skip-dependencies"));
        Assert.IsFalse(arguments.Contains("--force"));
        Assert.IsFalse(arguments.Contains("--allow-reboot"));
        runner.Exit.TrySetResult(0);
        Assert.IsTrue((await operation).Succeeded);
    }

    [DataTestMethod]
    [DataRow("winget", true)]
    [DataRow("local", false)]
    [DataRow("msix", false)]
    public async Task FakeUninstallTargetsOnlyExactInstalledIdentityAndVersion(string source, bool hasSource)
    {
        var runner = new FakeRunner();
        var package = Package with { Source = source, InstalledVersion = "1.0", AvailableVersion = string.Empty, CanUninstall = true };
        var operation = new WingetCliInstaller(runner).RunUninstallAsync(package, null, CancellationToken.None, new() { Scope = "User" });
        await runner.Started.Task;
        var start = runner.StartInfo!;
        var arguments = start.ArgumentList;
        Assert.AreEqual("uninstall", arguments[0]);
        Assert.AreEqual(package.Id, arguments[arguments.IndexOf("--id") + 1]);
        Assert.AreEqual("1.0", arguments[arguments.IndexOf("--version") + 1]);
        Assert.AreEqual("user", arguments[arguments.IndexOf("--scope") + 1]);
        Assert.IsTrue(arguments.Contains("--exact"));
        Assert.IsTrue(arguments.Contains("--interactive"));
        Assert.AreEqual(hasSource, arguments.Contains("--source"));
        Assert.IsFalse(start.UseShellExecute);
        Assert.AreEqual(string.Empty, start.Verb);
        foreach (string disallowed in new[] { "--all", "--all-versions", "--force", "--purge", "--include-unknown", "--architecture", "--accept-package-agreements", "--accept-source-agreements" })
        {
            Assert.IsFalse(arguments.Contains(disallowed));
        }

        runner.Exit.TrySetResult(0);
        Assert.IsTrue((await operation).Succeeded);
    }

    [TestMethod]
    public async Task CancellingFakeUninstallWaitsForActualExitAndMapsNativeCancellation()
    {
        var runner = new FakeRunner();
        using var cancellation = new CancellationTokenSource();
        var package = Package with { InstalledVersion = "1.0", CanUninstall = true };
        var operation = new WingetCliInstaller(runner).RunUninstallAsync(package, null, cancellation.Token);
        await runner.Started.Task;
        cancellation.Cancel();
        Assert.IsTrue(runner.CancellationObserved);
        Assert.IsFalse(operation.IsCompleted);
        runner.Exit.TrySetResult(unchecked((int)0x800704C7));
        var result = await operation;
        Assert.IsTrue(result.Cancelled);
        Assert.IsTrue(result.CancellationRequested);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task InvalidOptionsOrUnverifiedRemovalNeverReachFakeRunner()
    {
        var runner = new FakeRunner();
        var installer = new WingetCliInstaller(runner);
        var installed = Package with { InstalledVersion = "1.0", CanUninstall = true };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => installer.RunAsync(Package, false, false, null, CancellationToken.None, new() { Scope = "all" }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => installer.RunAsync(Package, false, false, null, CancellationToken.None, new() { Architecture = "x64 --force" }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => installer.RunUninstallAsync(installed, null, CancellationToken.None, new() { Architecture = "x64" }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => installer.RunUninstallAsync(installed with { Id = "--all" }, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<WingetOperationException>(() => installer.RunUninstallAsync(installed with { CanUninstall = false }, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<WingetOperationException>(() => installer.RunUninstallAsync(installed with { Source = "third-party" }, null, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => installer.RunUninstallAsync(installed, null, cancelled.Token));
        Assert.AreEqual(0, runner.Calls);
    }

    [DataTestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(true, 0)]
    [DataRow(true, unchecked((int)0x800704C7))]
    public async Task OutputSnapshotsArePublishedWithoutGuessedPercentagesAndRetainedInEveryResult(bool uninstall, int exitCode)
    {
        var runner = new FakeRunner();
        var progress = new CapturingProgress();
        var installer = new WingetCliInstaller(runner);
        var operation = uninstall
            ? installer.RunUninstallAsync(Package with { InstalledVersion = "1.0", CanUninstall = true }, progress, CancellationToken.None)
            : installer.RunAsync(Package, false, false, progress, CancellationToken.None);
        await runner.Started.Task;
        runner.OutputReceived!("Downloading 47%\n");
        runner.OutputReceived!("Native operation finished\n");
        runner.Exit.TrySetResult(exitCode);
        var result = await operation;
        Assert.AreEqual("Native operation finished\n", result.Output);
        Assert.AreEqual("Native operation finished\n", progress.Items[^1].Output);
        Assert.IsTrue(progress.Items.All(item => item.Percentage is null), "Localized output is display text, never parsed as package progress.");
        Assert.AreEqual(exitCode == 0, result.Succeeded);
    }

    [TestMethod]
    public async Task ThrowingOutputObserverDoesNotAlterNativeExitOrLoseResultOutput()
    {
        var runner = new FakeRunner();
        var progress = new CapturingProgress { Throw = true };
        var operation = new WingetCliInstaller(runner).RunAsync(Package, false, false, progress, CancellationToken.None);
        await runner.Started.Task;
        runner.OutputReceived!("Completed\n");
        runner.Exit.TrySetResult(0);
        var result = await operation;
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("Completed\n", result.Output);
    }

    [DataTestMethod]
    [DataRow(0, true, false, false)]
    [DataRow(3010, true, true, false)]
    [DataRow(unchecked((int)0x8A150109), true, true, false)]
    [DataRow(unchecked((int)0x8A15010A), false, false, false)]
    [DataRow(unchecked((int)0x800704C7), false, false, true)]
    [DataRow(unchecked((int)0x8A15010C), false, false, true)]
    [DataRow(unchecked((int)0x8A150011), false, false, false)]
    public void ExitCodeMappingDistinguishesSuccessRebootCancellationAndHashFailure(int code, bool succeeded, bool reboot, bool cancelled)
    {
        var result = WingetCliInstaller.MapExitCode(code);
        Assert.AreEqual(succeeded, result.Succeeded);
        Assert.AreEqual(reboot, result.RebootRequired);
        Assert.AreEqual(cancelled, result.Cancelled);
        Assert.AreEqual(code, result.ErrorCode);
    }

    private sealed class CapturingProgress : IProgress<WingetOperationProgress>
    {
        internal bool Throw { get; init; }
        internal List<WingetOperationProgress> Items { get; } = [];

        public void Report(WingetOperationProgress value)
        {
            Items.Add(value);
            if (Throw)
            {
                throw new InvalidOperationException("Synthetic UI observer failure.");
            }
        }
    }

    private sealed class FakeRunner : IWingetCliProcessRunner
    {
        internal int Calls { get; private set; }
        internal bool CancellationObserved { get; private set; }
        internal ProcessStartInfo? StartInfo { get; private set; }
        internal Action<string>? OutputReceived { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<int> RunAsync(ProcessStartInfo startInfo, Action<string> outputReceived, Action cancellationRequested, CancellationToken cancellationToken)
        {
            Calls++;
            StartInfo = startInfo;
            OutputReceived = outputReceived;
            Assert.IsTrue(Path.IsPathFullyQualified(startInfo.FileName));
            using var registration = cancellationToken.Register(() =>
            {
                CancellationObserved = true;
                cancellationRequested();
            });
            Started.TrySetResult();
            return await Exit.Task;
        }
    }
}
