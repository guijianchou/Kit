// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
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
    private ContentDialog activeDialog;
    private WingetWorkspace visibleWorkspace = WingetWorkspace.Updates;
    private PackageViewMode appliedListViewMode = PackageViewMode.Details;
    private double appliedWorkspaceWidth = double.NaN;

    public WingetPage()
    {
        ViewModel = new WingetViewModel(SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default), SettingsRepository<WingetSettings>.GetInstance(SettingsUtils.Default), ShellPage.SendDefaultIPCMessage);
        InitializeComponent();
        DataContext = ViewModel;
        settingsWindow = App.GetSettingsWindow();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        settingsWindow.Closed += OnWindowClosed;
        settingsWindow.AppWindow.Changed += OnWindowChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private enum PackageViewMode
    {
        Details = 0,
        Compact = 1,
        Grid = 2,
    }

    public WingetViewModel ViewModel { get; }

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

    public double ProgressOpacity(bool busy) => busy ? 1 : 0;

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
        FiltersFlyout.Hide();
        ViewModel.SetPageActive(false);
        base.OnNavigatedFrom(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateVisibility();
        UpdateWorkspaceLayout();
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
            FiltersFlyout.Hide();
            PrimaryActionButton.Icon = new SymbolIcon(workspace switch
            {
                WingetWorkspace.Installed => Symbol.Delete,
                WingetWorkspace.Discover => Symbol.Download,
                _ => Symbol.Sync,
            });
        }

        var viewMode = (PackageViewMode)ViewModel.ViewModeIndex;
        if (viewMode != PackageViewMode.Grid && viewMode != appliedListViewMode)
        {
            var template = (DataTemplate)Resources[viewMode == PackageViewMode.Compact ? "CompactPackageTemplate" : "DetailedPackageTemplate"];
            if (!ReferenceEquals(PackagesList.ItemTemplate, template))
            {
                PackagesList.ItemTemplate = template;
            }

            appliedListViewMode = viewMode;
        }
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateWorkspaceLayout();

    private void UpdateWorkspaceLayout()
    {
        if (WorkspaceBody == null || WingetEnableCard == null)
        {
            return;
        }

        // The enable card and workspace share the same finite Grid cell. Explicit
        // child constraints also prevent long IDs or CommandBar overflow from
        // feeding a larger DesiredSize back into that cell.
        double width = Math.Max(0, WingetViewport.ActualWidth - WingetViewport.Padding.Left - WingetViewport.Padding.Right);

        // Reserve space for the page header, workspace commands, table header
        // and visible package rows; only preferences scroll on short windows.
        AutomaticSettingsScroll.MaxHeight = Math.Clamp(WingetViewport.ActualHeight - 540, 96, 280);
        if (width <= 0 || Math.Abs(width - appliedWorkspaceWidth) < 0.5)
        {
            return;
        }

        PackageWorkspaceGrid.Width = width;
        WingetEnableCard.Width = width;
        WingetSettingsExpander.Width = width;
        appliedWorkspaceWidth = width;
        bool compact = width < 900;
        if (compact != compactFilters)
        {
            FiltersFlyout.Hide();
            if (compact)
            {
                FiltersHost.Children.Remove(FiltersPanel);
                FiltersPanel.Width = Math.Min(280, Math.Max(220, width - 48));
                FiltersPanel.MaxHeight = 500;
                FiltersFlyout.Content = FiltersPanel;
            }
            else
            {
                FiltersFlyout.Content = null;
                FiltersPanel.Width = double.NaN;
                FiltersPanel.MaxHeight = double.PositiveInfinity;
                FiltersHost.Children.Add(FiltersPanel);
            }

            compactFilters = compact;
        }

        FilterColumn.Width = new GridLength(compact ? 0 : 190);
        WorkspaceBody.ColumnSpacing = compact ? 0 : 12;
        CompactFiltersButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        AutomaticSettingsLabel.Visibility = width < 650 ? Visibility.Collapsed : Visibility.Visible;

        bool stackedFooter = width < 650;
        AutomaticSettingsFooter.ColumnDefinitions[1].Width = stackedFooter ? new GridLength(0) : GridLength.Auto;
        AutomaticSettingsFooter.ColumnSpacing = stackedFooter ? 0 : 16;
        AutomaticSettingsFooter.RowSpacing = stackedFooter ? 8 : 0;
        Grid.SetColumn(WingetHelpLink, stackedFooter ? 0 : 1);
        Grid.SetRow(WingetHelpLink, stackedFooter ? 1 : 0);
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
        FiltersFlyout.Hide();
        settingsWindow.Closed -= OnWindowClosed;
        settingsWindow.AppWindow.Changed -= OnWindowChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ViewModel.ReloadAsync();

    private async void Cancel_Click(object sender, RoutedEventArgs e)
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

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanSearch)
        {
            await ViewModel.RequestAsync("search");
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.CanSearch)
        {
            e.Handled = true;
            await ViewModel.RequestAsync("search");
        }
    }

    private void Package_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WingetPackageRow row)
        {
            ViewModel.ActiveSelection = row;
        }
    }

    private void SelectAllPackages_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAllVisible(ViewModel.IsAllVisibleSelected != true);

    private async void Package_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await RunDialogActionAsync(ShowDetailsAsync);

    private async void Details_Click(object sender, RoutedEventArgs e) => await RunDialogActionAsync(ShowDetailsAsync);

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
        var content = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 640,
        };
        content.Children.Add(Paragraph(Text(prepared.Kind == "uninstall" ? "BatchUninstallExplanation" : "BatchInstallExplanation")));
        content.Children.Add(Paragraph(Text("ElevationConfirmation")));
        content.Children.Add(Paragraph(DescribeOptions(prepared.Options)));
        foreach (var details in prepared.Packages)
        {
            var item = new StackPanel
            {
                Spacing = 5,
            };
            item.Children.Add(Paragraph(details.Package.Name, true));
            item.Children.Add(Paragraph(details.Package.Id + " · " + details.Package.Source));
            item.Children.Add(Paragraph(details.Package.InstalledVersion + (prepared.Kind == "uninstall" ? string.Empty : "  →  " + details.Package.AvailableVersion)));
            if (!string.IsNullOrWhiteSpace(details.License))
            {
                item.Children.Add(Paragraph(Text("LicenseLabel") + ": " + details.License));
            }

            foreach (var agreement in details.Agreements)
            {
                item.Children.Add(Paragraph(agreement.Label + Environment.NewLine + agreement.Text + Environment.NewLine + agreement.Url));
            }

            content.Children.Add(new Border { BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 12), Child = item });
        }

        var dialog = MakeDialog(Text(prepared.Kind == "uninstall" ? "BatchUninstallTitle" : prepared.Kind == "upgrade" ? "BatchUpgradeTitle" : "BatchInstallTitle"), ScrollContent(content));
        dialog.PrimaryButtonText = Text(prepared.Packages.Any(item => item.Agreements.Count > 0) ? "AcceptContinue" : "Continue");
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

    private async Task ShowDetailsAsync()
    {
        var selected = ViewModel.ActiveSelection;
        if (selected == null)
        {
            return;
        }

        var content = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 640,
        };
        var identity = new Grid { ColumnSpacing = 12 };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.Children.Add(new Border
        {
            Style = BadgeStyle("Accent"),
            Child = new FontIcon { Glyph = "\uE7B8", Style = GlyphStyle("Accent") },
        });
        var identityText = BoundParagraph(nameof(ViewModel.SelectionIdentity));
        identityText.VerticalAlignment = VerticalAlignment.Center;
        identityText.FontSize = 12;
        Grid.SetColumn(identityText, 1);
        identity.Children.Add(identityText);
        content.Children.Add(identity);
        var loading = new StackPanel { Name = "DetailsLoadingPanel", Spacing = 10 };
        var progress = new ProgressRing { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left };
        loading.Children.Add(progress);
        loading.Children.Add(Paragraph(Text("Phase_LoadingDetails")));
        var cancel = new Button { Name = "DetailsCancelButton", Content = Text("Cancel/Content") };
        cancel.Click += Cancel_Click;
        loading.Children.Add(cancel);
        content.Children.Add(loading);

        var error = new StackPanel { Name = "DetailsErrorPanel", Spacing = 10 };
        error.Children.Add(BoundParagraph(nameof(ViewModel.DetailsErrorText)));
        var retry = new Button { Name = "DetailsRetryButton", Content = Text("RetryDetails") };
        retry.Click += async (_, _) => await LoadDialogDetailsAsync();
        error.Children.Add(retry);
        content.Children.Add(error);

        var detailsContent = new StackPanel { Name = "DetailsContentPanel", Spacing = 12 };
        var versions = BoundParagraph(nameof(ViewModel.SelectionVersions));
        versions.ClearValue(TextBlock.FontWeightProperty);
        versions.Style = (Style)Resources["WingetVersionTextStyle"];
        var overview = new StackPanel { Spacing = 8 };
        overview.Children.Add(versions);
        overview.Children.Add(BoundParagraph(nameof(ViewModel.SelectionPublisher)));
        overview.Children.Add(BoundParagraph(nameof(ViewModel.PackageDescription)));
        detailsContent.Children.Add(DetailSurface(overview));
        detailsContent.Children.Add(DetailSurface(BoundParagraph(nameof(ViewModel.InstallExplanation))));
        var homepage = new HyperlinkButton { Content = Text("PackageHomepage"), Padding = new Thickness(0) };
        var license = Paragraph(string.Empty);
        var licenseLink = new HyperlinkButton { Content = Text("LicenseLink"), Padding = new Thickness(0) };
        var metadata = new StackPanel { Spacing = 6 };
        metadata.Children.Add(homepage);
        metadata.Children.Add(license);
        metadata.Children.Add(licenseLink);
        detailsContent.Children.Add(metadata);
        var releaseNotes = new StackPanel { Spacing = 8 };
        var releaseHeading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        releaseHeading.Children.Add(new FontIcon { Glyph = "\uE8A5", Style = GlyphStyle("Accent") });
        releaseHeading.Children.Add(Paragraph(Text("ReleaseNotesExpander/Header"), true));
        releaseNotes.Children.Add(releaseHeading);
        releaseNotes.Children.Add(BoundParagraph(nameof(ViewModel.ReleaseNotes)));
        var link = new HyperlinkButton { Content = Text("ReleaseLink/Content"), Padding = new Thickness(0) };
        releaseNotes.Children.Add(link);
        detailsContent.Children.Add(DetailSurface(releaseNotes));
        content.Children.Add(detailsContent);
        var dialog = MakeDialog(selected.Name, ScrollContent(content));
        dialog.CloseButtonText = Text("CloseDetails");

        void UpdateDetailsState()
        {
            if (!ReferenceEquals(selected, ViewModel.ActiveSelection))
            {
                dialog.Hide();
                return;
            }

            loading.Visibility = ViewModel.IsLoadingDetails ? Visibility.Visible : Visibility.Collapsed;
            progress.IsActive = ViewModel.IsLoadingDetails;
            cancel.IsEnabled = ViewModel.CanCancelOperation;
            error.Visibility = ViewModel.HasDetailsError ? Visibility.Visible : Visibility.Collapsed;
            retry.IsEnabled = ViewModel.CanRetryDetails;
            detailsContent.Visibility = ViewModel.HasDetails ? Visibility.Visible : Visibility.Collapsed;
            homepage.NavigateUri = WingetViewModel.SafeWebUri(ViewModel.SelectedDetails?.PackageUrl);
            homepage.Visibility = homepage.NavigateUri != null ? Visibility.Visible : Visibility.Collapsed;
            license.Text = string.IsNullOrWhiteSpace(ViewModel.SelectedDetails?.License) ? string.Empty : Text("LicenseLabel") + ": " + ViewModel.SelectedDetails.License;
            license.Visibility = string.IsNullOrEmpty(license.Text) ? Visibility.Collapsed : Visibility.Visible;
            licenseLink.NavigateUri = WingetViewModel.SafeWebUri(ViewModel.SelectedDetails?.LicenseUrl);
            licenseLink.Visibility = licenseLink.NavigateUri != null ? Visibility.Visible : Visibility.Collapsed;
            link.NavigateUri = WingetViewModel.SafeWebUri(ViewModel.SelectedDetails?.Package.ReleaseNotesUrl);
            link.Visibility = link.NavigateUri != null ? Visibility.Visible : Visibility.Collapsed;
        }

        PropertyChangedEventHandler changed = (_, _) => UpdateDetailsState();
        ViewModel.PropertyChanged += changed;
        try
        {
            UpdateDetailsState();
            var showing = ShowDialogAsync(dialog);

            // Open immediately. Bindings update this same dialog when metadata
            // arrives; closing it does not hold the page's dialog-action guard.
            if (!ViewModel.HasDetails && ViewModel.CanActOnPackage)
            {
                _ = LoadDialogDetailsAsync();
            }

            await showing;
        }
        finally
        {
            ViewModel.PropertyChanged -= changed;
            cancel.Click -= Cancel_Click;
        }
    }

    private Border DetailSurface(UIElement child) => new()
    {
        Style = (Style)Resources["WingetDetailSurfaceStyle"],
        Child = child,
    };

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
        };
        content.Children.Add(command);
        var copy = new Button
        {
            Content = Text("CopyCommand/Content"),
        };
        copy.Click += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(command.Text);
            Clipboard.SetContent(data);
        };
        content.Children.Add(copy);
        await ShowDialogAsync(MakeDialog(Text("AdvancedExpander/Header"), ScrollContent(content)));
    });

    private async void Options_Click(object sender, RoutedEventArgs e) => await RunDialogActionAsync(async () =>
    {
        int tab = ViewModel.ActiveTabIndex;
        var content = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 480,
        };
        var silent = new CheckBox
        {
            Content = Text("SilentOption"),
            IsChecked = ViewModel.SilentInstall,
        };
        var scope = new ComboBox
        {
            Header = Text("ScopeOption"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[]
            {
                Text("OptionDefault"),
                Text("ScopeUser"),
                Text("ScopeMachine"),
            },
            SelectedIndex = ViewModel.InstallScopeIndex,
        };
        var architecture = new ComboBox
        {
            Header = Text("ArchitectureOption"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[]
            {
                Text("OptionDefault"),
                "x64",
                "x86",
                "ARM64",
            },
            SelectedIndex = ViewModel.InstallArchitectureIndex,
        };
        content.Children.Add(silent);
        content.Children.Add(scope);
        if (tab != (int)WingetWorkspace.Installed)
        {
            content.Children.Add(architecture);
        }

        content.Children.Add(Paragraph(Text("OptionsExplanation")));
        var dialog = MakeDialog(Text("OptionsTitle"), content);
        dialog.PrimaryButtonText = Text("ApplyOptions");
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && currentPage && tab == ViewModel.ActiveTabIndex)
        {
            ViewModel.SilentInstall = silent.IsChecked == true;
            ViewModel.InstallScopeIndex = scope.SelectedIndex;
            if (tab != (int)WingetWorkspace.Installed)
            {
                ViewModel.InstallArchitectureIndex = architecture.SelectedIndex;
            }
        }
    });

    private async void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveTabIndex != (int)WingetWorkspace.Updates)
        {
            return;
        }

        try
        {
            await ViewModel.IgnoreSelectedAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionError(ex);
        }
    }

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
                Content = Paragraph(item.Id + " · " + item.Source),
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
        var content = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 640,
        };
        content.Children.Add(Paragraph(Text("QueueTitle"), true));
        content.Children.Add(new ItemsControl { ItemsSource = ViewModel.QueueItems, ItemTemplate = (DataTemplate)Resources["QueueTemplate"] });
        content.Children.Add(Paragraph(Text("HistoryTitle"), true));
        content.Children.Add(new ItemsControl { ItemsSource = ViewModel.HistoryItems, ItemTemplate = (DataTemplate)Resources["QueueTemplate"] });
        content.Children.Add(BoundParagraph(nameof(ViewModel.Activity)));
        await ShowDialogAsync(MakeDialog(Text("Activity/Header"), ScrollContent(content)));
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

    private TextBlock BoundParagraph(string propertyName)
    {
        var paragraph = Paragraph(string.Empty);
        paragraph.SetBinding(TextBlock.TextProperty, new Binding
        {
            Source = ViewModel,
            Path = new PropertyPath(propertyName),
            Mode = BindingMode.OneWay,
        });
        return paragraph;
    }

    private static ScrollViewer ScrollContent(UIElement content) => new()
    {
        Content = content,
        MaxHeight = 500,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    private string DescribeOptions(WingetPackageOptions options) => Text("ScopeOption") + ": " + (options.Scope ?? Text("OptionDefault")) + " · " + Text("ArchitectureOption") + ": " + (options.Architecture ?? Text("OptionDefault")) + " · " + Text("SilentOption") + ": " + (options.Silent ? Text("OptionOn") : Text("OptionOff"));
}
