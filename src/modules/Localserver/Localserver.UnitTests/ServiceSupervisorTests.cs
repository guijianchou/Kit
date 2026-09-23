// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Localserver.UnitTests;

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Kit.LocalserverWorker;
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
    public async Task LoadingTwiceDoesNotDuplicateRunners()
    {
        WriteCatalog(("alpha", true));
        using var supervisor = new ServiceSupervisor(_dataDirectory);

        await supervisor.LoadAsync();
        await supervisor.LoadAsync();

        Assert.AreEqual(1, supervisor.SupervisedCount, "Re-loading must not create a second runner for the same service.");
    }
}
