// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.WingetLib;
using Microsoft.UI.Dispatching;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class WingetPackageRow
{
    private bool isSelected;
    private bool isIgnored;

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    public bool IsIgnored
    {
        get => isIgnored;
        set
        {
            if (Set(ref isIgnored, value))
            {
                OnPropertyChanged(nameof(StateLabel));
            }
        }
    }

    public string SourceLabel => WingetSourceFilter.Label(Source);

    public string StateLabel => !IsMetadataResolved ? WingetViewModel.L("DetailsNotLoaded") : IsIgnored ? WingetViewModel.L("IgnoredState") : Package.IsUpdateAvailable ? WingetViewModel.L("UpdateAvailableState") : IsInstalled ? WingetViewModel.L("AlreadyInstalled") : string.Empty;
}

public sealed class WingetSourceFilter(string key, int initialCount, bool selected) : Observable
{
    private bool isSelected = selected;
    private int count = initialCount;

    public string Key { get; } = key;

    public string Name => Label(Key);

    public int Count
    {
        get => count;
        set => Set(ref count, value);
    }

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    internal static string Label(string source) => source.ToLowerInvariant() switch
    {
        "winget" => "WinGet",
        "msix" => "MSIX / Store",
        "msstore" => "Microsoft Store",
        "local" => WingetViewModel.L("LocalSource"),
        _ => source,
    };
}

public sealed class WingetQueueRow(WingetQueueItem item) : Observable
{
    public WingetQueueItem Item { get; private set; } = item;

    public void Update(WingetQueueItem item)
    {
        if (Item == item)
        {
            return;
        }

        var previous = Item;
        Item = item;
        if (previous.Package.Name != item.Package.Name)
        {
            OnPropertyChanged(nameof(Name));
        }

        if (previous.Package.Id != item.Package.Id || previous.Package.Source != item.Package.Source)
        {
            OnPropertyChanged(nameof(Identity));
        }

        if (previous.Kind != item.Kind)
        {
            OnPropertyChanged(nameof(KindText));
        }

        if (previous.State != item.State)
        {
            OnPropertyChanged(nameof(StateText));
        }

        if (previous.Message != item.Message)
        {
            OnPropertyChanged(nameof(Message));
        }

        if (previous.Result?.Output != item.Result?.Output)
        {
            OnPropertyChanged(nameof(Output));
        }
    }

    public string Name => Item.Package.Name;

    public string Identity => Item.Package.Id + " · " + WingetSourceFilter.Label(Item.Package.Source);

    public string KindText => WingetViewModel.L("Action_" + Item.Kind);

    public string StateText => WingetViewModel.L("Queue_" + Item.State);

    public string Message => Item.Message ?? string.Empty;

    public string Output => Item.Result?.Output ?? string.Empty;
}

public sealed partial class WingetViewModel
{
    private static readonly JsonSerializerOptions BundleJsonOptions = new() { WriteIndented = true };
    private readonly HashSet<WingetPackageRow> observedRows = new();
    private readonly Dictionary<string, bool> sourceSelection = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, WorkspacePreferences> workspacePreferences = new();
    private WingetPackageRow? selectedInstalledPackage;
    private string filterText = string.Empty;
    private bool exactMatch;
    private bool includeUnknown;
    private bool showIgnored;
    private bool sortDescending;
    private bool silentInstall;
    private bool refreshingWorkspace;
    private bool installedRequested;
    private int sortIndex;
    private int viewModeIndex;
    private int installScopeIndex;
    private int installArchitectureIndex;

    private sealed record WorkspacePreferences(string Filter, bool Exact, int Sort, bool Descending, int View, Dictionary<string, bool> Sources);

    private void SaveWorkspacePreferences() => workspacePreferences[ActiveTabIndex] = new(FilterText, ExactMatch, SortIndex, SortDescending, ViewModeIndex, new(sourceSelection, StringComparer.OrdinalIgnoreCase));

    private void RestoreWorkspacePreferences()
    {
        workspacePreferences.TryGetValue(ActiveTabIndex, out var preferences);
        filterText = preferences?.Filter ?? string.Empty;
        exactMatch = preferences?.Exact ?? false;
        sortIndex = preferences?.Sort ?? 0;
        sortDescending = preferences?.Descending ?? false;
        viewModeIndex = preferences?.View ?? 0;
        sourceSelection.Clear();
        if (preferences != null)
        {
            foreach (var pair in preferences.Sources)
            {
                sourceSelection[pair.Key] = pair.Value;
            }
        }
    }

    public ObservableCollection<WingetPackageRow> InstalledPackages { get; } = new();

    public ObservableCollection<WingetPackageRow> DisplayPackages { get; } = new();

    public ObservableCollection<WingetSourceFilter> SourceFilters { get; } = new();

    public ObservableCollection<WingetQueueRow> QueueItems { get; } = new();

    public ObservableCollection<WingetQueueRow> HistoryItems { get; } = new();

    public IReadOnlyList<WingetPackageIdentity> IgnoredPackages => status.IgnoredPackages;

    public WingetPackageRow? SelectedInstalledPackage
    {
        get => selectedInstalledPackage;
        set
        {
            if (Set(ref selectedInstalledPackage, value))
            {
                NotifyState();
            }
        }
    }

    private ObservableCollection<WingetPackageRow> CurrentRows => ActiveTabIndex switch
    {
        (int)WingetWorkspace.Updates => Packages,
        (int)WingetWorkspace.Discover => SearchResults,
        _ => InstalledPackages,
    };

    public string FilterText
    {
        get => filterText;
        set => SetWorkspaceFilter(ref filterText, value ?? string.Empty);
    }

    public bool ExactMatch
    {
        get => exactMatch;
        set => SetWorkspaceFilter(ref exactMatch, value);
    }

    public bool IncludeUnknown
    {
        get => includeUnknown;
        set => SetWorkspaceFilter(ref includeUnknown, value);
    }

    public bool ShowIgnored
    {
        get => showIgnored;
        set => SetWorkspaceFilter(ref showIgnored, value);
    }

    public int SortIndex
    {
        get => sortIndex;
        set => SetWorkspaceFilter(ref sortIndex, Math.Clamp(value, 0, 4));
    }

    public bool SortDescending
    {
        get => sortDescending;
        set => SetWorkspaceFilter(ref sortDescending, value);
    }

    public int ViewModeIndex
    {
        get => viewModeIndex;
        set
        {
            if (Set(ref viewModeIndex, Math.Clamp(value, 0, 2)))
            {
                NotifyState();
            }
        }
    }

    public bool SilentInstall
    {
        get => silentInstall;
        set
        {
            if (Set(ref silentInstall, value))
            {
                NotifyState();
            }
        }
    }

    public int InstallScopeIndex
    {
        get => installScopeIndex;
        set
        {
            if (Set(ref installScopeIndex, Math.Clamp(value, 0, 2)))
            {
                NotifyState();
            }
        }
    }

    public int InstallArchitectureIndex
    {
        get => installArchitectureIndex;
        set
        {
            if (Set(ref installArchitectureIndex, Math.Clamp(value, 0, 3)))
            {
                NotifyState();
            }
        }
    }

    public int SelectedCount => DisplayPackages.Count(row => row.IsSelected);

    public string SelectionSummary => string.Format(CultureInfo.CurrentCulture, L("SelectedCount"), SelectedCount);

    public bool? IsAllVisibleSelected
    {
        get => DisplayPackages.Count == 0 || SelectedCount == 0 ? false : SelectedCount == DisplayPackages.Count ? true : null;
        set
        {
            if (value.HasValue)
            {
                SelectAllVisible(value.Value);
            }
        }
    }

    public string WorkspaceTitle => L(ActiveTabIndex switch { (int)WingetWorkspace.Updates => "UpdatesWorkspace", (int)WingetWorkspace.Discover => "DiscoverWorkspace", _ => "InstalledWorkspace" });

    public string WorkspaceSubtitle => string.Format(CultureInfo.CurrentCulture, L("WorkspaceCount"), CurrentRows.Count, DisplayPackages.Count, SelectedCount)
        + (ActiveTabIndex == (int)WingetWorkspace.Updates ? " · " + LastCheckText : string.Empty);

    public string PrimaryActionLabel => L(ActiveTabIndex switch { (int)WingetWorkspace.Updates => "UpdateSelection", (int)WingetWorkspace.Discover => "ReviewInstallSelection", _ => "UninstallSelection" });

    public bool CanPrepareSelection => CanCheck && !RecoveryRequired && SelectedCount is > 0 and <= 50 &&
        DisplayPackages.Where(row => row.IsSelected).All(row => ActiveTabIndex switch
        {
            (int)WingetWorkspace.Discover => (!row.IsMetadataResolved || !row.IsInstalled) && row.Source.Equals("winget", StringComparison.OrdinalIgnoreCase),
            (int)WingetWorkspace.Installed => row.Package.CanUninstall,
            _ => row.IsInstalled && !row.IsIgnored,
        });

    public bool CanReload => RecoveryRequired ? CanCheck : ActiveTabIndex == (int)WingetWorkspace.Discover ? CanSearch : CanCheck;

    public bool HasRows => DisplayPackages.Count > 0;

    public bool IsQueueRunning => IsBusy && status.OperationKind == "queue";

    public bool RecoveryRequired => status.RecoveryRequired;

    public void SelectAllVisible(bool selected)
    {
        refreshingWorkspace = true;
        foreach (var row in DisplayPackages)
        {
            row.IsSelected = selected;
        }

        refreshingWorkspace = false;
        NotifyState();
    }

    public Task ReloadAsync() => RunOnUiContextAsync(async () =>
    {
        if (RecoveryRequired)
        {
            if (!await SendWorkspaceRequestAsync(new WingetServiceRequest { Command = "check" }, waitForCompletion: true) || RecoveryRequired)
            {
                return false;
            }
        }

        if (ActiveTabIndex != (int)WingetWorkspace.Discover || CanSearch)
        {
            await RequestAsync(ActiveTabIndex switch { (int)WingetWorkspace.Updates => "check", (int)WingetWorkspace.Discover => "search", _ => "list-installed" });
        }

        return true;
    });

    public Task<bool> LoadDetailsAsync() => RunOnUiContextAsync(async () =>
    {
        if (SelectedDetails != null)
        {
            return true;
        }

        if (ActiveSelection is not { } row || !CanActOnPackage)
        {
            return false;
        }

        detailsRequestRow = row;
        loadingDetails = true;
        detailsError = null;
        NotifyState();
        try
        {
            bool completed = await SendWorkspaceRequestAsync(
                new WingetServiceRequest
                {
                    Command = "details",
                    PackageId = row.Id,
                    Source = row.Source,
                    AvailableVersion = row.Package.AvailableVersion,
                    Packages = [IdentityOf(row.Package)],
                },
                waitForCompletion: true);
            if (ReferenceEquals(row, ActiveSelection) && (!completed || SelectedDetails == null))
            {
                detailsError = string.IsNullOrWhiteSpace(ErrorText) ? L("DetailsUnavailable") : ErrorText;
            }

            return completed && SelectedDetails != null;
        }
        finally
        {
            loadingDetails = false;
            NotifyState();
        }
    });

    public Task<WingetPreparedOperation?> PrepareSelectionAsync() => RunOnUiContextAsync(async () =>
    {
        if (!CanPrepareSelection)
        {
            return null;
        }

        var identities = SelectedIdentities();
        string kind = CurrentKind;
        var options = CurrentOptions;
        bool completed = await SendWorkspaceRequestAsync(new WingetServiceRequest
        {
            Command = "prepare",
            OperationKind = kind,
            Packages = identities,
            Options = options,
        }, waitForCompletion: true);
        if (completed && status.PreparedOperation is { } prepared)
        {
            if (PreparedSelectionMatches(prepared))
            {
                return prepared;
            }

            localError = L("ConfirmationChanged");
            NotifyState();
        }

        return null;
    });

    public bool PreparedSelectionMatches(WingetPreparedOperation prepared) => IsEnabled && connected && !IsBusy && !RecoveryRequired &&
        prepared.Kind == CurrentKind && prepared.Options == CurrentOptions &&
        prepared.Packages.All(details => details.Package.IsMetadataResolved) &&
        SelectedCount == prepared.Packages.Count && SelectedIdentities().ToHashSet().SetEquals(prepared.Packages.Select(details => IdentityOf(details.Package))) &&
        DateTimeOffset.UtcNow - prepared.CreatedUtc < TimeSpan.FromMinutes(10);

    public Task SubmitPreparedAsync(WingetPreparedOperation prepared) => RunOnUiContextAsync(async () =>
    {
        if (!PreparedSelectionMatches(prepared))
        {
            localError = L("ConfirmationChanged");
            NotifyState();
            return false;
        }

        return await SendWorkspaceRequestAsync(new WingetServiceRequest
        {
            Command = "enqueue",
            PreparationId = prepared.Id,

            // Metadata resolution can reorder the visible list. The confirmed
            // identities are a set, but the service preserves prepared queue order.
            Packages = prepared.Packages.Select(details => IdentityOf(details.Package)).ToArray(),
            Options = CurrentOptions,
            AcceptPackageAgreements = true,
        });
    });

    public Task IgnoreSelectedAsync() => SetIgnoredAsync(SelectedIdentities(), true);

    public Task SetIgnoredAsync(IEnumerable<WingetPackageIdentity> packages, bool ignore) => RunOnUiContextAsync(async () =>
    {
        var identities = packages.ToArray();
        if (!CanCheck || identities.Length == 0)
        {
            return false;
        }

        return await SendWorkspaceRequestAsync(new WingetServiceRequest { Command = ignore ? "ignore" : "unignore", Packages = identities });
    });

    public Task CancelQueueAsync() => RunOnUiContextAsync(() => SendWorkspaceRequestAsync(new WingetServiceRequest
    {
        Command = IsQueueRunning ? "cancel-queue" : "cancel",
        OperationId = status.OperationId,
    }));

    public string CreateCsv()
    {
        static string Cell(string value)
        {
            // Prevent package metadata becoming a spreadsheet formula when opened.
            if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0]))
            {
                value = "'" + value;
            }

            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        var result = new StringBuilder("Name,Id,InstalledVersion,AvailableVersion,Source\r\n");
        foreach (var row in DisplayPackages)
        {
            result.AppendLine(string.Join(",", new[] { row.Name, row.Id, row.Package.InstalledVersion, row.Package.AvailableVersion, row.Source }.Select(Cell)));
        }

        return result.ToString();
    }

    public string CreateBundleJson()
    {
        var selected = DisplayPackages.Where(row => row.IsSelected).ToArray();
        if (selected.Length == 0 || selected.Any(row => !row.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(L("BundleSourceRestriction"));
        }

        var packages = selected.DistinctBy(row => row.Id, StringComparer.OrdinalIgnoreCase).Select(row => new Dictionary<string, object?>
        {
            ["PackageIdentifier"] = row.Id,
        }).ToArray();
        var bundle = new Dictionary<string, object?>
        {
            ["$schema"] = "https://aka.ms/winget-packages.schema.2.0.json",
            ["CreationDate"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["Sources"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Packages"] = packages,
                    ["SourceDetails"] = new Dictionary<string, string>
                    {
                        ["Argument"] = "https://cdn.winget.microsoft.com/cache",
                        ["Identifier"] = "Microsoft.Winget.Source_8wekyb3d8bbwe",
                        ["Name"] = "winget",
                        ["Type"] = "Microsoft.PreIndexed.Package",
                    },
                },
            },
        };
        return JsonSerializer.Serialize(bundle, BundleJsonOptions);
    }

    private string CurrentKind => ActiveTabIndex switch { (int)WingetWorkspace.Updates => "upgrade", (int)WingetWorkspace.Discover => "install", _ => "uninstall" };

    private WingetPackageOptions CurrentOptions => new()
    {
        Silent = SilentInstall,
        Scope = InstallScopeIndex switch { 1 => "user", 2 => "machine", _ => null },
        Architecture = ActiveTabIndex == (int)WingetWorkspace.Installed ? null : InstallArchitectureIndex switch { 1 => "x64", 2 => "x86", 3 => "arm64", _ => null },
        IncludeUnknown = ActiveTabIndex == (int)WingetWorkspace.Updates && IncludeUnknown,
    };

    private static WingetPackageIdentity IdentityOf(WingetPackageUpdate package) => new(package.Id, package.Source, package.InstalledVersion, package.AvailableVersion);

    private static string CreateUninstallCommand(WingetPackageUpdate package)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        return "winget uninstall --id " + Quote(package.Id) + " --exact --version " + Quote(package.InstalledVersion)
            + (package.Source is "winget" or "msstore" ? " --source " + Quote(package.Source) : string.Empty) + " --interactive";
    }

    private WingetPackageIdentity[] SelectedIdentities() => DisplayPackages.Where(row => row.IsSelected).Select(row => IdentityOf(row.Package)).ToArray();

    private void SetWorkspaceFilter<T>(ref T field, T value)
    {
        if (Set(ref field, value))
        {
            RefreshWorkspace();
        }
    }

    private void RefreshWorkspace()
    {
        if (refreshingWorkspace || disposed)
        {
            return;
        }

        refreshingWorkspace = true;
        try
        {
            var allRows = Packages.Concat(SearchResults).Concat(InstalledPackages).ToHashSet();
            foreach (var row in observedRows.Except(allRows).ToArray())
            {
                row.PropertyChanged -= OnWorkspaceRowChanged;
                observedRows.Remove(row);
            }

            foreach (var row in allRows)
            {
                if (observedRows.Add(row))
                {
                    row.PropertyChanged += OnWorkspaceRowChanged;
                }

                row.IsIgnored = status.IgnoredPackages.Any(identity => identity.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase) && identity.Source.Equals(row.Source, StringComparison.OrdinalIgnoreCase));
            }

            var groups = CurrentRows.GroupBy(row => row.Source, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            var filters = new List<WingetSourceFilter>();
            foreach (var group in groups)
            {
                var filter = SourceFilters.FirstOrDefault(item => item.Key.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
                if (filter == null)
                {
                    filter = new WingetSourceFilter(group.Key, group.Count(), !sourceSelection.TryGetValue(group.Key, out bool enabled) || enabled);
                    filter.PropertyChanged += OnSourceFilterChanged;
                }

                filter.IsSelected = !sourceSelection.TryGetValue(group.Key, out bool selected) || selected;
                filter.Count = group.Count();
                filters.Add(filter);
            }

            foreach (var old in SourceFilters.Except(filters))
            {
                old.PropertyChanged -= OnSourceFilterChanged;
            }

            Reconcile(SourceFilters, filters);
            var enabledSources = filters.Where(filter => filter.IsSelected).Select(filter => filter.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string query = FilterText.Trim();
            bool Matches(WingetPackageRow row) => query.Length == 0 || (ExactMatch
                ? row.Id.Equals(query, StringComparison.OrdinalIgnoreCase) || row.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
                : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(term => row.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || row.Id.Contains(term, StringComparison.OrdinalIgnoreCase)));
            var visible = CurrentRows.Where(row => enabledSources.Contains(row.Source) && Matches(row)
                && (ActiveTabIndex != (int)WingetWorkspace.Updates || ((ShowIgnored || !row.IsIgnored) && (IncludeUnknown || !row.Package.InstalledVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase)))))
                .ToList();
            string SortKey(WingetPackageRow row) => SortIndex switch { 1 => row.Id, 2 => row.InstalledVersion, 3 => row.AvailableVersion, 4 => row.Source, _ => row.Name };
            visible.Sort((left, right) =>
            {
                int comparison = NaturalCompare(SortKey(left), SortKey(right));
                if (comparison == 0)
                {
                    comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Identity, right.Identity);
                }

                return SortDescending ? -comparison : comparison;
            });
            var retained = visible.ToHashSet();
            foreach (var row in CurrentRows.Where(row => !retained.Contains(row)))
            {
                row.IsSelected = false;
            }

            var focus = ActiveSelection;
            Reconcile(DisplayPackages, visible);
            ActiveSelection = focus != null && retained.Contains(focus) ? focus : null;
        }
        finally
        {
            refreshingWorkspace = false;
        }

        NotifyState();
    }

    private void OnWorkspaceRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!refreshingWorkspace && e.PropertyName == nameof(WingetPackageRow.IsSelected))
        {
            NotifyState();
        }
    }

    private void OnSourceFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!refreshingWorkspace && e.PropertyName == nameof(WingetSourceFilter.IsSelected) && sender is WingetSourceFilter filter)
        {
            sourceSelection[filter.Key] = filter.IsSelected;
            RefreshWorkspace();
        }
    }

    private void RefreshOperationRows()
    {
        static void Sync(ObservableCollection<WingetQueueRow> rows, IReadOnlyList<WingetQueueItem> items)
        {
            var existing = rows.ToDictionary(row => row.Item.Id, StringComparer.Ordinal);
            var desired = items.Select(item =>
            {
                if (existing.TryGetValue(item.Id, out var row))
                {
                    row.Update(item);
                    return row;
                }

                return new WingetQueueRow(item);
            }).ToArray();
            Reconcile(rows, desired);
        }

        Sync(QueueItems, status.Queue);
        Sync(HistoryItems, status.History);
        restartRequired |= status.Queue.Any(item => item.Result?.RebootRequired == true);
    }

    private static void Reconcile<T>(ObservableCollection<T> rows, IReadOnlyList<T> desired)
    {
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

    private Task<T> RunOnUiContextAsync<T>(Func<Task<T>> action)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            if (dispatcher.HasThreadAccess)
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            }

            return action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private async Task<bool> SendWorkspaceRequestAsync(WingetServiceRequest request, bool waitForCompletion = false)
    {
        bool control = request.Command is "cancel" or "cancel-queue";
        if (disposed || !IsEnabled || pageLifetime == null || (!control && (requestPending || status.IsBusy)))
        {
            return false;
        }

        var lifetime = pageLifetime;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        bool IsCurrent() => !disposed && IsEnabled && !token.IsCancellationRequested && ReferenceEquals(pageLifetime, lifetime);
        if (!control)
        {
            requestPending = true;
            pendingCommand = request.Command;
        }
        else
        {
            cancellationPending = true;
        }

        localError = null;
        NotifyState();
        try
        {
            var reply = await client.SendCommandAsync(request.Command, JsonSerializer.Serialize(request, WingetServiceJsonContext.Default.WingetServiceRequest), token);
            if (!IsCurrent())
            {
                return false;
            }

            var ack = reply.Success && !string.IsNullOrEmpty(reply.DataJson) ? JsonSerializer.Deserialize(reply.DataJson, WingetServiceJsonContext.Default.WingetServiceAck) : null;
            if (ack?.Accepted != true)
            {
                localError = ack?.Error ?? reply.Error?.Message ?? L("RequestFailed");
                return false;
            }

            do
            {
                await PollAsync();
                if (!IsCurrent())
                {
                    return false;
                }

                if (!waitForCompletion)
                {
                    return true;
                }

                if (status.LastResult is { } result && result.OperationId == ack.OperationId)
                {
                    return result.Succeeded;
                }

                await Task.Delay(150, token);
            }
            while (IsCurrent());
        }
        catch (OperationCanceledException)
        {
            if (!disposed && ReferenceEquals(pageLifetime, lifetime) && !lifetime.IsCancellationRequested)
            {
                localError = L("PrepareTimeout");
            }
        }
        catch (Exception exception)
        {
            if (!disposed && ReferenceEquals(pageLifetime, lifetime))
            {
                localError = L("RequestFailed") + " " + exception.Message;
            }
        }
        finally
        {
            if (!control)
            {
                requestPending = false;
                pendingCommand = null;
            }
            else
            {
                cancellationPending = false;
            }

            if (!disposed)
            {
                NotifyState();
            }
        }

        return false;
    }

    private static int NaturalCompare(string left, string right)
    {
        int a = 0;
        int b = 0;
        while (a < left.Length && b < right.Length)
        {
            if (char.IsAsciiDigit(left[a]) && char.IsAsciiDigit(right[b]))
            {
                int aEnd = a;
                int bEnd = b;
                while (aEnd < left.Length && char.IsAsciiDigit(left[aEnd]))
                {
                    aEnd++;
                }

                while (bEnd < right.Length && char.IsAsciiDigit(right[bEnd]))
                {
                    bEnd++;
                }

                string an = left[a..aEnd].TrimStart('0');
                string bn = right[b..bEnd].TrimStart('0');
                int number = an.Length.CompareTo(bn.Length);
                if (number == 0)
                {
                    number = StringComparer.Ordinal.Compare(an, bn);
                }

                if (number != 0)
                {
                    return number;
                }

                a = aEnd;
                b = bEnd;
                continue;
            }

            int character = char.ToUpperInvariant(left[a]).CompareTo(char.ToUpperInvariant(right[b]));
            if (character != 0)
            {
                return character;
            }

            a++;
            b++;
        }

        return (left.Length - a).CompareTo(right.Length - b);
    }

    private void DisposeWorkspace()
    {
        foreach (var row in observedRows)
        {
            row.PropertyChanged -= OnWorkspaceRowChanged;
        }

        foreach (var filter in SourceFilters)
        {
            filter.PropertyChanged -= OnSourceFilterChanged;
        }

        observedRows.Clear();
    }

    private void EnsureInstalledLoaded()
    {
        if (ActiveTabIndex == (int)WingetWorkspace.Installed && !installedRequested && pageLifetime != null && CanCheck)
        {
            installedRequested = true;
            _ = RequestAsync("list-installed");
        }
    }
}
