// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kit.WingetLib;
using Kit.WingetService;
using ManagedCommon.ServiceHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class WingetServiceTests
{
    private string _directory = string.Empty;
    private static readonly WingetPackageUpdate Package = new("Example.App", "Example", "1.0", "2.0", "winget", null, null, WingetInstallEligibility.UserWithoutElevation, "User install without elevation", "winget upgrade --id Example.App --exact");

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "KitWingetServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public async Task SharedPipeRoundTripAndServerReplyDoNotBlockOnAnEncodingPreamble()
    {
        string moduleKey = "WingetPipeTest_" + Guid.NewGuid().ToString("N");
        var server = new KitServicePipeServer(moduleKey, (request, _) => Task.FromResult(KitPipeResponse.Ok(request.Command)));
        server.Start();
        try
        {
            var client = new KitServicePipeClient(moduleKey, Environment.ProcessPath!, TimeSpan.FromSeconds(2));
            var response = await client.SendCommandAsync("quick-ack").WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsTrue(response.Success);
            Assert.AreEqual("quick-ack", response.DataJson);
            using var wire = new NamedPipeClientStream(".", KitServicePipeUtils.GetPipeName(moduleKey), PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await wire.ConnectAsync(timeout.Token);
            await wire.WriteAsync(Encoding.UTF8.GetBytes("{\"Version\":1,\"Command\":\"wire-ack\"}\n"), timeout.Token);
            byte[] first = new byte[1];
            await wire.ReadExactlyAsync(first, timeout.Token);
            Assert.AreEqual((byte)'{', first[0], "The reply must begin with JSON, without a BOM flush before the request is read.");
        }
        finally
        {
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [TestMethod]
    public async Task SharedPipeClientWritesJsonWithoutAPreamble()
    {
        string moduleKey = "WingetWireTest_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(KitServicePipeUtils.GetPipeName(moduleKey), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task<byte> inspect = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            byte[] first = new byte[1];
            await server.ReadExactlyAsync(first, timeout.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            _ = await reader.ReadLineAsync(timeout.Token);
            await server.WriteAsync(Encoding.UTF8.GetBytes("{\"Version\":1,\"Success\":true,\"DataJson\":\"wire-ack\"}\n"), timeout.Token);
            return first[0];
        });
        var client = new KitServicePipeClient(moduleKey, Environment.ProcessPath!, TimeSpan.FromSeconds(2));
        var response = await client.SendCommandAsync("quick-ack").WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual("wire-ack", response.DataJson);
        Assert.AreEqual((byte)'{', await inspect, "The client must start with JSON, without a BOM.");
    }

    [TestMethod]
    public void EmptyNotificationExceptionsRemainVisibleInBothLanguages()
    {
        var exception = new IOException(string.Empty, unchecked((int)0x80070490));
        string english = WingetUpdateNotifier.FormatError(exception, false);
        string chinese = WingetUpdateNotifier.FormatError(exception, true);
        StringAssert.Contains(english, "Windows notification failed");
        StringAssert.Contains(chinese, "Windows 通知失败");
        StringAssert.Contains(english, "IOException, 0x80070490");
        StringAssert.Contains(chinese, "IOException, 0x80070490");
    }

    [TestMethod]
    public void DailyChecksUseSuccessWatermarkAndRespectOptOut()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var state = new WingetPersistentState();
        Assert.IsTrue(WingetServicePolicy.IsDue(state, true, now));
        Assert.IsFalse(WingetServicePolicy.IsDue(state, false, now));
        Assert.IsNull(WingetServicePolicy.NextCheck(state, false, now));
        state = WingetServicePolicy.CompleteCheck(state, now, [Package]);
        Assert.IsFalse(WingetServicePolicy.IsDue(state, true, now.AddHours(23.99)));
        Assert.IsTrue(WingetServicePolicy.IsDue(state, true, now.AddHours(24)));
    }

    [TestMethod]
    public void FailedAndInterruptedChecksBackOffWithoutAdvancingSuccess()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var state = WingetServicePolicy.BeginAttempt(new(), now);
        Assert.IsFalse(WingetServicePolicy.IsDue(state, true, now.AddMinutes(1)));
        Assert.IsNull(state.LastSuccessUtc);
        state = WingetServicePolicy.FailCheck(state, now);
        Assert.AreEqual(now.AddMinutes(5), state.RetryAfterUtc);
        state = WingetServicePolicy.FailCheck(state, now);
        Assert.AreEqual(now.AddMinutes(10), state.RetryAfterUtc);
        for (int i = 0; i < 20; i++)
        {
            state = WingetServicePolicy.FailCheck(state, now);
        }

        Assert.AreEqual(now.AddHours(6), state.RetryAfterUtc);
        Assert.IsNull(state.LastSuccessUtc);
    }

    [TestMethod]
    public void StateAndNotificationIdentitySurviveRestart()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var second = Package with { Id = "Other.App" };
        string fingerprint = WingetServicePolicy.Fingerprint([Package, second]);
        Assert.AreEqual(fingerprint, WingetServicePolicy.Fingerprint([second, Package]));
        Assert.AreNotEqual(fingerprint, WingetServicePolicy.Fingerprint([Package with { AvailableVersion = "3.0" }, second]));
        var original = WingetServicePolicy.CompleteCheck(new(), now, [Package]) with
        {
            NotificationFingerprint = fingerprint,
            LastNotifiedUtc = now,
        };
        new WingetStateStore(_directory).Save(original);
        var loaded = new WingetStateStore(_directory).Load();
        Assert.AreEqual(original.LastSuccessUtc, loaded.LastSuccessUtc);
        Assert.AreEqual(fingerprint, loaded.NotificationFingerprint);
        Assert.AreEqual(Package, loaded.Packages.Single());
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public void InvalidSettingsPreserveAnExplicitOptOut()
    {
        string path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{\"properties\":{\"daily_check_enabled\":{\"value\":false},\"notify_updates\":{\"value\":false}}}");
        var settings = WingetServiceSettings.Read(path, new());
        Assert.IsFalse(settings.DailyCheckEnabled);
        Assert.IsFalse(settings.NotifyUpdates);
        File.WriteAllText(path, "{\"properties\":");
        Assert.AreEqual(settings, WingetServiceSettings.Read(path, settings));
        File.WriteAllText(path, "{\"properties\":123}");
        Assert.AreEqual(settings, WingetServiceSettings.Read(path, settings));
    }

    [TestMethod]
    public void PortableNotificationRegistrationQuotesPathsAndPreservesOtherApplications()
    {
        const string current = @"C:\Portable Kit\Kit.exe";
        const string previous = @"C:\Old Kit\Kit.exe";
        string desired = WingetNotificationRegistration.Command(current, " \"%1\"");
        Assert.AreEqual("\"C:\\Portable Kit\\Kit.exe\" \"%1\"", desired);
        Assert.IsTrue(WingetNotificationRegistration.CanReplaceCommand(null, desired, null, " \"%1\""));
        Assert.IsTrue(WingetNotificationRegistration.CanReplaceCommand(desired, desired, null, " \"%1\""));
        Assert.IsTrue(WingetNotificationRegistration.CanReplaceCommand(WingetNotificationRegistration.Command(previous, " \"%1\""), desired, previous, " \"%1\""));
        Assert.IsFalse(WingetNotificationRegistration.CanReplaceCommand("\"C:\\Other App\\Other.exe\" \"%1\"", desired, previous, " \"%1\""));
        StringAssert.StartsWith(WingetNotificationRegistration.ApplicationKey, @"Software\Classes\AppUserModelId\");
    }

    [TestMethod]
    public async Task SlowCheckAcknowledgesImmediatelyAndAcceptsCancellation()
    {
        var client = new FakeClient { BlockChecks = true };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        var response = runtime.HandleRequestAsync(new KitPipeRequest { Command = "check" }, CancellationToken.None);
        Assert.IsTrue(response.IsCompletedSuccessfully, "A check request must not wait for WinGet work.");
        var accepted = ParseAck(await response);
        Assert.IsTrue(accepted.Accepted);
        await client.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(runtime.GetStatus().IsBusy);
        var second = ParseAck(await runtime.HandleRequestAsync(new KitPipeRequest { Command = "check" }, CancellationToken.None));
        Assert.IsFalse(second.Accepted);
        Assert.AreEqual(1, client.CheckCalls);
        var cancelled = ParseAck(await runtime.HandleRequestAsync(Envelope("cancel", new() { OperationId = accepted.OperationId }), CancellationToken.None));
        Assert.IsTrue(cancelled.Accepted);
        await WaitIdleAsync(runtime);
        Assert.AreEqual("Cancelled", runtime.GetStatus().Phase);
        Assert.IsNull(runtime.GetStatus().LastSuccessUtc);
    }

    [TestMethod]
    public async Task StaleVersionAndUnreviewedActionsNeverReachTheClient()
    {
        new WingetStateStore(_directory).Save(WingetServicePolicy.CompleteCheck(new(), DateTimeOffset.UtcNow, [Package]));
        var client = new FakeClient();
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        var stale = ParseAck(await runtime.HandleRequestAsync(Envelope("upgrade", PackageRequest() with { AvailableVersion = "1.5" }), CancellationToken.None));
        Assert.IsFalse(stale.Accepted);
        var unreviewed = ParseAck(await runtime.HandleRequestAsync(Envelope("download", PackageRequest()), CancellationToken.None));
        Assert.IsFalse(unreviewed.Accepted);
        Assert.AreEqual(0, client.DownloadCalls);
        Assert.AreEqual(0, client.InstallCalls);
        var details = ParseAck(await runtime.HandleRequestAsync(Envelope("details", PackageRequest()), CancellationToken.None));
        Assert.IsTrue(details.Accepted);
        await WaitIdleAsync(runtime);
        var download = ParseAck(await runtime.HandleRequestAsync(Envelope("download", PackageRequest()), CancellationToken.None));
        Assert.IsTrue(download.Accepted);
        await WaitIdleAsync(runtime);
        Assert.AreEqual(1, client.DownloadCalls);
        Assert.AreEqual(0, client.InstallCalls, "Downloading must never implicitly install a package.");
    }

    [TestMethod]
    public async Task PersistedNotificationFingerprintSuppressesRepeatedCheckToasts()
    {
        var notifier = new FakeNotifier();
        await using (var first = new WingetServiceRuntime(new FakeClient(), _directory, notifier))
        {
            await first.HandleRequestAsync(new KitPipeRequest { Command = "check" }, CancellationToken.None);
            await WaitIdleAsync(first);
            Assert.AreEqual(1, notifier.Count);
        }

        await using (var second = new WingetServiceRuntime(new FakeClient(), _directory, notifier))
        {
            await second.HandleRequestAsync(new KitPipeRequest { Command = "check" }, CancellationToken.None);
            await WaitIdleAsync(second);
            Assert.AreEqual(1, notifier.Count);
            Assert.IsNotNull(second.GetStatus().LastNotifiedUtc);
        }
    }

    [TestMethod]
    public async Task InstallUsesReviewedEligibilityAndStillRequiresAgreements()
    {
        var initial = Package with { InstallEligibility = WingetInstallEligibility.ManualOnly };
        new WingetStateStore(_directory).Save(WingetServicePolicy.CompleteCheck(new(), DateTimeOffset.UtcNow, [initial]));
        var client = new FakeClient
        {
            ReviewedPackage = Package,
            Agreements = [new("License", "Package terms", null)],
        };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        await runtime.HandleRequestAsync(Envelope("details", PackageRequest()), CancellationToken.None);
        await WaitIdleAsync(runtime);
        var refused = ParseAck(await runtime.HandleRequestAsync(Envelope("upgrade", PackageRequest()), CancellationToken.None));
        Assert.IsFalse(refused.Accepted);
        Assert.AreEqual(0, client.InstallCalls);
        var accepted = ParseAck(await runtime.HandleRequestAsync(Envelope("upgrade", PackageRequest() with { AcceptPackageAgreements = true }), CancellationToken.None));
        Assert.IsTrue(accepted.Accepted);
        await WaitIdleAsync(runtime);
        Assert.AreEqual(1, client.InstallCalls);
        Assert.AreEqual(0, runtime.GetStatus().Packages.Count);
    }

    [TestMethod]
    public async Task RestartRechecksPendingInstallEvenWhenDailyChecksAreDisabled()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var pending = WingetServicePolicy.CompleteCheck(new(), now, [Package]) with
        {
            PendingInstall = new(Package.Id, Package.Source, Package.AvailableVersion, now),
        };
        new WingetStateStore(_directory).Save(pending);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{\"properties\":{\"daily_check_enabled\":{\"value\":false},\"notify_updates\":{\"value\":false}}}");
        var client = new FakeClient { DelayChecksUntilReleased = true };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        Assert.AreEqual(0, runtime.GetStatus().Packages.Count);
        var stale = ParseAck(await runtime.HandleRequestAsync(Envelope("upgrade", PackageRequest()), CancellationToken.None));
        Assert.IsFalse(stale.Accepted);
        runtime.StartDetection();
        await client.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(runtime.GetStatus().IsBusy);
        Assert.AreEqual(0, runtime.GetStatus().Packages.Count);
        client.CheckRelease.TrySetResult();
        await WaitIdleAsync(runtime);
        Assert.AreEqual(1, client.CheckCalls);
        Assert.AreEqual(0, client.InstallCalls);
        Assert.IsNull(new WingetStateStore(_directory).Load().PendingInstall);
        Assert.IsNull(runtime.GetStatus().NextCheckUtc);
        Assert.AreEqual(1, runtime.GetStatus().Packages.Count);
        Assert.IsNull(runtime.GetStatus().SelectedDetails, "Recovery must not restore old reviewed details.");
    }

    [TestMethod]
    public async Task InstallerIsNeverCalledWhenPendingStateCannotBePersisted()
    {
        new WingetStateStore(_directory).Save(WingetServicePolicy.CompleteCheck(new(), DateTimeOffset.UtcNow, [Package]));
        var client = new FakeClient();
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        await runtime.HandleRequestAsync(Envelope("details", PackageRequest()), CancellationToken.None);
        await WaitIdleAsync(runtime);
        string stateDirectory = Path.Combine(_directory, "State");
        File.Delete(Path.Combine(stateDirectory, "service-state.json"));
        Directory.Delete(stateDirectory);
        File.WriteAllText(stateDirectory, "A file prevents creating the pending state directory.");
        var ack = ParseAck(await runtime.HandleRequestAsync(Envelope("upgrade", PackageRequest()), CancellationToken.None));
        Assert.IsTrue(ack.Accepted);
        await WaitIdleAsync(runtime);
        Assert.AreEqual(0, client.InstallCalls);
        Assert.IsFalse(runtime.GetStatus().LastResult!.Succeeded);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedOrCancelledInstallOnlyRetriesReadOnlyRecovery(bool cancel)
    {
        new WingetStateStore(_directory).Save(WingetServicePolicy.CompleteCheck(new(), DateTimeOffset.UtcNow, [Package]));
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{\"properties\":{\"daily_check_enabled\":{\"value\":false},\"notify_updates\":{\"value\":false}}}");
        var client = new FakeClient
        {
            BlockInstalls = cancel,
            InstallSucceeds = false,
            FailChecks = true,
            OnInstall = () => Assert.IsNotNull(new WingetStateStore(_directory).Load().PendingInstall, "Persist the marker before invoking the installer."),
        };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        await runtime.HandleRequestAsync(Envelope("details", PackageRequest()), CancellationToken.None);
        await WaitIdleAsync(runtime);
        var ack = ParseAck(await runtime.HandleRequestAsync(Envelope("upgrade", PackageRequest()), CancellationToken.None));
        Assert.IsTrue(ack.Accepted);
        await client.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancel)
        {
            await runtime.HandleRequestAsync(Envelope("cancel", new() { OperationId = ack.OperationId }), CancellationToken.None);
        }

        await WaitIdleAsync(runtime);
        Assert.IsFalse(runtime.GetStatus().LastResult!.Succeeded);
        Assert.IsNotNull(new WingetStateStore(_directory).Load().PendingInstall);
        Assert.AreEqual(0, runtime.GetStatus().Packages.Count);
        runtime.Tick();
        await client.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitIdleAsync(runtime);
        Assert.IsNotNull(new WingetStateStore(_directory).Load().PendingInstall);
        Assert.IsTrue(runtime.GetStatus().NextCheckUtc > DateTimeOffset.UtcNow);
        runtime.Tick();
        Assert.AreEqual(1, client.CheckCalls, "Recovery failures must respect retry backoff.");
        Assert.AreEqual(1, client.InstallCalls, "Recovery never retries installation.");
    }

    [TestMethod]
    public async Task SearchInstallRequiresCurrentSearchIdentityDetailsAndAgreements()
    {
        var fresh = Package with { InstalledVersion = string.Empty, InstallEligibility = WingetInstallEligibility.RequiresElevation };
        var client = new FakeClient { SearchResults = [fresh], Agreements = [new("Terms", "Review these terms", null)] };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        var invalid = ParseAck(await runtime.HandleRequestAsync(Envelope("search", new() { Query = "x" }), CancellationToken.None));
        Assert.IsFalse(invalid.Accepted);
        Assert.AreEqual(0, client.SearchCalls);
        var search = ParseAck(await runtime.HandleRequestAsync(Envelope("search", new() { Query = " Example " }), CancellationToken.None));
        Assert.IsTrue(search.Accepted);
        await WaitIdleAsync(runtime);
        Assert.AreEqual("Example", runtime.GetStatus().SearchQuery);
        Assert.AreEqual(1, runtime.GetStatus().SearchResults.Count);
        Assert.AreEqual(0, runtime.GetStatus().Packages.Count);
        var unreviewed = ParseAck(await runtime.HandleRequestAsync(Envelope("install", PackageRequest()), CancellationToken.None));
        Assert.IsFalse(unreviewed.Accepted);
        await runtime.HandleRequestAsync(Envelope("details", PackageRequest()), CancellationToken.None);
        await WaitIdleAsync(runtime);
        var unaccepted = ParseAck(await runtime.HandleRequestAsync(Envelope("install", PackageRequest()), CancellationToken.None));
        Assert.IsFalse(unaccepted.Accepted);
        var upgrade = ParseAck(await runtime.HandleRequestAsync(Envelope("upgrade", PackageRequest()), CancellationToken.None));
        Assert.IsFalse(upgrade.Accepted);
        var stale = ParseAck(await runtime.HandleRequestAsync(Envelope("install", PackageRequest() with { AvailableVersion = "stale", AcceptPackageAgreements = true }), CancellationToken.None));
        Assert.IsFalse(stale.Accepted);
        var accepted = ParseAck(await runtime.HandleRequestAsync(Envelope("install", PackageRequest() with { AcceptPackageAgreements = true }), CancellationToken.None));
        Assert.IsTrue(accepted.Accepted, "Elevation-eligible packages use the explicit native WinGet path.");
        await WaitIdleAsync(runtime);
        Assert.AreEqual(1, client.NewInstallCalls);
        Assert.IsTrue(runtime.GetStatus().LastResult!.Succeeded);
        Assert.AreEqual("2.0", runtime.GetStatus().SearchResults.Single().InstalledVersion);
        Assert.IsNull(new WingetStateStore(_directory).Load().PendingInstall);
    }

    [TestMethod]
    public async Task NewInstallRejectsAlreadyInstalledSearchResultAndDoesNotImplicitlyUpgrade()
    {
        var client = new FakeClient { SearchResults = [Package] };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        await runtime.HandleRequestAsync(Envelope("search", new() { Query = "Example" }), CancellationToken.None);
        await WaitIdleAsync(runtime);
        await runtime.HandleRequestAsync(Envelope("details", PackageRequest()), CancellationToken.None);
        await WaitIdleAsync(runtime);
        var ack = ParseAck(await runtime.HandleRequestAsync(Envelope("install", PackageRequest()), CancellationToken.None));
        Assert.IsFalse(ack.Accepted);
        Assert.AreEqual(0, client.NewInstallCalls);
    }

    [TestMethod]
    public async Task CancellingNativeUpgradeKeepsBusyUntilExitAndDoesNotOverwriteSuccess()
    {
        new WingetStateStore(_directory).Save(WingetServicePolicy.CompleteCheck(new(), DateTimeOffset.UtcNow, [Package]));
        var client = new FakeClient { WaitForNativeInstaller = true };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        await runtime.HandleRequestAsync(Envelope("details", PackageRequest()), CancellationToken.None);
        await WaitIdleAsync(runtime);
        var ack = ParseAck(await runtime.HandleRequestAsync(Envelope("upgrade", PackageRequest()), CancellationToken.None));
        await client.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await runtime.HandleRequestAsync(Envelope("cancel", new() { OperationId = ack.OperationId }), CancellationToken.None);
        Assert.IsTrue(runtime.GetStatus().IsBusy);
        Assert.AreEqual("Cancelling", runtime.GetStatus().Phase);
        StringAssert.Contains(runtime.GetStatus().Message!, "native installer");
        var concurrent = ParseAck(await runtime.HandleRequestAsync(Envelope("search", new() { Query = "Example" }), CancellationToken.None));
        Assert.IsFalse(concurrent.Accepted);
        client.InstallRelease.TrySetResult();
        await WaitIdleAsync(runtime);
        Assert.AreEqual("Completed", runtime.GetStatus().Phase);
        Assert.IsTrue(runtime.GetStatus().LastResult!.Install!.CancellationRequested);
        Assert.IsTrue(runtime.GetStatus().LastResult!.Succeeded);
    }

    [TestMethod]
    public async Task SuccessfulCliWithDelayedCatalogKeepsRecoveryMarkerWithoutReportingFailure()
    {
        new WingetStateStore(_directory).Save(WingetServicePolicy.CompleteCheck(new(), DateTimeOffset.UtcNow, [Package]));
        var client = new FakeClient { VerificationPending = true };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        await runtime.HandleRequestAsync(Envelope("details", PackageRequest()), CancellationToken.None);
        await WaitIdleAsync(runtime);
        await runtime.HandleRequestAsync(Envelope("upgrade", PackageRequest()), CancellationToken.None);
        await WaitIdleAsync(runtime);
        Assert.AreEqual("Completed", runtime.GetStatus().Phase);
        Assert.IsTrue(runtime.GetStatus().LastResult!.Succeeded);
        Assert.IsTrue(runtime.GetStatus().LastResult!.Install!.VerificationPending);
        Assert.IsNotNull(new WingetStateStore(_directory).Load().PendingInstall);
        Assert.AreEqual(0, runtime.GetStatus().Packages.Count);
    }

    [TestMethod]
    public async Task SearchHasBoundedResultsAndDoesNotInvalidateCheckedUpdates()
    {
        new WingetStateStore(_directory).Save(WingetServicePolicy.CompleteCheck(new(), DateTimeOffset.UtcNow, [Package]));
        var client = new FakeClient { SearchResults = Enumerable.Range(0, 101).Select(index => Package with { Id = $"Example.{index}", InstalledVersion = string.Empty }).ToArray() };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        await runtime.HandleRequestAsync(Envelope("search", new() { Query = "Example" }), CancellationToken.None);
        await WaitIdleAsync(runtime);
        Assert.AreEqual(100, runtime.GetStatus().SearchResults.Count);
        Assert.IsTrue(runtime.GetStatus().SearchResultsTruncated);
        Assert.AreEqual(1, runtime.GetStatus().Packages.Count);
        Assert.AreEqual(0, client.InstallCalls);
    }

    [TestMethod]
    public async Task DiscoveryDetailsResolveUnknownVersionsAndRejectTheOldLightweightIdentity()
    {
        var unresolved = WingetClient.CreateSearchSummary(Package.Id, Package.Name);
        var resolved = Package with { InstalledVersion = string.Empty };
        var client = new FakeClient { SearchResults = [unresolved], ReviewedPackage = resolved };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        await runtime.HandleRequestAsync(Envelope("search", new() { Query = "Example" }), CancellationToken.None);
        await WaitIdleAsync(runtime);
        var searchStatus = runtime.GetStatus();
        Assert.IsFalse(searchStatus.SearchResults.Single().IsMetadataResolved);
        Assert.IsTrue(searchStatus.LastResult!.ElapsedMilliseconds >= 0);
        var originalRequest = new WingetServiceRequest { Packages = [WingetServicePolicy.Identity(unresolved)] };
        Assert.IsFalse(ParseAck(await runtime.HandleRequestAsync(Envelope("install", originalRequest), CancellationToken.None)).Accepted);
        Assert.IsTrue(ParseAck(await runtime.HandleRequestAsync(Envelope("details", originalRequest), CancellationToken.None)).Accepted);
        await WaitIdleAsync(runtime);
        Assert.IsTrue(runtime.GetStatus().LastResult!.Succeeded);
        Assert.AreEqual(resolved, runtime.GetStatus().SearchResults.Single());
        Assert.AreEqual(resolved, runtime.GetStatus().SelectedDetails!.Package);
        Assert.IsFalse(ParseAck(await runtime.HandleRequestAsync(Envelope("prepare", originalRequest with { OperationKind = "install" }), CancellationToken.None)).Accepted);
        Assert.AreEqual(0, client.NewInstallCalls);
    }

    [DataTestMethod]
    [DataRow("id")]
    [DataRow("source")]
    [DataRow("unresolved")]
    [DataRow("empty-version")]
    public async Task DiscoveryDetailsRejectUnverifiedOrDifferentPackageMetadata(string invalidField)
    {
        var unresolved = WingetClient.CreateSearchSummary(Package.Id, Package.Name);
        var resolved = Package with { InstalledVersion = string.Empty };
        var incorrect = invalidField switch
        {
            "id" => resolved with { Id = "Other.App" },
            "source" => resolved with { Source = "msstore" },
            "unresolved" => resolved with { IsMetadataResolved = false },
            _ => resolved with { AvailableVersion = string.Empty },
        };
        var client = new FakeClient { SearchResults = [unresolved], ReviewedPackage = incorrect };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        await runtime.HandleRequestAsync(Envelope("search", new() { Query = "Example" }), CancellationToken.None);
        await WaitIdleAsync(runtime);
        await runtime.HandleRequestAsync(Envelope("details", new() { Packages = [WingetServicePolicy.Identity(unresolved)] }), CancellationToken.None);
        await WaitIdleAsync(runtime);
        Assert.IsFalse(runtime.GetStatus().LastResult!.Succeeded);
        Assert.IsNull(runtime.GetStatus().SelectedDetails);
        Assert.AreEqual(unresolved, runtime.GetStatus().SearchResults.Single());
        Assert.AreEqual(0, client.NewInstallCalls);
    }

    [TestMethod]
    public void PackageMetadataResolutionDefaultsToVerifiedForOlderSerializedSnapshots()
    {
        var status = new WingetServiceStatus { SearchResults = [Package] };
        string json = JsonSerializer.Serialize(status, WingetServiceJsonContext.Default.WingetServiceStatus);
        var legacy = JsonNode.Parse(json)!;
        Assert.IsTrue(legacy["searchResults"]![0]!.AsObject().Remove("isMetadataResolved"));
        json = legacy.ToJsonString();
        Assert.IsFalse(json.Contains("isMetadataResolved", StringComparison.Ordinal));
        var restored = JsonSerializer.Deserialize(json, WingetServiceJsonContext.Default.WingetServiceStatus)!;
        Assert.IsTrue(restored.SearchResults.Single().IsMetadataResolved);
        var unresolved = status with { SearchResults = [WingetClient.CreateSearchSummary(Package.Id, Package.Name)] };
        var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(unresolved, WingetServiceJsonContext.Default.WingetServiceStatus), WingetServiceJsonContext.Default.WingetServiceStatus)!;
        Assert.IsFalse(roundTrip.SearchResults.Single().IsMetadataResolved);
    }

    [TestMethod]
    public void OlderPersistedUpdatesAndHistoryKeepResolvedMetadataWhileUnknownRowsRoundTrip()
    {
        var store = new WingetStateStore(_directory);
        store.Save(new WingetPersistentState
        {
            Packages = [Package],
            History = [new("previous", Package, "upgrade", "Succeeded")],
        });
        string path = Path.Combine(_directory, "State", "service-state.json");
        var legacy = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.IsTrue(legacy["packages"]![0]!.AsObject().Remove("isMetadataResolved"));
        Assert.IsTrue(legacy["history"]![0]!["package"]!.AsObject().Remove("isMetadataResolved"));
        string legacyJson = legacy.ToJsonString();
        Assert.IsFalse(legacyJson.Contains("isMetadataResolved", StringComparison.Ordinal));
        File.WriteAllText(path, legacyJson);
        var restored = store.Load();
        Assert.IsTrue(restored.Packages.Single().IsMetadataResolved);
        Assert.IsTrue(restored.History.Single().Package.IsMetadataResolved);
        Assert.AreEqual(Package, restored.Packages.Single());

        var unresolved = WingetClient.CreateSearchSummary(Package.Id, Package.Name);
        store.Save(restored with { Packages = [unresolved] });
        Assert.IsFalse(store.Load().Packages.Single().IsMetadataResolved);
    }

    [DataTestMethod]
    [DataRow("success")]
    [DataRow("failure")]
    [DataRow("cancel")]
    public async Task RefreshingSearchReplacesThePreviousQueryAndRowsOnlyAfterSuccess(string completion)
    {
        var original = Enumerable.Range(0, 101).Select(index => Package with { Id = index == 0 ? Package.Id : $"Example.{index}", InstalledVersion = string.Empty }).ToArray();
        var client = new FakeClient { SearchResults = original };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        await runtime.HandleRequestAsync(Envelope("search", new() { Query = "original" }), CancellationToken.None);
        await WaitIdleAsync(runtime);
        await runtime.HandleRequestAsync(Envelope("details", PackageRequest()), CancellationToken.None);
        await WaitIdleAsync(runtime);
        var previous = runtime.GetStatus();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = Package with { Id = "Replacement.App", InstalledVersion = string.Empty };
        client.SearchOperation = async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            if (completion == "failure")
            {
                throw new WingetOperationException("Synthetic search failure");
            }

            return [replacement];
        };

        try
        {
            var ack = ParseAck(await runtime.HandleRequestAsync(Envelope("search", new() { Query = " replacement " }), CancellationToken.None));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var pending = runtime.GetStatus();
            Assert.IsTrue(pending.IsBusy);
            Assert.AreEqual("original", pending.SearchQuery);
            Assert.AreSame(previous.SearchResults, pending.SearchResults, "Refreshing must not emit an empty intermediate collection.");
            Assert.AreSame(previous.SelectedDetails, pending.SelectedDetails);
            Assert.IsTrue(pending.SearchResultsTruncated);
            if (completion == "cancel")
            {
                await runtime.HandleRequestAsync(Envelope("cancel", new() { OperationId = ack.OperationId }), CancellationToken.None);
            }
            else
            {
                release.TrySetResult();
            }

            await WaitIdleAsync(runtime);
            var finished = runtime.GetStatus();
            if (completion == "success")
            {
                Assert.AreEqual("replacement", finished.SearchQuery);
                Assert.AreEqual(replacement, finished.SearchResults.Single());
                Assert.IsFalse(finished.SearchResultsTruncated);
                Assert.IsNull(finished.SelectedDetails);
                Assert.IsTrue(finished.LastResult!.Succeeded);
            }
            else
            {
                Assert.AreEqual("original", finished.SearchQuery);
                Assert.AreSame(previous.SearchResults, finished.SearchResults);
                Assert.AreSame(previous.SelectedDetails, finished.SelectedDetails);
                Assert.IsTrue(finished.SearchResultsTruncated);
                Assert.IsFalse(finished.LastResult!.Succeeded, "Failure must not relabel old results as the new query.");
            }
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task SingleOperationBoundsProgressAndFinalOutputAndResetsItForTheNextOperation()
    {
        new WingetStateStore(_directory).Save(WingetServicePolicy.CompleteCheck(new(), DateTimeOffset.UtcNow, [Package]));
        string finalOutput = new('f', 20000);
        var client = new FakeClient { WaitForNativeInstaller = true, InstallOutput = finalOutput };
        await using var runtime = new WingetServiceRuntime(client, _directory, new FakeNotifier());
        try
        {
            await runtime.HandleRequestAsync(Envelope("details", PackageRequest()), CancellationToken.None);
            await WaitIdleAsync(runtime);
            var ack = ParseAck(await runtime.HandleRequestAsync(Envelope("upgrade", PackageRequest()), CancellationToken.None));
            await client.InstallStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsNull(runtime.GetStatus().Output);
            string progressOutput = "prefix😀" + new string('p', (16 * 1024) - 1);
            client.ProgressSink!.Report(new("WaitingForInstaller", null) { Output = progressOutput });
            Assert.AreEqual(new string('p', (16 * 1024) - 1), runtime.GetStatus().Output, "Truncation must not leave an isolated low surrogate.");
            await runtime.HandleRequestAsync(Envelope("cancel", new() { OperationId = ack.OperationId }), CancellationToken.None);
            client.ProgressSink.Report(new("WaitingForInstaller", null) { Output = "native installer still running" });
            Assert.AreEqual("Cancelling", runtime.GetStatus().Phase);
            Assert.AreEqual("native installer still running", runtime.GetStatus().Output, "Output continues while cancellation waits for native exit.");
            client.InstallRelease.TrySetResult();
            await WaitIdleAsync(runtime);
            Assert.AreEqual(finalOutput[^16384..], runtime.GetStatus().Output);
            Assert.AreEqual(runtime.GetStatus().Output, runtime.GetStatus().LastResult!.Install!.Output);
            client.ProgressSink.Report(new("WaitingForInstaller", null) { Output = "late output" });
            Assert.AreEqual(finalOutput[^16384..], runtime.GetStatus().Output, "Completed operations must ignore delayed progress.");
            await runtime.HandleRequestAsync(Envelope("search", new() { Query = "Example" }), CancellationToken.None);
            Assert.IsNull(runtime.GetStatus().Output);
            await WaitIdleAsync(runtime);
        }
        finally
        {
            client.InstallRelease.TrySetResult();
        }
    }

    private static WingetServiceRequest PackageRequest() => new() { PackageId = Package.Id, Source = Package.Source, AvailableVersion = Package.AvailableVersion };

    private static KitPipeRequest Envelope(string command, WingetServiceRequest request)
        => new() { Command = command, PayloadJson = JsonSerializer.Serialize(request, WingetServiceJsonContext.Default.WingetServiceRequest) };

    private static WingetServiceAck ParseAck(KitPipeResponse response)
    {
        Assert.IsTrue(response.Success);
        return JsonSerializer.Deserialize(response.DataJson!, WingetServiceJsonContext.Default.WingetServiceAck)!;
    }

    private static async Task WaitIdleAsync(WingetServiceRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (runtime.GetStatus().IsBusy)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeNotifier : IWingetUpdateNotifier
    {
        internal int Count { get; private set; }

        public string? Show(IReadOnlyList<WingetPackageUpdate> packages)
        {
            Count++;
            return null;
        }
    }

    private sealed class FakeClient : IWingetClient
    {
        internal bool BlockChecks { get; init; }
        internal bool DelayChecksUntilReleased { get; init; }
        internal bool FailChecks { get; init; }
        internal bool BlockInstalls { get; init; }
        internal bool WaitForNativeInstaller { get; init; }
        internal bool InstallSucceeds { get; init; } = true;
        internal bool VerificationPending { get; init; }
        internal IReadOnlyList<WingetPackageUpdate> SearchResults { get; init; } = [];
        internal Func<string, CancellationToken, Task<IReadOnlyList<WingetPackageUpdate>>>? SearchOperation { get; set; }
        internal string? InstallOutput { get; init; }
        internal IProgress<WingetOperationProgress>? ProgressSink { get; private set; }
        internal Action? OnInstall { get; init; }
        internal WingetPackageUpdate? ReviewedPackage { get; init; }
        internal IReadOnlyList<WingetPackageAgreement> Agreements { get; init; } = [];
        internal int CheckCalls { get; private set; }
        internal int DownloadCalls { get; private set; }
        internal int InstallCalls { get; private set; }
        internal int NewInstallCalls { get; private set; }
        internal int SearchCalls { get; private set; }
        internal TaskCompletionSource CheckStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CheckRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource InstallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource InstallRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WingetAvailability> DetectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WingetAvailability(true, "test", "not-executed.exe", null));

        public Task<IReadOnlyList<WingetPackageUpdate>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchCalls++;
            return SearchOperation?.Invoke(query, cancellationToken) ?? Task.FromResult(SearchResults);
        }

        public Task<IReadOnlyList<WingetPackageUpdate>> ListInstalledAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WingetPackageUpdate>>([Package with { CanUninstall = true }]);

        public async Task<IReadOnlyList<WingetPackageUpdate>> CheckUpdatesAsync(CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            CheckStarted.TrySetResult();
            if (BlockChecks)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (DelayChecksUntilReleased)
            {
                await CheckRelease.Task.WaitAsync(cancellationToken);
            }

            if (FailChecks)
            {
                throw new WingetOperationException("Controlled recovery check failure.");
            }

            return [Package];
        }

        public Task<WingetPackageDetails> GetDetailsAsync(WingetPackageUpdate package, CancellationToken cancellationToken = default)
            => Task.FromResult(new WingetPackageDetails(ReviewedPackage ?? package, null, null, null, null, null, Agreements));

        public Task<WingetDownloadResult> DownloadAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            return Task.FromResult(new WingetDownloadResult("not-created", [], true));
        }

        public Task<WingetInstallResult> InstallAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, WingetPackageOptions? options = null, CancellationToken cancellationToken = default)
        {
            NewInstallCalls++;
            return UpgradeAsync(package, progress, acceptPackageAgreements, options, cancellationToken);
        }

        public async Task<WingetInstallResult> UpgradeAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, bool acceptPackageAgreements = false, WingetPackageOptions? options = null, CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            ProgressSink = progress;
            OnInstall?.Invoke();
            InstallStarted.TrySetResult();
            if (BlockInstalls)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (WaitForNativeInstaller)
            {
                await InstallRelease.Task;
            }

            return new WingetInstallResult(InstallSucceeds, false, false, InstallSucceeds ? null : "Controlled install failure.", null)
            {
                CancellationRequested = cancellationToken.IsCancellationRequested,
                VerificationPending = VerificationPending,
                Output = InstallOutput,
            };
        }

        public Task<WingetInstallResult> UninstallAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress = null, WingetPackageOptions? options = null, CancellationToken cancellationToken = default)
            => UpgradeAsync(package, progress, cancellationToken: cancellationToken, options: options);
    }
}
