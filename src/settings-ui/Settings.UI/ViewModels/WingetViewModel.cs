// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Library.Interfaces;
using Kit.WingetLib;
using ManagedCommon.ServiceHost;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Kit.Settings.UI.ViewModels;

public enum WingetWorkspace
{
    Updates,
    Installed,
    Discover,
}

public sealed partial class WingetPackageRow(WingetPackageUpdate package) : Observable
{
    public WingetPackageUpdate Package { get; private set; } = package;

    public void Update(WingetPackageUpdate package)
    {
        if (Package != package)
        {
            Package = package;
            foreach (string property in new[] { nameof(Name), nameof(Identity), nameof(Versions), nameof(Id), nameof(Source), nameof(SourceLabel), nameof(InstalledVersion), nameof(AvailableVersion), nameof(IsMetadataResolved), nameof(IsInstalled), nameof(InstallationLabel), nameof(StateLabel) })
            {
                OnPropertyChanged(property);
            }
        }
    }

    public string Name => Package.Name;

    public string Identity => Package.Id + " · " + Package.Source;

    public string Versions => !IsMetadataResolved ? WingetViewModel.L("MetadataPending") : string.IsNullOrEmpty(Package.AvailableVersion) ? Package.InstalledVersion : IsInstalled ? Package.InstalledVersion + "  →  " + Package.AvailableVersion : Package.AvailableVersion;

    public string Id => Package.Id;

    public string Source => Package.Source;

    public string InstalledVersion => !IsMetadataResolved ? WingetViewModel.L("NotCheckedShort") : IsInstalled ? Package.InstalledVersion : "—";

    public string AvailableVersion => !IsMetadataResolved ? WingetViewModel.L("NotCheckedShort") : Package.AvailableVersion;

    public bool IsMetadataResolved => Package.IsMetadataResolved;

    public bool IsInstalled => IsMetadataResolved && !string.IsNullOrWhiteSpace(Package.InstalledVersion);

    public string InstallationLabel => !IsMetadataResolved ? WingetViewModel.L("InstallationUnknown") : IsInstalled ? WingetViewModel.L("AlreadyInstalled") : WingetViewModel.L("AvailableToInstall");
}

public sealed partial class WingetViewModel : PageViewModelBase
{
    private readonly ISettingsRepository<GeneralSettings> generalRepository;
    private readonly ISettingsRepository<WingetSettings> moduleRepository;
    private readonly Func<string, int> sendMessage;
    private readonly KitServicePipeClient client;
    private readonly DispatcherQueue dispatcher;
    private readonly DispatcherTimer timer;
    private readonly Dictionary<string, object?> presentedValues = new(StringComparer.Ordinal);
    private CancellationTokenSource? pageLifetime;
    private WingetServiceStatus status = new();
    private WingetPackageRow? selectedPackage;
    private WingetPackageRow? selectedSearchPackage;
    private int activeTabIndex;
    private string updateFilterText = string.Empty;
    private string searchQuery = string.Empty;
    private bool polling;
    private bool disposed;
    private bool connected;
    private bool requestPending;
    private string? pendingCommand;
    private string? pendingSearchQuery;
    private bool cancellationPending;
    private bool loadingDetails;
    private string? detailsError;
    private WingetPackageRow? detailsRequestRow;
    private WingetDownloadResult? lastDownload;
    private bool restartRequired;
    private string? connectionError;
    private string? localError;
    private string? visibleResultId;
    private string? successfulSearchQuery;
    private long? successfulSearchElapsedMilliseconds;
    private string activity = string.Empty;
    private WingetServiceOperationResult? lastPackageResult;
    private bool applyingStatus;

    public WingetViewModel(
        ISettingsRepository<GeneralSettings> generalRepository,
        ISettingsRepository<WingetSettings> moduleRepository,
        Func<string, int> sendMessage)
    {
        this.generalRepository = generalRepository;
        this.moduleRepository = moduleRepository;
        this.sendMessage = sendMessage;
        dispatcher = DispatcherQueue.GetForCurrentThread();
        var runtimeDirectory = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        client = new KitServicePipeClient("Winget", Path.Combine(runtimeDirectory, "Kit.WingetService.exe"));
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += OnTimerTick;
        generalRepository.SettingsChanged += OnGeneralSettingsChanged;
    }

    protected override string ModuleName => WingetSettings.ModuleName;

    public ObservableCollection<WingetPackageRow> Packages { get; } = new();

    public ObservableCollection<WingetPackageRow> FilteredUpdates { get; } = new();

    public ObservableCollection<WingetPackageRow> SearchResults { get; } = new();

    public int ActiveTabIndex
    {
        get => activeTabIndex;
        set
        {
            int next = Math.Clamp(value, (int)WingetWorkspace.Updates, (int)WingetWorkspace.Discover);
            if (activeTabIndex != next)
            {
                SaveWorkspacePreferences();
                activeTabIndex = next;
                RestoreWorkspacePreferences();
                OnPropertyChanged(nameof(ActiveTabIndex));
                RefreshWorkspace();
                EnsureInstalledLoaded();
            }
        }
    }

    public string UpdateFilterText
    {
        get => updateFilterText;
        set
        {
            if (Set(ref updateFilterText, value ?? string.Empty))
            {
                RefreshUpdateFilter();
                NotifyState();
            }
        }
    }

    public string SearchQuery
    {
        get => searchQuery;
        set
        {
            if (Set(ref searchQuery, value ?? string.Empty))
            {
                NotifyState();
            }
        }
    }

    public WingetPackageRow? SelectedSearchPackage
    {
        get => selectedSearchPackage;
        set
        {
            if (Set(ref selectedSearchPackage, value))
            {
                NotifyState();
            }
        }
    }

    public WingetPackageRow? ActiveSelection
    {
        get => ActiveTabIndex switch { (int)WingetWorkspace.Updates => SelectedPackage, (int)WingetWorkspace.Discover => SelectedSearchPackage, _ => SelectedInstalledPackage };
        set
        {
            if (ActiveTabIndex == (int)WingetWorkspace.Updates)
            {
                SelectedPackage = value;
            }
            else if (ActiveTabIndex == (int)WingetWorkspace.Discover)
            {
                SelectedSearchPackage = value;
            }
            else
            {
                SelectedInstalledPackage = value;
            }
        }
    }

    public string InstallActionLabel => ActiveTabIndex == (int)WingetWorkspace.Updates ? L("UpgradeSelected") : ActiveSelection?.IsInstalled == true ? L("AlreadyInstalled") : L("InstallSelected");

    public string SelectionIdentity => ActiveSelection?.Identity ?? string.Empty;

    public string SelectionPublisher => SelectedDetails?.Publisher ?? string.Empty;

    public bool CanSearch => CanCheck && status.Availability is { IsAvailable: true } && SearchQuery.Trim().Length is >= 2 and <= 200;

    public string SearchResultText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(status.SearchQuery))
            {
                return L("SearchCatalog");
            }

            string summary = string.Format(CultureInfo.CurrentCulture, L(status.SearchResultsTruncated ? "SearchResultsLimited" : "SearchResultsCount"), SearchResults.Count, status.SearchQuery);
            return successfulSearchQuery == status.SearchQuery && successfulSearchElapsedMilliseconds is { } elapsed
                ? summary + " · " + string.Format(CultureInfo.CurrentCulture, L("SearchElapsed"), elapsed / 1000d)
                : summary;
        }
    }

    public string SearchEmptyText => status.IsBusy && status.OperationKind == "search" ? L("Searching") :
        string.IsNullOrWhiteSpace(status.SearchQuery) ? L("SearchHint") : L("NoSearchResults");

    public Visibility SearchEmptyVisibility => SearchResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsEnabled
    {
        get => generalRepository.SettingsConfig.Enabled.Winget;
        set
        {
            if (IsEnabled == value)
            {
                return;
            }

            generalRepository.SettingsConfig.Enabled.Winget = value;
            sendMessage(new OutGoingGeneralSettings(generalRepository.SettingsConfig).ToString());
            RefreshEnabledState();
        }
    }

    public bool DailyCheckEnabled
    {
        get => moduleRepository.SettingsConfig.Properties.DailyCheckEnabled.Value;
        set
        {
            if (DailyCheckEnabled != value)
            {
                moduleRepository.SettingsConfig.Properties.DailyCheckEnabled.Value = value;
                SaveSettings();
                NotifyState();
            }
        }
    }

    public bool NotifyUpdates
    {
        get => moduleRepository.SettingsConfig.Properties.NotifyUpdates.Value;
        set
        {
            if (NotifyUpdates != value)
            {
                moduleRepository.SettingsConfig.Properties.NotifyUpdates.Value = value;
                SaveSettings();
                NotifyState();
            }
        }
    }

    public WingetPackageRow? SelectedPackage
    {
        get => selectedPackage;
        set
        {
            if (Set(ref selectedPackage, value))
            {
                NotifyState();
            }
        }
    }

    public WingetPackageDetails? SelectedDetails =>
        status.SelectedDetails is { } details && ActiveSelection is { } row &&
        details.Package.Id == row.Package.Id && details.Package.Source == row.Package.Source &&
        details.Package.AvailableVersion == row.Package.AvailableVersion &&
        details.Package.InstalledVersion == row.Package.InstalledVersion ? details : null;

    public bool IsBusy => IsEnabled && connected && status.IsBusy;

    public bool CanCheck => IsEnabled && connected && !IsBusy && !requestPending;

    public bool CanActOnPackage => CanCheck && status.Availability is { IsAvailable: true } && ActiveSelection != null;

    public bool CanDownload => CanActOnPackage && SelectedDetails != null;

    public bool CanInstall => CanDownload && ActiveSelection?.IsMetadataResolved == true && SelectedDetails?.Package.IsMetadataResolved == true && SelectedDetails.Package.InstallEligibility is WingetInstallEligibility.UserWithoutElevation or WingetInstallEligibility.RequiresElevation
        && (ActiveTabIndex == (int)WingetWorkspace.Updates || ActiveSelection?.IsInstalled == false);

    public bool HasSelection => ActiveSelection != null;

    public bool HasError => IsEnabled && !string.IsNullOrWhiteSpace(ErrorText);

    public string ErrorText => localError ?? connectionError ??
        (status.Availability is { IsAvailable: false } unavailable ? unavailable.Error : null) ??
        (!status.IsBusy && status.LastResult?.Succeeded == false ? status.LastResult.Message : null) ?? string.Empty;

    public bool HasNotificationError => IsEnabled && NotifyUpdates && !string.IsNullOrWhiteSpace(status.NotificationError);

    public string NotificationErrorText => status.NotificationError ?? string.Empty;

    public bool RestartRequired => restartRequired;

    public bool HasReleaseNotesLink => SafeWebUri(SelectedDetails?.Package.ReleaseNotesUrl) != null;

    public bool HasDownload => !string.IsNullOrWhiteSpace(DownloadDirectory);

    public string DownloadDirectory => lastDownload?.Directory ?? string.Empty;

    public double Progress => Math.Clamp(status.ProgressPercent ?? 0, 0, 100);

    public bool IsProgressIndeterminate => status.ProgressPercent == null;

    public string StatusText => !IsEnabled ? L("Disabled") :
        !connected ? L(string.IsNullOrWhiteSpace(connectionError) ? "Connecting" : "Unavailable") :
        status.Availability is not { IsAvailable: true } ? L("Unavailable") :
        IsBusy ? L("Working") : L("Ready");

    public string VersionText => status.Availability?.Version is { Length: > 0 } version ? "WinGet " + version : L("VersionUnknown");

    public string UpdateCountText => string.Format(CultureInfo.CurrentCulture, L("UpdateCount"), Packages.Count);

    public string EmptyText => CurrentRows.Count > 0 ? L("WorkspaceNoMatches") : ActiveTabIndex switch
    {
        (int)WingetWorkspace.Discover => SearchEmptyText,
        (int)WingetWorkspace.Installed => IsBusy && status.OperationKind == "list-installed" ? L("InstalledLoading") : L("InstalledEmpty"),
        _ => status.LastSuccessUtc == null ? L("NotChecked") : L("UpToDate"),
    };

    public Visibility EmptyVisibility => FilteredUpdates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string LastCheckText => string.Format(CultureInfo.CurrentCulture, L("LastCheck"), FormatTime(status.LastSuccessUtc));

    public string NextCheckText => string.Format(CultureInfo.CurrentCulture, L("NextCheck"), DailyCheckEnabled ? FormatTime(status.NextCheckUtc) : L("ManualOnly"));

    public string OperationText => IsBusy ? L("Phase_" + status.Phase) :
        status.LastResult?.Install?.VerificationPending == true ? L("VerificationPending") :
        status.LastResult?.Succeeded == true ? L("Completed") : string.Empty;

    public string SelectionTitle => ActiveSelection?.Name ?? L("SelectPackage");

    public string SelectionVersions => ActiveSelection?.Versions ?? string.Empty;

    public string ReleaseNotes => SelectedDetails?.Package.ReleaseNotes is { Length: > 0 } notes ? notes : L("NoReleaseNotes");

    public string PackageDescription => SelectedDetails?.Description ?? L("LoadDetailsHint");

    public string InstallExplanation => ActiveTabIndex == (int)WingetWorkspace.Installed ? L("UninstallExplanation") : SelectedDetails == null ? L("LoadDetailsHint") : ActiveTabIndex == (int)WingetWorkspace.Discover && ActiveSelection?.IsInstalled == true ? L("InstalledHint") :
        SelectedDetails.Package.InstallEligibility switch
        {
            WingetInstallEligibility.UserWithoutElevation => L("UserInstall"),
            WingetInstallEligibility.RequiresElevation => L("ElevationInstall"),
            _ => L("ManualInstall"),
        };

    public string ManualCommand => ActiveTabIndex == (int)WingetWorkspace.Installed && ActiveSelection is { } installed
        ? CreateUninstallCommand(installed.Package)
        : SelectedDetails?.Package.ManualUpgradeCommand ?? ActiveSelection?.Package.ManualUpgradeCommand ?? string.Empty;

    public string Activity => activity;

    internal bool ConfirmationMatches(WingetPackageDetails expected) => SelectedDetails is { } current &&
        current.Package == expected.Package && current.License == expected.License && current.LicenseUrl == expected.LicenseUrl &&
        current.Agreements.SequenceEqual(expected.Agreements);

    public static string L(string name)
    {
        string value = ResourceLoaderInstance.ResourceLoader.GetString("Winget_" + name);
        return string.IsNullOrEmpty(value) ? name : value;
    }

    public static Uri? SafeWebUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;

    public void SetPageActive(bool active)
    {
        if (disposed)
        {
            return;
        }

        if (!active)
        {
            timer.Stop();
            pageLifetime?.Cancel();
            pageLifetime?.Dispose();
            pageLifetime = null;
            return;
        }

        if (pageLifetime != null)
        {
            return;
        }

        pageLifetime = new CancellationTokenSource();
        timer.Start();
        _ = PollAsync();
    }

    public void RefreshEnabledState()
    {
        if (disposed)
        {
            return;
        }

        if (!IsEnabled)
        {
            connected = false;
            connectionError = null;
            localError = null;
        }

        NotifyState();
        _ = PollAsync();
    }

    public Task RequestAsync(string command, bool acceptPackageAgreements = false)
    {
        // Native button and Enter callbacks do not always carry a managed UI context.
        var previousContext = SynchronizationContext.Current;
        try
        {
            if (dispatcher.HasThreadAccess)
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            }

            return RequestCoreAsync(command, acceptPackageAgreements);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private async Task RequestCoreAsync(string command, bool acceptPackageAgreements)
    {
        if (command == "cancel")
        {
            await CancelQueueAsync();
            return;
        }

        if (disposed || !IsEnabled || pageLifetime == null || requestPending || (status.IsBusy && command != "cancel"))
        {
            return;
        }

        if ((command == "search" && !CanSearch)
            || (command is "install" or "upgrade" && !CanInstall)
            || (command == "install" && ActiveTabIndex != (int)WingetWorkspace.Discover)
            || (command == "upgrade" && ActiveTabIndex != (int)WingetWorkspace.Updates))
        {
            return;
        }

        var lifetime = pageLifetime;
        var token = lifetime.Token;
        bool IsCurrentRequest() => !disposed && IsEnabled && !token.IsCancellationRequested && ReferenceEquals(pageLifetime, lifetime);
        requestPending = true;
        pendingCommand = command;
        if (command == "search")
        {
            pendingSearchQuery = SearchQuery.Trim();
        }

        localError = null;
        NotifyState();
        try
        {
            var request = new WingetServiceRequest
            {
                Command = command,
                PackageId = ActiveSelection?.Package.Id,
                Source = ActiveSelection?.Package.Source,
                AvailableVersion = ActiveSelection?.Package.AvailableVersion,
                Packages = ActiveSelection is { } selection ? [IdentityOf(selection.Package)] : [],
                OperationId = status.OperationId,
                AcceptPackageAgreements = acceptPackageAgreements,
                Query = command == "search" ? SearchQuery.Trim() : null,
            };
            var reply = await client.SendCommandAsync(command, JsonSerializer.Serialize(request, WingetServiceJsonContext.Default.WingetServiceRequest), token);
            if (!IsCurrentRequest())
            {
                return;
            }

            if (!reply.Success)
            {
                localError = reply.Error?.Message ?? L("RequestFailed");
            }
            else if (!string.IsNullOrEmpty(reply.DataJson))
            {
                var ack = JsonSerializer.Deserialize(reply.DataJson, WingetServiceJsonContext.Default.WingetServiceAck);
                if (ack is { Accepted: false })
                {
                    localError = ack.Error ?? L("RequestFailed");
                }
            }

            await PollAsync();
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentRequest())
            {
                connectionError = L("ServiceUnavailable");
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentRequest())
            {
                connectionError = L("ServiceUnavailable") + " " + ex.Message;
            }
        }
        finally
        {
            requestPending = false;
            pendingCommand = null;
            if (!disposed)
            {
                NotifyState();
            }
        }
    }

    private static string FormatTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? L("Never");

    internal void ReportActionError(Exception exception)
    {
        localError = L("RequestFailed") + " " + exception.Message;
        NotifyState();
    }

    internal void ApplyStatus(WingetServiceStatus updated)
    {
        var previous = status;
        connected = true;
        connectionError = null;
        status = updated;
        applyingStatus = true;
        try
        {
            bool catalogChanged = !previous.Packages.SequenceEqual(updated.Packages)
                || !previous.SearchResults.SequenceEqual(updated.SearchResults)
                || !previous.InstalledPackages.SequenceEqual(updated.InstalledPackages)
                || !previous.IgnoredPackages.SequenceEqual(updated.IgnoredPackages)
                || previous.SearchQuery != updated.SearchQuery;
            if (catalogChanged)
            {
                SyncRows(Packages, status.Packages);
                SyncRows(SearchResults, status.SearchResults);
                SyncRows(InstalledPackages, status.InstalledPackages, includeInstalledVersion: true);
                if (SelectedInstalledPackage != null && !InstalledPackages.Contains(SelectedInstalledPackage))
                {
                    SelectedInstalledPackage = null;
                }

                if (SelectedPackage != null && !Packages.Contains(SelectedPackage))
                {
                    SelectedPackage = null;
                }

                if (SelectedSearchPackage != null && !SearchResults.Contains(SelectedSearchPackage))
                {
                    SelectedSearchPackage = null;
                }

                RefreshUpdateFilter();

                RefreshWorkspace();
            }

            RefreshOperationRows();
            if (status.LastResult is { } result && visibleResultId != result.OperationId)
            {
                visibleResultId = result.OperationId;
                if (result is { Kind: "search", Succeeded: true })
                {
                    successfulSearchQuery = status.SearchQuery;
                    successfulSearchElapsedMilliseconds = result.ElapsedMilliseconds is >= 0 ? result.ElapsedMilliseconds : null;
                }

                if (result.Kind is "queue" or "install" or "upgrade" or "uninstall")
                {
                    lastPackageResult = result;
                }

                if (result is { Succeeded: true, Download.HashVerified: true })
                {
                    lastDownload = result.Download;
                }

                restartRequired |= result.Install?.RebootRequired == true;
                string entry = DateTime.Now.ToString("T", CultureInfo.CurrentCulture) + " · " + L("Action_" + result.Kind) + ": " +
                    (result.Install?.VerificationPending == true ? L("VerificationPending") : result.Message ?? (result.Succeeded ? L("Completed") : L("RequestFailed")));
                activity = entry + Environment.NewLine + activity;
                if (activity.Length > 12000)
                {
                    activity = activity[..12000];
                }
            }
        }
        finally
        {
            applyingStatus = false;
        }

        NotifyState();
    }

    private void SaveSettings()
    {
        var message = new SndModuleSettings<SndWingetSettings>(new SndWingetSettings(moduleRepository.SettingsConfig));
        sendMessage(message.ToJsonString());
    }

    private void OnGeneralSettingsChanged(GeneralSettings settings) => dispatcher.TryEnqueue(RefreshEnabledState);

    private async void OnTimerTick(object? sender, object e) => await PollAsync();

    private Task PollAsync()
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            if (dispatcher.HasThreadAccess)
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            }

            return PollCoreAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private async Task PollCoreAsync()
    {
        if (polling || disposed || !IsEnabled || pageLifetime == null)
        {
            return;
        }

        var lifetime = pageLifetime;
        var token = lifetime.Token;
        bool IsCurrentPoll() => !disposed && IsEnabled && !token.IsCancellationRequested && ReferenceEquals(pageLifetime, lifetime);
        polling = true;
        try
        {
            var reply = await client.SendCommandAsync("get-status", ct: token);
            if (!reply.Success || string.IsNullOrEmpty(reply.DataJson))
            {
                throw new InvalidOperationException(reply.Error?.Message ?? L("RequestFailed"));
            }

            var updated = JsonSerializer.Deserialize(reply.DataJson, WingetServiceJsonContext.Default.WingetServiceStatus);
            if (updated == null || !IsCurrentPoll())
            {
                return;
            }

            ApplyStatus(updated);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentPoll())
            {
                // The active page was not cancelled, so the transport timed out.
                connected = false;
                connectionError = L("ServiceUnavailable");
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentPoll())
            {
                connected = false;
                connectionError = L("ServiceUnavailable") + " " + ex.Message;
            }
        }
        finally
        {
            polling = false;
            if (!disposed)
            {
                NotifyState();
                EnsureInstalledLoaded();
            }
        }
    }

    private void PublishValue(string property, object? value)
    {
        if (!presentedValues.TryGetValue(property, out var previous) || !Equals(previous, value))
        {
            presentedValues[property] = value;
            OnPropertyChanged(property);
        }
    }

    private static void SyncRows(ObservableCollection<WingetPackageRow> rows, IReadOnlyList<WingetPackageUpdate> packages, bool includeInstalledVersion = false)
    {
        string Key(WingetPackageUpdate package) => package.Source + "\n" + package.Id + (includeInstalledVersion ? "\n" + package.InstalledVersion : string.Empty);
        var byIdentity = rows.ToDictionary(row => Key(row.Package), StringComparer.OrdinalIgnoreCase);
        var desired = packages.DistinctBy(Key, StringComparer.OrdinalIgnoreCase).Select(package =>
        {
            if (byIdentity.TryGetValue(Key(package), out var row))
            {
                row.Update(package);
                return row;
            }

            return new WingetPackageRow(package);
        }).ToList();
        if (rows.SequenceEqual(desired))
        {
            return;
        }

        var retained = desired.ToHashSet();
        for (int index = rows.Count - 1; index >= 0; index--)
        {
            if (!retained.Contains(rows[index]))
            {
                rows.RemoveAt(index);
            }
        }

        for (int index = 0; index < desired.Count; index++)
        {
            int current = rows.IndexOf(desired[index]);
            if (current < 0)
            {
                rows.Insert(index, desired[index]);
            }
            else if (current != index)
            {
                rows.Move(current, index);
            }
        }
    }

    private void RefreshUpdateFilter()
    {
        var terms = UpdateFilterText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var visible = Packages.Where(row => terms.All(term => row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Id.Contains(term, StringComparison.OrdinalIgnoreCase) || row.Source.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
        if (!FilteredUpdates.SequenceEqual(visible))
        {
            var retained = visible.ToHashSet();
            for (int index = FilteredUpdates.Count - 1; index >= 0; index--)
            {
                if (!retained.Contains(FilteredUpdates[index]))
                {
                    FilteredUpdates.RemoveAt(index);
                }
            }

            for (int index = 0; index < visible.Count; index++)
            {
                int current = FilteredUpdates.IndexOf(visible[index]);
                if (current < 0)
                {
                    FilteredUpdates.Insert(index, visible[index]);
                }
                else if (current != index)
                {
                    FilteredUpdates.Move(current, index);
                }
            }
        }

        if (SelectedPackage != null && !visible.Contains(SelectedPackage))
        {
            SelectedPackage = null;
        }
    }

    public override void Dispose()
    {
        SetPageActive(false);
        disposed = true;
        timer.Tick -= OnTimerTick;
        generalRepository.SettingsChanged -= OnGeneralSettingsChanged;
        DisposeWorkspace();
        base.Dispose();
    }
}
