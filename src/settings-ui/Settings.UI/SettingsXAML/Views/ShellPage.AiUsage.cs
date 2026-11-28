// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Controls;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.Storage;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Kit.Settings.UI.Views;

public sealed partial class ShellPage
{
    private const double AiUsageFlyoutWidth = 456;
    private const double AiUsageFlyoutDefaultHeight = 636;

    private static readonly string[] ModelPaletteBrushKeys =
    [
        "KitUsageCpuBrush",
        "KitUsageDiskBrush",
        "KitUsageMemoryBrush",
        "KitDataSeriesUdpBrush",
        "KitUsageGpuBrush",
        "KitDataSeriesTcpBrush",
    ];

    private readonly object _aiUsageGate = new();
    private readonly AiUsageStore _aiUsageFallbackStore = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _aiUsageAccessibility = new();
    private AiDiagnosticsStore? _aiUsageStore;
    private AiUsageStore? _aiUsagePersistentStore;
    private AiSessionActivityTracker? _aiUsageActivityTracker;
    private AiUsageTranslucentBackdrop? _aiUsageBackdrop;
    private AppWindow? _aiUsageWindow;
    private DispatcherQueueTimer? _aiUsageLiveTimer;
    private AiDiagnosticsSnapshot? _aiUsageSnapshot;
    private AiUsageOverview? _aiUsageOverview;
    private AiSessionActivitySnapshot? _aiUsageActivity;
    private string _aiUsageActiveKernel = AiKernelCatalog.Codex;
    private AiUsageTimeRange _aiUsageRange = AiUsageTimeRange.Today;
    private AiUsageTab _aiUsageTab = AiUsageTab.Overview;
    private AiUsageUpdate? _aiUsagePending;
    private bool _aiUsageActive;
    private bool _aiUsageWorkerRunning;
    private bool _aiUsageUiQueued;
    private bool _aiUsageBackdropInitialized;
    private bool _aiUsageThemeSubscribed;
    private bool _aiUsageHighContrastSubscribed;
    private bool _aiUsageHighContrastNotificationsUnavailable;
    private long _aiUsageGeneration;
    private long _aiUsageRequestedVersion;

    private enum AiUsageTab
    {
        Overview = 0,
        Models = 1,
        Activity = 2,
    }

    private void NavigationView_AiUsageInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (!_disposed && ReferenceEquals(args.InvokedItemContainer, AiUsageNavigationItem))
        {
            ShowAiUsageFlyout();
        }
    }

    private void ShowAiUsageFlyout()
    {
        EnsureAiUsageBackdrop();
        AiUsageFlyout.Placement = FlyoutPlacementMode.TopEdgeAlignedLeft;
        AiUsageFlyout.ShowAt(
            AiUsageNavigationItem,
            new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.TopEdgeAlignedLeft,
                Position = new Windows.Foundation.Point(14, -4),
            });
    }

    private void AiUsageContent_Loaded(object sender, RoutedEventArgs args)
    {
        AppearanceService.RegisterRoot(AiUsageContent);
        SyncAiUsageFlyoutTheme();
    }

    private void EnsureAiUsageBackdrop()
    {
        if (_aiUsageBackdropInitialized)
        {
            return;
        }

        _aiUsageBackdropInitialized = true;
        AiUsageFlyout.ShouldConstrainToRootBounds = false;
        AiUsageFlyout.Placement = FlyoutPlacementMode.TopEdgeAlignedLeft;

        if (DesktopAcrylicController.IsSupported() || MicaController.IsSupported())
        {
            _aiUsageBackdrop = new AiUsageTranslucentBackdrop(ActualTheme);
            AiUsageFlyout.SystemBackdrop = _aiUsageBackdrop;
        }
        else
        {
            AiUsageFlyout.SystemBackdrop = null;
        }
    }

    private void UpdateAiUsageFlyoutSize(AppWindow? window)
    {
        double targetHeight = AiUsageFlyoutDefaultHeight;
        double targetWidth = Math.Min(AiUsageFlyoutWidth, Math.Max(300, (XamlRoot?.Size.Width ?? AiUsageFlyoutWidth + 32) - 32));
        try
        {
            if (window is not null)
            {
                DisplayArea display = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
                double scale = XamlRoot?.RasterizationScale is > 0 ? XamlRoot.RasterizationScale : 1.0;
                double availableDip = (display.WorkArea.Height / scale) - 96;
                targetHeight = Math.Clamp(Math.Floor(availableDip), Math.Min(360, availableDip), AiUsageFlyoutDefaultHeight);
                targetWidth = Math.Min(targetWidth, Math.Max(300, (display.WorkArea.Width / scale) - 32));
            }
        }
        catch (Exception)
        {
            targetHeight = AiUsageFlyoutDefaultHeight;
        }

        // All tabs share the same bounded viewport; only the body scrolls.
        AiUsageContent.Width = targetWidth;
        AiUsageContent.Height = targetHeight;
        AiUsageContent.MinHeight = targetHeight;
        AiUsageContent.MaxHeight = targetHeight;
        bool narrow = targetWidth < 400;
        Grid.SetColumnSpan(AiUsageHeaderTitle, narrow ? 2 : 1);
        Grid.SetRow(AiUsageHeaderActions, narrow ? 1 : 0);
        Grid.SetColumn(AiUsageHeaderActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(AiUsageHeaderActions, narrow ? 2 : 1);
        AiUsageHeaderActions.HorizontalAlignment = HorizontalAlignment.Right;
    }

    private void SyncAiUsageFlyoutTheme()
    {
        ElementTheme theme = ActualTheme;
        AiUsageContent.RequestedTheme = theme;
        _aiUsageBackdrop?.UpdateTheme(theme);

        if (VisualTreeHelper.GetParent(AiUsageContent) is FrameworkElement parent)
        {
            parent.RequestedTheme = theme;
            DependencyObject? cursor = parent;
            while (cursor is not null)
            {
                if (cursor is FlyoutPresenter presenter)
                {
                    presenter.RequestedTheme = theme;
                    break;
                }

                cursor = VisualTreeHelper.GetParent(cursor);
            }
        }

        AiUsageContent.Background = _aiUsageAccessibility.HighContrast
            ? CreateAiUsageSystemBrush(background: true)
            : AiUsageFlyout.SystemBackdrop is null
            ? ResolveAiUsageBrush("SolidBackgroundFillColorTertiaryBrush", ThemeBrushHelper.SecondaryTextBrush)
            : null;

        ApplyAiUsageGlassSurfaces();
    }

    private void ApplyAiUsageGlassSurfaces()
    {
        Brush glassCard = CreateGlassCardBrush();
        Brush glassBorder = CreateGlassBorderBrush();
        Brush glassInset = CreateGlassInsetBrush();

        Border[] staticCards =
        [
            AiUsageBreakdownCard,
            AiUsageOutcomeCard,
            AiUsageTopModelsCard,
            AiUsageByTaskCard,
        ];

        ApplyAiUsageMetricSurface(AiUsageKpiTokensTile, AiUsageTotalTokensText, AiUsageTokensMetricIcon,
            0xE7F0FC, 0xB8CEE9, 0x165C9C, 0x1C2D42, 0x3C5976, 0x8CCBFF);
        ApplyAiUsageMetricSurface(AiUsageKpiCacheTile, AiUsageCacheHitRateText, AiUsageCacheMetricIcon,
            0xE7F2EC, 0xB8D7C3, 0x17643A, 0x1C3327, 0x3D624E, 0x88D6A1);
        ApplyAiUsageMetricSurface(AiUsageKpiTasksTile, AiUsageTasksCountValueText, AiUsageTasksMetricIcon,
            0xEFEAF7, 0xCDBFDE, 0x66458C, 0x30283D, 0x625173, 0xD3B3F0);
        ApplyAiUsageMetricSurface(AiUsageKpiLatencyTile, AiUsageAvgLatencyText, AiUsageLatencyMetricIcon,
            0xE4F3F1, 0xAFD5CF, 0x146C64, 0x193431, 0x3B625C, 0x80D9CC);

        foreach (Border card in staticCards)
        {
            card.Background = glassCard;
            card.BorderBrush = glassBorder;
        }

        AiUsageTabTrackBorder.Background = CreateGlassTabTrackBrush();
        AiUsageTabTrackBorder.BorderBrush = glassBorder;

        AiUsageModelsEmptyState.Background = glassInset;
        AiUsageNoActiveCard.Background = glassInset;
        AiUsageNoSessionEventsCard.Background = glassInset;
        AiUsageNoRecentCallsCard.Background = glassInset;

        AiUsageLegacyModelsCard.Background = ResolveAiUsageBrush("BadgeWarningBackgroundBrush", glassInset);
        AiUsageLegacyModelsCard.BorderBrush = _aiUsageAccessibility.HighContrast ? glassBorder : CreateTintedBorderBrush(Color.FromArgb(90, 240, 154, 42));
    }

    private void ApplyAiUsageMetricSurface(Border card, TextBlock value, FontIcon icon,
        uint lightFill, uint lightStroke, uint lightText, uint darkFill, uint darkStroke, uint darkText)
    {
        bool light = ActualTheme == ElementTheme.Light;
        bool highContrast = _aiUsageAccessibility.HighContrast;
        card.Background = highContrast ? CreateAiUsageSystemBrush(background: true) : UsageColorBrush(light ? lightFill : darkFill);
        card.BorderBrush = highContrast ? CreateAiUsageSystemBrush(background: false) : UsageColorBrush(light ? lightStroke : darkStroke);
        value.Foreground = highContrast ? CreateAiUsageSystemBrush(background: false) : UsageColorBrush(light ? lightText : darkText);
        icon.Foreground = value.Foreground;
    }

    private static SolidColorBrush UsageColorBrush(uint rgb) => new(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    private static SolidColorBrush CreateAiUsageSystemBrush(bool background) => new(new Windows.UI.ViewManagement.UISettings().GetColorValue(
        background ? Windows.UI.ViewManagement.UIColorType.Background : Windows.UI.ViewManagement.UIColorType.Foreground));

    private void AiUsageFlyout_Opening(object? sender, object args)
    {
        EnsureAiUsageBackdrop();
        AppWindow? window = App.GetSettingsWindow()?.AppWindow;
        if (window is null || !IsAiUsageWindowVisible(window))
        {
            AiUsageFlyout.Hide();
            return;
        }

        UpdateAiUsageFlyoutSize(window);
        AiUsageClearConfirmBar.Visibility = Visibility.Collapsed;
        SyncAiUsageFlyoutTheme();

        lock (_aiUsageGate)
        {
            if (_disposed || _aiUsageActive)
            {
                return;
            }

            _aiUsageActive = true;
            _aiUsageRange = AiUsageTimeRange.Today;
            _aiUsageOverview = null;
            _aiUsageGeneration++;
            _aiUsageWindow = window;
            window.Changed += OnAiUsageWindowChanged;
            AiHubEngine.InstanceChanged += OnAiUsageChanged;
        }

        if (!_aiUsageThemeSubscribed)
        {
            ActualThemeChanged += OnAiUsageActualThemeChanged;
            _aiUsageThemeSubscribed = true;
        }
        if (!_aiUsageHighContrastSubscribed && !_aiUsageHighContrastNotificationsUnavailable)
        {
            try
            {
                _aiUsageAccessibility.HighContrastChanged += OnAiUsageHighContrastChanged;
                _aiUsageHighContrastSubscribed = true;
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or NotSupportedException)
            {
                // Some desktop hosts expose the current setting but no notification
                // source. Opening, theme synchronization and refresh still read it.
                _aiUsageHighContrastNotificationsUnavailable = true;
            }
        }

        RefreshAiUsageText();
        RequestAiUsageRefresh();
    }

    private void AiUsageFlyout_Opened(object? sender, object args)
    {
        SyncAiUsageFlyoutTheme();
        UpdateAiUsageLiveTimer();
    }

    private void AiUsageFlyout_Closed(object? sender, object args)
    {
        AiUsageClearConfirmBar.Visibility = Visibility.Collapsed;
        StopAiUsageObservation();
    }

    private void OnAiUsageActualThemeChanged(FrameworkElement sender, object args)
    {
        if (!_aiUsageActive || _disposed)
        {
            return;
        }

        SyncAiUsageFlyoutTheme();
        RefreshAiUsageText();
    }

    private void OnAiUsageHighContrastChanged(Windows.UI.ViewManagement.AccessibilitySettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_aiUsageActive && !_disposed)
            {
                SyncAiUsageFlyoutTheme();
                RefreshAiUsageText();
            }
        });
    }

    private void OnAiUsageWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (ReferenceEquals(sender, _aiUsageWindow) && args.DidSizeChange)
        {
            UpdateAiUsageFlyoutSize(sender);
        }
        if (ReferenceEquals(sender, _aiUsageWindow) && !IsAiUsageWindowVisible(sender))
        {
            // A retained Settings window can stay Loaded when hidden for background
            // work. Native Flyout.Closed is not guaranteed on that path.
            StopAiUsageObservation();
            AiUsageFlyout.Hide();
        }
    }

    private static bool IsAiUsageWindowVisible(AppWindow window) => window.IsVisible &&
        !(window.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized);

    private void ShellPage_AiUsageUnloaded(object sender, RoutedEventArgs args)
    {
        StopAiUsageObservation();
        AiUsageFlyout.Hide();
        AiUsageFlyout.SystemBackdrop = null;
        _aiUsageBackdrop?.Dispose();
        _aiUsageBackdrop = null;
        _aiUsageBackdropInitialized = false;
    }

    private void OnAiUsageChanged(object? sender, EventArgs args) => RequestAiUsageRefresh();

    private void AiUsageRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        AiUsageClearConfirmBar.Visibility = Visibility.Collapsed;
        RequestAiUsageRefresh();
    }

    private void AiUsageClearButton_Click(object sender, RoutedEventArgs e)
    {
        AiUsageClearConfirmBar.Visibility = AiUsageClearConfirmBar.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void AiUsageClearCancelButton_Click(object sender, RoutedEventArgs e)
    {
        AiUsageClearConfirmBar.Visibility = Visibility.Collapsed;
    }

    private void AiUsageClearConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        AiUsageClearConfirmBar.Visibility = Visibility.Collapsed;
        AiUsageStore targetStore = AiHubEngine.ExistingEngine?.UsageStore ?? _aiUsageFallbackStore;
        _ = Task.Run(() =>
        {
            targetStore.ClearHistory();
            RequestAiUsageRefresh();
        });
    }

    private void AiUsageRangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse(tag, ignoreCase: true, out AiUsageTimeRange range) && range != _aiUsageRange)
        {
            _aiUsageRange = range;
            UpdateAiUsageRangeButtons();
            RequestAiUsageRefresh();
        }
    }

    private void AiUsageTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse(tag, ignoreCase: true, out AiUsageTab tab))
        {
            SelectAiUsageTab(tab);
        }
    }

    private void AiUsageViewAllModels_Click(object sender, RoutedEventArgs e) => SelectAiUsageTab(AiUsageTab.Models);

    private void AiUsageLiveBannerButton_Click(object sender, RoutedEventArgs e) => SelectAiUsageTab(AiUsageTab.Activity);

    private void SelectAiUsageTab(AiUsageTab tab)
    {
        _aiUsageTab = tab;
        AiUsageOverviewView.Visibility = tab == AiUsageTab.Overview ? Visibility.Visible : Visibility.Collapsed;
        AiUsageModelsView.Visibility = tab == AiUsageTab.Models ? Visibility.Visible : Visibility.Collapsed;
        AiUsageActivityView.Visibility = tab == AiUsageTab.Activity ? Visibility.Visible : Visibility.Collapsed;
        AiUsageBodyScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        UpdateAiUsageTabButtons();
        RenderAiUsageLiveHeaderAndActive();
    }

    private void RequestAiUsageRefresh()
    {
        lock (_aiUsageGate)
        {
            if (!_aiUsageActive || _disposed)
            {
                return;
            }

            _aiUsageRequestedVersion++;
            if (_aiUsageWorkerRunning)
            {
                return;
            }

            _aiUsageWorkerRunning = true;
        }

        // Single coalesced worker reads SQLite + live session state off the UI thread
        // and never asks the factory to initialize an AI engine.
        _ = Task.Run(ReadAiUsageSnapshots);
    }

    private void ReadAiUsageSnapshots()
    {
        while (true)
        {
            AiDiagnosticsStore? store;
            AiUsageStore usageStore;
            AiSessionActivityTracker? activityTracker;
            string? sessionId;
            string activeKernel;
            AiUsageTimeRange range;
            long generation;
            long version;
            lock (_aiUsageGate)
            {
                if (!_aiUsageActive || _disposed)
                {
                    _aiUsageWorkerRunning = false;
                    return;
                }

                TaskAiEngine? engine = AiHubEngine.ExistingEngine;
                store = engine?.Diagnostics;
                usageStore = engine?.UsageStore ?? _aiUsageFallbackStore;
                activityTracker = engine?.SessionActivity;
                sessionId = engine?.SessionId;
                activeKernel = engine?.ActiveKernel ?? AiKernelCatalog.Codex;
                range = _aiUsageRange;

                if (!ReferenceEquals(store, _aiUsageStore))
                {
                    if (_aiUsageStore is not null)
                    {
                        _aiUsageStore.Changed -= OnAiUsageChanged;
                    }

                    _aiUsageStore = store;
                    if (store is not null)
                    {
                        store.Changed += OnAiUsageChanged;
                    }
                }

                if (!ReferenceEquals(usageStore, _aiUsagePersistentStore))
                {
                    if (_aiUsagePersistentStore is not null)
                    {
                        _aiUsagePersistentStore.Changed -= OnAiUsageChanged;
                    }

                    _aiUsagePersistentStore = usageStore;
                    usageStore.Changed += OnAiUsageChanged;
                }

                if (!ReferenceEquals(activityTracker, _aiUsageActivityTracker))
                {
                    if (_aiUsageActivityTracker is not null)
                    {
                        _aiUsageActivityTracker.Changed -= OnAiUsageChanged;
                    }

                    _aiUsageActivityTracker = activityTracker;
                    if (activityTracker is not null)
                    {
                        activityTracker.Changed += OnAiUsageChanged;
                    }
                }

                generation = _aiUsageGeneration;
                version = _aiUsageRequestedVersion;
            }

            AiDiagnosticsSnapshot? snapshot = null;
            AiUsageOverview? overview = null;
            AiSessionActivitySnapshot? activity = null;
            bool readSucceeded = true;
            try
            {
                snapshot = store?.GetSnapshot();
                overview = usageStore.GetOverview(range, sessionId, maxRecentCalls: 30);
                activity = activityTracker?.GetSnapshot();
            }
            catch (Exception)
            {
                // Retain the last successful display if this observational read fails.
                readSucceeded = false;
            }

            bool queueUpdate = false;
            bool repeat;
            lock (_aiUsageGate)
            {
                if (_aiUsageActive && !_disposed && readSucceeded && generation == _aiUsageGeneration
                    && version == _aiUsageRequestedVersion && ReferenceEquals(store, _aiUsageStore))
                {
                    _aiUsagePending = new(snapshot, overview, activity, activeKernel, generation, version);
                    if (!_aiUsageUiQueued)
                    {
                        _aiUsageUiQueued = true;
                        queueUpdate = true;
                    }
                }

                repeat = _aiUsageActive && !_disposed && (generation != _aiUsageGeneration || version != _aiUsageRequestedVersion);
                if (!repeat)
                {
                    _aiUsageWorkerRunning = false;
                }
            }

            if (queueUpdate && !DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyAiUsageSnapshot))
            {
                lock (_aiUsageGate)
                {
                    _aiUsageUiQueued = false;
                    _aiUsagePending = null;
                }
            }

            if (!repeat)
            {
                return;
            }
        }
    }

    private void ApplyAiUsageSnapshot()
    {
        lock (_aiUsageGate)
        {
            _aiUsageUiQueued = false;
            AiUsageUpdate? update = _aiUsagePending;
            _aiUsagePending = null;
            if (!_aiUsageActive || _disposed || update is null || update.Generation != _aiUsageGeneration
                || update.Version != _aiUsageRequestedVersion)
            {
                return;
            }

            _aiUsageSnapshot = update.Snapshot;
            _aiUsageOverview = update.Overview;
            _aiUsageActivity = update.Activity;
            _aiUsageActiveKernel = update.ActiveKernel;
        }

        RefreshAiUsageText();
        UpdateAiUsageLiveTimer();
    }

    private void StopAiUsageObservation()
    {
        StopAiUsageLiveTimer();
        if (_aiUsageThemeSubscribed)
        {
            ActualThemeChanged -= OnAiUsageActualThemeChanged;
            _aiUsageThemeSubscribed = false;
        }
        if (_aiUsageHighContrastSubscribed)
        {
            try
            {
                _aiUsageAccessibility.HighContrastChanged -= OnAiUsageHighContrastChanged;
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or NotSupportedException)
            {
                // The native notification source can disappear during teardown.
                // Do not register another handler if its removal cannot be confirmed.
                _aiUsageHighContrastNotificationsUnavailable = true;
            }
            finally
            {
                _aiUsageHighContrastSubscribed = false;
            }
        }

        lock (_aiUsageGate)
        {
            _aiUsageActive = false;
            _aiUsageGeneration++;
            _aiUsageRequestedVersion++;
            _aiUsagePending = null;
            AiHubEngine.InstanceChanged -= OnAiUsageChanged;
            if (_aiUsageWindow is not null)
            {
                _aiUsageWindow.Changed -= OnAiUsageWindowChanged;
                _aiUsageWindow = null;
            }

            if (_aiUsageStore is not null)
            {
                _aiUsageStore.Changed -= OnAiUsageChanged;
                _aiUsageStore = null;
            }

            if (_aiUsagePersistentStore is not null)
            {
                _aiUsagePersistentStore.Changed -= OnAiUsageChanged;
                _aiUsagePersistentStore = null;
            }

            if (_aiUsageActivityTracker is not null)
            {
                _aiUsageActivityTracker.Changed -= OnAiUsageChanged;
                _aiUsageActivityTracker = null;
            }
        }
    }

    private void UpdateAiUsageLiveTimer()
    {
        bool shouldTick = _aiUsageActive && !_disposed && (_aiUsageActivity?.ActiveExecutions.Count ?? 0) > 0;
        if (!shouldTick)
        {
            StopAiUsageLiveTimer();
            return;
        }

        if (_aiUsageLiveTimer is null)
        {
            _aiUsageLiveTimer = DispatcherQueue.CreateTimer();
            _aiUsageLiveTimer.Interval = TimeSpan.FromSeconds(1);
            _aiUsageLiveTimer.IsRepeating = true;
            _aiUsageLiveTimer.Tick += OnAiUsageLiveTimerTick;
        }

        if (!_aiUsageLiveTimer.IsRunning)
        {
            _aiUsageLiveTimer.Start();
        }
    }

    private void StopAiUsageLiveTimer()
    {
        if (_aiUsageLiveTimer is not null)
        {
            _aiUsageLiveTimer.Stop();
            _aiUsageLiveTimer.Tick -= OnAiUsageLiveTimerTick;
            _aiUsageLiveTimer = null;
        }
    }

    private void OnAiUsageLiveTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_aiUsageActive || _disposed || (_aiUsageActivity?.ActiveExecutions.Count ?? 0) == 0)
        {
            StopAiUsageLiveTimer();
            return;
        }

        RenderAiUsageLiveHeaderAndActive();
    }

    private void RefreshAiUsageText()
    {
        var loader = ResourceLoaderInstance.ResourceLoader;
        ToolTipService.SetToolTip(AiUsageRefreshButton, loader.GetString("Shell_AiUsageRefreshTooltip"));
        ToolTipService.SetToolTip(AiUsageClearButton, loader.GetString("Shell_AiUsageClearTooltip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AiUsageRefreshButton, loader.GetString("Shell_AiUsageRefreshTooltip"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AiUsageClearButton, loader.GetString("Shell_AiUsageClearTooltip"));

        AiUsagePiNoticeBar.Visibility = string.Equals(_aiUsageActiveKernel, AiKernelCatalog.Pi, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Project a synthetic AiDiagnosticsSnapshot fixture when no SQLite overview has been loaded
        // (keeps TypographySmoke's render-only fixture path working cleanly).
        AiUsageOverview effective = BuildEffectiveOverview();
        Dictionary<string, Brush> modelBrushes = BuildModelColorMap(effective.Models);

        ApplyAiUsageGlassSurfaces();
        UpdateAiUsageRangeButtons();
        UpdateAiUsageTabButtons(effective);
        RenderAiUsageLiveHeaderAndActive();
        RenderAiUsageOverview(loader, effective, modelBrushes);
        RenderAiUsageModels(loader, effective, modelBrushes);
        RenderAiUsageActivity(loader, effective, modelBrushes);
        RenderAiUsageFooter(loader, effective);
    }

    private AiUsageOverview BuildEffectiveOverview()
    {
        if (!_aiUsageActive && _aiUsageSnapshot is { } injected && (injected.RouteAttempts > 0 || injected.RequestCount > 0))
        {
            return ProjectSnapshotToOverview(injected);
        }

        if (_aiUsageOverview is { } overview && (overview.RouteCalls > 0 || overview.TaskCount > 0 || _aiUsageSnapshot is null))
        {
            return overview;
        }

        if (_aiUsageSnapshot is { } snap && (snap.RouteAttempts > 0 || snap.RequestCount > 0))
        {
            return ProjectSnapshotToOverview(snap);
        }

        return _aiUsageOverview ?? new AiUsageOverview { Range = _aiUsageRange };
    }

    private AiUsageOverview ProjectSnapshotToOverview(AiDiagnosticsSnapshot snap)
    {
        long uncached = Math.Max(0, snap.InputTokens - snap.CachedInputTokens);
        double? hitRate = snap.ReportedInputRoutes > 0 && snap.ReportedCachedInputRoutes > 0 && snap.InputTokens > 0
            ? (double)Math.Min(snap.InputTokens, snap.CachedInputTokens) / snap.InputTokens
            : null;
        return new AiUsageOverview
        {
            Range = _aiUsageRange,
            PersistenceAvailable = _aiUsageOverview?.PersistenceAvailable ?? true,
            TaskCount = snap.RequestCount,
            SucceededTasks = snap.Succeeded,
            FailedTasks = snap.Failed,
            CancelledTasks = snap.Cancelled,
            TimeoutTasks = snap.Timeout,
            PartialTasks = snap.Partial,
            TaskElapsedMilliseconds = snap.ElapsedMilliseconds,
            RouteCalls = snap.RouteAttempts,
            MainCalls = snap.MainAttempts,
            FallbackCalls = snap.FallbackAttempts,
            NativeRetries = snap.NativeRetryCount,
            KnownUsageCalls = snap.KnownUsageRoutes,
            ReportedInputCalls = snap.ReportedInputRoutes,
            ReportedCachedInputCalls = snap.ReportedCachedInputRoutes,
            ReportedOutputCalls = snap.ReportedOutputRoutes,
            InputTokens = snap.InputTokens,
            CachedInputTokens = snap.CachedInputTokens,
            UncachedInputTokens = uncached,
            OutputTokens = snap.OutputTokens,
            TotalTokens = snap.InputTokens + snap.OutputTokens,
            CacheHitRate = hitRate,
            AverageRouteElapsedMilliseconds = snap.RouteAttempts > 0 && snap.ElapsedMilliseconds > 0
                ? (double)snap.ElapsedMilliseconds / snap.RouteAttempts
                : null,
        };
    }

    private Dictionary<string, Brush> BuildModelColorMap(IReadOnlyList<AiModelUsageSummary> models)
    {
        var map = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < models.Count; index++)
        {
            string name = models[index].Model;
            if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
            {
                string brushKey = ModelPaletteBrushKeys[index % ModelPaletteBrushKeys.Length];
                map[name] = ResolveAiUsageBrush(brushKey, ThemeBrushHelper.SecondaryTextBrush);
            }
        }

        return map;
    }

    private Brush GetModelAccentBrush(string? modelName, Dictionary<string, Brush> modelBrushes)
    {
        if (!string.IsNullOrEmpty(modelName) && modelBrushes.TryGetValue(modelName, out Brush? mapped))
        {
            return mapped;
        }

        return ResolveAiUsageBrush("KitUsageCpuBrush", ThemeBrushHelper.SecondaryTextBrush);
    }

    private void RenderAiUsageLiveHeaderAndActive()
    {
        var loader = ResourceLoaderInstance.ResourceLoader;
        IReadOnlyList<AiActiveExecution> activeList = _aiUsageActivity?.ActiveExecutions ?? Array.Empty<AiActiveExecution>();
        int activeCount = activeList.Count;
        bool isLive = activeCount > 0;

        AiUsageLiveDot.Fill = isLive
            ? ResolveAiUsageBrush("KitUsageDiskBrush", ThemeBrushHelper.SecondaryTextBrush)
            : ResolveAiUsageBrush("TextFillColorTertiaryBrush", ThemeBrushHelper.SecondaryTextBrush);
        AiUsageLivePill.Background = isLive
            ? ResolveAiUsageBrush("BadgeSuccessBackgroundBrush", ThemeBrushHelper.SecondaryTextBrush)
            : CreateGlassInsetBrush();
        AiUsageLivePill.BorderBrush = isLive
            ? ResolveAiUsageBrush("KitUsageDiskBrush", ThemeBrushHelper.StrokeDefaultBrush)
            : CreateGlassBorderBrush();
        AiUsageLiveStatusText.Foreground = isLive
            ? ResolveAiUsageBrush("BadgeSuccessForegroundBrush", ThemeBrushHelper.SecondaryTextBrush)
            : ResolveAiUsageBrush("TextFillColorSecondaryBrush", ThemeBrushHelper.SecondaryTextBrush);
        AiUsageLiveStatusText.Text = isLive
            ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageLiveFormat"), activeCount)
            : loader.GetString("Shell_AiUsageIdle");

        if (isLive && _aiUsageTab != AiUsageTab.Activity)
        {
            AiActiveExecution lead = activeList[0];
            AiUsageLiveBannerButton.Visibility = Visibility.Visible;
            AiUsageLiveBannerButton.Background = CreateGlassCardBrush();
            string taskLabel = FormatTaskName(loader, lead.PluginId, lead.TaskId);
            string phaseLabel = FormatPhase(loader, lead.Phase, lead.RetryCount);
            AiUsageLiveBannerTitleText.Text = activeCount > 1
                ? $"{taskLabel} (+{activeCount - 1})"
                : taskLabel;

            var detailParts = new List<string>();
            if (!string.IsNullOrEmpty(lead.Model))
            {
                detailParts.Add(lead.Model);
            }

            if (lead.BatchIndex > 0 && lead.TotalBatches > 0)
            {
                detailParts.Add(string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageBatchFormat"), lead.BatchIndex, lead.TotalBatches));
            }

            detailParts.Add(phaseLabel);
            AiUsageLiveBannerDetailText.Text = string.Join(" · ", detailParts);
            TimeSpan elapsed = DateTimeOffset.UtcNow - lead.StartedUtc;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            AiUsageLiveBannerElapsedText.Text = FormatDurationMs((long)elapsed.TotalMilliseconds);
        }
        else
        {
            AiUsageLiveBannerButton.Visibility = Visibility.Collapsed;
        }

        // Keep Activity tab active-executions list in sync with live timer ticks.
        AiUsageActiveListPanel.Children.Clear();
        AiUsageNoActiveCard.Visibility = isLive ? Visibility.Collapsed : Visibility.Visible;
        foreach (AiActiveExecution exec in activeList)
        {
            AiUsageActiveListPanel.Children.Add(BuildActiveExecutionCard(loader, exec));
        }
    }

    private void RenderAiUsageOverview(
        Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader,
        AiUsageOverview data,
        Dictionary<string, Brush> modelBrushes)
    {
        bool hasAnyActivity = data.RouteCalls > 0 || data.TaskCount > 0;
        string notReported = loader.GetString("Shell_AiUsageNotReported");

        // Hero KPI 1: Total Tokens
        bool hasTokens = data.ReportedInputCalls > 0 || data.ReportedOutputCalls > 0;
        AiUsageTotalTokensText.Text = hasTokens ? data.TotalTokens.ToString("N0", CultureInfo.CurrentCulture) : notReported;
        AiUsageTotalTokensSubText.Text = hasTokens
            ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageUncachedSubFormat"), data.UncachedInputTokens)
            : (hasAnyActivity ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageCoverageFormat"), data.KnownUsageCalls, data.RouteCalls) : "—");

        // Hero KPI 2: Prompt Cache Hit Rate
        AiUsageCacheHitRateText.Text = data.CacheHitRate.HasValue
            ? (data.CacheHitRate.Value * 100.0).ToString("F1", CultureInfo.CurrentCulture) + "%"
            : "—";
        AiUsageCacheHitSubText.Text = data.ReportedCachedInputCalls > 0
            ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageCachedSubFormat"), data.CachedInputTokens)
            : notReported;

        // Hero KPI 3: Tasks & Calls
        AiUsageTasksCountValueText.Text = data.TaskCount.ToString("N0", CultureInfo.CurrentCulture);
        AiUsageCallsSplitText.Text = string.Format(
            CultureInfo.CurrentCulture,
            loader.GetString("Shell_AiUsageCallsSplitFormat"),
            data.RouteCalls,
            data.MainCalls,
            data.FallbackCalls);

        // Hero KPI 4: Avg Duration
        AiUsageAvgLatencyText.Text = data.AverageRouteElapsedMilliseconds.HasValue && data.TotalRouteElapsedMilliseconds > 0
            ? FormatDurationMs((long)Math.Round(data.AverageRouteElapsedMilliseconds.Value))
            : "—";
        AiUsageDurationSubText.Text = string.Format(
            CultureInfo.CurrentCulture,
            loader.GetString("Shell_AiUsageDurationSubFormat"),
            FormatDurationMs(data.TotalRouteElapsedMilliseconds),
            data.NativeRetries);

        // 3-Segment Proportion Bar + live percentage legend
        if (hasTokens && data.TotalTokens > 0)
        {
            AiUsageProportionSection.Visibility = Visibility.Visible;
            AiUsageUncachedBarCol.Width = new GridLength(Math.Max(0, data.UncachedInputTokens), GridUnitType.Star);
            AiUsageCachedBarCol.Width = new GridLength(Math.Max(0, data.CachedInputTokens), GridUnitType.Star);
            AiUsageOutputBarCol.Width = new GridLength(Math.Max(0, data.OutputTokens), GridUnitType.Star);

            double uncachedPct = data.UncachedInputTokens * 100.0 / data.TotalTokens;
            double cachedPct = data.CachedInputTokens * 100.0 / data.TotalTokens;
            double outputPct = data.OutputTokens * 100.0 / data.TotalTokens;
            AiUsageUncachedLegendText.Text = $"{loader.GetString("Shell_AiUsageUncachedLegend/Text")} {uncachedPct:F0}%";
            AiUsageCachedLegendText.Text = $"{loader.GetString("Shell_AiUsageCachedLegend/Text")} {cachedPct:F0}%";
            AiUsageOutputLegendText.Text = $"{loader.GetString("Shell_AiUsageOutputLegend/Text")} {outputPct:F0}%";
        }
        else
        {
            AiUsageProportionSection.Visibility = Visibility.Collapsed;
        }

        // Token rows (Codex only: Input, Cached Input, Output — no Pi Cache write)
        SetCounter(AiUsageInputText, AiUsageInputCoverageText, data.InputTokens, data.ReportedInputCalls, data.RouteCalls, hasAnyActivity);
        SetCounter(AiUsageCacheReadText, AiUsageCacheReadCoverageText, data.CachedInputTokens, data.ReportedCachedInputCalls, data.RouteCalls, hasAnyActivity);
        SetCounter(AiUsageOutputText, AiUsageOutputCoverageText, data.OutputTokens, data.ReportedOutputCalls, data.RouteCalls, hasAnyActivity);

        // Task outcome card with semantic colored pills
        AiUsageTasksText.Text = !hasAnyActivity
            ? (_aiUsageRange == AiUsageTimeRange.Session
                ? loader.GetString("Shell_AiUsageNotStarted")
                : loader.GetString("Shell_AiUsageNoUsageInRange"))
            : string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageTasksFormat"), data.TaskCount, data.SucceededTasks, data.FailedTasks);

        AiUsageOutcomePillsPanel.Children.Clear();
        if (hasAnyActivity)
        {
            if (data.SucceededTasks > 0)
            {
                AiUsageOutcomePillsPanel.Children.Add(CreateStatusPill($"{loader.GetString("Shell_AiUsageOutcomeSucceeded")} {data.SucceededTasks:N0}", "BadgeSuccess"));
            }

            if (data.FailedTasks > 0)
            {
                AiUsageOutcomePillsPanel.Children.Add(CreateStatusPill($"{loader.GetString("Shell_AiUsageOutcomeFailed")} {data.FailedTasks:N0}", "BadgeCritical"));
            }

            if (data.CancelledTasks > 0)
            {
                AiUsageOutcomePillsPanel.Children.Add(CreateStatusPill($"{loader.GetString("Shell_AiUsageOutcomeCancelled")} {data.CancelledTasks:N0}", "BadgeWarning"));
            }

            if (data.TimeoutTasks > 0)
            {
                AiUsageOutcomePillsPanel.Children.Add(CreateStatusPill($"{loader.GetString("Shell_AiUsageOutcomeTimeout")} {data.TimeoutTasks:N0}", "BadgeWarning"));
            }

            if (data.PartialTasks > 0)
            {
                AiUsageOutcomePillsPanel.Children.Add(CreateStatusPill($"{loader.GetString("Shell_AiUsageOutcomePartial")} {data.PartialTasks:N0}", "BadgeInfo"));
            }

            if (data.NativeRetries > 0)
            {
                AiUsageOutcomePillsPanel.Children.Add(CreateStatusPill(string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageRetryFormat"), data.NativeRetries), "BadgeWarning"));
            }
        }

        AiUsageOutcomePillsPanel.Visibility = AiUsageOutcomePillsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiUsageOutcomesText.Visibility = hasAnyActivity ? Visibility.Visible : Visibility.Collapsed;
        AiUsageOutcomesText.Text = hasAnyActivity
            ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageOutcomesFormat"), data.CancelledTasks, data.TimeoutTasks, data.PartialTasks, data.NativeRetries)
            : string.Empty;

        // Top models preview (attributed real models only, up to 4)
        AiUsageTopModelsPanel.Children.Clear();
        if (data.Models.Count > 0)
        {
            AiUsageTopModelsCard.Visibility = Visibility.Visible;
            foreach (AiModelUsageSummary model in data.Models.Take(4))
            {
                AiUsageTopModelsPanel.Children.Add(BuildCompactModelRow(loader, model, GetModelAccentBrush(model.Model, modelBrushes)));
            }
        }
        else
        {
            AiUsageTopModelsCard.Visibility = Visibility.Collapsed;
        }

        // By task breakdown
        AiUsageByTaskPanel.Children.Clear();
        if (data.Tasks.Count > 0)
        {
            AiUsageByTaskCard.Visibility = Visibility.Visible;
            foreach (AiPluginTaskUsageSummary task in data.Tasks.Take(6))
            {
                AiUsageByTaskPanel.Children.Add(BuildTaskSummaryRow(loader, task));
            }
        }
        else
        {
            AiUsageByTaskCard.Visibility = Visibility.Collapsed;
        }

        void SetCounter(TextBlock valueBlock, TextBlock coverageBlock, long tokens, long reported, long totalCalls, bool showCoverage)
        {
            valueBlock.Text = reported > 0 ? tokens.ToString("N0", CultureInfo.CurrentCulture) : notReported;
            coverageBlock.Text = showCoverage
                ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageCoverageFormat"), reported, totalCalls)
                : string.Empty;
            coverageBlock.Visibility = showCoverage ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RenderAiUsageModels(
        Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader,
        AiUsageOverview data,
        Dictionary<string, Brush> modelBrushes)
    {
        AiUsageModelsListPanel.Children.Clear();
        bool hasAttributed = data.Models.Count > 0;
        bool hasLegacy = data.LegacyUnattributedModel is { Calls: > 0 };

        if (!hasAttributed && !hasLegacy)
        {
            AiUsageModelsEmptyState.Visibility = Visibility.Visible;
            AiUsageModelsEmptyText.Text = loader.GetString("Shell_AiUsageNoUsageInRange");
            AiUsageLegacyModelsCard.Visibility = Visibility.Collapsed;
            return;
        }

        AiUsageModelsEmptyState.Visibility = Visibility.Collapsed;
        foreach (AiModelUsageSummary model in data.Models)
        {
            AiUsageModelsListPanel.Children.Add(BuildDetailedModelCard(loader, model, GetModelAccentBrush(model.Model, modelBrushes)));
        }

        if (data.LegacyUnattributedModel is { Calls: > 0 } legacy)
        {
            AiUsageLegacyModelsCard.Visibility = Visibility.Visible;
            AiUsageLegacyModelsTitleText.Text = loader.GetString("Shell_AiUsageLegacyModelsTitle");
            AiUsageLegacyModelsStatsText.Text = string.Format(
                CultureInfo.CurrentCulture,
                loader.GetString("Shell_AiUsageLegacyModelsStatsFormat"),
                legacy.Calls,
                legacy.TotalTokens,
                legacy.InputTokens,
                legacy.CachedInputTokens,
                legacy.OutputTokens);
            AiUsageLegacyModelsHintText.Text = loader.GetString("Shell_AiUsageLegacyModelsHint");
        }
        else
        {
            AiUsageLegacyModelsCard.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderAiUsageActivity(
        Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader,
        AiUsageOverview data,
        Dictionary<string, Brush> modelBrushes)
    {
        // Live session events
        AiUsageSessionEventsPanel.Children.Clear();
        IReadOnlyList<AiSessionActivityEvent> events = _aiUsageActivity?.Events ?? Array.Empty<AiSessionActivityEvent>();
        AiUsageNoSessionEventsCard.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int visibleEvents = Math.Min(events.Count, 25);
        for (int index = 0; index < visibleEvents; index++)
        {
            AiUsageSessionEventsPanel.Children.Add(BuildSessionEventRow(loader, events[index], isLast: index == visibleEvents - 1));
        }

        // Persisted recent calls
        AiUsageRecentCallsPanel.Children.Clear();
        AiUsageNoRecentCallsCard.Visibility = data.RecentCalls.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (AiUsageCallRecord call in data.RecentCalls.Take(25))
        {
            AiUsageRecentCallsPanel.Children.Add(BuildRecentCallCard(loader, call, modelBrushes));
        }
    }

    private void RenderAiUsageFooter(Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader, AiUsageOverview data)
    {
        if (!data.PersistenceAvailable)
        {
            AiUsageFooterText.Text = loader.GetString("Shell_AiUsageFooterUnavailable");
            return;
        }

        AiUsageFooterText.Text = data.RouteCalls > 0 || data.SessionCount > 0
            ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageFooterFormat"), data.SessionCount, data.RouteCalls)
            : loader.GetString("Shell_AiUsageFooterEmpty");
    }

    private void UpdateAiUsageRangeButtons()
    {
        Brush selectedBg = ResolveAiUsageBrush("BadgeInfoBackgroundBrush", ThemeBrushHelper.SecondaryTextBrush);
        Brush selectedBorder = ResolveAiUsageBrush("KitUsageCpuBrush", ThemeBrushHelper.StrokeDefaultBrush);
        Brush selectedFg = ResolveAiUsageBrush("BadgeInfoForegroundBrush", ThemeBrushHelper.SecondaryTextBrush);
        Brush secondaryFg = ResolveAiUsageBrush("TextFillColorSecondaryBrush", ThemeBrushHelper.SecondaryTextBrush);
        Brush transparent = new SolidColorBrush(Colors.Transparent);

        Button[] buttons =
        [
            AiUsageRangeAllButton,
            AiUsageRangeSessionButton,
            AiUsageRangeTodayButton,
            AiUsageRange7dButton,
            AiUsageRange30dButton,
        ];

        foreach (Button button in buttons)
        {
            bool isSelected = button.Tag is string tag && Enum.TryParse(tag, true, out AiUsageTimeRange parsed) && parsed == _aiUsageRange;
            button.Background = isSelected ? selectedBg : transparent;
            button.BorderBrush = isSelected ? selectedBorder : transparent;
            button.Foreground = isSelected ? selectedFg : secondaryFg;
            button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void UpdateAiUsageTabButtons(AiUsageOverview? overview = null)
    {
        var loader = ResourceLoaderInstance.ResourceLoader;
        int modelCount = (overview ?? _aiUsageOverview)?.DistinctModelCount ?? 0;
        int activeCount = _aiUsageActivity?.ActiveExecutions.Count ?? 0;

        AiUsageTabOverviewText.Text = loader.GetString("Shell_AiUsageTabOverview");
        AiUsageTabModelsText.Text = modelCount > 0
            ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageTabModelsFormat"), modelCount)
            : loader.GetString("Shell_AiUsageTabModels");
        AiUsageTabActivityText.Text = activeCount > 0
            ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageTabActivityFormat"), activeCount)
            : loader.GetString("Shell_AiUsageTabActivity");

        bool isLight = ActualTheme == ElementTheme.Light;
        Brush secondaryFg = ResolveAiUsageBrush("TextFillColorSecondaryBrush", ThemeBrushHelper.SecondaryTextBrush);
        Brush primaryFg = ResolveAiUsageBrush("TextFillColorPrimaryBrush", ThemeBrushHelper.SecondaryTextBrush);
        Brush transparent = new SolidColorBrush(Colors.Transparent);

        (Button Button, FontIcon Icon, TextBlock Text, Border Indicator, AiUsageTab Tab, Brush AccentBrush, Color DarkTint, Color DarkBorder, Color LightBorder)[] tabs =
        [
            (
                AiUsageTabOverviewButton,
                AiUsageTabOverviewIcon,
                AiUsageTabOverviewText,
                AiUsageTabOverviewIndicator,
                AiUsageTab.Overview,
                ResolveAiUsageBrush("KitUsageCpuBrush", ThemeBrushHelper.SecondaryTextBrush),
                Color.FromArgb(30, 56, 182, 255),
                Color.FromArgb(52, 110, 200, 255),
                Color.FromArgb(46, 0, 120, 212)),
            (
                AiUsageTabModelsButton,
                AiUsageTabModelsIcon,
                AiUsageTabModelsText,
                AiUsageTabModelsIndicator,
                AiUsageTab.Models,
                ResolveAiUsageBrush("KitUsageMemoryBrush", ThemeBrushHelper.SecondaryTextBrush),
                Color.FromArgb(30, 177, 70, 194),
                Color.FromArgb(52, 205, 128, 232),
                Color.FromArgb(46, 116, 77, 169)),
            (
                AiUsageTabActivityButton,
                AiUsageTabActivityIcon,
                AiUsageTabActivityText,
                AiUsageTabActivityIndicator,
                AiUsageTab.Activity,
                ResolveAiUsageBrush("KitUsageDiskBrush", ThemeBrushHelper.SecondaryTextBrush),
                Color.FromArgb(30, 56, 173, 98),
                Color.FromArgb(52, 85, 205, 132),
                Color.FromArgb(46, 14, 122, 13)),
        ];

        foreach (var item in tabs)
        {
            bool isSelected = item.Tab == _aiUsageTab;
            if (_aiUsageAccessibility.HighContrast)
            {
                item.Button.Background = isSelected ? CreateAiUsageSystemBrush(background: false) : CreateAiUsageSystemBrush(background: true);
                item.Button.BorderBrush = CreateAiUsageSystemBrush(background: false);
                item.Icon.Foreground = CreateAiUsageSystemBrush(background: isSelected);
                item.Text.Foreground = item.Icon.Foreground;
                item.Text.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
                item.Indicator.Background = item.Icon.Foreground;
                item.Indicator.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
                continue;
            }
            item.Button.Background = isSelected
                ? (isLight ? new SolidColorBrush(Color.FromArgb(196, 255, 255, 255)) : new SolidColorBrush(item.DarkTint))
                : transparent;
            item.Button.BorderBrush = isSelected
                ? new SolidColorBrush(isLight ? item.LightBorder : item.DarkBorder)
                : transparent;
            item.Icon.Foreground = isSelected ? item.AccentBrush : secondaryFg;
            item.Text.Foreground = isSelected ? (isLight ? item.AccentBrush : primaryFg) : secondaryFg;
            item.Text.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
            item.Indicator.Background = item.AccentBrush;
            item.Indicator.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private UIElement BuildCompactModelRow(
        Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader,
        AiModelUsageSummary model,
        Brush accentBrush)
    {
        var root = new StackPanel { Spacing = 4 };
        var topRow = new Grid { ColumnSpacing = 8 };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        leftPanel.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = accentBrush, VerticalAlignment = VerticalAlignment.Center });

        string modelDisplay = string.IsNullOrEmpty(model.Model) ? loader.GetString("Shell_AiUsageUnknownModel") : model.Model;
        TextBlock nameText = CreateUsageText(modelDisplay, 13, mono: !string.IsNullOrEmpty(model.Model), bold: true);
        nameText.TextTrimming = TextTrimming.CharacterEllipsis;
        nameText.TextWrapping = TextWrapping.NoWrap;
        leftPanel.Children.Add(nameText);
        topRow.Children.Add(leftPanel);

        string rightSummary = model.TotalTokens > 0
            ? $"{model.TotalTokens.ToString("N0", CultureInfo.CurrentCulture)} ({model.TokenSharePercent.ToString("F0", CultureInfo.CurrentCulture)}%)"
            : $"{model.Calls:N0}";
        TextBlock totalText = CreateUsageText(rightSummary, 12, mono: true, bold: true, foreground: accentBrush);
        Grid.SetColumn(totalText, 1);
        topRow.Children.Add(totalText);
        root.Children.Add(topRow);

        double share = Math.Clamp(model.TokenSharePercent, 0, 100);
        var barTrack = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = CreateGlassInsetBrush(),
        };
        var barGrid = new Grid();
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 2.0), GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 100 - Math.Max(share, 2.0)), GridUnitType.Star) });
        barGrid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = accentBrush,
        });
        barTrack.Child = barGrid;
        root.Children.Add(barTrack);
        return root;
    }

    private UIElement BuildDetailedModelCard(
        Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader,
        AiModelUsageSummary model,
        Brush accentBrush)
    {
        var card = CreateUsageCard(leftAccentBrush: accentBrush);
        var stack = new StackPanel { Spacing = 7 };

        // Top row: dot + model name + colored share pill
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        nameRow.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = accentBrush, VerticalAlignment = VerticalAlignment.Center });
        string modelDisplay = string.IsNullOrEmpty(model.Model) ? loader.GetString("Shell_AiUsageUnknownModel") : model.Model;
        TextBlock nameText = CreateUsageText(modelDisplay, 14, mono: !string.IsNullOrEmpty(model.Model), bold: true);
        nameRow.Children.Add(nameText);
        header.Children.Add(nameRow);

        var shareBadge = new Border
        {
            Background = CreateGlassInsetBrush(),
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = CreateUsageText(
                $"{model.TotalTokens.ToString("N0", CultureInfo.CurrentCulture)} · {model.TokenSharePercent.ToString("F1", CultureInfo.CurrentCulture)}%",
                12,
                mono: true,
                bold: true,
                foreground: accentBrush),
        };
        Grid.SetColumn(shareBadge, 1);
        header.Children.Add(shareBadge);
        stack.Children.Add(header);

        // Metadata chips: model source, api_mode, effort, context window
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalSpacing = 5, VerticalSpacing = 4 };
        chips.Children.Add(CreateStatusPill(
            model.CodexReportedCalls > 0
                ? loader.GetString("Shell_AiUsageModelSourceCodex")
                : loader.GetString("Shell_AiUsageModelSourceConfig"),
            model.CodexReportedCalls > 0 ? "BadgeSuccess" : "BadgeInfo"));
        if (!string.IsNullOrWhiteSpace(model.DistinctApiModes))
        {
            foreach (string mode in model.DistinctApiModes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2))
            {
                chips.Children.Add(CreateNeutralChip(mode));
            }
        }

        if (!string.IsNullOrWhiteSpace(model.DistinctEfforts))
        {
            foreach (string effort in model.DistinctEfforts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(2))
            {
                chips.Children.Add(CreateNeutralChip(effort));
            }
        }

        if (model.MaxContextWindowTokens > 0)
        {
            chips.Children.Add(CreateNeutralChip(string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageCtxShortFormat"), FormatCompactTokens(model.MaxContextWindowTokens))));
        }

        stack.Children.Add(chips);

        // Colored proportional share bar
        double share = Math.Clamp(model.TokenSharePercent, 0, 100);
        var barTrack = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = CreateGlassInsetBrush(),
        };
        var barGrid = new Grid();
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 2.0), GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 100 - Math.Max(share, 2.0)), GridUnitType.Star) });
        barGrid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = accentBrush,
        });
        barTrack.Child = barGrid;
        stack.Children.Add(barTrack);

        // 3-column Codex token mini-grid inside subtle translucent inset
        var tokenInset = new Border
        {
            Background = CreateGlassInsetBrush(),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 7, 9, 7),
        };
        var tokenGrid = new Grid { ColumnSpacing = 8 };
        tokenGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tokenGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tokenGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Brush blueBrush = ResolveAiUsageBrush("KitUsageCpuBrush", ThemeBrushHelper.SecondaryTextBrush);
        Brush greenBrush = ResolveAiUsageBrush("KitUsageDiskBrush", ThemeBrushHelper.SecondaryTextBrush);
        Brush violetBrush = ResolveAiUsageBrush("KitUsageMemoryBrush", ThemeBrushHelper.SecondaryTextBrush);

        string inputSub = string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageUncachedTinyFormat"), model.UncachedInputTokens);
        string cachedSub = model.CacheHitRate.HasValue
            ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageCacheHitTinyFormat"), model.CacheHitRate.Value * 100.0)
            : "—";
        string outputSub = model.TotalInputItems > 0
            ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageItemsShortFormat"), model.TotalInputItems)
            : string.Empty;

        UIElement inputCol = BuildTokenMiniColumn(
            loader.GetString("Shell_AiUsageMetricInputShort"),
            model.InputTokens.ToString("N0", CultureInfo.CurrentCulture),
            inputSub,
            blueBrush);
        UIElement cachedCol = BuildTokenMiniColumn(
            loader.GetString("Shell_AiUsageMetricCachedShort"),
            model.CachedInputTokens.ToString("N0", CultureInfo.CurrentCulture),
            cachedSub,
            greenBrush);
        UIElement outputCol = BuildTokenMiniColumn(
            loader.GetString("Shell_AiUsageMetricOutputShort"),
            model.OutputTokens.ToString("N0", CultureInfo.CurrentCulture),
            outputSub,
            violetBrush);

        Grid.SetColumn((FrameworkElement)cachedCol, 1);
        Grid.SetColumn((FrameworkElement)outputCol, 2);
        tokenGrid.Children.Add(inputCol);
        tokenGrid.Children.Add(cachedCol);
        tokenGrid.Children.Add(outputCol);
        tokenInset.Child = tokenGrid;
        stack.Children.Add(tokenInset);

        string metaLine = string.Format(
            CultureInfo.CurrentCulture,
            loader.GetString("Shell_AiUsageModelMetaFormat"),
            model.Calls,
            model.MainCalls,
            model.FallbackCalls,
            model.SucceededCalls,
            model.FailedCalls + model.TimeoutCalls,
            model.NativeRetries);
        stack.Children.Add(CreateUsageText(metaLine, 12, secondary: true));

        var timingParts = new List<string>
        {
            string.Format(
                CultureInfo.CurrentCulture,
                loader.GetString("Shell_AiUsageModelDurationFormat"),
                model.AverageElapsedMilliseconds.HasValue ? FormatDurationMs((long)Math.Round(model.AverageElapsedMilliseconds.Value)) : "—",
                FormatLocalTimestamp(model.LastUsedUtc)),
        };
        if (model.AverageQueueMilliseconds is > 100)
        {
            timingParts.Insert(
                1,
                string.Format(
                    CultureInfo.CurrentCulture,
                    loader.GetString("Shell_AiUsageQueueShortFormat"),
                    FormatDurationMs((long)Math.Round(model.AverageQueueMilliseconds.Value))));
        }

        stack.Children.Add(CreateUsageText(string.Join(" · ", timingParts), 12, secondary: true));

        WrapUsageCardContent(card, stack);
        return card;
    }

    private UIElement BuildTokenMiniColumn(string label, string value, string sub, Brush accentBrush)
    {
        var panel = new StackPanel { Spacing = 2 };
        var hdr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        hdr.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = accentBrush, VerticalAlignment = VerticalAlignment.Center });
        hdr.Children.Add(CreateUsageText(label, 12, secondary: true));
        panel.Children.Add(hdr);
        panel.Children.Add(CreateUsageText(value, 13, mono: true, bold: true, foreground: accentBrush));
        if (!string.IsNullOrEmpty(sub))
        {
            panel.Children.Add(CreateUsageText(sub, 12, secondary: true));
        }

        return panel;
    }

    private UIElement BuildTaskSummaryRow(Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader, AiPluginTaskUsageSummary task)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Brush taskColor = ResolveTaskAccentBrush(task.TaskId);
        grid.Children.Add(new Border
        {
            Width = 3,
            Height = 28,
            CornerRadius = new CornerRadius(1.5),
            Background = taskColor,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var left = new StackPanel { Spacing = 1 };
        left.Children.Add(CreateUsageText(FormatTaskName(loader, task.PluginId, task.TaskId), 13, bold: true));
        string sub = $"{task.RouteCalls:N0} · {FormatDurationMs((long)Math.Round(task.AverageElapsedMilliseconds ?? 0))}";
        left.Children.Add(CreateUsageText(sub, 12, secondary: true));
        Grid.SetColumn(left, 1);
        grid.Children.Add(left);

        var tokenPill = new Border
        {
            Background = CreateGlassInsetBrush(),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = CreateUsageText(task.TotalTokens.ToString("N0", CultureInfo.CurrentCulture), 12, mono: true, bold: true, foreground: taskColor),
        };
        Grid.SetColumn(tokenPill, 2);
        grid.Children.Add(tokenPill);
        return grid;
    }

    private UIElement BuildActiveExecutionCard(Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader, AiActiveExecution exec)
    {
        Brush phaseBrush = ResolvePhaseAccentBrush(exec.Phase);
        var card = CreateUsageCard(leftAccentBrush: phaseBrush, borderOverride: phaseBrush);
        var stack = new StackPanel { Spacing = 5 };

        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        top.Children.Add(CreateUsageText(FormatTaskName(loader, exec.PluginId, exec.TaskId), 13, bold: true));

        TimeSpan elapsed = DateTimeOffset.UtcNow - exec.StartedUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var elapsedPill = new Border
        {
            Background = CreateGlassInsetBrush(),
            BackgroundSizing = BackgroundSizing.OuterBorderEdge,
            BorderBrush = phaseBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 2),
            Child = CreateUsageText(FormatDurationMs((long)elapsed.TotalMilliseconds), 12, mono: true, bold: true, foreground: phaseBrush),
        };
        Grid.SetColumn(elapsedPill, 1);
        top.Children.Add(elapsedPill);
        stack.Children.Add(top);

        var badges = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalSpacing = 6, VerticalSpacing = 4 };
        badges.Children.Add(CreateStatusPill(
            FormatPhase(loader, exec.Phase, exec.RetryCount),
            ResolvePhaseBadgePrefix(exec.Phase)));
        if (exec.BatchIndex > 0 && exec.TotalBatches > 0)
        {
            badges.Children.Add(CreateStatusPill(
                string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageBatchFormat"), exec.BatchIndex, exec.TotalBatches),
                "BadgeInfo"));
        }

        stack.Children.Add(badges);

        var metaParts = new List<string>();
        if (!string.IsNullOrEmpty(exec.Model))
        {
            metaParts.Add(exec.Model);
        }

        if (!string.IsNullOrEmpty(exec.Route))
        {
            metaParts.Add(FormatRoute(loader, exec.Route));
        }

        if (!string.IsNullOrEmpty(exec.Effort))
        {
            metaParts.Add(exec.Effort);
        }

        if (metaParts.Count > 0)
        {
            stack.Children.Add(CreateUsageText(string.Join(" · ", metaParts), 12, mono: true, secondary: true));
        }

        WrapUsageCardContent(card, stack);
        return card;
    }

    private UIElement BuildSessionEventRow(Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader, AiSessionActivityEvent evt, bool isLast)
    {
        Brush dotBrush = ResolveEventAccentBrush(evt);
        var border = new Border
        {
            Background = CreateGlassInsetBrush(),
            BorderBrush = CreateGlassBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 7, 10, 7),
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Timeline dot + vertical rail
        var rail = new Grid { Width = 12, VerticalAlignment = VerticalAlignment.Stretch };
        if (!isLast)
        {
            rail.Children.Add(new Rectangle
            {
                Width = 1.5,
                Margin = new Thickness(0, 12, 0, -7),
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill = CreateGlassBorderBrush(),
            });
        }

        rail.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = dotBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 0, 0),
        });
        grid.Children.Add(rail);

        string localTime = evt.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        TextBlock timeBlock = CreateUsageText(localTime, 12, mono: true, secondary: true);
        Grid.SetColumn(timeBlock, 1);
        grid.Children.Add(timeBlock);

        var right = new StackPanel { Spacing = 2 };
        string headline = FormatEventHeadline(loader, evt);
        right.Children.Add(CreateUsageText(headline, 12, bold: true));

        var details = new List<string>();
        if (!string.IsNullOrEmpty(evt.Model))
        {
            details.Add(evt.Model);
        }

        if (evt.BatchIndex > 0 && evt.TotalBatches > 0)
        {
            details.Add(string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageBatchFormat"), evt.BatchIndex, evt.TotalBatches));
        }

        if (evt.InputTokens.HasValue || evt.OutputTokens.HasValue)
        {
            long inTok = evt.InputTokens ?? 0;
            long outTok = evt.OutputTokens ?? 0;
            string tok = $"In {inTok:N0}";
            if (evt.CachedInputTokens is > 0)
            {
                tok += $" ({evt.CachedInputTokens.Value:N0})";
            }

            tok += $" · Out {outTok:N0}";
            details.Add(tok);
        }

        if (evt.ElapsedMilliseconds > 0)
        {
            details.Add(FormatDurationMs(evt.ElapsedMilliseconds));
        }

        if (!string.IsNullOrEmpty(evt.Reason) && !string.Equals(evt.Reason, "None", StringComparison.OrdinalIgnoreCase))
        {
            details.Add(evt.HttpStatusCode.HasValue ? $"{evt.Reason} (HTTP {evt.HttpStatusCode.Value})" : evt.Reason);
        }

        if (details.Count > 0)
        {
            right.Children.Add(CreateUsageText(string.Join(" · ", details), 12, secondary: true));
        }

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        border.Child = grid;
        return border;
    }

    private UIElement BuildRecentCallCard(
        Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader,
        AiUsageCallRecord call,
        Dictionary<string, Brush> modelBrushes)
    {
        string badgePrefix = call.Outcome switch
        {
            "succeeded" => "BadgeSuccess",
            "cancelled" => "BadgeWarning",
            _ => "BadgeCritical",
        };
        Brush outcomeBrush = call.Outcome switch
        {
            "succeeded" => ResolveAiUsageBrush("KitUsageDiskBrush", ThemeBrushHelper.SecondaryTextBrush),
            "cancelled" => ResolveAiUsageBrush("KitUsageGpuBrush", ThemeBrushHelper.SecondaryTextBrush),
            _ => ResolveAiUsageBrush("BadgeCriticalForegroundBrush", ThemeBrushHelper.SecondaryTextBrush),
        };

        var card = CreateUsageCard(leftAccentBrush: outcomeBrush);
        var stack = new StackPanel { Spacing = 5 };

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(CreateUsageText(FormatTaskName(loader, call.PluginId, call.TaskId), 13, bold: true));
        Border pill = CreateStatusPill(FormatOutcome(loader, call.Outcome), badgePrefix);
        Grid.SetColumn(pill, 1);
        header.Children.Add(pill);
        stack.Children.Add(header);

        // Chips row: time + colored model chip + route + effort/mode + batch + items
        var metaRow = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalSpacing = 5, VerticalSpacing = 4 };
        metaRow.Children.Add(CreateNeutralChip(FormatLocalTimestamp(call.TimestampUtc), mono: true));

        string modelDisplay = string.IsNullOrEmpty(call.Model) ? loader.GetString("Shell_AiUsageUnknownModel") : call.Model;
        Brush modelDotBrush = string.IsNullOrEmpty(call.Model)
            ? ResolveAiUsageBrush("TextFillColorTertiaryBrush", ThemeBrushHelper.SecondaryTextBrush)
            : GetModelAccentBrush(call.Model, modelBrushes);
        var modelChipPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        modelChipPanel.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = modelDotBrush, VerticalAlignment = VerticalAlignment.Center });
        modelChipPanel.Children.Add(CreateUsageText(modelDisplay, 12, mono: !string.IsNullOrEmpty(call.Model), bold: !string.IsNullOrEmpty(call.Model)));
        metaRow.Children.Add(new Border
        {
            Background = CreateGlassInsetBrush(),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1, 6, 1),
            Child = modelChipPanel,
        });

        if (!string.IsNullOrEmpty(call.Route))
        {
            metaRow.Children.Add(CreateNeutralChip(FormatRoute(loader, call.Route)));
        }

        if (!string.IsNullOrEmpty(call.ApiMode))
        {
            metaRow.Children.Add(CreateNeutralChip(call.ApiMode));
        }

        if (!string.IsNullOrEmpty(call.Effort))
        {
            metaRow.Children.Add(CreateNeutralChip(call.Effort));
        }

        if (call.BatchIndex > 0 && call.TotalBatches > 0)
        {
            metaRow.Children.Add(CreateNeutralChip(string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageBatchFormat"), call.BatchIndex, call.TotalBatches)));
        }

        if (call.InputItemCount > 0)
        {
            metaRow.Children.Add(CreateNeutralChip(string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageItemsShortFormat"), call.InputItemCount)));
        }

        stack.Children.Add(metaRow);

        var metricsParts = new List<string>();
        if (call.InputTokens.HasValue || call.OutputTokens.HasValue)
        {
            long inTok = call.InputTokens ?? 0;
            long outTok = call.OutputTokens ?? 0;
            string tok = $"In {inTok:N0}";
            if (call.CachedInputTokens.HasValue)
            {
                tok += $" ({call.CachedInputTokens.Value:N0})";
            }

            tok += $" · Out {outTok:N0}";
            metricsParts.Add(tok);
        }
        else
        {
            metricsParts.Add(loader.GetString("Shell_AiUsageNotReported"));
        }

        if (call.ElapsedMilliseconds > 0)
        {
            metricsParts.Add(FormatDurationMs(call.ElapsedMilliseconds));
        }

        if (call.QueueMilliseconds > 100)
        {
            metricsParts.Add(string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageQueueShortFormat"), FormatDurationMs(call.QueueMilliseconds)));
        }

        if (call.NativeRetryCount > 0)
        {
            metricsParts.Add(string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageRetryFormat"), call.NativeRetryCount));
        }

        if (!string.IsNullOrEmpty(call.Reason) && !string.Equals(call.Reason, "None", StringComparison.OrdinalIgnoreCase))
        {
            metricsParts.Add(call.HttpStatusCode.HasValue ? $"{call.Reason} ({call.HttpStatusCode.Value})" : call.Reason);
        }

        stack.Children.Add(CreateUsageText(string.Join(" · ", metricsParts), 12, mono: true));

        WrapUsageCardContent(card, stack);
        return card;
    }

    private Border CreateUsageCard(Brush? leftAccentBrush = null, Brush? borderOverride = null)
    {
        var border = new Border
        {
            Background = CreateGlassCardBrush(),
            BackgroundSizing = BackgroundSizing.OuterBorderEdge,
            BorderBrush = borderOverride ?? CreateGlassBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = leftAccentBrush is not null ? new Thickness(0) : new Thickness(12, 9, 12, 9),
        };
        if (leftAccentBrush is not null)
        {
            var outer = new Grid();
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outer.Children.Add(new Border
            {
                Background = leftAccentBrush,
                CornerRadius = new CornerRadius(9, 0, 0, 9),
            });
            border.Child = outer;
        }

        return border;
    }

    private static void WrapUsageCardContent(Border card, UIElement content)
    {
        if (card.Child is Grid outer && outer.ColumnDefinitions.Count == 2)
        {
            var host = new Border
            {
                Padding = new Thickness(11, 9, 12, 9),
                Child = content,
            };
            Grid.SetColumn(host, 1);
            outer.Children.Add(host);
        }
        else
        {
            card.Child = content;
        }
    }

    private Border CreateNeutralChip(string text, bool mono = false) => new()
    {
        Background = CreateGlassInsetBrush(),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 1, 6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = CreateUsageText(text, 12, mono: mono, secondary: true),
    };

    private Border CreateStatusPill(string text, string badgePrefix) => new()
    {
        Background = ResolveAiUsageBrush(badgePrefix + "BackgroundBrush", ThemeBrushHelper.SecondaryTextBrush),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(7, 2, 7, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = CreateUsageText(
            text,
            12,
            bold: true,
            foreground: ResolveAiUsageBrush(badgePrefix + "ForegroundBrush", ThemeBrushHelper.SecondaryTextBrush)),
    };

    private SolidColorBrush CreateGlassCardBrush() => _aiUsageAccessibility.HighContrast
        ? CreateAiUsageSystemBrush(background: true)
        : ActualTheme == ElementTheme.Light
            ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(96, 18, 23, 34));

    private SolidColorBrush CreateGlassInsetBrush() => _aiUsageAccessibility.HighContrast
        ? CreateAiUsageSystemBrush(background: true)
        : ActualTheme == ElementTheme.Light
            ? new SolidColorBrush(Color.FromArgb(20, 15, 23, 42))
            : new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));

    private SolidColorBrush CreateGlassTabTrackBrush() => _aiUsageAccessibility.HighContrast
        ? CreateAiUsageSystemBrush(background: true)
        : ActualTheme == ElementTheme.Light
            ? new SolidColorBrush(Color.FromArgb(18, 15, 23, 42))
            : new SolidColorBrush(Color.FromArgb(44, 10, 14, 22));

    private SolidColorBrush CreateGlassBorderBrush() => _aiUsageAccessibility.HighContrast
        ? CreateAiUsageSystemBrush(background: false)
        : ActualTheme == ElementTheme.Light
            ? new SolidColorBrush(Color.FromArgb(40, 15, 23, 42))
            : new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));

    private static SolidColorBrush CreateTintedBorderBrush(Color color) => new(color);

    private Brush ResolveTaskAccentBrush(string taskId) => taskId.ToLowerInvariant() switch
    {
        "security-audit" => ResolveAiUsageBrush("KitUsageCpuBrush", ThemeBrushHelper.SecondaryTextBrush),
        "system-optimization" => ResolveAiUsageBrush("KitUsageDiskBrush", ThemeBrushHelper.SecondaryTextBrush),
        "route-analysis" => ResolveAiUsageBrush("KitDataSeriesUdpBrush", ThemeBrushHelper.SecondaryTextBrush),
        "self-test" => ResolveAiUsageBrush("KitUsageMemoryBrush", ThemeBrushHelper.SecondaryTextBrush),
        _ => ResolveAiUsageBrush("KitUsageGpuBrush", ThemeBrushHelper.SecondaryTextBrush),
    };

    private Brush ResolvePhaseAccentBrush(string phase) => phase.ToLowerInvariant() switch
    {
        "reasoning" => ResolveAiUsageBrush("KitUsageMemoryBrush", ThemeBrushHelper.SecondaryTextBrush),
        "responding" => ResolveAiUsageBrush("KitUsageCpuBrush", ThemeBrushHelper.SecondaryTextBrush),
        "validating" => ResolveAiUsageBrush("KitUsageDiskBrush", ThemeBrushHelper.SecondaryTextBrush),
        "retrying" or "failover" => ResolveAiUsageBrush("KitUsageGpuBrush", ThemeBrushHelper.SecondaryTextBrush),
        "failed" => ResolveAiUsageBrush("BadgeCriticalForegroundBrush", ThemeBrushHelper.SecondaryTextBrush),
        _ => ResolveAiUsageBrush("KitDataSeriesUdpBrush", ThemeBrushHelper.SecondaryTextBrush),
    };

    private static string ResolvePhaseBadgePrefix(string phase) => phase.ToLowerInvariant() switch
    {
        "retrying" or "failover" or "queued" => "BadgeWarning",
        "failed" => "BadgeCritical",
        "responding" or "started" or "waiting" => "BadgeInfo",
        _ => "BadgeSuccess",
    };

    private Brush ResolveEventAccentBrush(AiSessionActivityEvent evt) => evt.EventType switch
    {
        "batch_completed" or "task_completed" => ResolveAiUsageBrush("KitUsageDiskBrush", ThemeBrushHelper.SecondaryTextBrush),
        "batch_failed" or "task_failed" => ResolveAiUsageBrush("BadgeCriticalForegroundBrush", ThemeBrushHelper.SecondaryTextBrush),
        "retry" or "failover" or "batch_cancelled" or "task_cancelled" => ResolveAiUsageBrush("KitUsageGpuBrush", ThemeBrushHelper.SecondaryTextBrush),
        _ => ResolvePhaseAccentBrush(evt.Phase),
    };

    private TextBlock CreateUsageText(string text, double baseFontSize, bool mono = false, bool bold = false, bool secondary = false, Brush? foreground = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = TypographyResources.FontSize(baseFontSize),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Foreground = foreground ?? ResolveAiUsageBrush(
                secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush",
                ThemeBrushHelper.SecondaryTextBrush),
        };
        TypographyResources.SetBaseFontSize(block, baseFontSize);
        if (mono && Application.Current?.Resources.TryGetValue("KitMonoFontFamily", out object? monoObj) == true && monoObj is FontFamily monoFamily)
        {
            block.FontFamily = monoFamily;
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        }
        else if (Application.Current?.Resources.TryGetValue("KitUiFontFamily", out object? uiObj) == true && uiObj is FontFamily uiFamily)
        {
            block.FontFamily = uiFamily;
        }

        return block;
    }

    private Brush ResolveAiUsageBrush(string key, Brush fallback) => ThemeBrushHelper.GetBrush(key, fallback, ActualTheme);

    private static string FormatCompactTokens(int tokens)
    {
        if (tokens >= 1_000_000)
        {
            return $"{tokens / 1_000_000.0:0.#}M";
        }

        if (tokens >= 1_000)
        {
            return $"{tokens / 1_000.0:0.#}k";
        }

        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatTaskName(Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader, string pluginId, string taskId) =>
        taskId.ToLowerInvariant() switch
        {
            "security-audit" => loader.GetString("Shell_AiUsageTaskSecurityAudit"),
            "system-optimization" => loader.GetString("Shell_AiUsageTaskSystemOptimization"),
            "route-analysis" => loader.GetString("Shell_AiUsageTaskRouteAnalysis"),
            "self-test" => loader.GetString("Shell_AiUsageTaskSelfTest"),
            _ => string.IsNullOrEmpty(pluginId) ? taskId : $"{pluginId} · {taskId}",
        };

    private static string FormatRoute(Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader, string route) =>
        route.ToLowerInvariant() switch
        {
            "main" => loader.GetString("Shell_AiUsageRouteMain"),
            "fallback" => loader.GetString("Shell_AiUsageRouteFallback"),
            _ => route,
        };

    private static string FormatPhase(Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader, string phase, int retryCount) =>
        phase.ToLowerInvariant() switch
        {
            "preparing" => loader.GetString("Shell_AiUsagePhasePreparing"),
            "queued" => loader.GetString("Shell_AiUsagePhaseQueued"),
            "waiting" => loader.GetString("Shell_AiUsagePhaseWaiting"),
            "started" => loader.GetString("Shell_AiUsagePhaseStarted"),
            "reasoning" => loader.GetString("Shell_AiUsagePhaseReasoning"),
            "responding" => loader.GetString("Shell_AiUsagePhaseResponding"),
            "retrying" => retryCount > 0
                ? string.Format(CultureInfo.CurrentCulture, loader.GetString("Shell_AiUsageRetryFormat"), retryCount)
                : loader.GetString("Shell_AiUsagePhaseRetrying"),
            "validating" => loader.GetString("Shell_AiUsagePhaseValidating"),
            "failover" => loader.GetString("Shell_AiUsagePhaseFailover"),
            "failed" => loader.GetString("Shell_AiUsagePhaseFailed"),
            _ => phase,
        };

    private static string FormatOutcome(Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader, string outcome) =>
        outcome.ToLowerInvariant() switch
        {
            "succeeded" => loader.GetString("Shell_AiUsageOutcomeSucceeded"),
            "failed" => loader.GetString("Shell_AiUsageOutcomeFailed"),
            "cancelled" => loader.GetString("Shell_AiUsageOutcomeCancelled"),
            "timeout" => loader.GetString("Shell_AiUsageOutcomeTimeout"),
            "partial" => loader.GetString("Shell_AiUsageOutcomePartial"),
            _ => outcome,
        };

    private static string FormatEventHeadline(Microsoft.Windows.ApplicationModel.Resources.ResourceLoader loader, AiSessionActivityEvent evt)
    {
        string task = FormatTaskName(loader, evt.PluginId, evt.TaskId);
        string status = evt.EventType switch
        {
            "task_started" => loader.GetString("Shell_AiUsagePhasePreparing"),
            "batch_queued" => loader.GetString("Shell_AiUsagePhaseQueued"),
            "batch_started" => loader.GetString("Shell_AiUsagePhaseStarted"),
            "retry" => FormatPhase(loader, "retrying", evt.RetryCount),
            "failover" => loader.GetString("Shell_AiUsagePhaseFailover"),
            "batch_completed" or "task_completed" => loader.GetString("Shell_AiUsageOutcomeSucceeded"),
            "batch_cancelled" or "task_cancelled" => loader.GetString("Shell_AiUsageOutcomeCancelled"),
            "task_partial" => loader.GetString("Shell_AiUsageOutcomePartial"),
            "batch_failed" or "task_failed" => FormatOutcome(loader, string.IsNullOrEmpty(evt.Outcome) ? "failed" : evt.Outcome),
            _ => FormatPhase(loader, evt.Phase, evt.RetryCount),
        };
        return $"{task} · {status}";
    }

    private static string FormatDurationMs(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "0s";
        }

        if (milliseconds < 1000)
        {
            return $"{milliseconds}ms";
        }

        TimeSpan ts = TimeSpan.FromMilliseconds(milliseconds);
        if (ts.TotalMinutes < 1)
        {
            return $"{ts.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture)}s";
        }

        if (ts.TotalHours < 1)
        {
            return $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s";
        }

        return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
    }

    private static string FormatLocalTimestamp(DateTimeOffset utc)
    {
        DateTimeOffset local = utc.ToLocalTime();
        DateTimeOffset today = DateTimeOffset.Now;
        return local.Date == today.Date
            ? local.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : local.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// High-clarity translucent frosted backdrop for the detached AI Usage flyout:
    /// uses Windows 11 Thin Desktop Acrylic (blurring Kit behind the flyout with tuned
    /// luminosity/tint) and falls back gracefully to Mica BaseAlt when acrylic is unavailable.
    /// </summary>
    private sealed partial class AiUsageTranslucentBackdrop : SystemBackdrop, IDisposable
    {
        private DesktopAcrylicController? _acrylicController;
        private MicaController? _micaController;
        private SystemBackdropConfiguration? _configuration;
        private ElementTheme _theme;

        public AiUsageTranslucentBackdrop(ElementTheme initialTheme)
        {
            _theme = initialTheme;
        }

        public void UpdateTheme(ElementTheme theme)
        {
            _theme = theme;
            ApplyThemeToControllers();
        }

        public void Dispose()
        {
            _acrylicController?.Dispose();
            _acrylicController = null;
            _micaController?.Dispose();
            _micaController = null;
            _configuration = null;
        }

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            Dispose();
            _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
            _configuration.IsInputActive = true;

            if (DesktopAcrylicController.IsSupported())
            {
                _acrylicController = new DesktopAcrylicController
                {
                    Kind = DesktopAcrylicKind.Thin,
                };
                ApplyThemeToControllers();
                _acrylicController.AddSystemBackdropTarget(connectedTarget);
            }
            else if (MicaController.IsSupported())
            {
                _micaController = new MicaController
                {
                    Kind = MicaKind.BaseAlt,
                };
                ApplyThemeToControllers();
                _micaController.AddSystemBackdropTarget(connectedTarget);
            }
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            base.OnTargetDisconnected(disconnectedTarget);
            Dispose();
        }

        protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
        {
            base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
            _configuration = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
            _configuration.IsInputActive = true;
            ApplyThemeToControllers();
        }

        private void ApplyThemeToControllers()
        {
            if (_configuration is not null)
            {
                _configuration.Theme = _theme switch
                {
                    ElementTheme.Dark => SystemBackdropTheme.Dark,
                    ElementTheme.Light => SystemBackdropTheme.Light,
                    _ => SystemBackdropTheme.Default,
                };
            }

            if (_acrylicController is not null)
            {
                if (_theme == ElementTheme.Light)
                {
                    _acrylicController.TintColor = Color.FromArgb(255, 244, 248, 255);
                    _acrylicController.FallbackColor = Color.FromArgb(255, 243, 246, 251);
                    _acrylicController.TintOpacity = 0.36f;
                    _acrylicController.LuminosityOpacity = 0.68f;
                }
                else
                {
                    _acrylicController.TintColor = Color.FromArgb(255, 22, 28, 40);
                    _acrylicController.FallbackColor = Color.FromArgb(255, 24, 28, 36);
                    _acrylicController.TintOpacity = 0.28f;
                    _acrylicController.LuminosityOpacity = 0.56f;
                }

                if (_configuration is not null)
                {
                    _acrylicController.SetSystemBackdropConfiguration(_configuration);
                }
            }
            else if (_micaController is not null && _configuration is not null)
            {
                _micaController.SetSystemBackdropConfiguration(_configuration);
            }
        }
    }

    private sealed record AiUsageUpdate(
        AiDiagnosticsSnapshot? Snapshot,
        AiUsageOverview? Overview,
        AiSessionActivitySnapshot? Activity,
        string ActiveKernel,
        long Generation,
        long Version);
}
