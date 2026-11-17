// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Localserver.UnitTests;

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Kit.LocalserverWorker;
using LocalServerHub.Core.Configuration;
using LocalServerHub.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The worker hosts the service runners that the Settings page used to own, so its
/// catalogue handling and lifecycle surface are pinned here. These tests never start a
/// child process: they cover loading, filtering and disposal.
/// </summary>
[TestClass]
public sealed class ServiceSupervisorTests
{
    private string _dataDirectory = string.Empty;

    [TestInitialize]
    public void CreateIsolatedDataDirectory()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "KitLocalserverWorkerTests", Guid.NewGuid().ToString("N"));
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

    private void WriteCatalog(params (string Id, bool Enabled)[] services)
    {
        // Shape mirrors ServiceDefinition: id/name/cwd/executable are required.
        var payload = new
        {
            services = System.Array.ConvertAll(services, s => new
            {
                id = s.Id,
                name = s.Id,
                cwd = _dataDirectory,
                executable = "cmd.exe",
                baseArgs = new[] { "/c", "exit 0" },
                isEnabled = s.Enabled,
            }),
        };

        File.WriteAllText(
            Path.Combine(_dataDirectory, "services.json"),
            JsonSerializer.Serialize(payload));
    }

    [TestMethod]
    public async Task MinimalCatalogPreservesOmittedAndNestedDefaults()
    {
        var payload = new
        {
            services = new[]
            {
                new
                {
                    id = "defaults",
                    name = "Defaults",
                    cwd = _dataDirectory,
                    executable = "cmd.exe",
                    health = new { },
                    restart = new { },
                    runtimes = new[] { new { kind = "node" } },
                    parameters = new[] { new { flag = "--enabled", name = "Enabled" } },
                    presets = new[] { new { name = "Default" } },
                },
            },
        };
        await File.WriteAllTextAsync(Path.Combine(_dataDirectory, "services.json"), JsonSerializer.Serialize(payload));

        var catalog = await new ServiceCatalogStore(_dataDirectory).LoadAsync();
        Assert.AreEqual(2, catalog.Version);
        var service = catalog.Services.Single();
        Assert.IsTrue(service.IsEnabled);
        Assert.AreEqual("auto", service.Encoding);
        Assert.AreEqual(10, service.StopTimeoutSec);
        Assert.AreEqual(0, service.BaseArgs.Count);
        Assert.AreEqual(0, service.Tags.Count);
        Assert.AreEqual(0, service.Env.Count);
        Assert.AreEqual(60, service.Health.TimeoutSec);
        Assert.AreEqual(2, service.Health.IntervalSec);
        Assert.AreEqual(30, service.Health.MonitorIntervalSec);
        CollectionAssert.AreEqual(new[] { 200 }, service.Health.ExpectStatus.ToArray());
        Assert.AreEqual(3, service.Restart.MaxRetries);
        CollectionAssert.AreEqual(new[] { 2, 5, 15 }, service.Restart.BackoffSec.ToArray());
        Assert.IsTrue(service.Runtimes.Single().IsRequired);
        Assert.IsTrue(service.Parameters.Single().IsEditable);
        Assert.AreEqual(0, service.Parameters.Single().Options.Count);
        Assert.AreEqual(0, service.Parameters.Single().ConflictsWith.Count);
        Assert.AreEqual(0, service.Parameters.Single().Requires.Count);
        Assert.AreEqual(0, service.Presets.Single().Args.Count);
    }

    [TestMethod]
    public async Task SparseLegacyHubSettingsPreserveDefaultsAndExplicitValues()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_dataDirectory, HubSettingsStore.SettingsFileName),
            "{\"theme\":\"Dark\",\"logRingBufferLines\":2345}");
        using var store = new HubSettingsStore(_dataDirectory);
        var settings = await store.LoadAsync();
        Assert.AreEqual(HubSettings.CurrentVersion, settings.Version);
        Assert.AreEqual(AppThemePreference.Dark, settings.Theme);
        Assert.AreEqual(2345, settings.LogRingBufferLines);
        Assert.AreEqual(10, settings.LogRotationSizeMB);
        Assert.AreEqual(5, settings.LogRetentionCopies);
        Assert.AreEqual("logs", settings.LogDirectory);
    }

    [TestMethod]
    public async Task CatalogMetadataPreservesEnumAndPrimitiveParameterValues()
    {
        var store = new ServiceCatalogStore(_dataDirectory);
        var catalog = new ServiceCatalog
        {
            Services =
            [
                new ServiceDefinition
                {
                    Id = "roundtrip",
                    Name = "Roundtrip",
                    Cwd = _dataDirectory,
                    Executable = "cmd.exe",
                    Restart = new RestartSettings { Policy = RestartPolicy.OnFailure },
                    Parameters = [new ParameterDefinition { Flag = "--enabled", Name = "Enabled", Default = true }],
                },
            ],
        };
        await store.SaveAsync(catalog);
        var loaded = await store.LoadAsync();
        Assert.AreEqual(RestartPolicy.OnFailure, loaded.Services[0].Restart.Policy);
        Assert.AreEqual(true, loaded.Services[0].Parameters[0].Default);
        using var settingsStore = new HubSettingsStore(_dataDirectory);
        await settingsStore.SaveAsync(new HubSettings { Theme = AppThemePreference.Dark });
        Assert.AreEqual(AppThemePreference.Dark, (await settingsStore.LoadAsync()).Theme);
    }

    [TestMethod]
    public async Task EmptyCatalogueSupervisesNothing()
    {
        using var supervisor = new ServiceSupervisor(_dataDirectory);

        await supervisor.LoadAsync();

        Assert.AreEqual(0, supervisor.SupervisedCount);
        Assert.AreEqual(0, supervisor.SupervisedIds.Count);
    }

    [TestMethod]
    public async Task DisabledServicesAreNotSupervised()
    {
        WriteCatalog(("enabled-service", true), ("disabled-service", false));
        using var supervisor = new ServiceSupervisor(_dataDirectory);

        await supervisor.LoadAsync();

        Assert.AreEqual(1, supervisor.SupervisedCount, "A disabled service must not be supervised.");
        Assert.IsTrue(supervisor.SupervisedIds.Contains("enabled-service"));
    }

    [TestMethod]
    public async Task StopAllIsSafeForNeverStartedServices()
    {
        // StopAllAsync is the worker's module-disable teardown. With nothing started it must
        // be a no-op that keeps supervision intact (this suite never starts child processes).
        WriteCatalog(("enabled-service", true));
        using var supervisor = new ServiceSupervisor(_dataDirectory);

        await supervisor.LoadAsync();
        await supervisor.StopAllAsync();

        Assert.AreEqual(1, supervisor.SupervisedCount, "Stopping must not remove supervision.");
        Assert.IsTrue(supervisor.SupervisedIds.Contains("enabled-service"));
    }

    [TestMethod]
    public async Task DescribeReportsIdAndStateForEverySupervisedService()
    {
        WriteCatalog(("alpha", true));
        using var supervisor = new ServiceSupervisor(_dataDirectory);
        await supervisor.LoadAsync();

        var described = supervisor.Describe();

        Assert.AreEqual(1, described.Count);
        Assert.AreEqual("alpha", described[0].Id);
        Assert.IsFalse(string.IsNullOrWhiteSpace(described[0].State), "Every supervised service reports a state.");
    }

    [TestMethod]
    public async Task UnknownServiceOperationsReportFailureInsteadOfThrowing()
    {
        using var supervisor = new ServiceSupervisor(_dataDirectory);
        await supervisor.LoadAsync();

        Assert.IsFalse(await supervisor.StartServiceAsync("does-not-exist"));
        Assert.IsFalse(await supervisor.StopServiceAsync("does-not-exist"));
    }

    [TestMethod]
    public async Task DisposeReleasesSupervisionAndIsIdempotent()
    {
        WriteCatalog(("alpha", true));
        var supervisor = new ServiceSupervisor(_dataDirectory);
        await supervisor.LoadAsync();
        Assert.AreEqual(1, supervisor.SupervisedCount);

        supervisor.Dispose();
        Assert.AreEqual(0, supervisor.SupervisedCount);

        // Disposing twice must not throw: the runner lifecycle is torn down once.
        supervisor.Dispose();
    }

    [TestMethod]
    public async Task ReloadReconcilesAddedRenamedAndDisabledDefinitions()
    {
        WriteCatalog(("alpha", true), ("disabled", false));
        using var supervisor = new ServiceSupervisor(_dataDirectory);
        await supervisor.LoadAsync();

        WriteCatalog(("renamed-alpha", true), ("beta", true));
        await supervisor.ReloadAsync(force: true);
        CollectionAssert.AreEquivalent(new[] { "renamed-alpha", "beta" }, supervisor.SupervisedIds.ToArray());

        WriteCatalog(("renamed-alpha", false), ("beta", true));
        await supervisor.ReloadAsync(force: true);
        CollectionAssert.AreEquivalent(new[] { "beta" }, supervisor.SupervisedIds.ToArray());
        Assert.IsTrue(supervisor.Describe().All(service => service.State == "Stopped"));
    }

    [TestMethod]
    public async Task LoadingTwiceDoesNotDuplicateRunners()
    {
        WriteCatalog(("alpha", true));
        using var supervisor = new ServiceSupervisor(_dataDirectory);

        await supervisor.LoadAsync();
        await supervisor.LoadAsync();

        Assert.AreEqual(1, supervisor.SupervisedCount, "Re-loading must not create a second runner for the same service.");
    }
}
