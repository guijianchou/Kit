// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Utilities;

namespace Kit.Settings.UI.Services;

/// <summary>Shares the updater's local state without checking releases or initializing Settings.</summary>
internal sealed class UpdateStateSource
{
    internal static UpdateStateSource Current { get; } = new();

    private readonly object gate = new();
    private readonly List<Subscription> subscriptions = [];
    private readonly Func<UpdatingSettings?> read;
    private readonly Func<Action, IFileSystemWatcher?> watch;
    private IFileSystemWatcher? watcher;
    private UpdatingSettings? snapshot;
    private bool manualCheckInProgress;
    private bool refreshing;
    private long requestedVersion;

    internal UpdateStateSource(Func<UpdatingSettings?>? read = null, Func<Action, IFileSystemWatcher?>? watch = null)
    {
        this.read = read ?? UpdatingSettings.LoadSettings;
        this.watch = watch ?? CreateWatcher;
    }

    internal bool IsChecking
    {
        get
        {
            lock (gate)
            {
                return manualCheckInProgress;
            }
        }
    }

    internal void SetManualCheckInProgress(bool checking)
    {
        Subscription[] listeners;
        UpdatingSettings? current;
        lock (gate)
        {
            if (manualCheckInProgress == checking)
            {
                return;
            }

            manualCheckInProgress = checking;
            current = Clone(snapshot);
            listeners = subscriptions.ToArray();
        }

        foreach (Subscription listener in listeners)
        {
            listener.Notify(Clone(current));
        }
    }

    internal UpdatingSettings? Snapshot
    {
        get
        {
            lock (gate)
            {
                return Clone(snapshot);
            }
        }
    }

    internal IDisposable Subscribe(Action<UpdatingSettings?> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var subscription = new Subscription(this, callback);
        lock (gate)
        {
            subscriptions.Add(subscription);
        }

        Refresh();
        return subscription;
    }

    internal void Refresh()
    {
        lock (gate)
        {
            requestedVersion++;
            if (refreshing || subscriptions.Count == 0)
            {
                return;
            }

            refreshing = true;
        }

        // Both watcher creation and file reads stay off the UI thread. A burst of
        // progress writes uses one worker; it cannot publish an older read last.
        _ = Task.Run(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        while (true)
        {
            long version;
            lock (gate)
            {
                if (subscriptions.Count == 0)
                {
                    refreshing = false;
                    return;
                }

                version = requestedVersion;
                try
                {
                    watcher ??= watch(Refresh);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // The current read still works when observation is unavailable.
                }
            }

            UpdatingSettings? settings = null;
            for (int attempt = 0; attempt < 4 && settings is null; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }

                try
                {
                    settings = read();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            Subscription[] listeners;
            lock (gate)
            {
                if (version != requestedVersion)
                {
                    continue;
                }

                snapshot = Clone(settings);
                listeners = subscriptions.ToArray();
            }

            foreach (Subscription listener in listeners)
            {
                listener.Notify(Clone(settings));
            }

            lock (gate)
            {
                if (version == requestedVersion)
                {
                    refreshing = false;
                    return;
                }
            }
        }
    }

    private static IFileSystemWatcher? CreateWatcher(Action refresh)
    {
        var observer = Helper.GetFileWatcher(string.Empty, UpdatingSettings.SettingsFile, refresh);
        if (observer is not null)
        {
            // UpdateStorage replaces the file atomically; LastWrite alone misses it.
            observer.NotifyFilter |= NotifyFilters.FileName;
            observer.Created += (_, _) => refresh();
            observer.Renamed += (_, _) => refresh();
            observer.Deleted += (_, _) => refresh();
            observer.Error += (_, _) => refresh();
        }

        return observer;
    }

    private static UpdatingSettings? Clone(UpdatingSettings? settings) => settings is null ? null : new()
    {
        State = settings.State,
        ReleasePageLink = settings.ReleasePageLink,
        LastCheckedDate = settings.LastCheckedDate,
        DownloadedInstallerFilename = settings.DownloadedInstallerFilename,
        DownloadedBytes = settings.DownloadedBytes,
        TotalBytes = settings.TotalBytes,
        ErrorCode = settings.ErrorCode,
    };

    private void Unsubscribe(Subscription subscription)
    {
        lock (gate)
        {
            subscriptions.Remove(subscription);
            if (subscriptions.Count == 0)
            {
                requestedVersion++;
                watcher?.Dispose();
                watcher = null;
            }
        }
    }

    private sealed class Subscription(UpdateStateSource owner, Action<UpdatingSettings?> callback) : IDisposable
    {
        private UpdateStateSource? source = owner;

        internal void Notify(UpdatingSettings? settings)
        {
            if (Volatile.Read(ref source) is not null)
            {
                try
                {
                    callback(settings);
                }
                catch (Exception)
                {
                    // An observer cannot interrupt another page's state refresh.
                }
            }
        }

        public void Dispose() => Interlocked.Exchange(ref source, null)?.Unsubscribe(this);
    }
}
