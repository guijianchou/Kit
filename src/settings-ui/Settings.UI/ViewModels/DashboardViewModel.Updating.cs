// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Globalization;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;

namespace Kit.Settings.UI.ViewModels;

public enum DashboardUpdateStatusKind
{
    Normal,
    Attention,
    Error,
}

public partial class DashboardViewModel
{
    private IDisposable? updateStateSubscription;
    private UpdatingSettings? updateSnapshot;
    private DashboardUpdatePresentation currentUpdatePresentation = new(DashboardUpdateStatusKind.Normal, "\uE895", "Dashboard_UpdateCheckPrompt", 0);
    private long updateStatusGeneration;
    private bool updateStatusActive;
    private bool isKitUpdateChecking;

    public string KitVersionLabel => $"Kit v{PowerToysVersion.TrimStart('v', 'V')}";

    public string KitVersionShortLabel => $"v{PowerToysVersion.TrimStart('v', 'V')}";

    public string KitUpdateTitle => ResourceLoader.GetString("Dashboard_UpdateTitle/Text") is { Length: > 0 } title ? title : "Kit Update";

    public string KitUpdateNotice => DashboardUpdateNotice.ResourceKey(updateSnapshot, PowerToysVersion) is { Length: > 0 } key
        ? ResourceLoader.GetString(key) : string.Empty;

    public bool HasKitUpdateNotice => !string.IsNullOrEmpty(KitUpdateNotice);

    public bool IsKitUpdateChecking => isKitUpdateChecking;

    public DashboardUpdateStatusKind KitUpdateStatusKind => currentUpdatePresentation.Kind;

    public string KitUpdateStatusGlyph => currentUpdatePresentation.Glyph;

    public bool IsKitUpdateSyncIcon => !isKitUpdateChecking && currentUpdatePresentation.Glyph is "\uE895" or "\uE777";

    public bool IsKitUpdateDownloadIcon => !isKitUpdateChecking && currentUpdatePresentation.Glyph == "\uE896";

    public bool IsKitUpdateErrorIcon => !isKitUpdateChecking && currentUpdatePresentation.Glyph == "\uE783";

    public string KitUpdateStatusText => DashboardUpdateStatus.FormatSubtitle(currentUpdatePresentation, ResourceLoader.GetString);

    public string KitUpdateTooltip => DashboardUpdateStatus.FormatTooltip(
        updateSnapshot,
        PowerToysVersion,
        isKitUpdateChecking,
        KitVersionLabel,
        KitUpdateStatusText,
        ResourceLoader.GetString);

    public string KitUpdateAccessibleName => string.IsNullOrEmpty(KitUpdateStatusText)
        ? $"{KitUpdateTitle} {KitVersionShortLabel}"
        : $"{KitUpdateTitle} {KitVersionShortLabel}, {KitUpdateStatusText}";

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

        ApplyUpdateState(UpdateStateSource.Current.Snapshot, UpdateStateSource.Current.IsChecking);
        updateStateSubscription = UpdateStateSource.Current.Subscribe(settings => RunOnUiThread(() =>
        {
            if (!isDisposed && updateStatusActive && generation == updateStatusGeneration)
            {
                ApplyUpdateState(settings, UpdateStateSource.Current.IsChecking);
            }
        }));
    }

    internal void ApplyUpdateState(UpdatingSettings? settings) =>
        ApplyUpdateState(settings, UpdateStateSource.Current.IsChecking);

    internal void ApplyUpdateState(UpdatingSettings? settings, bool checking)
    {
        updateSnapshot = settings;
        isKitUpdateChecking = checking;
        currentUpdatePresentation = DashboardUpdateStatus.Resolve(settings, PowerToysVersion, checking, DateTimeOffset.Now);
        RefreshUpdateLocalization();
    }

    internal void RefreshUpdateRelativeTime()
    {
        if (isDisposed || !updateStatusActive)
        {
            return;
        }

        DashboardUpdatePresentation updated = DashboardUpdateStatus.Resolve(
            updateSnapshot,
            PowerToysVersion,
            isKitUpdateChecking,
            DateTimeOffset.Now);
        if (updated == currentUpdatePresentation)
        {
            return;
        }

        currentUpdatePresentation = updated;
        OnPropertyChanged(nameof(KitUpdateStatusText));
        OnPropertyChanged(nameof(KitUpdateTooltip));
        OnPropertyChanged(nameof(KitUpdateAccessibleName));
    }

    private void RefreshUpdateLocalization()
    {
        OnPropertyChanged(nameof(KitVersionLabel));
        OnPropertyChanged(nameof(KitVersionShortLabel));
        OnPropertyChanged(nameof(KitUpdateTitle));
        OnPropertyChanged(nameof(KitUpdateNotice));
        OnPropertyChanged(nameof(HasKitUpdateNotice));
        OnPropertyChanged(nameof(IsKitUpdateChecking));
        OnPropertyChanged(nameof(KitUpdateStatusKind));
        OnPropertyChanged(nameof(KitUpdateStatusGlyph));
        OnPropertyChanged(nameof(IsKitUpdateSyncIcon));
        OnPropertyChanged(nameof(IsKitUpdateDownloadIcon));
        OnPropertyChanged(nameof(IsKitUpdateErrorIcon));
        OnPropertyChanged(nameof(KitUpdateStatusText));
        OnPropertyChanged(nameof(KitUpdateTooltip));
        OnPropertyChanged(nameof(KitUpdateAccessibleName));
    }
}

internal readonly record struct DashboardUpdatePresentation(
    DashboardUpdateStatusKind Kind,
    string Glyph,
    string SubtitleResourceKey,
    int RelativeAmount);

internal static class DashboardUpdateStatus
{
    private const string SyncGlyph = "\uE895";
    private const string DownloadGlyph = "\uE896";
    private const string RestartGlyph = "\uE777";
    private const string ErrorGlyph = "\uE783";

    internal static DashboardUpdatePresentation Resolve(
        UpdatingSettings? settings,
        string currentVersion,
        bool isChecking,
        DateTimeOffset now)
    {
        if (isChecking)
        {
            return new(DashboardUpdateStatusKind.Normal, SyncGlyph, "Dashboard_UpdateChecking", 0);
        }

        string noticeKey = DashboardUpdateNotice.ResourceKey(settings, currentVersion);
        if (!string.IsNullOrEmpty(noticeKey))
        {
            return noticeKey switch
            {
                "Dashboard_UpdateRecovery" or "Dashboard_UpdateCheckFailed" or "Dashboard_UpdateFailed" =>
                    new(DashboardUpdateStatusKind.Error, ErrorGlyph, noticeKey, 0),
                "Dashboard_UpdateReady" =>
                    new(DashboardUpdateStatusKind.Attention, RestartGlyph, noticeKey, 0),
                "Dashboard_UpdateAvailable" =>
                    new(DashboardUpdateStatusKind.Attention, DownloadGlyph, noticeKey, 0),
                _ =>
                    new(DashboardUpdateStatusKind.Normal, SyncGlyph, noticeKey, 0),
            };
        }

        if (settings is { State: UpdatingSettings.UpdatingState.UpToDate } && settings.LastCheckedDateTime is { } checkedAt)
        {
            DateTime checkedLocal = checkedAt.Kind == DateTimeKind.Utc ? checkedAt.ToLocalTime() : checkedAt;
            TimeSpan elapsed = now.LocalDateTime - checkedLocal;
            if (elapsed < TimeSpan.FromMinutes(1))
            {
                return new(DashboardUpdateStatusKind.Normal, SyncGlyph, "Dashboard_UpdateLastCheckedJustNow", 0);
            }

            if (elapsed < TimeSpan.FromMinutes(2))
            {
                return new(DashboardUpdateStatusKind.Normal, SyncGlyph, "Dashboard_UpdateLastCheckedMinute", 1);
            }

            if (elapsed < TimeSpan.FromHours(1))
            {
                return new(DashboardUpdateStatusKind.Normal, SyncGlyph, "Dashboard_UpdateLastCheckedMinutes", (int)elapsed.TotalMinutes);
            }

            if (elapsed < TimeSpan.FromHours(2))
            {
                return new(DashboardUpdateStatusKind.Normal, SyncGlyph, "Dashboard_UpdateLastCheckedHour", 1);
            }

            if (elapsed < TimeSpan.FromDays(1))
            {
                return new(DashboardUpdateStatusKind.Normal, SyncGlyph, "Dashboard_UpdateLastCheckedHours", (int)elapsed.TotalHours);
            }

            if (elapsed < TimeSpan.FromDays(2))
            {
                return new(DashboardUpdateStatusKind.Normal, SyncGlyph, "Dashboard_UpdateLastCheckedDay", 1);
            }

            return new(DashboardUpdateStatusKind.Normal, SyncGlyph, "Dashboard_UpdateLastCheckedDays", (int)elapsed.TotalDays);
        }

        return new(DashboardUpdateStatusKind.Normal, SyncGlyph, "Dashboard_UpdateCheckPrompt", 0);
    }

    internal static string FormatSubtitle(DashboardUpdatePresentation presentation, Func<string, string> getString)
    {
        string template = getString(presentation.SubtitleResourceKey);
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return presentation.RelativeAmount > 0 && template.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, template, presentation.RelativeAmount)
            : template;
    }

    internal static string FormatTooltip(
        UpdatingSettings? settings,
        string currentVersion,
        bool isChecking,
        string versionLabel,
        string subtitleText,
        Func<string, string> getString)
    {
        string hint = getString("Dashboard_UpdateOpenSettingsHint");
        if (isChecking)
        {
            return JoinLines($"{versionLabel} · {subtitleText}", hint);
        }

        string noticeKey = DashboardUpdateNotice.ResourceKey(settings, currentVersion);
        if (!string.IsNullOrEmpty(noticeKey))
        {
            string header = !string.IsNullOrEmpty(settings?.NewVersion) &&
                noticeKey is "Dashboard_UpdateAvailable" or "Dashboard_UpdateReady" or "Dashboard_UpdateDownloading" or "Dashboard_UpdateVerifying" or "Dashboard_UpdateInstalling"
                ? $"{versionLabel} · {subtitleText} ({settings.NewVersion})"
                : $"{versionLabel} · {subtitleText}";
            return JoinLines(header, hint);
        }

        if (settings is { State: UpdatingSettings.UpdatingState.UpToDate } && !string.IsNullOrEmpty(settings.LastCheckedDateLocalized))
        {
            string upToDate = getString("Dashboard_UpdateUpToDate");
            string checkedPrefix = getString("General_VersionLastChecked/Text");
            string header = string.IsNullOrEmpty(upToDate) ? versionLabel : $"{versionLabel} · {upToDate}";
            string checkedLine = string.IsNullOrEmpty(checkedPrefix)
                ? settings.LastCheckedDateLocalized
                : $"{checkedPrefix}{settings.LastCheckedDateLocalized}";
            return JoinLines(header, checkedLine, hint);
        }

        return JoinLines(versionLabel, hint);
    }

    private static string JoinLines(params string[] lines)
    {
        string result = string.Empty;
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            result = string.IsNullOrEmpty(result) ? line : $"{result}\n{line}";
        }

        return result;
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
