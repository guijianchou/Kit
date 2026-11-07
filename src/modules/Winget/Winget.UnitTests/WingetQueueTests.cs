// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using Kit.WingetLib;
using Kit.WingetService;
using ManagedCommon.ServiceHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class WingetQueueTests
{
    private static readonly WingetPackageUpdate First = new("Example.One", "Example One", "1.0", "2.0", "winget", null, null, WingetInstallEligibility.UserWithoutElevation, "User install", "winget upgrade --id Example.One --exact") { CanUninstall = true, IsUpdateAvailable = true };
    private static readonly WingetPackageUpdate Second = First with { Id = "Example.Two", Name = "Example Two" };
    private string _directory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "KitWingetQueueTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void DefaultNotificationCandidatesExcludeUnknownAndIgnoredWithoutDroppingFullSnapshot()
    {
        var unknown = First with { Id = "Example.Unknown", InstalledVersion = "uNkNoWn" };
        var sameIdOtherSource = First with { Source = "other-source" };
        var state = new WingetPersistentState { IgnoredPackages = [WingetServicePolicy.Identity(First)], NotificationFingerprint = "previous" };
        state = WingetServicePolicy.CompleteCheck(state, DateTimeOffset.UtcNow, [First, unknown, sameIdOtherSource]);
        Assert.AreEqual(3, state.Packages.Count);
        Assert.AreEqual(sameIdOtherSource, WingetServicePolicy.VisibleUpdates(state).Single());
        state = WingetServicePolicy.CompleteCheck(state, DateTimeOffset.UtcNow, [First, unknown]);
        Assert.AreEqual(2, state.Packages.Count);
        Assert.AreEqual(0, WingetServicePolicy.VisibleUpdates(state).Count);
        Assert.IsNull(state.NotificationFingerprint, "No visible updates resets notification deduplication for a future visible update.");
    }

    [TestMethod]
    public async Task InstalledWorkspaceAndIgnoredUpdatesUseFullSourceIdentityAndSurviveRestart()
    {
        var differentSource = First with { Source = "local" };
        Seed(First, differentSource);
        var notifier = new FakeNotifier();
        var client = new FakeClient { Updates = [First, differentSource], Installed = [First, differentSource] };
        await using (var runtime = Create(client, notifier))
        {
            Assert.IsTrue((await Send(runtime, "ignore", new() { Packages = [WingetServicePolicy.Identity(First)] })).Accepted);
            Assert.AreEqual(2, runtime.GetStatus().Packages.Count, "Ignored updates remain in the complete service snapshot.");
            Assert.IsTrue((await Send(runtime, "check")).Accepted);
            await Idle(runtime);
            Assert.AreEqual(1, notifier.Shown.Single().Count);
            Assert.AreEqual("local", notifier.Shown.Single().Single().Source);
            Assert.IsTrue((await Send(runtime, "list-installed")).Accepted);
            await Idle(runtime);
            Assert.AreEqual(2, runtime.GetStatus().InstalledPackages.Count);
            Assert.AreEqual(0, client.ActionCalls.Count);
        }

        await using var restarted = Create(new());
        Assert.AreEqual("winget", restarted.GetStatus().IgnoredPackages.Single().Source);
        Assert.IsTrue((await Send(restarted, "unignore", new() { Packages = [WingetServicePolicy.Identity(First with { AvailableVersion = "9.0" })] })).Accepted);
        Assert.AreEqual(0, new WingetStateStore(_directory).Load().IgnoredPackages.Count, "Unignore matches source + ID even after a version changes.");
    }

    [TestMethod]
    public async Task PreparationRejectsStaleDuplicateOversizedAndUnsupportedOptionsWithoutCallingInstaller()
    {
        Seed(First);
        var client = new FakeClient();
        await using var runtime = Create(client);
        var identity = WingetServicePolicy.Identity(First);
        Assert.IsFalse((await Send(runtime, "prepare", new() { OperationKind = "upgrade", Packages = [identity with { InstalledVersion = "0.5" }] })).Accepted);
        Assert.IsFalse((await Send(runtime, "prepare", new() { OperationKind = "upgrade", Packages = [identity, identity] })).Accepted);
        Assert.IsFalse((await Send(runtime, "prepare", new() { OperationKind = "upgrade", Packages = Enumerable.Repeat(identity, 51).ToArray() })).Accepted);
        Assert.IsFalse((await Send(runtime, "prepare", new() { OperationKind = "upgrade", Packages = [identity], Options = new() { Architecture = "arm" } })).Accepted);
        Assert.IsFalse((await Send(runtime, "prepare", new() { OperationKind = "upgrade", Packages = [identity], Options = new() { Scope = "arbitrary arguments" } })).Accepted);
        Assert.AreEqual(0, client.DetailsCalls);
        Assert.AreEqual(0, client.ActionCalls.Count);
    }

    [TestMethod]
    public async Task ConfirmationBindsAgreementsOptionsAndOneUseTokenBeforeStrictlySerialExecution()
    {
        Seed(First, Second);
        var options = new WingetPackageOptions { Silent = true, Scope = "user", Architecture = "x64", IncludeUnknown = true };
        var client = new FakeClient { Agreements = [new("Terms", "Synthetic package terms", null)] };
        await using var runtime = Create(client);
        var prepared = await Prepare(runtime, "upgrade", [First, Second], options);
        Assert.AreEqual(0, client.ActionCalls.Count, "Preparation only reads metadata.");
        Assert.AreEqual(2, prepared.Packages.Count);
        Assert.IsFalse((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id })).Accepted);
        Assert.IsFalse((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id, AcceptPackageAgreements = true, Options = options with { Silent = false } })).Accepted);
        Assert.IsTrue((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id, AcceptPackageAgreements = true })).Accepted);
        await Idle(runtime);
        Assert.AreEqual(2, client.ActionCalls.Count);
        Assert.AreEqual(1, client.MaximumConcurrentActions);
        Assert.IsTrue(client.ActionCalls.All(call => call.Options == options && call.AcceptAgreements));
        Assert.IsTrue(runtime.GetStatus().Queue.All(item => item.State == "Succeeded"));
        Assert.AreEqual(2, new WingetStateStore(_directory).Load().History.Count);
        Assert.IsFalse((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id, AcceptPackageAgreements = true })).Accepted, "Confirmation tokens cannot be replayed.");
    }

    [TestMethod]
    public async Task PreparationExpiresAtTenMinutesAndRefreshInvalidatesIt()
    {
        Seed(First);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var client = new FakeClient();
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier(), () => now);
        var prepared = await Prepare(runtime, "upgrade", [First]);
        now = now.AddMinutes(10);
        Assert.IsNull(runtime.GetStatus().PreparedOperation);
        Assert.IsFalse((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id })).Accepted);
        prepared = await Prepare(runtime, "upgrade", [First]);
        await Send(runtime, "check");
        await Idle(runtime);
        Assert.IsFalse((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id })).Accepted);
        Assert.AreEqual(0, client.ActionCalls.Count);
    }

    [TestMethod]
    public async Task CancellingQueueLeavesCurrentNativeOperationBusyAndCancelsOnlyLaterItems()
    {
        Seed(First, Second);
        var client = new FakeClient { BlockFirstAction = true };
        await using var runtime = Create(client);
        try
        {
            var prepared = await Prepare(runtime, "upgrade", [First, Second]);
            var ack = await Send(runtime, "enqueue", new() { PreparationId = prepared.Id });
            await client.ActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsNotNull(new WingetStateStore(_directory).Load().PendingInstall, "The durable recovery marker must precede native invocation.");
            Assert.IsTrue((await Send(runtime, "cancel-queue", new() { OperationId = ack.OperationId })).Accepted);
            Assert.IsTrue(runtime.GetStatus().IsBusy);
            Assert.IsFalse(client.CurrentToken.IsCancellationRequested, "Queue cancellation must not cancel the running native operation.");
            Assert.AreEqual("Cancelled", new WingetStateStore(_directory).Load().Queue[1].State);
            Assert.IsFalse((await Send(runtime, "list-installed")).Accepted, "The operation gate stays reserved until native completion.");
            client.ActionRelease.TrySetResult();
            await Idle(runtime);
            Assert.AreEqual(1, client.ActionCalls.Count);
            CollectionAssert.AreEqual(new[] { "Succeeded", "Cancelled" }, runtime.GetStatus().Queue.Select(item => item.State).ToArray());
        }
        finally
        {
            client.ActionRelease.TrySetResult();
        }
    }

    [TestMethod]
    public async Task OrdinaryFailureReadsInstalledStateBeforeContinuingRemainingItems()
    {
        Seed(First, Second);
        var client = new FakeClient { Results = [new(false, false, false, "Synthetic installer failure", 1), new(true, false, false, null, null)] };
        await using var runtime = Create(client);
        var prepared = await Prepare(runtime, "upgrade", [First, Second]);
        await Send(runtime, "enqueue", new() { PreparationId = prepared.Id });
        await Idle(runtime);
        Assert.AreEqual(2, client.ActionCalls.Count);
        Assert.AreEqual(1, client.CheckCalls);
        Assert.AreEqual(1, client.InstalledCalls);
        CollectionAssert.AreEqual(new[] { "Failed", "Succeeded" }, runtime.GetStatus().Queue.Select(item => item.State).ToArray());
        Assert.IsFalse(runtime.GetStatus().RecoveryRequired);
    }

    [DataTestMethod]
    [DataRow("cancelled")]
    [DataRow("pending")]
    [DataRow("manual")]
    [DataRow("recovery-failed")]
    public async Task UncertainOrCancelledActionStopsTheRestAndRequiresReadOnlyRecovery(string kind)
    {
        Seed(First, Second);
        var result = new WingetInstallResult(kind == "pending", false, kind == "manual", "Synthetic uncertain result", null)
        {
            Cancelled = kind == "cancelled", VerificationPending = kind == "pending",
        };
        var client = new FakeClient { Results = [result], FailChecks = kind == "recovery-failed" };
        await using var runtime = Create(client);
        var prepared = await Prepare(runtime, "upgrade", [First, Second]);
        await Send(runtime, "enqueue", new() { PreparationId = prepared.Id });
        await Idle(runtime);
        Assert.AreEqual(1, client.ActionCalls.Count);
        Assert.IsTrue(runtime.GetStatus().RecoveryRequired);
        Assert.IsNotNull(new WingetStateStore(_directory).Load().PendingInstall);
        CollectionAssert.AreEqual(new[] { "NeedsVerification", "Cancelled" }, runtime.GetStatus().Queue.Select(item => item.State).ToArray());
        Assert.IsFalse((await Send(runtime, "prepare", new() { OperationKind = "upgrade", Packages = [WingetServicePolicy.Identity(Second)] })).Accepted);
    }

    [TestMethod]
    public async Task InstalledOnlyUninstallNeedsNoRemoteManifestOrPackageAgreements()
    {
        var local = First with { Source = "local", AvailableVersion = string.Empty, InstallEligibility = WingetInstallEligibility.ManualOnly, IsUpdateAvailable = false };
        var client = new FakeClient { Installed = [local], Agreements = [new("Remote terms", "Not applicable to uninstall", null)] };
        await using var runtime = Create(client);
        await Send(runtime, "list-installed");
        await Idle(runtime);
        var prepared = await Prepare(runtime, "uninstall", [local], new() { Silent = true, Scope = "machine" });
        Assert.AreEqual(0, client.DetailsCalls);
        Assert.AreEqual(0, prepared.Packages.Single().Agreements.Count);
        Assert.IsTrue((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id })).Accepted);
        await Idle(runtime);
        Assert.AreEqual("uninstall", client.ActionCalls.Single().Kind);
        Assert.AreEqual(0, runtime.GetStatus().InstalledPackages.Count);
        Assert.IsFalse(runtime.GetStatus().RecoveryRequired);
    }

    [TestMethod]
    public async Task InstalledDetailsAndUninstallKeepDifferentInstalledVersionsDistinct()
    {
        var first = First with { Source = "local", AvailableVersion = string.Empty };
        var second = first with { InstalledVersion = "1.5" };
        var client = new FakeClient { Installed = [first, second] };
        await using var runtime = Create(client);
        await Send(runtime, "list-installed");
        await Idle(runtime);
        Assert.IsFalse(
            (await Send(runtime, "details", new() { PackageId = first.Id, Source = first.Source, AvailableVersion = string.Empty })).Accepted,
            "Legacy details without installed-version identity must reject ambiguous installed entries.");
        Assert.IsTrue((await Send(runtime, "details", new() { Packages = [WingetServicePolicy.Identity(second)] })).Accepted);
        await Idle(runtime);
        Assert.AreEqual("1.5", runtime.GetStatus().SelectedDetails!.Package.InstalledVersion);
        var prepared = await Prepare(runtime, "uninstall", [first, second]);
        await Send(runtime, "enqueue", new() { PreparationId = prepared.Id });
        await Idle(runtime);
        Assert.AreEqual(2, client.ActionCalls.Count);
        CollectionAssert.AreEqual(new[] { "1.0", "1.5" }, client.ActionCalls.Select(call => call.Package.InstalledVersion).ToArray());
        Assert.AreEqual(0, runtime.GetStatus().InstalledPackages.Count);
    }

    [TestMethod]
    public async Task RemoteDetailsDoNotEraseVerifiedInstalledUninstallCapability()
    {
        var summary = First with { CanUninstall = false };
        Seed(summary);
        var client = new FakeClient { Installed = [First], ChangedDetails = summary };
        await using var runtime = Create(client);
        await Send(runtime, "list-installed");
        await Idle(runtime);
        await Send(runtime, "details", new() { Packages = [WingetServicePolicy.Identity(summary)] });
        await Idle(runtime);
        Assert.IsTrue(
            runtime.GetStatus().InstalledPackages.Single().CanUninstall,
            "Remote installer metadata does not replace installed-catalog identity verification.");
        var prepared = await Prepare(runtime, "uninstall", runtime.GetStatus().InstalledPackages);
        Assert.IsTrue(prepared.Packages.Single().Package.CanUninstall);
        Assert.IsTrue((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id })).Accepted);
        await Idle(runtime);
        Assert.AreEqual("uninstall", client.ActionCalls.Single().Kind);
    }

    [TestMethod]
    public async Task DiscoveryBatchInstallsOnlyPreparedUninstalledSearchResults()
    {
        var fresh = First with { InstalledVersion = string.Empty, CanUninstall = false, IsUpdateAvailable = false };
        var client = new FakeClient { SearchResults = [fresh] };
        await using var runtime = Create(client);
        await Send(runtime, "search", new() { Query = "Example" });
        await Idle(runtime);
        var prepared = await Prepare(runtime, "install", [fresh]);
        await Send(runtime, "enqueue", new() { PreparationId = prepared.Id });
        await Idle(runtime);
        Assert.AreEqual("install", client.ActionCalls.Single().Kind);
        Assert.AreEqual("2.0", runtime.GetStatus().InstalledPackages.Single().InstalledVersion);
        Assert.AreEqual("2.0", runtime.GetStatus().SearchResults.Single().InstalledVersion);
        Assert.IsFalse((await Send(runtime, "prepare", new() { OperationKind = "install", Packages = [WingetServicePolicy.Identity(fresh)] })).Accepted);
    }

    [TestMethod]
    public async Task UnresolvedDiscoveryPreparationBindsResolvedVersionOptionsAndAgreements()
    {
        var unresolved = WingetClient.CreateSearchSummary(First.Id, First.Name);
        var resolved = First with { InstalledVersion = string.Empty, CanUninstall = false };
        var options = new WingetPackageOptions { Scope = "user", Architecture = "x64" };
        var client = new FakeClient
        {
            SearchResults = [unresolved],
            ChangedDetails = resolved,
            Agreements = [new("Terms", "Review before installation", null)],
        };
        await using var runtime = Create(client);
        await Send(runtime, "search", new() { Query = "Example" });
        await Idle(runtime);
        var prepared = await Prepare(runtime, "install", [unresolved], options);
        Assert.AreEqual(options, client.LastDetailsOptions);
        Assert.AreEqual(resolved, prepared.Packages.Single().Package);
        Assert.AreEqual(resolved, runtime.GetStatus().SearchResults.Single());
        Assert.IsFalse((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id })).Accepted, "Unresolved discovery must not bypass agreement review.");
        var originalIdentityRequest = new WingetServiceRequest
        {
            PreparationId = prepared.Id,
            AcceptPackageAgreements = true,
            Packages = [WingetServicePolicy.Identity(unresolved)],
        };
        Assert.IsFalse((await Send(runtime, "enqueue", originalIdentityRequest)).Accepted, "The confirmation binds the resolved version, not the original unknown version.");
        Assert.IsTrue((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id, AcceptPackageAgreements = true })).Accepted);
        await Idle(runtime);
        Assert.AreEqual(resolved, client.ActionCalls.Single().Package);
        Assert.IsFalse((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id, AcceptPackageAgreements = true })).Accepted);
    }

    [TestMethod]
    public async Task UnknownInstalledStateIsResolvedAndDisplayedBeforeInstallIsRejected()
    {
        var unresolved = WingetClient.CreateSearchSummary(First.Id, First.Name);
        var client = new FakeClient { SearchResults = [unresolved], ChangedDetails = First };
        await using var runtime = Create(client);
        await Send(runtime, "search", new() { Query = "Example" });
        await Idle(runtime);
        Assert.IsTrue((await Send(runtime, "prepare", new() { OperationKind = "install", Packages = [WingetServicePolicy.Identity(unresolved)] })).Accepted);
        await Idle(runtime);
        var status = runtime.GetStatus();
        Assert.IsFalse(status.LastResult!.Succeeded);
        StringAssert.Contains(status.LastResult.Message!, "already installed");
        Assert.AreEqual(First, status.SearchResults.Single());
        Assert.IsNotNull(status.SelectedDetails);
        Assert.IsNull(status.PreparedOperation);
        Assert.AreEqual(0, client.ActionCalls.Count);
    }

    [TestMethod]
    public async Task FailedSecondResolutionNeverConfirmsOrRunsTheFirstPackage()
    {
        var first = WingetClient.CreateSearchSummary(First.Id, First.Name);
        var second = WingetClient.CreateSearchSummary(Second.Id, Second.Name);
        var client = new FakeClient
        {
            SearchResults = [first, second],
            DetailsOperation = (package, _, _) => Task.FromResult(new WingetPackageDetails(
                package.Id == First.Id ? First with { InstalledVersion = string.Empty, CanUninstall = false } : Second with { Source = "msstore" },
                null,
                null,
                null,
                null,
                null,
                [])),
        };
        await using var runtime = Create(client);
        await Send(runtime, "search", new() { Query = "Example" });
        await Idle(runtime);
        Assert.IsTrue((await Send(runtime, "prepare", new() { OperationKind = "install", Packages = [WingetServicePolicy.Identity(first), WingetServicePolicy.Identity(second)] })).Accepted);
        await Idle(runtime);
        Assert.AreEqual(2, client.DetailsCalls);
        Assert.IsFalse(runtime.GetStatus().LastResult!.Succeeded);
        Assert.IsNull(runtime.GetStatus().PreparedOperation);
        Assert.AreEqual(0, client.ActionCalls.Count);
    }

    [TestMethod]
    public async Task CancellingMetadataPreparationShowsProgressAndNeverCreatesAConfirmation()
    {
        var unresolved = WingetClient.CreateSearchSummary(First.Id, First.Name);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WingetPackageDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            SearchResults = [unresolved],
            DetailsOperation = (_, _, token) =>
            {
                started.TrySetResult();
                return release.Task.WaitAsync(token);
            },
        };
        await using var runtime = Create(client);
        await Send(runtime, "search", new() { Query = "Example" });
        await Idle(runtime);
        var ack = await Send(runtime, "prepare", new() { OperationKind = "install", Packages = [WingetServicePolicy.Identity(unresolved)] });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending = runtime.GetStatus();
        Assert.AreEqual("Preparing", pending.Phase);
        StringAssert.Contains(pending.Message!, "1/1");
        StringAssert.Contains(pending.Message!, First.Name);
        Assert.AreEqual(0.0, pending.ProgressPercent);
        Assert.IsTrue((await Send(runtime, "cancel", new() { OperationId = ack.OperationId })).Accepted);
        await Idle(runtime);
        Assert.AreEqual("Cancelled", runtime.GetStatus().Phase);
        Assert.IsNull(runtime.GetStatus().PreparedOperation);
        Assert.AreEqual(unresolved, runtime.GetStatus().SearchResults.Single());
        Assert.AreEqual(0, client.ActionCalls.Count);
    }

    [TestMethod]
    public async Task RestartMarksInterruptedQueueWithoutReplayingAndKeepsBoundedHistory()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var old = Enumerable.Range(0, 100).Select(index => new WingetQueueItem("old-" + index, First, "upgrade", "Succeeded", CompletedUtc: now.AddMinutes(-index))).ToArray();
        new WingetStateStore(_directory).Save(new()
        {
            Packages = [First, Second], History = old,
            Queue = [new("running", First, "upgrade", "Running", StartedUtc: now), new("waiting", Second, "upgrade", "Pending")],
        });
        var client = new FakeClient();
        await using var runtime = Create(client);
        CollectionAssert.AreEqual(new[] { "NeedsVerification", "Cancelled" }, runtime.GetStatus().Queue.Select(item => item.State).ToArray());
        Assert.AreEqual(100, runtime.GetStatus().History.Count);
        Assert.AreEqual(100, new WingetStateStore(_directory).Load().History.Count);
        Assert.IsTrue(runtime.GetStatus().RecoveryRequired);
        Assert.AreEqual(0, client.ActionCalls.Count);
        await Send(runtime, "check");
        await Idle(runtime);
        Assert.IsFalse(runtime.GetStatus().RecoveryRequired);
        Assert.AreEqual(0, client.ActionCalls.Count, "Recovery only reads state; an interrupted queue never resumes automatically.");
    }

    [TestMethod]
    public async Task NoQueuedInstallerStartsIfDurableQueueAdmissionFails()
    {
        Seed(First);
        var client = new FakeClient();
        await using var runtime = Create(client);
        var prepared = await Prepare(runtime, "upgrade", [First]);
        string stateDirectory = Path.Combine(_directory, "State");
        File.Delete(Path.Combine(stateDirectory, "service-state.json"));
        Directory.Delete(stateDirectory);
        File.WriteAllText(stateDirectory, "Synthetic storage failure");
        await Send(runtime, "enqueue", new() { PreparationId = prepared.Id });
        await Idle(runtime);
        Assert.AreEqual(0, client.ActionCalls.Count);
        Assert.IsTrue(runtime.GetStatus().Queue.All(item => item.State == "Cancelled"));
        Assert.IsFalse((await Send(runtime, "enqueue", new() { PreparationId = prepared.Id })).Accepted);
    }

    [TestMethod]
    public async Task InstalledVersionChangeDuringMetadataLookupNeverCreatesConfirmation()
    {
        Seed(First);
        var client = new FakeClient { ChangedDetails = First with { InstalledVersion = "1.5" } };
        await using var runtime = Create(client);
        Assert.IsTrue((await Send(runtime, "prepare", new() { OperationKind = "upgrade", Packages = [WingetServicePolicy.Identity(First)] })).Accepted);
        await Idle(runtime);
        Assert.IsNull(runtime.GetStatus().PreparedOperation);
        Assert.IsFalse(runtime.GetStatus().LastResult!.Succeeded);
        Assert.AreEqual(0, client.ActionCalls.Count);
    }

    [TestMethod]
    public async Task QueueKeepsBoundedOutputInStatusAndPerItemHistoryWithoutMixingLaterItems()
    {
        Seed(First, Second);
        string firstOutput = new('a', 20000);
        string secondOutput = new('b', 20000);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient { BlockFirstAction = true };
        IProgress<WingetOperationProgress>? firstProgress = null;
        IProgress<WingetOperationProgress>? secondProgress = null;
        client.ActionOperation = async (index, progress, token) =>
        {
            if (index == 0)
            {
                firstProgress = progress;
                progress!.Report(new("WaitingForInstaller", null) { Output = "first in progress" });
                return new(true, false, false, null, null) { Output = firstOutput };
            }

            secondProgress = progress;
            secondStarted.TrySetResult();
            await releaseSecond.Task.WaitAsync(token);
            return new(true, false, false, null, null) { Output = secondOutput };
        };
        await using var runtime = Create(client);
        try
        {
            var prepared = await Prepare(runtime, "upgrade", [First, Second]);
            var ack = await Send(runtime, "enqueue", new() { PreparationId = prepared.Id });
            await client.ActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsNull(runtime.GetStatus().Output);
            client.ActionRelease.TrySetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsNull(runtime.GetStatus().Output, "Each new queue item starts with a fresh output buffer.");
            Assert.AreEqual(firstOutput[^16384..], runtime.GetStatus().Queue[0].Result!.Output);
            firstProgress!.Report(new("WaitingForInstaller", null) { Output = "delayed first-item output" });
            Assert.IsNull(runtime.GetStatus().Output, "A completed queue item's delayed progress cannot overwrite the next item's output.");
            secondProgress!.Report(new("WaitingForInstaller", null) { Output = new string('p', 20000) });
            Assert.AreEqual(new string('p', 16384), runtime.GetStatus().Output);
            await Send(runtime, "cancel-queue", new() { OperationId = ack.OperationId });
            secondProgress.Report(new("WaitingForInstaller", null) { Output = "current native operation still running" });
            Assert.AreEqual("Cancelling", runtime.GetStatus().Phase);
            Assert.AreEqual("current native operation still running", runtime.GetStatus().Output);
            releaseSecond.TrySetResult();
            await Idle(runtime);
            var status = runtime.GetStatus();
            Assert.AreEqual(secondOutput[^16384..], status.Output);
            Assert.AreEqual(status.Output, status.LastResult!.Install!.Output);
            var saved = new WingetStateStore(_directory).Load();
            string savedFirst = saved.History.Single(item => item.Package.Id == First.Id).Result!.Output!;
            string savedSecond = saved.History.Single(item => item.Package.Id == Second.Id).Result!.Output!;
            Assert.IsTrue(Encoding.UTF8.GetByteCount(savedFirst) <= WingetStateStore.MaximumSavedOutputUtf8Bytes);
            Assert.IsTrue(Encoding.UTF8.GetByteCount(savedSecond) <= WingetStateStore.MaximumSavedOutputUtf8Bytes);
            StringAssert.StartsWith(savedFirst, "[Earlier output truncated for saved history]");
            StringAssert.EndsWith(savedFirst, new string('a', 4000));
            StringAssert.EndsWith(savedSecond, new string('b', 4000));
            Assert.AreEqual(1, Directory.GetFiles(_directory, "*", SearchOption.AllDirectories).Length, "Output is bounded inside existing state, without standalone log files.");
            await Send(runtime, "list-installed");
            Assert.IsNull(runtime.GetStatus().Output);
            await Idle(runtime);
        }
        finally
        {
            client.ActionRelease.TrySetResult();
            releaseSecond.TrySetResult();
        }
    }

    [TestMethod]
    public async Task QueueExceptionRetainsTheLastProgressOutputInHistory()
    {
        Seed(First);
        var client = new FakeClient
        {
            ActionOperation = (_, progress, _) =>
            {
                progress!.Report(new("WaitingForInstaller", null) { Output = "native error details" });
                throw new WingetOperationException("Synthetic operation error");
            },
        };
        await using var runtime = Create(client);
        var prepared = await Prepare(runtime, "upgrade", [First]);
        await Send(runtime, "enqueue", new() { PreparationId = prepared.Id });
        await Idle(runtime);
        Assert.AreEqual("native error details", runtime.GetStatus().Output);
        Assert.AreEqual("native error details", new WingetStateStore(_directory).Load().History.Single().Result!.Output);
        Assert.AreEqual("Failed", runtime.GetStatus().Queue.Single().State);
    }

    [TestMethod]
    public void SavedOutputUsesUtf8ByteLimitsWhilePreservingAllHistoryAndRecoveryState()
    {
        string output = string.Concat(Enumerable.Repeat("安装😀", 4096));
        var result = new WingetInstallResult(false, true, false, "Keep this failure message", 123) { Output = output };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var state = new WingetPersistentState
        {
            Packages = [First],
            PendingInstall = new(First.Id, First.Source, First.AvailableVersion, now),
            History = Enumerable.Range(0, 100).Select(index => new WingetQueueItem("history-" + index, First, "upgrade", "Failed", CompletedUtc: now.AddMinutes(-index)) { Result = result }).ToArray(),
            Queue = Enumerable.Range(0, 50).Select(index => new WingetQueueItem("queue-" + index, Second, "upgrade", "Failed", CompletedUtc: now) { Result = result }).ToArray(),
        };
        var store = new WingetStateStore(_directory);
        store.Save(state);
        var saved = store.Load();
        Assert.AreEqual(100, saved.History.Count);
        Assert.AreEqual(50, saved.Queue.Count);
        Assert.AreEqual(state.PendingInstall, saved.PendingInstall);
        Assert.AreEqual(output, state.History[0].Result!.Output, "Saving a shorter tail must not mutate live status output.");
        foreach (var item in saved.History.Concat(saved.Queue))
        {
            Assert.AreEqual("Keep this failure message", item.Result!.Error);
            Assert.AreEqual(123, item.Result.ErrorCode);
            Assert.IsTrue(item.Result.RebootRequired);
            Assert.IsTrue(Encoding.UTF8.GetByteCount(item.Result.Output!) <= 4096);
            StringAssert.StartsWith(item.Result.Output!, "[Earlier output truncated for saved history]");
            StringAssert.EndsWith(item.Result.Output!, "安装😀");
            Assert.IsFalse(item.Result.Output!.Contains('�'), "UTF-8 truncation must preserve complete Unicode scalars.");
        }

        Assert.IsTrue(new FileInfo(Path.Combine(_directory, "State", "service-state.json")).Length <= 4 * 1024 * 1024);
    }

    [TestMethod]
    public void OversizedStateDropsOldOutputTailsBeforeQueueOutputWithoutDroppingFailureOrRecoveryRecords()
    {
        string output = new('安', 16384);
        var result = new WingetInstallResult(false, false, false, "Persistent error", 55) { Output = output };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var state = new WingetPersistentState
        {
            Packages = [First with { ReleaseNotes = new string('m', 3_950_000) }],
            PendingInstall = new(First.Id, First.Source, First.AvailableVersion, now),
            History = [new WingetQueueItem("oldest", First, "upgrade", "Failed", CompletedUtc: now.AddHours(-1)) { Result = result }],
            Queue = Enumerable.Range(0, 50).Select(index => new WingetQueueItem("queue-" + index, Second, "upgrade", "Failed", CompletedUtc: now.AddMinutes(index)) { Result = result }).ToArray(),
        };
        var store = new WingetStateStore(_directory);
        store.Save(state);
        var saved = store.Load();
        Assert.AreEqual(1, saved.History.Count);
        Assert.AreEqual(50, saved.Queue.Count);
        Assert.AreEqual(state.PendingInstall, saved.PendingInstall);
        Assert.AreEqual(state.Packages.Single(), saved.Packages.Single());
        StringAssert.StartsWith(saved.History.Single().Result!.Output!, "[Output omitted");
        Assert.IsTrue(saved.Queue.Any(item => item.Result!.Output!.StartsWith("[Output omitted", StringComparison.Ordinal)));
        Assert.IsTrue(saved.History.Concat(saved.Queue).All(item => item.Result!.Error == "Persistent error" && item.Result.ErrorCode == 55));
        Assert.IsTrue(new FileInfo(Path.Combine(_directory, "State", "service-state.json")).Length <= 4 * 1024 * 1024);
    }

    [TestMethod]
    public void IrreducibleOversizedMetadataFailsWithoutReplacingPreviouslySavedRecoveryState()
    {
        var store = new WingetStateStore(_directory);
        var pending = new WingetPendingInstall(First.Id, First.Source, First.AvailableVersion, DateTimeOffset.UtcNow);
        store.Save(new() { Packages = [First], PendingInstall = pending });
        var oversized = new WingetPersistentState { Packages = [First with { ReleaseNotes = new string('m', 4 * 1024 * 1024) }] };
        Assert.ThrowsExactly<IOException>(() => store.Save(oversized));
        Assert.AreEqual(pending, store.Load().PendingInstall);
        Assert.AreEqual(First, store.Load().Packages.Single());
    }

    private void Seed(params WingetPackageUpdate[] packages)
        => new WingetStateStore(_directory).Save(WingetServicePolicy.CompleteCheck(new(), DateTimeOffset.UtcNow, packages));

    private WingetServiceRuntime Create(FakeClient client, FakeNotifier? notifier = null) => new(client, _directory, notifier ?? new());

    private static async Task<WingetPreparedOperation> Prepare(WingetServiceRuntime runtime, string kind, IReadOnlyList<WingetPackageUpdate> packages, WingetPackageOptions? options = null)
    {
        Assert.IsTrue((await Send(runtime, "prepare", new() { OperationKind = kind, Packages = packages.Select(WingetServicePolicy.Identity).ToArray(), Options = options })).Accepted);
        await Idle(runtime);
        Assert.IsNotNull(runtime.GetStatus().PreparedOperation, runtime.GetStatus().LastResult?.Message);
        return runtime.GetStatus().PreparedOperation!;
    }

    private static async Task<WingetServiceAck> Send(WingetServiceRuntime runtime, string command, WingetServiceRequest? request = null)
    {
        var reply = await runtime.HandleRequestAsync(new KitPipeRequest { Command = command, PayloadJson = JsonSerializer.Serialize(request ?? new(), WingetServiceJsonContext.Default.WingetServiceRequest) }, CancellationToken.None);
        Assert.IsTrue(reply.Success, reply.Error?.Message);
        return JsonSerializer.Deserialize(reply.DataJson!, WingetServiceJsonContext.Default.WingetServiceAck)!;
    }

    private static async Task Idle(WingetServiceRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (runtime.GetStatus().IsBusy)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeNotifier : IWingetUpdateNotifier
    {
        internal List<IReadOnlyList<WingetPackageUpdate>> Shown { get; } = [];
        public string? Show(IReadOnlyList<WingetPackageUpdate> packages)
        {
            Shown.Add(packages.ToArray());
            return null;
        }
    }

    private sealed record ActionCall(string Kind, WingetPackageUpdate Package, bool AcceptAgreements, WingetPackageOptions? Options);

    private sealed class FakeClient : IWingetClient
    {
        private int _active;
        internal IReadOnlyList<WingetPackageUpdate> Updates { get; init; } = [First, Second];
        internal IReadOnlyList<WingetPackageUpdate> Installed { get; init; } = [First, Second];
        internal IReadOnlyList<WingetPackageUpdate> SearchResults { get; init; } = [];
        internal IReadOnlyList<WingetPackageAgreement> Agreements { get; init; } = [];
        internal IReadOnlyList<WingetInstallResult> Results { get; init; } = [];
        internal Func<int, IProgress<WingetOperationProgress>?, CancellationToken, Task<WingetInstallResult>>? ActionOperation { get; set; }
        internal WingetPackageUpdate? ChangedDetails { get; init; }
        internal Func<WingetPackageUpdate, WingetPackageOptions?, CancellationToken, Task<WingetPackageDetails>>? DetailsOperation { get; init; }
        internal WingetPackageOptions? LastDetailsOptions { get; private set; }
        internal bool BlockFirstAction { get; init; }
        internal bool FailChecks { get; init; }
        internal int DetailsCalls { get; private set; }
        internal int CheckCalls { get; private set; }
        internal int InstalledCalls { get; private set; }
        internal int MaximumConcurrentActions { get; private set; }
        internal CancellationToken CurrentToken { get; private set; }
        internal List<ActionCall> ActionCalls { get; } = [];
        internal TaskCompletionSource ActionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ActionRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WingetAvailability> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WingetAvailability(true, "fake", null, null));
        public Task<IReadOnlyList<WingetPackageUpdate>> CheckUpdatesAsync(CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            if (FailChecks)
            {
                throw new WingetOperationException("Synthetic recovery failure");
            }

            return Task.FromResult(Updates);
        }

        public Task<IReadOnlyList<WingetPackageUpdate>> ListInstalledAsync(CancellationToken cancellationToken = default)
        {
            InstalledCalls++;
            return Task.FromResult(Installed);
        }

        public Task<IReadOnlyList<WingetPackageUpdate>> SearchAsync(string query, CancellationToken cancellationToken = default) => Task.FromResult(SearchResults);
        public Task<WingetPackageDetails> GetDetailsAsync(WingetPackageUpdate package, CancellationToken cancellationToken = default)
            => GetDetailsAsync(package, null, cancellationToken);

        public Task<WingetPackageDetails> GetDetailsAsync(WingetPackageUpdate package, WingetPackageOptions? options, CancellationToken cancellationToken)
        {
            DetailsCalls++;
            LastDetailsOptions = options;
            return DetailsOperation?.Invoke(package, options, cancellationToken)
                ?? Task.FromResult(new WingetPackageDetails(ChangedDetails ?? package, null, null, null, null, null, Agreements));
        }

        public Task<WingetDownloadResult> DownloadAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A queue must not implicitly download packages.");
        public Task<WingetInstallResult> InstallAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, WingetPackageOptions? options = null, CancellationToken cancellationToken = default)
            => RunAction("install", package, progress, acceptPackageAgreements, options, cancellationToken);
        public Task<WingetInstallResult> UpgradeAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, WingetPackageOptions? options = null, CancellationToken cancellationToken = default)
            => RunAction("upgrade", package, progress, acceptPackageAgreements, options, cancellationToken);
        public Task<WingetInstallResult> UninstallAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, WingetPackageOptions? options = null, CancellationToken cancellationToken = default)
            => RunAction("uninstall", package, progress, false, options, cancellationToken);

        private async Task<WingetInstallResult> RunAction(string kind, WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress, bool agreements, WingetPackageOptions? options, CancellationToken token)
        {
            MaximumConcurrentActions = Math.Max(MaximumConcurrentActions, Interlocked.Increment(ref _active));
            int index = ActionCalls.Count;
            ActionCalls.Add(new(kind, package, agreements, options));
            CurrentToken = token;
            ActionStarted.TrySetResult();
            try
            {
                if (BlockFirstAction && index == 0)
                {
                    await ActionRelease.Task;
                }

                if (ActionOperation is not null)
                {
                    return await ActionOperation(index, progress, token);
                }

                return index < Results.Count ? Results[index] : new(true, false, false, null, null);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
