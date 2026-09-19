// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.LocalserverWorker;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalServerHub.Core.Configuration;
using LocalServerHub.Core.Models;
using LocalServerHub.Windows;
using LocalserverLib.Common;
using ManagedCommon;

/// <summary>
/// Headless supervision of the services defined in the Localserver catalog.
/// </summary>
/// <remarks>
/// The Settings page used to own these <see cref="ServiceRunner"/> instances, so the
/// runners - and with them health monitoring and restart-with-backoff - died with the
/// window even though the child processes survived. This host keeps them alive for as
/// long as the worker runs.
///
/// The worker is deliberately additive: it never writes the catalog, and it starts
/// services only when asked (see <see cref="WorkerOptions.AutoStart"/>), so it cannot
/// fight the Settings page over ownership of a service.
/// </remarks>
public sealed class ServiceSupervisor : IDisposable
{
    private readonly string _dataDirectory;
    private readonly ServiceCatalogStore _catalogStore;
    private readonly Dictionary<string, ServiceRunner> _runners = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ServiceSupervisor(string dataDirectory)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _catalogStore = new ServiceCatalogStore(dataDirectory);
    }

    /// <summary>Number of services currently supervised.</summary>
    public int SupervisedCount
    {
        get
        {
            lock (_runners)
            {
                return _runners.Count;
            }
        }
    }

    /// <summary>Identifiers of the supervised services.</summary>
    public IReadOnlyList<string> SupervisedIds
    {
        get
        {
            lock (_runners)
            {
                return _runners.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// Creates a runner per enabled service in the catalog without starting it. Recovery
    /// of a previously owned process tree happens per runner in <see cref="StartAsync"/>.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ServiceCatalog catalog = await _catalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ServiceDefinition> services = catalog.Services;

        lock (_runners)
        {
            foreach (ServiceDefinition definition in services)
            {
                // Disabled services get no runner: they must not be supervised or started.
                if (string.IsNullOrWhiteSpace(definition.Id) || !definition.IsEnabled || _runners.ContainsKey(definition.Id))
                {
                    continue;
                }

                _runners[definition.Id] = new ServiceRunner(definition, formatMessage: FormatMessage);
            }
        }

        Logger.LogInfo($"[Localserver.Worker] Loaded {SupervisedCount} service definition(s) from {_dataDirectory}.");
    }

    /// <summary>
    /// Starts every service that is marked to start with the hub. Services already running
    /// (including trees adopted from a previous session) are left as they are.
    /// </summary>
    public async Task StartAutoStartServicesAsync(CancellationToken cancellationToken = default)
    {
        foreach ((string id, ServiceRunner runner) in Snapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!runner.Definition.IsEnabled)
            {
                continue;
            }

            try
            {
                if (runner.State == ServiceState.Running)
                {
                    continue;
                }

                Logger.LogInfo($"[Localserver.Worker] Starting '{id}'.");
                await runner.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One failing service must not stop the rest of the catalog.
                Logger.LogError($"[Localserver.Worker] Failed to start '{id}'.", ex);
            }
        }
    }

    /// <summary>Starts a single supervised service on request.</summary>
    public async Task<bool> StartServiceAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        ServiceRunner? runner = Find(serviceId);
        if (runner is null)
        {
            return false;
        }

        try
        {
            await runner.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Localserver.Worker] Failed to start '{serviceId}'.", ex);
            return false;
        }
    }

    /// <summary>Stops a single supervised service; the child tree is released, not killed.</summary>
    public async Task<bool> StopServiceAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        ServiceRunner? runner = Find(serviceId);
        if (runner is null)
        {
            return false;
        }

        try
        {
            await runner.StopAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Localserver.Worker] Failed to stop '{serviceId}'.", ex);
            return false;
        }
    }

    /// <summary>Snapshot of the supervised services and their current state.</summary>
    public IReadOnlyList<(string Id, string State, string? LastError)> Describe()
    {
        return Snapshot()
            .Select(pair => (pair.Id, pair.Runner.State.ToString(), pair.Runner.LastError))
            .ToList();
    }

    private List<(string Id, ServiceRunner Runner)> Snapshot()
    {
        lock (_runners)
        {
            return _runners.Select(pair => (pair.Key, pair.Value)).ToList();
        }
    }

    private ServiceRunner? Find(string serviceId)
    {
        lock (_runners)
        {
            return _runners.TryGetValue(serviceId, out ServiceRunner? runner) ? runner : null;
        }
    }

    /// <summary>
    /// Resource-key based formatting without a UI resource loader: the worker has no
    /// WinUI resources, so it falls back to the invariant English text.
    /// </summary>
    private static string FormatMessage(string resourceKey, string fallback, params object?[] args)
    {
        try
        {
            return args is { Length: > 0 }
                ? string.Format(CultureInfo.InvariantCulture, fallback, args)
                : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Releases every runner. Disposing a runner stops supervision only: the owned
    /// process trees are preserved (job objects are created with killOnClose disabled),
    /// matching the behaviour of closing the Settings window today.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<(string Id, ServiceRunner Runner)> snapshot = Snapshot();
        lock (_runners)
        {
            _runners.Clear();
        }

        foreach ((string id, ServiceRunner runner) in snapshot)
        {
            try
            {
                runner.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Localserver.Worker] Failed to dispose '{id}'.", ex);
            }
        }

        _gate.Dispose();
    }
}
