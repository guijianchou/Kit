// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;

namespace Kit.Settings.UI.ViewModels;

public partial class DashboardViewModel
{
    private IDisposable? updateStateSubscription;
    private UpdatingSettings? updateSnapshot;
    private long updateStatusGeneration;
    private bool updateStatusActive;

    public string KitVersionLabel => $"Kit v{PowerToysVersion.TrimStart('v', 'V')}";

    public string KitUpdateNotice => DashboardUpdateNotice.ResourceKey(updateSnapshot, PowerToysVersion) is { Length: > 0 } key
        ? ResourceLoader.GetString(key) : string.Empty;

    public bool HasKitUpdateNotice => !string.IsNullOrEmpty(KitUpdateNotice);

    internal void SetUpdateStatusActive(bool active)
    {
        if ((isDisposed && active) || updateStatusActive == active)
        {
            return;
        }

        updateStatusActive = active;
        long generation = ++updateStatusGeneration;
        updateStateSubscription?.Dispose();
        updateStateSubscription = null;
        if (!active)
        {
            return;
        }

        ApplyUpdateState(UpdateStateSource.Current.Snapshot);
        updateStateSubscription = UpdateStateSource.Current.Subscribe(settings => RunOnUiThread(() =>
        {
            if (!isDisposed && updateStatusActive && generation == updateStatusGeneration)
            {
                ApplyUpdateState(settings);
            }
        }));
    }

    internal void ApplyUpdateState(UpdatingSettings? settings)
    {
        updateSnapshot = settings;
        RefreshUpdateLocalization();
    }

    private void RefreshUpdateLocalization()
    {
        OnPropertyChanged(nameof(KitVersionLabel));
        OnPropertyChanged(nameof(KitUpdateNotice));
        OnPropertyChanged(nameof(HasKitUpdateNotice));
    }
}

internal static class DashboardUpdateNotice
{
    internal static string ResourceKey(UpdatingSettings? settings, string currentVersion)
    {
        if (settings is null)
        {
            return string.Empty;
        }

        if (settings.ErrorCode == "recoveryRequired")
        {
            return "Dashboard_UpdateRecovery";
        }

        if (settings.State == UpdatingSettings.UpdatingState.NetworkError)
        {
            return "Dashboard_UpdateCheckFailed";
        }

        // An old portable install can leave its state file behind after replacement.
        // Only a strictly newer, valid release can advertise a pending update.
        if (!TryVersion(settings.NewVersion, out Version? target) ||
            !TryVersion(currentVersion, out Version? current) || target <= current)
        {
            return string.Empty;
        }

        return settings.State switch
        {
            UpdatingSettings.UpdatingState.ReadyToDownload => "Dashboard_UpdateAvailable",
            UpdatingSettings.UpdatingState.ReadyToInstall => "Dashboard_UpdateReady",
            UpdatingSettings.UpdatingState.Downloading => "Dashboard_UpdateDownloading",
            UpdatingSettings.UpdatingState.Verifying => "Dashboard_UpdateVerifying",
            UpdatingSettings.UpdatingState.Installing => "Dashboard_UpdateInstalling",
            UpdatingSettings.UpdatingState.ErrorDownloading => "Dashboard_UpdateFailed",
            _ => string.Empty,
        };
    }

    private static bool TryVersion(string? value, out Version? version)
    {
        if (Version.TryParse(value?.Trim().TrimStart('v', 'V'), out Version? parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
            return true;
        }

        version = null;
        return false;
    }
}
