// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.ViewModels;
using Kit.WingetLib;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Kit.Settings.UI.Views;

public sealed partial class WingetPage : NavigablePage, IRefreshablePage
{
    private readonly MainWindow settingsWindow;
    private bool currentPage;
    private bool disposed;
    private bool confirmationOpen;
    private bool compactFilters;
    private bool filtersExpanded;
    private bool narrowTableColumns;
    private bool resizingDetailsPane;
    private double inlinePaneLength = 420;
    private long openPaneLengthCallbackToken;
    private ContentDialog activeDialog;
    private WingetPackageRow detailsRow;
    private WingetWorkspace visibleWorkspace = WingetWorkspace.Updates;
    private PackageViewMode appliedListViewMode = PackageViewMode.Details;
    private double appliedWorkspaceWidth = double.NaN;

    public WingetPage()
    {
        ViewModel = new WingetViewModel(
            SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default),
            SettingsRepository<WingetSettings>.GetInstance(SettingsUtils.Default),
            ShellPage.SendDefaultIPCMessage);
        InitializeComponent();
        DataContext = ViewModel;
        settingsWindow = App.GetSettingsWindow();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        settingsWindow.Closed += OnWindowClosed;
        settingsWindow.AppWindow.Changed += OnWindowChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        WingetDetailsPane.CloseRequested += OnDetailsCloseRequested;
        WingetDetailsPane.CancelRequested += OnDetailsCancelRequested;
        WingetDetailsPane.RetryRequested += OnDetailsRetryRequested;
        WingetDetailsPane.ApplyVersionRequested += OnDetailsApplyVersionRequested;
        WingetDetailsHost.PaneClosed += OnDetailsPaneClosed;
        WingetDetailsHost.SizeChanged += OnDetailsHostSizeChanged;
        openPaneLengthCallbackToken = WingetDetailsHost.RegisterPropertyChangedCallback(
            SplitView.OpenPaneLengthProperty,
            OnDetailsOpenPaneLengthChanged);
        AddHandler(KeyDownEvent, new KeyEventHandler(OnPageKeyDown), handledEventsToo: true);
    }

    private enum PackageViewMode
    {
        Details = 0,
        Compact = 1,
        Grid = 2,
    }

    public WingetViewModel ViewModel { get; }

    public string ModuleTitle => "Winget/ModuleTitle".GetLocalized();

    public string ModuleDescription => "Winget/ModuleDescription".GetLocalized();

    public string Text(string name) => WingetViewModel.L(name);

    public Style BadgeStyle(string tone) => (Style)Resources["Winget" + tone + "BadgeStyle"];

    public Style GlyphStyle(string tone) => (Style)Resources["Winget" + tone + "GlyphStyle"];

    public string WorkspaceGlyph(int tab) => tab switch
    {
        (int)WingetWorkspace.Installed => "\uE770",
        (int)WingetWorkspace.Discover => "\uE774",
        _ => "\uE895",
    };

    public Style WorkspaceBadgeStyle(int tab) => BadgeStyle(tab == (int)WingetWorkspace.Installed ? "Success" : "Accent");

    public Style WorkspaceGlyphStyle(int tab) => GlyphStyle(tab == (int)WingetWorkspace.Installed ? "Success" : "Accent");

    public Visibility IsUpdatesTab(int index) => index == (int)WingetWorkspace.Updates ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsDiscoverTab(int index) => index == (int)WingetWorkspace.Discover ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowDiscoveryStart(int tab, bool hasSearchResults) => tab == (int)WingetWorkspace.Discover && !hasSearchResults ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowSearchToolbar(int tab, bool hasSearchResults) => tab == (int)WingetWorkspace.Discover && hasSearchResults ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowEmpty(int tab, bool hasRows, bool hasSearchResults) => !hasRows && (tab != (int)WingetWorkspace.Discover || hasSearchResults) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowTable(int tab, bool hasSearchResults, int view) => view != (int)PackageViewMode.Grid && (tab != (int)WingetWorkspace.Discover || hasSearchResults) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowList(int mode) => mode != (int)PackageViewMode.Grid ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowTiles(int mode) => mode == (int)PackageViewMode.Grid ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowNoItems(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool HasSelectionCount(int count) => count > 0;

    public bool CanShowManualCommand(bool hasSelection, string command) => hasSelection && !string.IsNullOrWhiteSpace(command);

    public bool ExceedsBatchLimit(int count) => count > 50;

    public Visibility ShowBatchLimit(int count) => count > 50 ? Visibility.Visible : Visibility.Collapsed;

    public void RefreshEnabledState() => ViewModel.RefreshEnabledState();

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        currentPage = true;
        RefreshEnabledState();
        UpdateVisibility();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        currentPage = false;
        activeDialog?.Hide();
        CloseDetailsPane();
        FiltersFlyout.Hide();
        ViewModel.SetPageActive(false);
        base.OnNavigatedFrom(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateVisibility();
        UpdateWorkspaceLayout();
        ResizeDetailsPane();
    }

    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        var workspace = (WingetWorkspace)ViewModel.ActiveTabIndex;
        if (workspace != visibleWorkspace)
        {
            visibleWorkspace = workspace;
            activeDialog?.Hide();
            CloseDetailsPane();
            FiltersFlyout.Hide();
            PrimaryActionButton.Icon = new SymbolIcon(workspace switch
            {
                WingetWorkspace.Installed => Symbol.Delete,
                WingetWorkspace.Discover => Symbol.Download,
                _ => Symbol.Sync,
            });
        }

        ApplyListItemTemplate();

        if (WingetDetailsHost.IsPaneOpen)
        {
            if (ViewModel.ActiveSelection == null || !ReferenceEquals(detailsRow, ViewModel.ActiveSelection))
            {
                CloseDetailsPane();
            }
            else
            {
                WingetDetailsPane.UpdateFromViewModel(ViewModel);
            }
        }
    }

    private void ApplyListItemTemplate()
    {
        var viewMode = (PackageViewMode)ViewModel.ViewModeIndex;
        if (viewMode == PackageViewMode.Grid)
        {
            return;
        }

        string key = (viewMode, narrowTableColumns) switch
        {
            (PackageViewMode.Compact, true) => "NarrowCompactPackageTemplate",
            (PackageViewMode.Compact, false) => "CompactPackageTemplate",
            (_, true) => "NarrowDetailedPackageTemplate",
            _ => "DetailedPackageTemplate",
        };
        var template = (DataTemplate)Resources[key];
        if (!ReferenceEquals(PackagesList.ItemTemplate, template))
        {
            PackagesList.ItemTemplate = template;
        }

        appliedListViewMode = viewMode;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateWorkspaceLayout();

    private void UpdateWorkspaceLayout()
    {
        if (WorkspaceBody == null || WingetSettingsExpander == null || WingetSections == null)
        {
            return;
        }

        // The header, settings expander and workspace share the same finite Grid width.
        // Explicit child widths prevent long IDs or CommandBar overflow from inflating DesiredSize.
        double width = Math.Max(0, WingetViewport.ActualWidth - WingetViewport.Padding.Left - WingetViewport.Padding.Right);
        if (width <= 0 || Math.Abs(width - appliedWorkspaceWidth) < 0.5)
        {
            return;
        }

        PackageWorkspaceGrid.Width = width;
        WingetSettingsExpander.Width = width;
        appliedWorkspaceWidth = width;
        UpdateFiltersLayout(width);

        bool narrow = width < 760;
        if (narrow != narrowTableColumns)
        {
            narrowTableColumns = narrow;
            HeaderIdColumn.Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
            HeaderSourceColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            HeaderIdButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            HeaderSourceButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            ApplyListItemTemplate();
        }
    }

    private void FiltersToggle_Changed(object sender, RoutedEventArgs e)
    {
        filtersExpanded = FiltersToggleButton.IsChecked == true;
        if (appliedWorkspaceWidth > 0)
        {
            UpdateFiltersLayout(appliedWorkspaceWidth);
        }
    }

    private void UpdateFiltersLayout(double width)
    {
        bool compact = width < 900;
        if (compact != compactFilters)
        {
            FiltersFlyout.Hide();
            if (compact)
            {
                FiltersHost.Children.Remove(FiltersPanel);
                FiltersFlyout.Content = FiltersPanel;
            }
            else
            {
                FiltersFlyout.Content = null;
                FiltersHost.Children.Add(FiltersPanel);
            }

            compactFilters = compact;
        }

        FiltersPanel.Width = compact ? Math.Min(280, Math.Max(220, width - 48)) : double.NaN;
        FiltersPanel.MaxHeight = compact ? 500 : double.PositiveInfinity;
        bool showSidebar = !compact && filtersExpanded;
        FiltersHost.Visibility = showSidebar ? Visibility.Visible : Visibility.Collapsed;
        FilterColumn.Width = new GridLength(showSidebar ? 190 : 0);
        WorkspaceBody.ColumnSpacing = showSidebar ? 12 : 0;
        CompactFiltersButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        FiltersToggleButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        string label = Text(filtersExpanded ? "HideFilters" : "ShowFilters");
        FiltersToggleLabel.Text = label;
        AutomationProperties.SetName(FiltersToggleButton, label);
    }

    private void OnDetailsHostSizeChanged(object sender, SizeChangedEventArgs e) => ResizeDetailsPane();

    private void OnDetailsOpenPaneLengthChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (!resizingDetailsPane && WingetDetailsHost.DisplayMode == SplitViewDisplayMode.Inline && WingetDetailsHost.OpenPaneLength >= 340)
        {
            inlinePaneLength = Math.Clamp(WingetDetailsHost.OpenPaneLength, 340, 620);
        }
    }

    private void ResizeDetailsPane()
    {
        if (WingetDetailsHost.ActualWidth <= 0)
        {
            return;
        }

        resizingDetailsPane = true;
        try
        {
            bool isInline = WingetDetailsHost.ActualWidth >= 1080;
            WingetDetailsHost.DisplayMode = isInline ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
            WingetDetailsHost.OpenPaneLength = WingetDetailsHost.ActualWidth < 640
                ? WingetDetailsHost.ActualWidth
                : isInline
                    ? Math.Clamp(inlinePaneLength, 340, Math.Min(620, WingetDetailsHost.ActualWidth * 0.42))
                    : 420;
            VisualStateManager.GoToState(this, isInline ? "DetailsPaneInline" : "DetailsPaneOverlay", useTransitions: false);
        }
        finally
        {
            resizingDetailsPane = false;
        }
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && WingetDetailsHost.IsPaneOpen)
        {
            e.Handled = true;
            CloseDetailsPane();
        }
    }

    private void OnDetailsCloseRequested(object sender, EventArgs e) => CloseDetailsPane();

    private void OnDetailsCancelRequested(object sender, EventArgs e) => Cancel_Click(sender, null);

    private void OnDetailsPaneClosed(SplitView sender, object args)
    {
        detailsRow = null;
        WingetDetailsPane.ClearBoundRow();
    }

    private async void OnDetailsRetryRequested(object sender, EventArgs e) => await LoadDialogDetailsAsync();

    private async void OnDetailsApplyVersionRequested(object sender, string version)
    {
        if (currentPage && detailsRow != null && ReferenceEquals(detailsRow, ViewModel.ActiveSelection) && !string.IsNullOrWhiteSpace(version))
        {
            try
            {
                await ViewModel.SelectVersionAsync(version);
            }
            catch (Exception exception)
            {
                ViewModel.ReportActionError(exception);
            }
        }
    }

    private void CloseDetailsPane()
    {
        if (WingetDetailsHost.IsPaneOpen)
        {
            WingetDetailsHost.IsPaneOpen = false;
        }

        detailsRow = null;
        WingetDetailsPane.ClearBoundRow();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.SetPageActive(false);

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs e) => UpdateVisibility();

    private void UpdateVisibility()
    {
        if (settingsWindow == null)
        {
            return;
        }

        bool minimized = settingsWindow.AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized;
        ViewModel.SetPageActive(!disposed && currentPage && IsLoaded && settingsWindow.AppWindow.IsVisible && !minimized);
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        if (e.Handled)
        {
            UpdateVisibility();
            return;
        }

        disposed = true;
        currentPage = false;
        activeDialog?.Hide();
        CloseDetailsPane();
        FiltersFlyout.Hide();
        if (openPaneLengthCallbackToken != 0)
        {
            WingetDetailsHost.UnregisterPropertyChangedCallback(SplitView.OpenPaneLengthProperty, openPaneLengthCallbackToken);
            openPaneLengthCallbackToken = 0;
        }

        WingetDetailsPane.CloseRequested -= OnDetailsCloseRequested;
        WingetDetailsPane.CancelRequested -= OnDetailsCancelRequested;
        WingetDetailsPane.RetryRequested -= OnDetailsRetryRequested;
        WingetDetailsPane.ApplyVersionRequested -= OnDetailsApplyVersionRequested;
        WingetDetailsHost.PaneClosed -= OnDetailsPaneClosed;
        WingetDetailsHost.SizeChanged -= OnDetailsHostSizeChanged;
        RemoveHandler(KeyDownEvent, new KeyEventHandler(OnPageKeyDown));
        settingsWindow.Closed -= OnWindowClosed;
        settingsWindow.AppWindow.Changed -= OnWindowChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ReloadAsync();
        }
        catch (Exception ex)
        {
            ManagedCommon.Logger.LogError("Settings action Reload_Click failed.", ex);
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.IsQueueRunning)
            {
                await ViewModel.CancelQueueAsync();
            }
            else
            {
                await ViewModel.RequestAsync("cancel");
            }
        }
        catch (Exception ex)
        {
            ManagedCommon.Logger.LogError("Settings action Cancel_Click failed.", ex);
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.CanSearch)
            {
                await ViewModel.RequestAsync("search");
            }
        }
        catch (Exception ex)
        {
            ManagedCommon.Logger.LogError("Settings action Search_Click failed.", ex);
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            if (e.Key == VirtualKey.Enter && ViewModel.CanSearch)
            {
                e.Handled = true;
                await ViewModel.RequestAsync("search");
            }
        }
        catch (Exception ex)
        {
            ManagedCommon.Logger.LogError("Settings action SearchBox_KeyDown failed.", ex);
        }
    }

    private void SortColumn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }
            && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int columnIndex))
        {
            ViewModel.ToggleSortColumn(columnIndex);
        }
    }

    private void Package_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WingetPackageRow row)
        {
            ViewModel.ActiveSelection = row;
            if (WingetDetailsHost.IsPaneOpen)
            {
                OpenDetailsPane();
            }
        }
    }

    private void PackageRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WingetPackageRow row } element)
        {
            return;
        }

        ViewModel.ActiveSelection = row;
        var flyout = new MenuFlyout();

        var detailsItem = new MenuFlyoutItem
        {
            Text = Text("DetailsAction/Label"),
            Icon = new SymbolIcon(Symbol.Document),
            IsEnabled = ViewModel.CanOpenDetails,
        };
        detailsItem.Click += (_, _) => OpenDetailsPane();
        flyout.Items.Add(detailsItem);

        var toggleItem = new MenuFlyoutItem
        {
            Text = Text("SelectPackageAction"),
            Icon = new SymbolIcon(Symbol.Accept),
        };
        toggleItem.Click += (_, _) => row.IsSelected = !row.IsSelected;
        flyout.Items.Add(toggleItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyIdItem = new MenuFlyoutItem
        {
            Text = Text("CopyIdAction"),
            Icon = new SymbolIcon(Symbol.Copy),
        };
        copyIdItem.Click += (_, _) => CopyTextToClipboard(row.Id);
        flyout.Items.Add(copyIdItem);

        string command = ViewModel.ManualCommand;
        if (!string.IsNullOrWhiteSpace(command))
        {
            var copyCommandItem = new MenuFlyoutItem
            {
                Text = Text("CopyManualCommandAction"),
                Icon = new FontIcon { Glyph = "\uE756" },
            };
            copyCommandItem.Click += (_, _) => CopyTextToClipboard(command);
            flyout.Items.Add(copyCommandItem);
        }

        flyout.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private static void CopyTextToClipboard(string value)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(value);
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            ManagedCommon.Logger.LogError("Clipboard copy failed on WingetPage.", ex);
        }
    }

    private void SelectAllPackages_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAllVisible(ViewModel.IsAllVisibleSelected != true);

    private void Package_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OpenDetailsPane();

    private void Details_Click(object sender, RoutedEventArgs e) => OpenDetailsPane();

    private void OpenDetailsPane()
    {
        var selected = ViewModel.ActiveSelection;
        if (selected == null || disposed)
        {
            return;
        }

        detailsRow = selected;
        ResizeDetailsPane();
        WingetDetailsPane.UpdateFromViewModel(ViewModel);
        WingetDetailsHost.IsPaneOpen = true;

        if (!ViewModel.HasDetails && ViewModel.CanActOnPackage)
        {
            _ = LoadDialogDetailsAsync();
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!disposed && WingetDetailsHost.IsPaneOpen)
            {
                WingetDetailsPane.FocusCloseButton();
            }
        });
    }

    private async void PrimaryAction_Click(object sender, RoutedEventArgs e) => await RunDialogActionAsync(async () =>
    {
        if (!ViewModel.CanPrepareSelection)
        {
            return;
        }

        int tab = ViewModel.ActiveTabIndex;
        var prepared = await ViewModel.PrepareSelectionAsync();
        if (prepared == null || disposed || !currentPage || tab != ViewModel.ActiveTabIndex)
        {
            return;
        }

        await ShowPreparedConfirmationAsync(prepared);
    });

    private async Task ShowPreparedConfirmationAsync(WingetPreparedOperation prepared)
    {
        var dialog = new WingetConfirmationDialog(prepared, DescribeOptions(prepared.Options));
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || disposed || !currentPage)
        {
            return;
        }

        if (!ViewModel.PreparedSelectionMatches(prepared))
        {
            ViewModel.ReportActionError(new InvalidOperationException(Text("ConfirmationChanged")));
            return;
        }

        await ViewModel.SubmitPreparedAsync(prepared);
    }

    private async Task LoadDialogDetailsAsync()
    {
        try
        {
            await ViewModel.LoadDetailsAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionError(ex);
        }
    }

    private async void Manual_Click(object sender, RoutedEventArgs e) => await RunDialogActionAsync(async () =>
    {
        if (ViewModel.ActiveSelection == null || string.IsNullOrWhiteSpace(ViewModel.ManualCommand))
        {
            return;
        }

        var content = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 640,
        };
        content.Children.Add(Paragraph(Text("ManualCommandExplanation")));
        var command = new TextBox
        {
            Text = ViewModel.ManualCommand,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["KitMonoFontFamily"],
        };
        content.Children.Add(command);
        var copy = new Button
        {
            Content = Text("CopyCommand/Content"),
        };
        copy.Click += (_, _) => CopyTextToClipboard(command.Text);
        content.Children.Add(copy);
        await ShowDialogAsync(MakeDialog(Text("AdvancedExpander/Header"), ScrollContent(content)));
    });

    private async void Options_Click(object sender, RoutedEventArgs e) => await RunDialogActionAsync(async () =>
    {
        int tab = ViewModel.ActiveTabIndex;
        var dialog = new WingetOptionsDialog(ViewModel, tab == (int)WingetWorkspace.Installed);
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && currentPage && tab == ViewModel.ActiveTabIndex)
        {
            ViewModel.SilentInstall = dialog.SilentInstall;
            ViewModel.InstallScopeIndex = dialog.ScopeIndex;
            if (tab != (int)WingetWorkspace.Installed)
            {
                ViewModel.InstallArchitectureIndex = dialog.ArchitectureIndex;
                ViewModel.InstallLocation = dialog.InstallLocation;
            }
        }
    });

    private async void Ignore_Click(object sender, RoutedEventArgs e) => await RunDialogActionAsync(async () =>
    {
        if (ViewModel.ActiveTabIndex != (int)WingetWorkspace.Updates)
        {
            return;
        }

        var selectedPackages = ViewModel.DisplayPackages.Where(row => row.IsSelected)
            .Select(row => new WingetPackageIdentity(row.Id, row.Source, row.Package.InstalledVersion, row.Package.AvailableVersion)).ToArray();
        var mode = new ComboBox
        {
            Header = Text("IgnoreScope"),
            ItemsSource = new[] { Text("IgnoreThisVersion"), Text("IgnoreAllVersions") },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var dialog = MakeDialog(Text("IgnoreScope"), mode);
        dialog.PrimaryButtonText = Text("ApplyOptions");
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && currentPage)
        {
            await ViewModel.SetIgnoredAsync(selectedPackages, true, mode.SelectedIndex == 0);
        }
    });

    private async void ManageIgnored_Click(object sender, RoutedEventArgs e) => await RunDialogActionAsync(async () =>
    {
        var content = new StackPanel
        {
            Spacing = 8,
            MaxWidth = 640,
        };
        var choices = new List<(CheckBox Box, WingetPackageIdentity Identity)>();
        foreach (var item in ViewModel.IgnoredPackages)
        {
            var box = new CheckBox
            {
                Content = Paragraph(item.Id + " · " + item.Source + " · " + (item.IgnoreOnlyThisVersion ? item.AvailableVersion : Text("IgnoreAllVersions"))),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            choices.Add((box, item));
            content.Children.Add(box);
        }

        if (choices.Count == 0)
        {
            content.Children.Add(Paragraph(Text("NoIgnoredPackages")));
        }

        var dialog = MakeDialog(Text("ManageIgnoredTitle"), ScrollContent(content));
        dialog.PrimaryButtonText = Text("RestoreSelected");
        dialog.IsPrimaryButtonEnabled = false;
        foreach (var choice in choices)
        {
            choice.Box.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = choices.Any(item => item.Box.IsChecked == true);
            choice.Box.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = choices.Any(item => item.Box.IsChecked == true);
        }

        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && currentPage)
        {
            await ViewModel.SetIgnoredAsync(choices.Where(item => item.Box.IsChecked == true).Select(item => item.Identity).ToArray(), false);
        }
    });

    private async void Activity_Click(object sender, RoutedEventArgs e) => await RunDialogActionAsync(async () =>
    {
        var dialog = new WingetActivityDialog(ViewModel);
        await ShowDialogAsync(dialog);
    });

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) => await RunDialogActionAsync(() => ExportAsync(false));

    private async void ExportBundle_Click(object sender, RoutedEventArgs e) => await RunDialogActionAsync(() => ExportAsync(true));

    private async Task ExportAsync(bool bundle)
    {
        if (disposed || !currentPage || (bundle ? ViewModel.SelectedCount == 0 : !ViewModel.HasRows))
        {
            return;
        }

        try
        {
            string contents = bundle ? ViewModel.CreateBundleJson() : ViewModel.CreateCsv();
            var picker = new FileSavePicker
            {
                SuggestedFileName = bundle ? "Kit-WinGet-bundle" : "Kit-WinGet-packages",
            };
            picker.FileTypeChoices.Add(bundle ? Text("BundleFileType") : Text("CsvFileType"), new List<string> { bundle ? ".json" : ".csv" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow));
            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                await FileIO.WriteTextAsync(file, contents);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionError(ex);
        }
    }

    private Task RunDialogActionAsync(Func<Task> action)
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue));
            }

            return RunDialogActionCoreAsync(action);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private async Task RunDialogActionCoreAsync(Func<Task> action)
    {
        if (confirmationOpen || disposed || !currentPage)
        {
            return;
        }

        confirmationOpen = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionError(ex);
        }
        finally
        {
            confirmationOpen = false;
        }
    }

    private ContentDialog MakeDialog(string title, object content) => new()
    {
        XamlRoot = XamlRoot,
        RequestedTheme = ActualTheme,
        Title = title,
        Content = content,
        CloseButtonText = Text("CancelText"),
        DefaultButton = ContentDialogButton.Close,
    };

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (activeDialog != null)
        {
            return ContentDialogResult.None;
        }

        dialog.XamlRoot = XamlRoot;
        dialog.RequestedTheme = ActualTheme;
        activeDialog = dialog;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(activeDialog, dialog))
            {
                activeDialog = null;
            }
        }
    }

    private static TextBlock Paragraph(string text, bool strong = false) => new()
    {
        Text = text ?? string.Empty,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
    };

    private static ScrollViewer ScrollContent(UIElement content) => new()
    {
        Content = content,
        MaxHeight = 500,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    private string DescribeOptions(WingetPackageOptions options) => Text("ScopeOption") + ": " + (options.Scope ?? Text("OptionDefault")) + " · " + Text("ArchitectureOption") + ": " + (options.Architecture ?? Text("OptionDefault")) + " · " + Text("SilentOption") + ": " + (options.Silent ? Text("OptionOn") : Text("OptionOff"))
        + (options.InstallLocation is null ? string.Empty : " · " + Text("InstallLocation") + ": " + options.InstallLocation);

    private void ResetFilters_Click(object sender, RoutedEventArgs e) => ViewModel.ResetFilters();
}
