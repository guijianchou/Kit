using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using Kit.WingetLib;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;

internal static class WingetPageSmoke
{
    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string Output = Environment.GetEnvironmentVariable("KIT_WINGET_PAGE_TEST_OUTPUT");
    private static readonly string Report = Path.Combine(Output, "winget-pages-smoke.log");
    private static readonly WingetAvailability Available = new(true, "1.29.fixture", null, null);
    private static readonly DateTimeOffset CheckedAt = new(2026, 11, 7, 10, 30, 0, TimeSpan.Zero);
    private static readonly WingetPackageUpdate LongPackage = new(
        "Fixture.VeryLongPackageIdentifier.With.Many.Segments.AndAnUnbrokenSuffix_abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789",
        "Microsoft Edge WebView2 Runtime / 超长中文软件名称用于验证真实窗口中的内容边界与文本省略",
        "156.0.4314.58-preview.abcdefghijklmnopqrstuvwxyz0123456789",
        "157.0.4411.42-release.abcdefghijklmnopqrstuvwxyz0123456789", "winget",
        "Release notes / 更新说明\nThis is a long synthetic description for package details, including Chinese text. 不应扩大表格的布局宽度。\n<script>Displayed as plain text</script>",
        "https://example.invalid/releases", WingetInstallEligibility.RequiresElevation, "Requires elevation", "winget upgrade --id Fixture.Example --source winget --exact")
        { CanUninstall = true, IsUpdateAvailable = true };
    private static readonly WingetPackageUpdate NormalPackage = LongPackage with { Id = "Fixture.Terminal", Name = "Windows Terminal", InstalledVersion = "1.24.12741.0", AvailableVersion = "1.25.2733.0" };
    private static readonly WingetPackageUpdate UnknownPackage = LongPackage with { Id = "Fixture.Unknown", Name = "Unknown version fixture", InstalledVersion = "Unknown", AvailableVersion = "2.0" };
    private static readonly WingetPackageUpdate SearchPackage = NormalPackage with { Id = "Fixture.NewSoftware", Name = "New software from the catalog / 软件目录中的新应用", InstalledVersion = string.Empty, IsUpdateAvailable = false };
    private static readonly WingetPackageUpdate LocalPackage = NormalPackage with { Id = "ARP\\Machine\\X86\\Fixture.Uninstaller", Name = "Local desktop application / 本地软件", Source = "local", AvailableVersion = string.Empty, IsUpdateAvailable = false };
    private static readonly WingetPackageUpdate StorePackage = NormalPackage with { Id = "MSIX\\Fixture.Package_long_package_family_identifier_8wekyb3d8bbwe", Name = "MSIX package / 已安装应用包", Source = "msix", AvailableVersion = string.Empty, IsUpdateAvailable = false };

    [STAThread]
    private static void Main()
    {
        File.WriteAllText(Report, "Real complete Settings shell; synthetic packages only. No install, update, uninstall, import, notification or clipboard execution. Enabled layout fixtures suspend all page polling.\n");
        try
        {
            string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "settings.json");
            using (var settings = JsonDocument.Parse(File.ReadAllText(settingsPath)))
                Require(!settings.RootElement.GetProperty("enabled").GetProperty("Winget").GetBoolean(), "Wrapper must disable Winget before startup.");
            Require(!new Kit.AiHub.Storage.AiHubSettingsStore().Load().IsEnabled, "Wrapper must disable shared AI service.");
            EnsureNoWorker();
            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
            WindowsAppRuntime_EnsureIsLoaded();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                var app = new App { StartupPage = typeof(GeneralPage) };
                DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
                {
                    try { await Task.Delay(350); await Run(); Log("PASS"); Environment.Exit(0); }
                    catch (Exception ex) { Log(ex.ToString()); Environment.Exit(1); }
                });
            });
        }
        catch (Exception ex) { Log(ex.ToString()); Environment.Exit(2); }
    }

    private static async Task Run()
    {
        var general = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default);
        general.StopWatching();
        general.SettingsConfig = new GeneralSettings();
        general.SettingsConfig.Enabled.Winget = false;
        general.SettingsConfig.Enabled.AiHub = false;
        general.NotifySettingsChanged();
        var module = SettingsRepository<WingetSettings>.GetInstance(SettingsUtils.Default);
        module.StopWatching();
        module.SettingsConfig = new WingetSettings();
        int ipcMessages = 0;
        ShellPage.SetDefaultSndMessageCallback(_ => ipcMessages++);
        var window = App.GetSettingsWindow();
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1510, 985));
        await Navigate(typeof(WingetPage));
        var page = (WingetPage)NavigationService.Frame.Content;
        var vm = page.ViewModel;
        Require(ReferenceEquals(page.DataContext, vm), "Winget must bind its own VM.");
        Require(!vm.IsEnabled && !vm.CanPrepareSelection && !vm.CanReload, "Disabled module must block package operations.");
        Require(!((AppBarButton)page.FindName("PrimaryActionButton")).IsEnabled, "Disabled primary action must be disabled in the real control.");
        await vm.RequestAsync("check");
        Require(ipcMessages == 0, "Disabled actions must not enable the module or send Runner settings.");
        Apply(vm, Snapshot());
        await Settle();
        AssertWorkspaceBounds(page, "disabled-startup");
        await CaptureShell("winget-disabled-full-shell.png");
        Log("Disabled guards and full-shell startup PASS");

        // Keep the page's activity source consistent with the suspended VM.
        // SetPageActive(false) alone is not a suspension: a delayed AppWindow
        // Changed or Loaded callback correctly reactivates a current visible page.
        // Preserve those handlers and the real cached navigation checks below.
        var fixtureActivity = new SyntheticPageActivityScope(page);
        try
        {
            general.SettingsConfig.Enabled.Winget = true;
            Apply(vm, Snapshot());
            Require(GetField<CancellationTokenSource>(vm, "pageLifetime") == null, "Enabled synthetic fixture must remain inactive.");
            await VerifySyntheticLifecycleIsolation(page);
            await VerifyWorkspaceState(page);
            await VerifyLayouts(page, general);
            await VerifyExpandedSettings(page, general);
            await VerifySemanticPresentation(page);
            await VerifyRepeatedStatusStability(page);
            await VerifyIgnoredStateLabel(page);
            await VerifyLatestOperationOutput(page);
            await VerifyDetailsDialog(page);
            await VerifyCancelledConfirmation(page);
            Require(ipcMessages == 0, "Synthetic layout and cancelled dialogs must not send settings.");
            EnsureNoWorker();
        }
        finally
        {
            ((ContentDialog)typeof(WingetPage).GetField("activeDialog", PrivateInstance).GetValue(page))?.Hide();
            general.SettingsConfig.Enabled.Winget = false;
            vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
            vm.ViewModeIndex = 0;
            vm.FilterText = string.Empty;
            vm.RefreshEnabledState();
            vm.SetPageActive(false);
            fixtureActivity.Dispose();
        }

        var original = page;
        for (int visit = 0; visit < 2; visit++)
        {
            await Navigate(typeof(GeneralPage));
            Require(GetField<CancellationTokenSource>(vm, "pageLifetime") == null && !GetField<DispatcherTimer>(vm, "timer").IsEnabled, "Leaving Winget must stop polling.");
            await Navigate(typeof(WingetPage));
            Require(ReferenceEquals(original, NavigationService.Frame.Content), "Winget navigation must reuse its page.");
            InvokeWindowChanged(page);
            Require((bool)typeof(WingetPage).GetField("currentPage", PrivateInstance).GetValue(page) &&
                GetField<CancellationTokenSource>(vm, "pageLifetime") != null && GetField<DispatcherTimer>(vm, "timer").IsEnabled && !GetField<bool>(vm, "polling"),
                "Real cached navigation must restore activity callbacks while the disabled module prevents service requests.");
        }
        vm.SetPageActive(false);
        Require(ipcMessages == 0, "Read-only navigation must not persist module settings.");
        EnsureNoWorker();
        Log("Cached navigation and inactive polling PASS");
    }

    private sealed class SyntheticPageActivityScope : IDisposable
    {
        private readonly WingetPage page;
        private readonly FieldInfo currentPageField;
        private readonly bool wasCurrentPage;

        public SyntheticPageActivityScope(WingetPage page)
        {
            this.page = page;
            currentPageField = typeof(WingetPage).GetField("currentPage", PrivateInstance)
                ?? throw new InvalidOperationException("Winget lifecycle activity field was not found.");
            wasCurrentPage = (bool)currentPageField.GetValue(page);
            Require(wasCurrentPage && page.IsLoaded && !page.ViewModel.IsEnabled,
                "Synthetic activity scope must enter from a loaded current page with Winget disabled.");

            // Reproduce the original race synchronously while IsEnabled=false
            // guarantees PollCoreAsync cannot connect to the real service.
            page.ViewModel.SetPageActive(false);
            InvokeWindowChanged(page);
            Require(GetField<CancellationTokenSource>(page.ViewModel, "pageLifetime") != null &&
                GetField<DispatcherTimer>(page.ViewModel, "timer").IsEnabled && !GetField<bool>(page.ViewModel, "polling"),
                "A current visible page must reactivate its lifetime on a window event, without polling a disabled module.");

            currentPageField.SetValue(page, false);
            page.ViewModel.SetPageActive(false);
            Log("Reproduced delayed-window activity reset safely with Winget disabled; enabled fixtures now suspend the page activity source PASS");
        }

        public void Dispose()
        {
            Require(!page.ViewModel.IsEnabled, "Disable the synthetic module before restoring real page activity.");
            page.ViewModel.SetPageActive(false);
            currentPageField.SetValue(page, wasCurrentPage);
        }
    }

    private static void InvokeWindowChanged(WingetPage page)
        => typeof(WingetPage).GetMethod("OnWindowChanged", PrivateInstance).Invoke(page, new object[] { App.GetSettingsWindow().AppWindow, null });

    private static async Task VerifySyntheticLifecycleIsolation(WingetPage page)
    {
        var vm = page.ViewModel;
        Require(vm.IsEnabled && !(bool)typeof(WingetPage).GetField("currentPage", PrivateInstance).GetValue(page), "Synthetic layout fixtures must be enabled but lifecycle-inactive.");
        for (int repeat = 0; repeat < 3; repeat++)
        {
            typeof(WingetPage).GetMethod("OnLoaded", PrivateInstance).Invoke(page, new object[] { page, null });
            InvokeWindowChanged(page);
            await Settle();
            Require(GetField<CancellationTokenSource>(vm, "pageLifetime") == null && !GetField<DispatcherTimer>(vm, "timer").IsEnabled && !GetField<bool>(vm, "polling"),
                "Loaded and delayed window callbacks must retain synthetic isolation without starting a service request.");
        }
        EnsureNoWorker();
        Log("Enabled synthetic fixtures remain isolated across real Loaded/window callback replays; lifecycle handlers remain attached PASS");
    }

    private static async Task VerifyWorkspaceState(WingetPage page)
    {
        var vm = page.ViewModel;
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        Apply(vm, Snapshot());
        Require(vm.DisplayPackages.Count >= 2, "Updates workspace must project synthetic update rows.");
        vm.ActiveSelection = vm.DisplayPackages.First();
        Require(vm.SelectedCount == 0, "Focusing details must not implicitly select a batch operation.");
        vm.DisplayPackages.First().IsSelected = true;
        Require(vm.SelectedCount == 1, "Checkbox selection must update batch count.");
        var retainedRow = vm.DisplayPackages.Single(item => item.Id == NormalPackage.Id);
        var hiddenRow = vm.DisplayPackages.Single(item => item.Id == LongPackage.Id);
        retainedRow.IsSelected = true;
        hiddenRow.IsSelected = true;
        vm.FilterText = "Fixture.Terminal";
        await Settle();
        Require(vm.DisplayPackages.Count == 1 && vm.DisplayPackages[0].Id == NormalPackage.Id, "Local filter must match package ID.");
        Require(ReferenceEquals(vm.DisplayPackages[0], retainedRow) && retainedRow.IsSelected && vm.SelectedCount == 1, "Filtering must preserve the checked row that remains visible.");
        Require(!hiddenRow.IsSelected, "Filtering out a checked row must clear its batch selection.");
        vm.FilterText = string.Empty;
        Require(retainedRow.IsSelected && !hiddenRow.IsSelected, "Clearing a filter must not restore hidden batch selections.");
        Require(vm.IsAllVisibleSelected == null, "Partial selection must project an indeterminate header checkbox.");
        var selectAll = (CheckBox)page.FindName("SelectAllPackagesCheckBox");
        var selectAllHandler = typeof(WingetPage).GetMethod("SelectAllPackages_Click", PrivateInstance);
        selectAllHandler.Invoke(page, new object[] { selectAll, new RoutedEventArgs() });
        await Settle();
        Require(vm.SelectedCount == vm.DisplayPackages.Count && selectAll.IsChecked == true, "Clicking mixed select-all must select every visible row and check the header.");
        selectAllHandler.Invoke(page, new object[] { selectAll, new RoutedEventArgs() });
        await Settle();
        Require(vm.SelectedCount == 0 && selectAll.IsChecked == false, "Clicking checked select-all must clear the visible selection.");
        vm.ActiveTabIndex = (int)WingetWorkspace.Discover;
        Apply(vm, Snapshot());
        Require(vm.DisplayPackages.Any(item => item.Id == SearchPackage.Id), "Discovery must use search results.");
        vm.FilterText = "no matching discovery package";
        await Settle();
        Require(vm.HasSearchResults && !vm.HasRows && ((FrameworkElement)page.FindName("DiscoverStartPanel")).Visibility == Visibility.Collapsed &&
            ((FrameworkElement)page.FindName("DiscoverSearchToolbar")).Visibility == Visibility.Visible && ((FrameworkElement)page.FindName("PackageEmptyText")).Visibility == Visibility.Visible,
            "Filtering existing discovery results to zero must preserve its search toolbar and show the filtered-empty state.");
        vm.FilterText = string.Empty;
        vm.ExactMatch = true;
        Apply(vm, Snapshot() with { SearchQuery = "go", SearchResultsTruncated = true });
        Require(vm.DisplayPackages.Count == vm.SearchResults.Count,
            "Exact local filtering with an empty filter must retain catalog matches even when the query matched a moniker or the results are truncated.");
        vm.FilterText = SearchPackage.Id;
        Require(vm.DisplayPackages.Count == 1 && vm.DisplayPackages[0].Id == SearchPackage.Id,
            "Exact matching must apply to the local name/ID filter independently of the catalog query.");
        vm.FilterText = "Fixture";
        Require(vm.DisplayPackages.Count == 0, "An exact local name/ID filter must not accept partial matches.");
        vm.FilterText = string.Empty;
        vm.ExactMatch = false;
        var lightweight = SearchPackage with { InstalledVersion = string.Empty, AvailableVersion = string.Empty, ManualUpgradeCommand = string.Empty,
            IsMetadataResolved = false, IsUpdateAvailable = false, CanUninstall = false, InstallEligibility = WingetInstallEligibility.ManualOnly };
        Apply(vm, Snapshot() with { SearchResults = new[] { lightweight }, SelectedDetails = null });
        var discoveryRow = vm.DisplayPackages.Single();
        vm.ActiveSelection = discoveryRow;
        discoveryRow.IsSelected = true;
        await Settle();
        Require(!discoveryRow.IsMetadataResolved && discoveryRow.InstalledVersion == WingetViewModel.L("NotCheckedShort") && discoveryRow.AvailableVersion == WingetViewModel.L("NotCheckedShort") &&
            discoveryRow.InstallationLabel == WingetViewModel.L("InstallationUnknown") && discoveryRow.StateLabel == WingetViewModel.L("DetailsNotLoaded"),
            "Lightweight discovery must show unknown versions and installation state, not available/not-installed.");
        Require(vm.CanPrepareSelection && !vm.CanInstall && vm.PrimaryActionLabel == WingetViewModel.L("ReviewInstallSelection") && vm.ManualCommand.Length == 0,
            "Unknown discovery metadata permits reviewed preparation only; direct installation and manual commands must stay unavailable.");
        var discoveryList = (ListView)page.FindName("PackagesList");
        var discoveryContainer = (ListViewItem)discoveryList.ContainerFromItem(discoveryRow);
        var discoveryCheckBox = Descendants(discoveryContainer).OfType<CheckBox>().Single();
        Require(discoveryCheckBox.Focus(FocusState.Keyboard), "Discovery fixture must focus its actual checkbox.");
        int blankRowNotifications = 0;
        System.ComponentModel.PropertyChangedEventHandler rowChanged = (_, args) => { if (string.IsNullOrEmpty(args.PropertyName)) blankRowNotifications++; };
        discoveryRow.PropertyChanged += rowChanged;
        var resolved = SearchPackage with { IsMetadataResolved = true, CanUninstall = false };
        Apply(vm, Snapshot() with { SearchResults = new[] { resolved }, SelectedDetails = Details(resolved) });
        await Settle();
        discoveryRow.PropertyChanged -= rowChanged;
        Require(ReferenceEquals(discoveryRow, vm.DisplayPackages.Single()) && ReferenceEquals(discoveryRow, vm.ActiveSelection) && discoveryRow.IsSelected &&
            ReferenceEquals(discoveryContainer, discoveryList.ContainerFromItem(discoveryRow)) && ReferenceEquals(discoveryCheckBox, Descendants(discoveryContainer).OfType<CheckBox>().Single()) && discoveryCheckBox.IsChecked == true,
            "Resolving discovery metadata must preserve its row, focused selection, checked state and realized controls.");
        Require(discoveryCheckBox.FocusState != FocusState.Unfocused && blankRowNotifications == 0 && vm.SelectedDetails?.Package == resolved && discoveryRow.IsMetadataResolved,
            "Metadata resolution must update named fields in place without blank row invalidation or focus loss.");
        vm.SearchQuery = "another package";
        var retainedSearch = Snapshot() with { SearchResults = new[] { resolved }, SelectedDetails = Details(resolved) };
        Apply(vm, retainedSearch with { IsBusy = true, OperationKind = "search", OperationId = "fixture-search", Phase = "Searching" });
        await Settle();
        Require(vm.IsSearching && !vm.CanSearch && vm.CanCancelOperation && ((Button)page.FindName("OperationCancelButton")).IsEnabled &&
            ((TextBlock)page.FindName("SearchStatusText")).Text.Contains(WingetViewModel.L("SearchKeepsResults"), StringComparison.Ordinal) && ReferenceEquals(discoveryRow, vm.DisplayPackages.Single()),
            "Searching must show progress, retain existing results and expose a usable cancellation action.");
        foreach (string outcome in new[] { "Cancelled", "Timed out" })
        {
            Apply(vm, retainedSearch with { Phase = outcome, LastResult = new WingetServiceOperationResult("fixture-search-" + outcome, "search", false, outcome) });
            await Settle();
            Require(!vm.IsSearching && vm.CanSearch && discoveryRow.IsSelected && ReferenceEquals(discoveryRow, vm.DisplayPackages.Single()) &&
                vm.SearchStatusText.Contains(WingetViewModel.L("SearchRetryPrevious"), StringComparison.Ordinal), "A cancelled/failed search must retain prior rows and permit retry.");
        }
        var emptySearch = retainedSearch with { SearchQuery = "another package", SearchResults = Array.Empty<WingetPackageUpdate>(), SelectedDetails = null,
            LastResult = new WingetServiceOperationResult("fixture-search-empty", "search", true) { ElapsedMilliseconds = 1234 } };
        Apply(vm, emptySearch);
        await Settle();
        Require(vm.CanSearch && !vm.HasRows && ((FrameworkElement)page.FindName("DiscoverStartPanel")).Visibility == Visibility.Visible &&
            ((TextBlock)page.FindName("SearchScopeText")).Text == WingetViewModel.L("SearchSourceScope"), "A successful empty search must keep the search entry usable and explain the catalog scope.");
        string searchElapsed = string.Format(CultureInfo.CurrentCulture, WingetViewModel.L("SearchElapsed"), 1.234);
        Require(vm.SearchResultText.EndsWith(searchElapsed, StringComparison.Ordinal) && ((TextBlock)page.FindName("SearchStatusText")).Text.Contains(searchElapsed, StringComparison.Ordinal),
            "The visible search result summary must format the successful search's 1234 ms using the current culture.");
        foreach (string kind in new[] { "details", "prepare" })
        {
            Apply(vm, emptySearch with { LastResult = new WingetServiceOperationResult("fixture-timing-" + kind, kind, true) { ElapsedMilliseconds = 98765 } });
            await Settle();
            Require(vm.SearchResultText.EndsWith(searchElapsed, StringComparison.Ordinal) && ((TextBlock)page.FindName("SearchStatusText")).Text.Contains(searchElapsed, StringComparison.Ordinal) &&
                !vm.SearchResultText.Contains(string.Format(CultureInfo.CurrentCulture, WingetViewModel.L("SearchElapsed"), 98.765), StringComparison.Ordinal),
                "Details/prepare completion must preserve the current search timing instead of reusing another operation's duration.");
        }
        Apply(vm, emptySearch with { LastResult = new WingetServiceOperationResult("fixture-search-no-timing", "search", true) });
        Require(!vm.SearchResultText.Contains(searchElapsed, StringComparison.Ordinal), "A newer successful search without timing must clear the previous duration.");
        Apply(vm, Snapshot() with { SearchResultsTruncated = true });
        await Settle();
        Require(vm.SearchResultsLimited && Descendants(page).OfType<TextBlock>().Any(block => block.Text == WingetViewModel.L("SearchRefine") && block.Visibility == Visibility.Visible),
            "Truncated catalog results must visibly ask for a narrower query.");
        Apply(vm, Snapshot() with { SearchResults = new[] { resolved with { InstalledVersion = "1.0", CanUninstall = true } } });
        vm.DisplayPackages.Single().IsSelected = true;
        Require(!vm.CanPrepareSelection, "A discovery row resolved as installed must block another install preparation.");
        Apply(vm, Snapshot());
        vm.ActiveTabIndex = (int)WingetWorkspace.Installed;
        Apply(vm, Snapshot());
        Require(vm.DisplayPackages.Any(item => item.Source == "local") && vm.DisplayPackages.Any(item => item.Source == "msix"), "Installed workspace must include local and MSIX records.");
        Require(vm.SourceFilters.Any(item => item.Key == "local") && vm.SourceFilters.Any(item => item.Key == "msix"), "Source filters must represent actual installed sources.");
        var local = vm.SourceFilters.First(item => item.Key == "local");
        local.IsSelected = false;
        Require(vm.DisplayPackages.All(item => item.Source != "local"), "Source checkbox must filter its actual source.");
        local.IsSelected = true;
        vm.SortIndex = 1;
        vm.SortDescending = false;
        Require(vm.DisplayPackages.Select(item => item.Id).SequenceEqual(vm.DisplayPackages.Select(item => item.Id).OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)), "ID sorting must update the real collection.");
        vm.SortIndex = 0;
        vm.SearchQuery = "x";
        Require(!vm.CanSearch, "A one-character query must not search.");
        vm.ActiveTabIndex = (int)WingetWorkspace.Discover;
        vm.SearchQuery = "fixture";
        Require(vm.CanSearch, "A valid discovery query should be enabled.");
        vm.SearchQuery = new string('x', 201);
        Require(!vm.CanSearch, "Overlong query must not search.");
        vm.SearchQuery = "fixture";
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        Apply(vm, Snapshot() with { RecoveryRequired = true });
        await Settle();
        Require(!vm.CanPrepareSelection, "Interrupted installation recovery must block package actions.");
        Require(Descendants(page).OfType<InfoBar>().Any(bar => bar.IsOpen && bar.Visibility == Visibility.Visible && bar.Message == WingetViewModel.L("RecoveryHint")), "Recovery must remain visible in the actual workspace InfoBar.");
        var oversized = Enumerable.Range(0, 51).Select(index => NormalPackage with { Id = "Fixture.Batch" + index, Name = "Batch fixture " + index }).ToArray();
        Apply(vm, Snapshot() with { Packages = oversized });
        vm.SelectAllVisible(true);
        await Settle();
        Require(vm.SelectedCount == 51 && !vm.CanPrepareSelection, "An oversized batch must stay disabled.");
        Require(Descendants(page).OfType<InfoBar>().Any(bar => bar.IsOpen && bar.Visibility == Visibility.Visible && bar.Message == WingetViewModel.L("BatchLimitHint")), "An oversized batch must explain its limit in a visible InfoBar.");
        Apply(vm, Snapshot());
        VerifyInstalledVersions(vm);
        VerifyExports(vm);
        VerifyWorkspaceMemory(vm);
        VerifyPreparedSelection(vm);
        Require(GetField<CancellationTokenSource>(vm, "pageLifetime") == null && !GetField<DispatcherTimer>(vm, "timer").IsEnabled, "Workspace regression fixtures must keep polling inactive.");
        EnsureNoWorker();
        Log("Three workspaces, independent focus/selection, source filters, ID filter, sorting and search/recovery guards PASS");
    }

    private static void VerifyInstalledVersions(WingetViewModel vm)
    {
        vm.ActiveTabIndex = (int)WingetWorkspace.Installed;
        vm.FilterText = string.Empty;
        vm.SortIndex = 2;
        vm.SortDescending = false;
        var version2 = NormalPackage with { Id = "Fixture.SideBySide", InstalledVersion = "2.0.0", AvailableVersion = string.Empty };
        var version10 = version2 with { InstalledVersion = "10.0.0" };
        Apply(vm, Snapshot() with { InstalledPackages = new[] { version10, version2 } });
        Require(vm.DisplayPackages.Count == 2 && vm.DisplayPackages.All(row => row.Id == version2.Id), "Two installed versions sharing one package ID must remain distinct rows.");
        Require(vm.DisplayPackages.Select(row => row.InstalledVersion).SequenceEqual(new[] { "2.0.0", "10.0.0" }), "Version sorting must order 2 before 10 rather than compare text lexically.");
        var original2 = vm.DisplayPackages[0];
        var original10 = vm.DisplayPackages[1];
        Apply(vm, Snapshot() with { InstalledPackages = new[] { version2, version10 } });
        Require(ReferenceEquals(original2, vm.DisplayPackages[0]) && ReferenceEquals(original10, vm.DisplayPackages[1]), "Refreshing side-by-side installed versions must preserve each row identity.");
        vm.SortDescending = true;
        Require(vm.DisplayPackages.Select(row => row.InstalledVersion).SequenceEqual(new[] { "10.0.0", "2.0.0" }), "Descending natural version sorting must reverse the complete numeric order.");
        vm.SortDescending = false;
        vm.SortIndex = 0;
        Apply(vm, Snapshot());
        Log("Natural version ordering and same-ID side-by-side installed identity PASS");
    }

    private static void VerifyExports(WingetViewModel vm)
    {
        vm.ActiveTabIndex = (int)WingetWorkspace.Installed;
        var quoted = NormalPackage with { Id = "Fixture.Quoted", Name = "App, \"quoted\"\r\nsecond line", InstalledVersion = "2.0", AvailableVersion = "10.0" };
        string[] dangerous = { "=SUM(1,2)", "+1", "-1", "@SUM(1,2)", "\tformula", "\rformula", "\nformula" };
        var csvRows = dangerous.Select((name, index) => quoted with { Id = "Fixture.Formula" + index, Name = name }).Append(quoted).ToArray();
        Apply(vm, Snapshot() with { InstalledPackages = csvRows });
        string csv = vm.CreateCsv();
        Require(csv.StartsWith("Name,Id,InstalledVersion,AvailableVersion,Source\r\n", StringComparison.Ordinal), "CSV must declare the exported package columns.");
        Require(csv.Contains("\"App, \"\"quoted\"\"\r\nsecond line\",\"Fixture.Quoted\"", StringComparison.Ordinal), "CSV must quote commas, double embedded quotes and preserve embedded newlines inside one cell.");
        foreach (string value in dangerous)
            Require(csv.Contains("\"'" + value + "\",", StringComparison.Ordinal), "CSV must neutralize a formula-capable leading character: " + ((int)value[0]).ToString());
        vm.FilterText = "Fixture.Quoted";
        string filteredCsv = vm.CreateCsv();
        Require(filteredCsv.Contains("Fixture.Quoted", StringComparison.Ordinal) && !filteredCsv.Contains("Fixture.Formula", StringComparison.Ordinal), "CSV must export the current filtered list only.");
        vm.FilterText = string.Empty;
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        Apply(vm, Snapshot());
        vm.SelectAllVisible(true);
        string[] expectedIds = vm.DisplayPackages.Where(row => row.IsSelected).Select(row => row.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        using (var bundle = JsonDocument.Parse(vm.CreateBundleJson()))
        {
            var root = bundle.RootElement;
            Require(root.GetProperty("$schema").GetString() == "https://aka.ms/winget-packages.schema.2.0.json", "Bundle must use the standard WinGet import schema.");
            var sources = root.GetProperty("Sources");
            Require(sources.GetArrayLength() == 1, "Community bundle must contain one exact source.");
            var details = sources[0].GetProperty("SourceDetails");
            Require(details.GetProperty("Name").GetString() == "winget" && details.GetProperty("Identifier").GetString() == "Microsoft.Winget.Source_8wekyb3d8bbwe" &&
                details.GetProperty("Type").GetString() == "Microsoft.PreIndexed.Package" && details.GetProperty("Argument").GetString() == "https://cdn.winget.microsoft.com/cache", "Bundle SourceDetails must identify the actual community catalog.");
            string[] actualIds = sources[0].GetProperty("Packages").EnumerateArray().Select(item => item.GetProperty("PackageIdentifier").GetString()).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Require(actualIds.SequenceEqual(expectedIds), "Bundle must include exactly the selected package identities.");
        }
        vm.ActiveTabIndex = (int)WingetWorkspace.Installed;
        Apply(vm, Snapshot());
        vm.SelectAllVisible(false);
        vm.DisplayPackages.Single(row => row.Source == "local").IsSelected = true;
        bool refusedLocal = false;
        try { vm.CreateBundleJson(); }
        catch (InvalidOperationException) { refusedLocal = true; }
        Require(refusedLocal, "A local uninstall record must never be exported as an installable WinGet bundle package.");
        vm.SelectAllVisible(false);
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        Apply(vm, Snapshot());
        vm.SelectAllVisible(false);
        Log("CSV quoting, all formula prefixes, filtered export, standard bundle source/selection and local-source refusal PASS");
    }

    private static void VerifyPreparedSelection(WingetViewModel vm)
    {
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        vm.FilterText = string.Empty;
        vm.IncludeUnknown = false;
        vm.SilentInstall = false;
        vm.InstallScopeIndex = 0;
        vm.InstallArchitectureIndex = 0;
        Apply(vm, Snapshot());
        vm.SelectAllVisible(true);
        var selected = vm.DisplayPackages.Where(row => row.IsSelected).ToArray();
        Require(selected.Length == 2, "Prepared-selection fixture requires two checked known-version packages.");
        var prepared = new WingetPreparedOperation("fixture-selection", "upgrade", selected.Select(row => Details(row.Package)).ToArray(), new WingetPackageOptions(), DateTimeOffset.UtcNow);
        Require(vm.PreparedSelectionMatches(prepared), "The exact two-package selection and options must match its prepared review.");
        vm.SortDescending = !vm.SortDescending;
        Require(vm.PreparedSelectionMatches(prepared), "Reordering the same complete identities must preserve the prepared review.");
        vm.SortDescending = !vm.SortDescending;
        Require(!vm.PreparedSelectionMatches(prepared with { Packages = prepared.Packages.Select(details => details with { Package = details.Package with { IsMetadataResolved = false } }).ToArray() }),
            "A prepared operation with unresolved metadata must never authorize installation.");
        selected[1].IsSelected = false;
        Require(!vm.PreparedSelectionMatches(prepared), "Removing one of two checked packages must invalidate its prepared review.");
        selected[1].IsSelected = true;
        Require(vm.PreparedSelectionMatches(prepared), "Restoring the exact selection must match before any option change.");
        vm.SilentInstall = true;
        Require(!vm.PreparedSelectionMatches(prepared), "Changing silent mode must invalidate prepared review.");
        vm.SilentInstall = false;
        vm.InstallScopeIndex = 1;
        Require(!vm.PreparedSelectionMatches(prepared), "Changing installation scope must invalidate prepared review.");
        vm.InstallScopeIndex = 0;
        vm.InstallArchitectureIndex = 1;
        Require(!vm.PreparedSelectionMatches(prepared), "Changing architecture must invalidate prepared review.");
        vm.InstallArchitectureIndex = 0;
        vm.IncludeUnknown = true;
        Require(!vm.PreparedSelectionMatches(prepared), "Changing unknown-version handling must invalidate prepared review.");
        vm.IncludeUnknown = false;
        Require(vm.PreparedSelectionMatches(prepared), "Restoring all options must recover the unchanged review identity.");
        Require(!vm.PreparedSelectionMatches(prepared with { CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-11) }), "Expired prepared operations must not authorize a submission.");
        Apply(vm, Snapshot() with { Packages = new[] { LongPackage with { AvailableVersion = "999.0.0" }, NormalPackage, UnknownPackage } });
        Require(!vm.PreparedSelectionMatches(prepared), "A new available version must invalidate the old batch review.");
        Apply(vm, Snapshot());
        vm.SelectAllVisible(true);
        vm.ActiveTabIndex = (int)WingetWorkspace.Installed;
        Require(!vm.PreparedSelectionMatches(prepared), "Switching to uninstall workspace must invalidate an upgrade review.");
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        vm.SelectAllVisible(false);
        Apply(vm, Snapshot());
        Log("Two-package prepared review rejects changed selection, options, versions, expiry and workspace PASS");
    }

    private static void VerifyWorkspaceMemory(WingetViewModel vm)
    {
        Require((int)WingetWorkspace.Updates == 0 && (int)WingetWorkspace.Installed == 1 && (int)WingetWorkspace.Discover == 2, "Workspace order must be Updates, Installed, then Discover.");
        Apply(vm, Snapshot());
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        vm.FilterText = "Fixture.Terminal";
        vm.SortIndex = 2;
        vm.ViewModeIndex = 1;
        var updateFocus = vm.DisplayPackages.Single();
        vm.ActiveSelection = updateFocus;
        vm.ActiveTabIndex = (int)WingetWorkspace.Installed;
        vm.FilterText = LocalPackage.Id;
        vm.ExactMatch = true;
        vm.SortIndex = 1;
        vm.SortDescending = true;
        vm.ViewModeIndex = 2;
        var installedFocus = vm.DisplayPackages.Single();
        vm.ActiveSelection = installedFocus;
        vm.ActiveTabIndex = (int)WingetWorkspace.Discover;
        vm.FilterText = SearchPackage.Id;
        vm.SortIndex = 0;
        vm.ViewModeIndex = 0;
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        Require(vm.FilterText == "Fixture.Terminal" && vm.SortIndex == 2 && vm.ViewModeIndex == 1 && !vm.ExactMatch && ReferenceEquals(updateFocus, vm.ActiveSelection), "Returning to Updates must restore its own filter, sort, view and focused package.");
        vm.ActiveTabIndex = (int)WingetWorkspace.Installed;
        Require(vm.FilterText == LocalPackage.Id && vm.ExactMatch && vm.SortIndex == 1 && vm.SortDescending && vm.ViewModeIndex == 2 && ReferenceEquals(installedFocus, vm.ActiveSelection), "Returning to Installed must restore its own search options and focused package.");
        vm.ActiveTabIndex = (int)WingetWorkspace.Discover;
        Require(vm.FilterText == SearchPackage.Id && !vm.ExactMatch && vm.SortIndex == 0 && vm.ViewModeIndex == 0, "Discovery must keep independent filter/view preferences.");
        foreach (var workspace in new[] { WingetWorkspace.Updates, WingetWorkspace.Installed, WingetWorkspace.Discover })
        {
            vm.ActiveTabIndex = (int)workspace;
            vm.FilterText = string.Empty;
            vm.ExactMatch = false;
            vm.SortIndex = 0;
            vm.SortDescending = false;
            vm.ViewModeIndex = 0;
        }
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        Log("Updates/Installed/Discover order and independent filter, sort, view and focused selection memory PASS");
    }

    private static async Task VerifyLayouts(WingetPage page, SettingsRepository<GeneralSettings> general)
    {
        var window = App.GetSettingsWindow();
        var root = (FrameworkElement)window.Content;
        var vm = page.ViewModel;
        var originalTheme = root.RequestedTheme;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                general.SettingsConfig.Enabled.Winget = false;
                vm.SetPageActive(false);
                root.RequestedTheme = theme;
                foreach (var size in new[] { (1670, 985), (1510, 985), (1280, 900), (760, 900) })
                {
                    general.SettingsConfig.Enabled.Winget = false;
                    vm.SetPageActive(false);
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Item1, size.Item2));
                    await Settle();
                    vm.SetPageActive(false);
                    Require(GetField<CancellationTokenSource>(vm, "pageLifetime") == null && !GetField<DispatcherTimer>(vm, "timer").IsEnabled, "Stable layout fixture must stop polling before enabling synthetic data.");
                    general.SettingsConfig.Enabled.Winget = true;
                    Apply(vm, Snapshot());
                    foreach (var workspace in new[] { WingetWorkspace.Updates, WingetWorkspace.Installed, WingetWorkspace.Discover })
                    {
                        vm.ActiveTabIndex = (int)workspace;
                        vm.ViewModeIndex = 0;
                        vm.FilterText = string.Empty;
                        Apply(vm, Snapshot());
                        foreach (var source in vm.SourceFilters) source.IsSelected = true;
                        vm.ActiveSelection = vm.DisplayPackages.FirstOrDefault();
                        vm.SelectAllVisible(false);
                        if (vm.DisplayPackages.Count > 0) vm.DisplayPackages[0].IsSelected = true;
                        await Settle();
                        Require(vm.IsEnabled, "Workspace screenshot must retain the enabled module state.");
                        Require(GetField<CancellationTokenSource>(vm, "pageLifetime") == null, "Stable enabled layout must not start page polling.");
                        AssertWorkspaceBounds(page, $"{theme}-{size.Item1}-{workspace}");
                        Require(page.ActualTheme == theme, "Workspace must inherit shell theme.");
                        await CaptureShell($"winget-{theme}-{size.Item1}-{workspace}-full-shell.png");
                    }
                    vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
                    Apply(vm, Snapshot() with { IsBusy = true, OperationKind = "upgrade", Phase = "WaitingForInstaller", ProgressPercent = 47,
                        Message = "Installing a very long synthetic package name / 正在处理超长中文软件名称以验证忙碌状态不会挤出视口" });
                    await Settle();
                    Require(vm.IsBusy && !((AppBarButton)page.FindName("PrimaryActionButton")).IsEnabled, "Busy operation must disable another batch.");
                    AssertWorkspaceBounds(page, $"{theme}-{size.Item1}-busy");
                    await CaptureShell($"winget-{theme}-{size.Item1}-busy-full-shell.png");
                    vm.ActiveTabIndex = (int)WingetWorkspace.Discover;
                    vm.SearchQuery = string.Empty;
                    Apply(vm, Snapshot() with { SearchQuery = null, SearchResults = Array.Empty<WingetPackageUpdate>() });
                    await Settle();
                    Require(((FrameworkElement)page.FindName("DiscoverStartPanel")).Visibility == Visibility.Visible, "Empty discovery must show the large search entry.");
                    AssertWorkspaceBounds(page, $"{theme}-{size.Item1}-discover-empty");
                    await CaptureShell($"winget-{theme}-{size.Item1}-discover-empty-full-shell.png");
                    if (size.Item1 == 760)
                    {
                        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
                        Apply(vm, Snapshot());
                        for (int mode = 1; mode <= 2; mode++)
                        {
                            vm.ViewModeIndex = mode;
                            await Settle();
                            AssertWorkspaceBounds(page, $"{theme}-{size.Item1}-view{mode}");
                            await CaptureShell($"winget-{theme}-{size.Item1}-view{mode}-full-shell.png");
                        }
                    }
                    EnsureNoWorker();
                }
            }
        }
        finally
        {
            general.SettingsConfig.Enabled.Winget = false;
            vm.SetPageActive(false);
            root.RequestedTheme = originalTheme;
            await Settle();
            vm.SetPageActive(false);
            vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
            vm.ViewModeIndex = 0;
            general.SettingsConfig.Enabled.Winget = true;
            Apply(vm, Snapshot());
        }
        Log("Complete-shell light/dark centered layouts at 1670x985, 1510x985, 1280x900 and 760x900, all workspaces, enabled/busy, empty discovery and three views PASS");
    }

    private static async Task VerifyExpandedSettings(WingetPage page, SettingsRepository<GeneralSettings> general)
    {
        var window = App.GetSettingsWindow();
        var shell = (FrameworkElement)window.Content;
        var vm = page.ViewModel;
        var originalTheme = shell.RequestedTheme;
        var expander = (Expander)page.FindName("WingetSettingsExpander");
        var scroll = (ScrollViewer)page.FindName("AutomaticSettingsScroll");
        var dailyCard = (FrameworkElement)page.FindName("DailySettingsCard");
        var notifyCard = (FrameworkElement)page.FindName("NotifySettingsCard");
        var footer = (FrameworkElement)page.FindName("AutomaticSettingsFooter");
        var help = (HyperlinkButton)page.FindName("WingetHelpLink");
        var details = (AppBarButton)page.FindName("DetailsButton");
        Require(details.Icon is SymbolIcon { Symbol: Symbol.Document }, "Package details must use the document icon instead of the help question mark.");
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                foreach (var size in new[] { (1670, 985), (1510, 985), (760, 900), (760, 720), (1280, 900), (1280, 720) })
                {
                    general.SettingsConfig.Enabled.Winget = false;
                    vm.SetPageActive(false);
                    expander.IsExpanded = false;
                    shell.RequestedTheme = theme;
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Item1, size.Item2));
                    await Settle();
                    vm.SetPageActive(false);
                    Require(GetField<CancellationTokenSource>(vm, "pageLifetime") == null, "Expanded settings resize must not start polling.");
                    general.SettingsConfig.Enabled.Winget = true;
                    vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
                    vm.ViewModeIndex = 0;
                    vm.FilterText = string.Empty;
                    Apply(vm, Snapshot());
                    expander.IsExpanded = true;
                    await Task.Delay(220);
                    await Settle();
                    scroll.ChangeView(null, 0, null, true);
                    await Settle();
                    string context = $"{theme}-{size.Item1}x{size.Item2}-settings-expanded";
                    await CaptureShell($"winget-{context}-full-shell.png");
                    try { AssertWorkspaceBounds(page, context); }
                    catch { LogExpandedLayout(page, context); throw; }
                    Rect expanderRect = Bounds(expander, shell);
                    Rect scrollRect = Bounds(scroll, shell);
                    Rect dailyRect = Bounds(dailyCard, shell);
                    Rect notifyRect = Bounds(notifyCard, shell);
                    Require(scrollRect.Left >= expanderRect.Left + 12 && scrollRect.Right <= expanderRect.Right - 12,
                        context + ": expanded content must have real horizontal padding.");
                    Require(dailyRect.Top >= scrollRect.Top - 1 && dailyRect.Top <= scrollRect.Top + 16,
                        context + ": settings content must start at the top of its scroll viewport.");
                    Require(scroll.ScrollableWidth <= 1 && scroll.ActualHeight > 80 && scroll.ActualHeight <= scroll.MaxHeight + 1,
                        context + ": settings must use a finite vertical scroll viewport without horizontal overflow.");
                    foreach (var card in new[] { dailyCard, notifyCard })
                    {
                        Rect cardRect = Bounds(card, shell);
                        Require(card.ActualHeight >= 48 && cardRect.Left >= scrollRect.Left - 1 && cardRect.Right <= scrollRect.Right + 1,
                            context + ": settings card collapsed or extends outside the scroll viewport.");
                        var toggle = Descendants(card).OfType<ToggleSwitch>().Single();
                        Rect toggleRect = Bounds(toggle, shell);
                        Require(toggleRect.Left >= cardRect.Left + 8 && toggleRect.Right <= cardRect.Right - 8 &&
                            toggleRect.Top >= cardRect.Top && toggleRect.Bottom <= cardRect.Bottom + 1,
                            context + ": settings toggle must be inset and contained by its card.");
                        Require(toggle.IsEnabled, context + ": enabled plugin must expose usable automatic settings switches.");
                    }
                    Require(Math.Abs(dailyRect.Left - notifyRect.Left) <= 1 && Math.Abs(dailyRect.Right - notifyRect.Right) <= 1 &&
                        notifyRect.Top >= dailyRect.Bottom + 8,
                        context + ": automatic settings must always stack Check daily above Windows notifications at matching left and right edges.");
                    Require(((TextBlock)page.FindName("LastCheckText")).Text == vm.LastCheckText &&
                        ((TextBlock)page.FindName("NextCheckText")).Text == vm.NextCheckText,
                        context + ": schedule summary must retain localized check times.");
                    var list = (ListView)page.FindName("PackagesList");
                    Require(list.ActualHeight >= 40, context + ": expanded settings must leave a usable package list.");
                    Require(help.NavigateUri?.AbsoluteUri == "https://learn.microsoft.com/en-us/windows/package-manager/winget/",
                        context + ": help must retain the official WinGet documentation target.");
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                    await Settle();
                    Require(help.Focus(FocusState.Keyboard), context + ": help must remain keyboard reachable.");
                    await Settle();
                    Rect footerRect = Bounds(footer, shell);
                    scrollRect = Bounds(scroll, shell);
                    Require(footerRect.Top >= scrollRect.Top - 1 && footerRect.Bottom <= scrollRect.Bottom + 1 &&
                        footerRect.Left >= scrollRect.Left - 1 && footerRect.Right <= scrollRect.Right + 1,
                        context + ": schedule and help must be fully reachable within the inner scroll viewport.");
                    if (scroll.ScrollableHeight > 1)
                        await CaptureShell($"winget-{context}-footer-full-shell.png");
                    Require(GetField<CancellationTokenSource>(vm, "pageLifetime") == null && !GetField<DispatcherTimer>(vm, "timer").IsEnabled,
                        context + ": expanding and scrolling settings must not resume service polling.");
                    Log($"EXPANDED {context}: content={scroll.ActualWidth:F1}x{scroll.ActualHeight:F1}, scroll={scroll.ScrollableHeight:F1}, listHeight={list.ActualHeight:F1}");
                    EnsureNoWorker();
                }
            }
        }
        finally
        {
            general.SettingsConfig.Enabled.Winget = false;
            vm.SetPageActive(false);
            expander.IsExpanded = false;
            shell.RequestedTheme = originalTheme;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 900));
            await Settle();
            vm.SetPageActive(false);
            general.SettingsConfig.Enabled.Winget = true;
            Apply(vm, Snapshot());
        }
        Log("Expanded automatic settings light/dark wide/narrow/short centered layout, vertically stacked options, padding, localized schedule, keyboard-reachable help and document details icon PASS");
    }

    private static void LogExpandedLayout(WingetPage page, string context)
    {
        page.UpdateLayout();
        var shell = (FrameworkElement)App.GetSettingsWindow().Content;
        foreach (string name in new[] { "WingetViewport", "WingetEnableCard", "WingetSettingsExpander", "AutomaticSettingsScroll", "DailySettingsCard", "NotifySettingsCard", "AutomaticSettingsFooter", "WingetSections", "PackageWorkspaceGrid", "WorkspaceCommands", "WingetOperationCard", "WorkspaceBody", "PackageTableViewport", "WingetPackageListCard", "PackageTableHeader", "PackagesList", "WorkspaceFooter" })
        {
            var element = (FrameworkElement)page.FindName(name);
            Rect rect = Bounds(element, shell);
            string rows = element is Grid grid ? "; rows=" + string.Join(",", grid.RowDefinitions.Select(row => row.ActualHeight.ToString("F1", CultureInfo.InvariantCulture))) : string.Empty;
            Log($"LAYOUT {context} {name}: x={rect.Left:F1}..{rect.Right:F1}, y={rect.Top:F1}..{rect.Bottom:F1}, size={element.ActualWidth:F1}x{element.ActualHeight:F1}, visibility={element.Visibility}{rows}");
        }
        var list = (ListView)page.FindName("PackagesList");
        var scroll = (ScrollViewer)page.FindName("AutomaticSettingsScroll");
        Log($"LAYOUT {context}: settingsMaxHeight={scroll.MaxHeight:F1}, settingsOffset={scroll.VerticalOffset:F1}/{scroll.ScrollableHeight:F1}, packageCount={list.Items.Count}, activeTab={page.ViewModel.ActiveTabIndex}, error={page.ViewModel.HasError}, notificationError={page.ViewModel.HasNotificationError}, recovery={page.ViewModel.RecoveryRequired}, operation={page.ViewModel.HasOperationFeedback}");
        foreach (var listScroll in Descendants(list).OfType<ScrollViewer>())
            Log($"LAYOUT {context} package scroll: viewport={listScroll.ViewportWidth:F1}x{listScroll.ViewportHeight:F1}, extent={listScroll.ExtentHeight:F1}, offset={listScroll.VerticalOffset:F1}/{listScroll.ScrollableHeight:F1}");
        foreach (var bar in Descendants(page).OfType<InfoBar>().Where(bar => bar.IsOpen && bar.Visibility == Visibility.Visible))
            Log($"LAYOUT {context} visible InfoBar: {bar.Title}; {bar.Message}; height={bar.ActualHeight:F1}");
        for (int index = 0; index < list.Items.Count; index++)
        {
            if (list.ContainerFromIndex(index) is FrameworkElement container)
            {
                Rect rect = Bounds(container, shell);
                Log($"LAYOUT {context} package[{index}]: y={rect.Top:F1}..{rect.Bottom:F1}, size={container.ActualWidth:F1}x{container.ActualHeight:F1}, visibility={container.Visibility}");
            }
        }
    }

    private static async Task VerifySemanticPresentation(WingetPage page)
    {
        var vm = page.ViewModel;
        var readinessBadge = (Border)page.FindName("ReadinessBadge");
        var readinessGlyph = (FontIcon)page.FindName("ReadinessGlyph");
        var operationBadge = (Border)page.FindName("OperationBadge");
        var operationGlyph = (FontIcon)page.FindName("OperationGlyph");
        ElementTheme originalTheme = page.RequestedTheme;
        try
        {
            foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                page.RequestedTheme = theme;
                Apply(vm, Snapshot());
                await Settle();
                Require(vm.ReadinessTone == "Success" && ReferenceEquals(readinessBadge.Style, page.BadgeStyle("Success")) && readinessGlyph.Glyph == vm.ReadinessGlyph,
                    "Available WinGet must bind its success badge and glyph in " + theme + ".");

                Apply(vm, Snapshot() with { Availability = new WingetAvailability(false, null, null, "Fixture missing App Installer") });
                await Settle();
                Require(vm.ReadinessTone == "Warning" && ReferenceEquals(readinessBadge.Style, page.BadgeStyle("Warning")),
                    "Unavailable WinGet must replace the ready badge with a warning.");

                var item = new WingetQueueItem("fixture-presentation", NormalPackage, "upgrade", "Running", "Synthetic progress");
                Apply(vm, Snapshot() with { IsBusy = true, OperationKind = "queue", Phase = "WaitingForInstaller", Queue = new[] { item } });
                await Settle();
                Require(vm.OperationTone == "Accent" && ReferenceEquals(operationBadge.Style, page.BadgeStyle("Accent")),
                    "Running operations must show an active state.");
                foreach (var outcome in new[] { (State: "Succeeded", Tone: "Success"), (State: "NeedsVerification", Tone: "Warning"), (State: "Failed", Tone: "Error") })
                {
                    Apply(vm, Snapshot() with { Queue = new[] { item with { State = outcome.State } } });
                    await Settle();
                    Require(vm.OperationTone == outcome.Tone && ReferenceEquals(operationBadge.Style, page.BadgeStyle(outcome.Tone)) &&
                        ReferenceEquals(operationGlyph.Style, page.GlyphStyle(outcome.Tone)) && operationGlyph.Glyph == vm.OperationGlyph,
                        "Operation outcome " + outcome.State + " must update the existing semantic badge and glyph.");
                }
                Apply(vm, Snapshot() with { Queue = new[] { item with { State = "Failed" }, item with { Id = "fixture-presentation-later", State = "Succeeded" } } });
                await Settle();
                Require(vm.OperationTone == "Error", "A later success must not hide a failed item in the same batch.");
                Require(ReferenceEquals(operationBadge, page.FindName("OperationBadge")) && ReferenceEquals(operationGlyph, page.FindName("OperationGlyph")),
                    "Semantic state changes must update the existing controls without replacing the operation surface.");
            }
        }
        finally
        {
            page.RequestedTheme = originalTheme;
            Apply(vm, Snapshot());
        }
        Log("Readiness and operation semantic badges update in light/dark themes, preserving controls and mixed-batch failure visibility PASS");
    }

    private static async Task VerifyRepeatedStatusStability(WingetPage page)
    {
        var vm = page.ViewModel;
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        vm.ViewModeIndex = 0;
        vm.SortIndex = 0;
        vm.SortDescending = false;
        vm.FilterText = string.Empty;
        var packages = Enumerable.Range(0, 80).Select(index => NormalPackage with { Id = "Fixture.Stable" + index.ToString("D3"), Name = "Stable package " + index.ToString("D3") }).ToArray();
        string output = string.Join("\n", Enumerable.Range(0, 80).Select(index => "Installer line " + index.ToString("D3") + " / 安装输出示例"));
        var runningItem = new WingetQueueItem("fixture-stable-queue", packages[0], "upgrade", "Running", "Preparing synthetic package", CheckedAt);
        var snapshot = Snapshot() with
        {
            Packages = packages, IsBusy = true, OperationId = "fixture-stable-operation", OperationKind = "queue", Phase = "WaitingForInstaller",
            ProgressPercent = 10, Output = output, Queue = new[] { runningItem }, SelectedDetails = null,
        };
        Apply(vm, CloneSnapshot(snapshot));
        var list = (ListView)page.FindName("PackagesList");
        var details = (Expander)page.FindName("OperationDetailsExpander");
        details.IsExpanded = true;
        await Settle();
        var queueList = (ItemsControl)page.FindName("OperationQueueList");
        var queueRow = vm.QueueItems.Single();
        var queueExpander = Descendants(queueList).OfType<Expander>().Single(item => item.Name == "QueueRowExpander");
        queueExpander.IsExpanded = true;
        var focusedRow = vm.DisplayPackages[30];
        vm.ActiveSelection = focusedRow;
        focusedRow.IsSelected = true;
        list.ScrollIntoView(focusedRow, ScrollIntoViewAlignment.Leading);
        await Settle();
        var container = (ListViewItem)list.ContainerFromItem(focusedRow);
        Require(container != null, "Stability fixture must realize its focused package container.");
        var rowContent = Descendants(container).OfType<Grid>().Single(item => item.Name == "PackageRowBounds");
        var rowCheckBox = Descendants(rowContent).OfType<CheckBox>().Single();
        Require(rowCheckBox.Focus(FocusState.Keyboard), "Stability fixture must establish real keyboard focus in the package row.");
        var focus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(page.XamlRoot);
        Require(ReferenceEquals(focus, rowCheckBox), "Stability fixture must focus the actual package checkbox.");
        var packageScroll = Descendants(list).OfType<ScrollViewer>().First();
        var outputScroll = (ScrollViewer)page.FindName("OperationOutputScroll");
        var outputText = (TextBlock)page.FindName("OperationOutputText");
        Require(outputScroll.ScrollableHeight > 100, "Installer output fixture must provide a scrollable reading region.");
        outputScroll.ChangeView(null, Math.Min(90, outputScroll.ScrollableHeight / 2), null, true);
        await Settle();
        double packageOffset = packageScroll.VerticalOffset;
        double outputOffset = outputScroll.VerticalOffset;
        var template = list.ItemTemplate;
        int blankNotifications = 0;
        int packageChanges = 0;
        int queueChanges = 0;
        System.ComponentModel.PropertyChangedEventHandler propertyChanged = (_, args) => { if (string.IsNullOrEmpty(args.PropertyName)) blankNotifications++; };
        System.Collections.Specialized.NotifyCollectionChangedEventHandler packagesChanged = (_, _) => packageChanges++;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler queuesChanged = (_, _) => queueChanges++;
        vm.PropertyChanged += propertyChanged;
        vm.DisplayPackages.CollectionChanged += packagesChanged;
        vm.QueueItems.CollectionChanged += queuesChanged;
        try
        {
            for (int repeat = 0; repeat < 8; repeat++)
            {
                // Recreate every record/list, just as independent JSON IPC replies do.
                Apply(vm, CloneSnapshot(snapshot));
                await Settle();
                AssertStableVisuals("identical JSON status " + repeat);
            }
            for (int progress = 11; progress <= 16; progress++)
            {
                output += "\nNew output line " + progress;
                Apply(vm, CloneSnapshot(snapshot with { ProgressPercent = progress, Output = output,
                    Queue = new[] { runningItem with { Message = "Synthetic progress " + progress } } }));
                await Settle();
                AssertStableVisuals("output/progress status " + progress);
                Require(outputText.Text == output, "Installer output must update in the existing selectable text control.");
            }
            Require(blankNotifications == 0 && packageChanges == 0 && queueChanges == 0, "Repeated status and output/progress updates must not publish whole-page notifications or rebuild package/queue collections.");
            var result = new WingetInstallResult(true, false, false, null, null) { Output = output };
            var completed = runningItem with { State = "Succeeded", Message = "Synthetic installation completed", CompletedUtc = CheckedAt.AddMinutes(1), Result = result };
            Apply(vm, CloneSnapshot(snapshot with { IsBusy = false, Phase = "Idle", ProgressPercent = null, Output = null,
                Queue = new[] { completed }, LastResult = new WingetServiceOperationResult("fixture-stable-operation", "upgrade", true, "Synthetic installation completed", Install: result) }));
            await Settle();
            Require(((FrameworkElement)page.FindName("WingetOperationCard")).Visibility == Visibility.Visible && vm.HasOperationFeedback, "Operation feedback must remain visible after completion.");
            Require(((TextBlock)page.FindName("OperationSummaryText")).Text == vm.OperationSummary && vm.OperationSummary.Length > 0 &&
                ((TextBlock)page.FindName("OperationDescriptionText")).Text.Contains("Synthetic installation completed", StringComparison.Ordinal), "Completed operation must show its durable result summary and description.");
            Require(((Button)page.FindName("OperationHistoryButton")).Visibility == Visibility.Visible && outputText.Text == output, "Completion must retain output and a directly visible history button.");
            Require(ReferenceEquals(queueRow, vm.QueueItems.Single()) && ReferenceEquals(queueExpander, Descendants(queueList).OfType<Expander>().Single(item => item.Name == "QueueRowExpander")) && queueExpander.IsExpanded,
                "Completing a queue item must update its existing expanded result container.");
            await CaptureShell("winget-completed-operation-full-shell.png");
        }
        finally
        {
            vm.PropertyChanged -= propertyChanged;
            vm.DisplayPackages.CollectionChanged -= packagesChanged;
            vm.QueueItems.CollectionChanged -= queuesChanged;
            details.IsExpanded = false;
            Apply(vm, Snapshot());
            vm.SelectAllVisible(false);
        }
        Require(GetField<CancellationTokenSource>(vm, "pageLifetime") == null, "Stability fixtures must never activate real service polling.");
        EnsureNoWorker();
        Log("Repeated JSON snapshots and live output/progress preserve package/queue containers, focus, selection, scroll, expanded results and durable completion feedback PASS");

        void AssertStableVisuals(string context)
        {
            Require(ReferenceEquals(template, list.ItemTemplate), context + ": item template changed without a view change.");
            Require(ReferenceEquals(container, list.ContainerFromItem(focusedRow)) && ReferenceEquals(rowContent, Descendants(container).OfType<Grid>().Single(item => item.Name == "PackageRowBounds")), context + ": package container/content rebuilt.");
            Require(ReferenceEquals(focus, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(page.XamlRoot)), context + ": keyboard focus moved.");
            Require(ReferenceEquals(focusedRow, vm.ActiveSelection) && focusedRow.IsSelected, context + ": package focus or checkbox selection was lost.");
            Require(Math.Abs(packageScroll.VerticalOffset - packageOffset) < 1 && Math.Abs(outputScroll.VerticalOffset - outputOffset) < 1, context + ": package/output reading position changed.");
            Require(ReferenceEquals(queueRow, vm.QueueItems.Single()) && ReferenceEquals(queueExpander, Descendants(queueList).OfType<Expander>().Single(item => item.Name == "QueueRowExpander")) && queueExpander.IsExpanded,
                context + ": queue row or expanded details rebuilt.");
        }
    }

    private static WingetServiceStatus CloneSnapshot(WingetServiceStatus snapshot) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, WingetServiceJsonContext.Default.WingetServiceStatus), WingetServiceJsonContext.Default.WingetServiceStatus);

    private static async Task VerifyIgnoredStateLabel(WingetPage page)
    {
        var vm = page.ViewModel;
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        vm.ViewModeIndex = 0;
        vm.FilterText = string.Empty;
        vm.ExactMatch = false;
        vm.ShowIgnored = true;
        Apply(vm, Snapshot());
        var list = (ListView)page.FindName("PackagesList");
        var row = vm.DisplayPackages.Single(item => item.Id == NormalPackage.Id);
        list.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
        await Settle();
        var container = (ListViewItem)list.ContainerFromItem(row);
        Require(container != null, "Ignored-state fixture must realize a package row.");
        var label = Descendants(container).OfType<TextBlock>().Single(block => block.Text == WingetViewModel.L("UpdateAvailableState"));
        try
        {
            var identity = new WingetPackageIdentity(row.Id, row.Source, row.Package.InstalledVersion, row.Package.AvailableVersion);
            Apply(vm, CloneSnapshot(Snapshot() with { IgnoredPackages = new[] { identity } }));
            await Settle();
            Require(row.IsIgnored && row.StateLabel == WingetViewModel.L("IgnoredState") && label.Text == row.StateLabel,
                "Showing ignored updates must update the existing row's visible state label when IsIgnored becomes true.");
            Require(ReferenceEquals(row, vm.DisplayPackages.Single(item => item.Id == row.Id)) && ReferenceEquals(container, list.ContainerFromItem(row)) && Descendants(container).Contains(label),
                "Ignoring a displayed package must preserve its existing row, container and label control.");
            Apply(vm, CloneSnapshot(Snapshot()));
            await Settle();
            Require(!row.IsIgnored && label.Text == WingetViewModel.L("UpdateAvailableState"), "Restoring an ignored package must update the same visible label back to update available.");
        }
        finally { vm.ShowIgnored = false; Apply(vm, Snapshot()); }
        Log("Displayed ignored-state changes update the same package state label in both directions PASS");
    }

    private static async Task VerifyLatestOperationOutput(WingetPage page)
    {
        var vm = page.ViewModel;
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        vm.FilterText = string.Empty;
        var packageA = NormalPackage with { Id = "Fixture.OutputA", Name = "Previous package A" };
        var packageB = NormalPackage with { Id = "Fixture.OutputB", Name = "Latest package B" };
        const string previousOutput = "Output belongs only to package A";
        var itemA = new WingetQueueItem("fixture-output-a", packageA, "upgrade", "Succeeded", "A completed")
        { Result = new WingetInstallResult(true, false, false, null, null) { Output = previousOutput } };
        var staleResult = new WingetServiceOperationResult("fixture-output-a", "upgrade", true, "A completed", Install: itemA.Result);
        var description = (TextBlock)page.FindName("OperationDescriptionText");
        var output = (TextBlock)page.FindName("OperationOutputText");
        try
        {
            Apply(vm, CloneSnapshot(Snapshot() with { Queue = new[] { itemA }, LastResult = staleResult }));
            await Settle();
            Require(vm.OperationOutput == previousOutput && output.Text == previousOutput, "Previous-package output fixture must populate the real output control first.");
            foreach (string state in new[] { "Failed", "Cancelled" })
            {
                var itemB = new WingetQueueItem("fixture-output-b", packageB, "upgrade", state, "B has no installer output")
                { Result = state == "Failed" ? new WingetInstallResult(false, false, false, "B failed before output", -1) : null };
                Apply(vm, CloneSnapshot(Snapshot() with { Queue = new[] { itemA, itemB }, LastResult = staleResult }));
                await Settle();
                Require(vm.OperationDescription.StartsWith(packageB.Name, StringComparison.Ordinal) && description.Text == vm.OperationDescription,
                    state + ": latest operation description must identify package B.");
                Require(vm.OperationOutput.Length == 0 && !vm.HasOperationOutput && output.Text.Length == 0,
                    state + ": package B without output must clear the main output instead of displaying package A's previous output.");
                Require(vm.QueueItems.First().Output == previousOutput && vm.QueueItems.Last().Output.Length == 0,
                    state + ": package A's output must remain associated with its own historical queue item only.");
            }
        }
        finally { Apply(vm, Snapshot()); }
        Log("Latest failed/cancelled package without output never inherits previous-package output PASS");
    }

    private static void AssertWorkspaceBounds(WingetPage page, string context)
    {
        page.UpdateLayout();
        var shell = (FrameworkElement)App.GetSettingsWindow().Content;
        var enable = (FrameworkElement)page.FindName("WingetEnableCard");
        var workspace = (FrameworkElement)page.FindName("PackageWorkspaceGrid");
        var table = (FrameworkElement)page.FindName("WingetPackageListCard");
        Rect pageRect = Bounds(page, shell);
        Rect enableRect = Bounds(enable, shell);
        Rect workspaceRect = Bounds(workspace, shell);
        double leftGutter = enableRect.Left - pageRect.Left;
        double rightGutter = pageRect.Right - enableRect.Right;
        double expectedContentWidth = Math.Min(1000, page.ActualWidth - 48);
        Require(enable.ActualWidth <= 1001 && Math.Abs(enable.ActualWidth - expectedContentWidth) <= 1,
            $"{context}: content width {enable.ActualWidth:F1} must fill the available page width up to the shared 1000 DIP maximum; expected {expectedContentWidth:F1}.");
        Require(leftGutter >= 23 && rightGutter >= 23 && Math.Abs(leftGutter - rightGutter) <= 1,
            $"{context}: content must retain equal left/right page gutters of at least 24 DIPs; actual {leftGutter:F1}/{rightGutter:F1}.");
        Rect expanderRect = Bounds((FrameworkElement)page.FindName("WingetSettingsExpander"), shell);
        Require(Math.Abs(expanderRect.Left - enableRect.Left) <= 1 && Math.Abs(expanderRect.Right - enableRect.Right) <= 1,
            $"{context}: automatic settings [{expanderRect.Left:F1},{expanderRect.Right:F1}] must align with EnableCard [{enableRect.Left:F1},{enableRect.Right:F1}].");
        Require(Math.Abs(workspaceRect.Left - enableRect.Left) <= 1, $"{context}: workspace left {workspaceRect.Left:F1} differs from EnableCard {enableRect.Left:F1}.");
        Require(Math.Abs(workspaceRect.Right - enableRect.Right) <= 1, $"{context}: workspace right {workspaceRect.Right:F1} differs from EnableCard {enableRect.Right:F1}.");
        Require(workspace.ActualWidth > 300 && table.ActualWidth > 250, $"{context}: workspace/table collapsed.");
        foreach (string name in new[] { "WingetSettingsExpander", "PackageWorkspaceGrid", "WorkspaceCommands", "WorkspaceBody", "PackageTableViewport", "WingetPackageListCard", "WorkspaceFooter" })
        {
            var element = (FrameworkElement)page.FindName(name);
            Rect rect = Bounds(element, shell);
            Require(rect.Left >= enableRect.Left - 1 && rect.Right <= enableRect.Right + 1 && rect.Right <= shell.ActualWidth + 1,
                $"{context}: {name} bounds [{rect.Left:F1},{rect.Right:F1}] exceed content [{enableRect.Left:F1},{enableRect.Right:F1}] / shell {shell.ActualWidth:F1}.");
        }
        Require(workspaceRect.Left >= pageRect.Left && workspaceRect.Bottom <= pageRect.Bottom + 1, $"{context}: workspace extends outside the page.");
        var list = page.ViewModel.ViewModeIndex == 2 ? (ListViewBase)page.FindName("PackageTiles") : (ListViewBase)page.FindName("PackagesList");
        Rect listRect = Bounds(list, shell);
        Require(listRect.Left >= enableRect.Left - 1 && listRect.Right <= enableRect.Right + 1, $"{context}: package list escapes viewport.");
        int checkedRows = 0;
        for (int index = 0; index < list.Items.Count; index++)
        {
            // GridView keeps measurement/recycled templates parked offscreen.
            // Only containers mapped to current items can represent displayed rows.
            if (list.ContainerFromIndex(index) is not FrameworkElement container)
                continue;
            Require(list.IndexFromContainer(container) == index, $"{context}: package container is mapped to the wrong current item.");
            if (container.Visibility != Visibility.Visible || container.ActualWidth <= 0 || container.ActualHeight <= 0)
                continue;
            Rect containerRect = Bounds(container, shell);
            // Virtualization may also keep current items above/below the viewport.
            // Never filter by horizontal intersection: that would hide clipping.
            if (containerRect.Bottom <= listRect.Top || containerRect.Top >= listRect.Bottom)
                continue;
            Require(containerRect.Left >= listRect.Left - 1 && containerRect.Right <= listRect.Right + 1,
                $"{context}: visible package container {index} [{containerRect.Left:F1},{containerRect.Right:F1}] escapes table [{listRect.Left:F1},{listRect.Right:F1}].");
            var rows = Descendants(container).OfType<FrameworkElement>().Where(item => item.Name == "PackageRowBounds" && item.ActualWidth > 0 && item.ActualHeight > 0).ToArray();
            Require(rows.Length == 1, $"{context}: visible package container {index} must have exactly one laid-out row, found {rows.Length}.");
            var row = rows[0];
            Rect rect = Bounds(row, shell);
            Require(rect.Left >= listRect.Left - 1 && rect.Right <= listRect.Right + 1, $"{context}: long package row [{rect.Left:F1},{rect.Right:F1}] escapes table [{listRect.Left:F1},{listRect.Right:F1}].");
            checkedRows++;
        }
        Require(list.Items.Count == 0 || checkedRows > 0, $"{context}: populated package list has no current visible rows to validate.");
        foreach (var scroll in Descendants(list).OfType<ScrollViewer>())
            Require(scroll.ScrollableWidth <= 1, $"{context}: package list has unintended horizontal scrolling.");
        var filters = (FrameworkElement)page.FindName("CompactFiltersButton");
        Require((workspace.ActualWidth < 900) == (filters.Visibility == Visibility.Visible), $"{context}: narrow filter flyout mode is incorrect.");
        Log($"BOUNDS {context}: shell={shell.ActualWidth:F1}, page=[{pageRect.Left:F1},{pageRect.Right:F1}], enable=[{enableRect.Left:F1},{enableRect.Right:F1}], gutters={leftGutter:F1}/{rightGutter:F1}, workspace=[{workspaceRect.Left:F1},{workspaceRect.Right:F1}], table={table.ActualWidth:F1}, visibleRows={checkedRows}/{list.Items.Count}");
    }

    private static async Task VerifyCancelledConfirmation(WingetPage page)
    {
        var vm = page.ViewModel;
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        vm.ViewModeIndex = 0;
        vm.FilterText = string.Empty;
        vm.ExactMatch = false;
        vm.ShowIgnored = false;
        Apply(vm, Snapshot());
        var firstDetails = Details(LongPackage) with { Agreements = new[] { new WingetPackageAgreement("Terms", "Synthetic first package agreement / 第一份协议", "https://example.invalid/terms") } };
        var secondDetails = Details(NormalPackage) with { Agreements = new[] { new WingetPackageAgreement("Second terms", "Synthetic second package agreement / 第二份协议", null) } };
        var prepared = new WingetPreparedOperation("fixture-preparation", "upgrade", new[] { firstDetails, secondDetails }, new WingetPackageOptions(), CheckedAt);
        var method = typeof(WingetPage).GetMethod("ShowPreparedConfirmationAsync", PrivateInstance);
        var field = typeof(WingetPage).GetField("activeDialog", PrivateInstance);
        string before = vm.Activity;
        try
        {
            Task showing = (Task)method.Invoke(page, new object[] { prepared });
            var dialog = (ContentDialog)field.GetValue(page);
            Require(dialog != null && dialog.DefaultButton == ContentDialogButton.Close, "Batch review must default to cancellation.");
            await (Task)method.Invoke(page, new object[] { prepared });
            Require(ReferenceEquals(dialog, field.GetValue(page)), "A duplicate confirmation must not replace the active dialog.");
            await WaitForDialogText(dialog, new[] { LongPackage.Name, NormalPackage.Name, LongPackage.AvailableVersion, "Synthetic first package agreement", "Synthetic second package agreement", WingetViewModel.L("ElevationConfirmation") });
            dialog.Hide();
            await showing;
            Require(vm.Activity == before && !vm.IsBusy && !GetField<bool>(vm, "requestPending"), "Cancelling batch review must send no operation.");
            Task second = (Task)method.Invoke(page, new object[] { prepared });
            await Settle();
            vm.ActiveTabIndex = (int)WingetWorkspace.Discover;
            await second;
            Require(field.GetValue(page) == null, "Changing workspace must dismiss stale batch review.");
            EnsureNoWorker();
        }
        finally { ((ContentDialog)field.GetValue(page))?.Hide(); }
        Log("Batch review shows every package/version/agreement and UAC guidance; duplicate guard, cancel and tab-dismissal PASS");
    }

    private static async Task VerifyDetailsDialog(WingetPage page)
    {
        var vm = page.ViewModel;
        vm.ActiveTabIndex = (int)WingetWorkspace.Updates;
        vm.ViewModeIndex = 0;
        vm.FilterText = string.Empty;
        vm.ExactMatch = false;
        vm.ShowIgnored = false;
        Apply(vm, Snapshot());
        vm.ActiveSelection = vm.DisplayPackages.Single(item => item.Id == LongPackage.Id);
        var field = typeof(WingetPage).GetField("activeDialog", PrivateInstance);
        var method = typeof(WingetPage).GetMethod("ShowDetailsAsync", PrivateInstance);
        try
        {
            Task showing = (Task)method.Invoke(page, Array.Empty<object>());
            var timer = Stopwatch.StartNew();
            while (field.GetValue(page) == null && timer.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(25);
            var dialog = (ContentDialog)field.GetValue(page);
            Require(dialog != null, "Selected package details must open in a dialog.");
            await WaitForDialogText(dialog, new[] { LongPackage.Id, LongPackage.ReleaseNotes, Details(LongPackage).Description });
            Require(Descendants(dialog).OfType<HyperlinkButton>().Any(link => link.NavigateUri?.AbsoluteUri == Details(LongPackage).PackageUrl) &&
                Descendants(dialog).OfType<TextBlock>().Any(block => block.Text.Contains("MIT", StringComparison.Ordinal)), "Package details must include its safe homepage and license.");
            Require(Descendants(dialog).OfType<TextBlock>().Where(block => block.Text == LongPackage.ReleaseNotes).All(block => block.TextWrapping == TextWrapping.Wrap && block.IsTextSelectionEnabled), "Release notes must remain selectable wrapping plain text.");
            dialog.Hide();
            await showing;
            AssertWorkspaceBounds(page, "details-closed");
            vm.ActiveTabIndex = (int)WingetWorkspace.Discover;
            vm.FilterText = string.Empty;
            vm.ExactMatch = false;
            var lightweight = SearchPackage with { IsMetadataResolved = false, InstalledVersion = string.Empty, AvailableVersion = string.Empty,
                ManualUpgradeCommand = string.Empty, CanUninstall = false, InstallEligibility = WingetInstallEligibility.ManualOnly };
            var loading = Snapshot() with { SearchResults = new[] { lightweight }, SelectedDetails = null, IsBusy = true,
                OperationId = "fixture-details", OperationKind = "details", Phase = "LoadingDetails" };
            Apply(vm, loading);
            vm.ActiveSelection = vm.DisplayPackages.Single();
            Task loadingDialog = (Task)method.Invoke(page, Array.Empty<object>());
            var liveDialog = (ContentDialog)field.GetValue(page);
            Require(liveDialog != null, "Details must open immediately while metadata is still loading.");
            await WaitForDialogText(liveDialog, new[] { WingetViewModel.L("Phase_LoadingDetails") });
            var loadingPanel = Descendants(liveDialog).OfType<StackPanel>().Single(panel => panel.Name == "DetailsLoadingPanel");
            var errorPanel = Descendants(liveDialog).OfType<StackPanel>().Single(panel => panel.Name == "DetailsErrorPanel");
            var detailsPanel = Descendants(liveDialog).OfType<StackPanel>().Single(panel => panel.Name == "DetailsContentPanel");
            var retry = Descendants(liveDialog).OfType<Button>().Single(button => button.Name == "DetailsRetryButton");
            var cancel = Descendants(liveDialog).OfType<Button>().Single(button => button.Name == "DetailsCancelButton");
            Require(loadingPanel.Visibility == Visibility.Visible && cancel.IsEnabled && detailsPanel.Visibility == Visibility.Collapsed,
                "Loading details must display progress and cancellation while resolved content is hidden.");
            Apply(vm, loading with { IsBusy = false, Phase = "Cancelled", LastResult = new WingetServiceOperationResult("fixture-details", "details", false, "Fixture details cancelled") });
            await Settle();
            Require(ReferenceEquals(liveDialog, field.GetValue(page)) && errorPanel.Visibility == Visibility.Visible && retry.IsEnabled && loadingPanel.Visibility == Visibility.Collapsed,
                "A failed/cancelled detail request must keep its dialog open and expose retry.");
            Require(!await vm.LoadDetailsAsync(), "Inactive synthetic details retry must not contact a service.");
            Apply(vm, loading with { OperationId = "fixture-details-retry" });
            await Settle();
            Require(loadingPanel.Visibility == Visibility.Visible && errorPanel.Visibility == Visibility.Collapsed && !retry.IsEnabled,
                "Retry must restore the existing loading controls without recreating the dialog.");
            var resolved = SearchPackage with { IsMetadataResolved = true };
            Apply(vm, Snapshot() with { SearchResults = new[] { resolved }, SelectedDetails = Details(resolved) });
            await WaitForDialogText(liveDialog, new[] { resolved.AvailableVersion, Details(resolved).Description });
            Require(ReferenceEquals(liveDialog, field.GetValue(page)) && detailsPanel.Visibility == Visibility.Visible && loadingPanel.Visibility == Visibility.Collapsed && errorPanel.Visibility == Visibility.Collapsed,
                "Resolved metadata must fill the same dialog and replace loading/error state.");
            liveDialog.Hide();
            await loadingDialog;
            Require(GetField<CancellationTokenSource>(vm, "pageLifetime") == null, "Details fixture must not resume service polling.");
            EnsureNoWorker();
        }
        finally { ((ContentDialog)field.GetValue(page))?.Hide(); }
        Log("Details open immediately; progress/cancel/error/retry/resolved states share one dialog, with safe homepage/license and selectable notes PASS");
    }

    private static async Task WaitForDialogText(ContentDialog dialog, string[] expected)
    {
        var timer = Stopwatch.StartNew();
        string[] actual = Array.Empty<string>();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            actual = Descendants(dialog).OfType<TextBlock>().Where(block => block.IsLoaded && block.ActualWidth > 0 && block.ActualHeight > 0).Select(block => block.Text).ToArray();
            if (expected.All(value => actual.Any(text => text.Contains(value, StringComparison.Ordinal)))) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException("Batch review missing visible content: " + string.Join(", ", expected.Select((value, index) => (value, index)).Where(item => !actual.Any(text => text.Contains(item.value, StringComparison.Ordinal))).Select(item => item.index)));
    }

    private static WingetServiceStatus Snapshot() => new()
    {
        Availability = Available, LastSuccessUtc = CheckedAt, NextCheckUtc = CheckedAt.AddDays(1),
        Packages = new[] { LongPackage, NormalPackage, UnknownPackage },
        SearchQuery = "fixture", SearchResults = new[] { SearchPackage, LongPackage },
        InstalledPackages = new[] { LongPackage, NormalPackage, LocalPackage, StorePackage },
        SelectedDetails = Details(LongPackage),
    };
    private static WingetPackageDetails Details(WingetPackageUpdate package) => new(package, "Fixture publisher / 示例发布者", new string('长', 180) + " / Synthetic package description", "https://example.invalid/package", "MIT", null, Array.Empty<WingetPackageAgreement>());
    private static void Apply(WingetViewModel vm, WingetServiceStatus status) => typeof(WingetViewModel).GetMethod("ApplyStatus", PrivateInstance).Invoke(vm, new object[] { status });
    private static T GetField<T>(WingetViewModel vm, string name) => (T)typeof(WingetViewModel).GetField(name, PrivateInstance).GetValue(vm);
    private static Rect Bounds(FrameworkElement item, UIElement root) => item.TransformToVisual(root).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));

    private static async Task Navigate(Type type)
    {
        Require(NavigationService.Navigate(type), "Could not navigate to " + type.Name);
        var page = (Page)NavigationService.Frame.Content;
        if (!page.IsLoaded)
        {
            var loaded = new TaskCompletionSource<bool>();
            RoutedEventHandler handler = null;
            handler = (_, _) => { page.Loaded -= handler; loaded.TrySetResult(true); };
            page.Loaded += handler;
            try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { page.Loaded -= handler; }
        }
        await Settle();
    }

    private static async Task Settle()
    {
        await Task.Delay(80);
        ((FrameworkElement)App.GetSettingsWindow().Content).UpdateLayout();
        var rendered = new TaskCompletionSource<bool>();
        EventHandler<object> handler = (_, _) => rendered.TrySetResult(true);
        CompositionTarget.Rendering += handler;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= handler; }
    }

    private static async Task CaptureShell(string name)
    {
        var windowRoot = (FrameworkElement)App.GetSettingsWindow().Content;
        var shell = windowRoot as ShellPage ?? Descendants(windowRoot).OfType<ShellPage>().Single();
        var root = (Grid)shell.FindName("RootGrid");
        var background = root.Background;
        root.Background = new SolidColorBrush(shell.ActualTheme == ElementTheme.Dark ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32) : Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243));
        var bitmap = new RenderTargetBitmap();
        try { await bitmap.RenderAsync(windowRoot); }
        finally { root.Background = background; }
        Require(bitmap.PixelWidth >= windowRoot.ActualWidth - 2 && bitmap.PixelHeight >= windowRoot.ActualHeight - 2, "Capture must include the complete window root, not a centered content child.");
        var pixels = await bitmap.GetPixelsAsync();
        var bytes = new byte[pixels.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        using var encoded = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        var png = new byte[encoded.Size];
        using (var reader = new Windows.Storage.Streams.DataReader(encoded.GetInputStreamAt(0))) { await reader.LoadAsync((uint)encoded.Size); reader.ReadBytes(png); }
        File.WriteAllBytes(Path.Combine(Output, name), png);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void EnsureNoWorker()
    {
        foreach (string name in new[] { "Kit.WingetService", "winget" })
        {
            var processes = Process.GetProcessesByName(name);
            try { Require(processes.Length == 0, "Isolated page test must not create a " + name + " process."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Log(string text) => File.AppendAllText(Report, text + "\n");
}
