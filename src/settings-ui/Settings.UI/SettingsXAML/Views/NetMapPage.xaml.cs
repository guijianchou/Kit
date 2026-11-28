// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using NetMapLib;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;

namespace Kit.Settings.UI.Views;

public sealed partial class NetMapPage : NavigablePage, IRefreshablePage, ILiveLocalizable
{
    private readonly MainWindow settingsWindow;
    private readonly Dictionary<int, FrameworkElement> hopMarkers = new();
    private readonly List<Storyboard> activeMapAnimations = new();
    private readonly List<FrameworkElement> activePulseRings = new();
    private bool heroPulseRunning;
    private bool windowSubscribed;
    private bool isCurrentPage;
    private bool mapLoaded;
    private NetMapSnapshot renderedSnapshot;
    private int renderedSelection = -1;
    private double renderedUserZoom = 1;
    private double userZoomFactor = 1;
    private MapViewport mapViewport = MapViewport.Fit([], 960, 480);
    private bool mapResizeQueued;

    public NetMapPage()
    {
        var settingsUtils = SettingsUtils.Default;
        ViewModel = new NetMapViewModel(
            SettingsRepository<GeneralSettings>.GetInstance(settingsUtils),
            SettingsRepository<NetMapSettings>.GetInstance(settingsUtils),
            ShellPage.SendDefaultIPCMessage);
        InitializeComponent();
        NetMapHealthStylesStore.Attach((ResourceDictionary)Resources["NetMapHealthStyles"]);
        DataContext = ViewModel;
        settingsWindow = App.GetSettingsWindow();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
        ActualThemeChanged += Page_ActualThemeChanged;
        ViewModel.MapChanged += DrawPoints;
    }

    public NetMapViewModel ViewModel { get; }

    public void RefreshEnabledState() => ViewModel.RefreshEnabledState();

    public void RefreshLocalization()
    {
        renderedSnapshot = null;
        DrawPoints();
    }

    public Style HealthDotStyle(ProbeHealth health) => (Style)HealthStyles[NetMapHealthStylesStore.Key(health, "Dot")];

    public Style HealthTextStyle(ProbeHealth health) => (Style)HealthStyles[NetMapHealthStylesStore.Key(health, "Text")];

    public Style HealthBoxStyle(ProbeHealth health) => (Style)HealthStyles[NetMapHealthStylesStore.Key(health, "Box")];

    public Style HealthPillStyle(ProbeHealth health) => (Style)HealthStyles[NetMapHealthStylesStore.Key(health, "Pill")];

    private ResourceDictionary HealthStyles => (ResourceDictionary)Resources["NetMapHealthStyles"];

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        isCurrentPage = true;

        // Returning during a transition can reuse a page without another Loaded event.
        ViewModel.RefreshEnabledState();
        UpdateVisibility();
        UpdateHeroPulse();
        DrawPoints();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        // Deactivate page-specific work while the cached page keeps its detection session.
        isCurrentPage = false;
        mapDragPosition = null;
        MapView.ReleasePointerCaptures();
        StopHeroPulse();
        StopMapAnimations();
        ViewModel.SetPageActive(false);
        base.OnNavigatedFrom(e);
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!windowSubscribed)
        {
            settingsWindow.AppWindow.Changed += Window_Changed;
            settingsWindow.Closed += Window_Closed;
            windowSubscribed = true;
        }

        ViewModel.RefreshEnabledState();
        UpdateVisibility();
        if (!mapLoaded)
        {
            overviewGeometry = BuildMapGeometry(WorldMap.Countries.SelectMany(country => country.Rings), closed: true, null);
            WorldOutline.Data = overviewGeometry;
            WorldGraticule.Data = BuildGraticuleGeometry();
            mapLoaded = true;
        }

        UpdateHeroPulse();
        DrawPoints();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        mapDragPosition = null;
        MapView.ReleasePointerCaptures();

        // Keep receiving window lifetime events while detection runs on another page.
        StopHeroPulse();
        StopMapAnimations();
        ViewModel.SetPageActive(false);
    }

    private void UnsubscribeWindow()
    {
        if (windowSubscribed)
        {
            settingsWindow.AppWindow.Changed -= Window_Changed;
            settingsWindow.Closed -= Window_Closed;
            windowSubscribed = false;
        }
    }

    private void Window_Changed(AppWindow sender, AppWindowChangedEventArgs args) => UpdateVisibility();

    private void UpdateVisibility()
    {
        bool visible = settingsWindow.AppWindow.IsVisible &&
            !(settingsWindow.AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized);
        ViewModel.SetPageActive(isCurrentPage && IsLoaded && visible);
        if (!settingsWindow.AppWindow.IsVisible)
        {
            ViewModel.IsDetectionOn = false;
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        StopHeroPulse();
        StopMapAnimations();
        ViewModel.SetPageActive(false);
        ViewModel.IsDetectionOn = false;
        if (!args.Handled)
        {
            UnsubscribeWindow();
            ViewModel.MapChanged -= DrawPoints;
            ViewModel.Dispose();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => ViewModel.IsDetectionOn = false;

    private void Start_Click(object sender, RoutedEventArgs e) => ViewModel.IsDetectionOn = true;

    private async void CopyIp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string ip || string.IsNullOrWhiteSpace(ip) || ip == "—" || ip == "N/A")
        {
            return;
        }

        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(ip);
            Clipboard.SetContent(dataPackage);

            if (btn.Content is FontIcon icon)
            {
                icon.Glyph = "\uE73E";
                await Task.Delay(1500);
                if (IsLoaded)
                {
                    icon.Glyph = "\uE8C8";
                }
            }
        }
        catch (Exception ex)
        {
            ManagedCommon.Logger.LogError("Settings action CopyIp_Click failed.", ex);
        }
    }

    private void MapZoomIn_Click(object sender, RoutedEventArgs e)
    {
        ZoomMap(1.35, new(MapView.ActualWidth / 2, MapView.ActualHeight / 2));
    }

    private void MapZoomOut_Click(object sender, RoutedEventArgs e)
    {
        ZoomMap(1 / 1.35, new(MapView.ActualWidth / 2, MapView.ActualHeight / 2));
    }

    private void MapZoomReset_Click(object sender, RoutedEventArgs e)
    {
        userZoomFactor = 1.0;
        manualMapViewport = null;
        renderedSnapshot = null;
        DrawPoints();
    }

    private void MapView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MapViewportClip == null)
        {
            return;
        }

        MapViewportClip.Rect = new(0, 0, e.NewSize.Width, e.NewSize.Height);
        manualMapViewport = manualMapViewport?.Resize(e.NewSize.Width, e.NewSize.Height);
        foreach (var layer in new[] { MapLabels, MapLinesGlow, MapLines, MapPulseLayer, MapPoints })
        {
            layer.Width = e.NewSize.Width;
            layer.Height = e.NewSize.Height;
        }

        // Several measurements can occur in the same layout pass at the stacking
        // breakpoint. Redraw once after layout settles, using the current size.
        renderedSnapshot = null;
        if (!mapResizeQueued)
        {
            mapResizeQueued = true;
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                mapResizeQueued = false;
                DrawPoints();
            }))
            {
                mapResizeQueued = false;
            }
        }
    }

    private async void AiCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.CheckRouteWithAiAsync();
        }
        catch (System.Exception ex)
        {
            ManagedCommon.Logger.LogError("Settings action AiCheck_Click failed.", ex);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ViewModel.ApplySettings();

    private async void UpdateAsn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.UpdateAsnAsync();
        }
        catch (System.Exception ex)
        {
            ManagedCommon.Logger.LogError("Settings action UpdateAsn_Click failed.", ex);
        }
    }

    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => ViewModel.CancelDataUpdate();

    private async void BrowseDatabase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".mmdb");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow));
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                if ((sender as Button)?.Tag?.ToString() == "ASN")
                {
                    ViewModel.AsnDatabase = file.Path;
                }
                else
                {
                    ViewModel.CityDatabase = file.Path;
                }
            }
        }
        catch (Exception)
        {
            // The editable path field remains available if the system picker fails.
            ViewModel.ReportLocalizedMessage("PickerFailed", InfoBarSeverity.Error);
        }
    }

    private void Page_ActualThemeChanged(FrameworkElement sender, object args)
    {
        renderedSnapshot = null;
        DispatcherQueue.TryEnqueue(DrawPoints);
    }

    private void HopList_SelectionChanged(object sender, SelectionChangedEventArgs e) => DispatcherQueue.TryEnqueue(DrawPoints);

    private void HopList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NetMapHopRow row)
        {
            int clickedIndex = ViewModel.Hops.IndexOf(row);
            if (clickedIndex >= 0 && HopList.SelectedIndex == clickedIndex)
            {
                DispatcherQueue.TryEnqueue(() => HopList.SelectedIndex = -1);
            }
        }
    }

    private void UpdateHeroPulse()
    {
        bool shouldPulse = isCurrentPage && IsLoaded && ViewModel.IsDetectionOn;
        if (shouldPulse == heroPulseRunning)
        {
            return;
        }

        heroPulseRunning = shouldPulse;
        if (shouldPulse)
        {
            KitMotionHelper.StartPulseRing(HeroPulseRing, 10d, maxScale: 2.35f, startOpacity: 0.55f, durationMs: 1700);
        }
        else
        {
            KitMotionHelper.StopPulseRing(HeroPulseRing);
        }
    }

    private void StopHeroPulse()
    {
        heroPulseRunning = false;
        KitMotionHelper.StopPulseRing(HeroPulseRing);
    }

    private void StopMapAnimations()
    {
        foreach (var storyboard in activeMapAnimations)
        {
            storyboard.Stop();
        }

        activeMapAnimations.Clear();

        foreach (var ring in activePulseRings)
        {
            KitMotionHelper.StopPulseRing(ring);
        }

        activePulseRings.Clear();
    }

    private static PathGeometry BuildGraticuleGeometry(int step = 30)
    {
        var geometry = new PathGeometry();
        foreach (double offset in new[] { -960.0, 0.0, 960.0 })
        {
            for (int lat = -90 + step; lat < 90; lat += step)
            {
                double y = (90 - lat) * 480.0 / 180.0;
                var fig = new PathFigure { StartPoint = new(offset, y), IsClosed = false, IsFilled = false };
                fig.Segments.Add(new LineSegment { Point = new(offset + 960.0, y) });
                geometry.Figures.Add(fig);
            }

            for (int lon = -180 + step; lon <= 180; lon += step)
            {
                double x = ((lon + 180) * 960.0 / 360.0) + offset;
                var fig = new PathFigure { StartPoint = new(x, 0), IsClosed = false, IsFilled = false };
                fig.Segments.Add(new LineSegment { Point = new(x, 480.0) });
                geometry.Figures.Add(fig);
            }
        }

        return geometry;
    }

    private Brush ResolvePageBrush(string resourceKey)
    {
        bool highContrast = false;
        try
        {
            highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        }
        catch
        {
        }

        string themeKey = highContrast
            ? "HighContrast"
            : ActualTheme == ElementTheme.Light ? "Light" : "Default";
        if (Resources.ThemeDictionaries.TryGetValue(themeKey, out object dictObj) &&
            dictObj is ResourceDictionary themeDict &&
            themeDict.TryGetValue(resourceKey, out object brushObj) &&
            brushObj is Brush themedBrush)
        {
            return themedBrush;
        }

        return Resources.TryGetValue(resourceKey, out object fallback) && fallback is Brush b
            ? b
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void DrawPoints()
    {
        UpdateHeroPulse();
        if (!isCurrentPage || !IsLoaded || MapPoints == null || MapDescription == null || MapView.ActualWidth <= 0 || MapView.ActualHeight <= 0)
        {
            return;
        }

        var snapshot = ViewModel.Snapshot;
        if (mapGeneration != snapshot.Generation)
        {
            mapGeneration = snapshot.Generation;
            manualMapViewport = null;
            userZoomFactor = 1;
        }

        int selected = HopList.SelectedIndex;
        bool redraw = renderedSnapshot == null || renderedSelection != selected || Math.Abs(renderedUserZoom - userZoomFactor) > 0.001 ||
            renderedSnapshot.Generation != snapshot.Generation || renderedSnapshot.Running != snapshot.Running ||
            renderedSnapshot.Direct.LastSuccess?.Identity != snapshot.Direct.LastSuccess?.Identity || renderedSnapshot.Proxy.LastSuccess?.Identity != snapshot.Proxy.LastSuccess?.Identity ||
            renderedSnapshot.Direct.Geography != snapshot.Direct.Geography || renderedSnapshot.Proxy.Geography != snapshot.Proxy.Geography ||
            renderedSnapshot.Direct.Latest?.Success != snapshot.Direct.Latest?.Success || renderedSnapshot.Proxy.Latest?.Success != snapshot.Proxy.Latest?.Success ||
            !renderedSnapshot.Hops.Select(hop => (hop.Ttl, hop.Ip, hop.Geography)).SequenceEqual(snapshot.Hops.Select(hop => (hop.Ttl, hop.Ip, hop.Geography)));
        if (!redraw)
        {
            foreach (var marker in hopMarkers)
            {
                if (marker.Key < ViewModel.Hops.Count)
                {
                    ToolTipService.SetToolTip(marker.Value, ViewModel.Hops[marker.Key].Detail);
                    if (marker.Value is Grid grid && grid.Children[0] is Shape shape)
                    {
                        shape.Style = HealthDotStyle(ViewModel.Hops[marker.Key].Health);
                    }
                }
            }

            if (selected >= 0 && selected < ViewModel.Hops.Count)
            {
                MapDescription.Text = ViewModel.Hops[selected].Detail;
            }

            return;
        }

        StopMapAnimations();
        MapPoints.Children.Clear();
        MapPulseLayer.Children.Clear();
        MapLinesGlow.Children.Clear();
        MapLines.Children.Clear();
        hopMarkers.Clear();
        renderedSnapshot = snapshot;
        renderedSelection = selected;
        renderedUserZoom = userZoomFactor;
        var origin = EgressPosition(snapshot.Direct);
        var target = EgressPosition(snapshot.Proxy);
        var positions = snapshot.Hops.Where(hop => hop.Geography is { Latitude: not null, Longitude: not null }).Select(hop => WorldMap.Locate(string.Empty, hop.Geography)).Where(point => point != null).ToList();
        if (origin != null)
        {
            positions.Add(origin);
        }

        if (target != null)
        {
            positions.Add(target);
        }

        FitMap(positions);
        var descriptions = new List<string>();
        DrawEgress(NetMapViewModel.Text("Direct"), snapshot.Direct, false, descriptions);
        DrawEgress(NetMapViewModel.Text("Proxy"), snapshot.Proxy, true, descriptions);
        MapCoordinate previous = origin;
        int previousTtl = 0;
        bool previousIsOrigin = true;
        bool targetInRoute = false;
        int labelIndex = selected;
        for (int i = 0; i < snapshot.Hops.Count; i++)
        {
            var hop = snapshot.Hops[i];
            if (hop.Geography is not { Longitude: not null, Latitude: not null } geo)
            {
                continue;
            }

            var point = WorldMap.Locate(string.Empty, geo);
            if (point == null)
            {
                continue;
            }

            if (previous != null)
            {
                AddRouteLine(previous, point, previousIsOrigin || hop.Ttl != previousTtl + 1);
            }

            targetInRoute = hop.Ip == snapshot.Proxy.LastSuccess?.Identity?.Ip;
            previous = point;
            previousTtl = hop.Ttl;
            previousIsOrigin = false;
            if (i < ViewModel.Hops.Count)
            {
                int index = i;
                bool highlight = selected == i || (targetInRoute && selected < 0);
                var marker = AddPoint(point, ViewModel.Hops[i].Detail, true, !snapshot.Running, selected == i ? 30 : targetInRoute ? 24 : 20, ViewModel.Hops[i].Ttl, ViewModel.Hops[i].Health, pulse: snapshot.Running && highlight);
                Canvas.SetZIndex(marker, selected == i ? 3 : targetInRoute ? 2 : 1);
                marker.Tapped += (_, _) => HopList.SelectedIndex = HopList.SelectedIndex == index ? -1 : index;
                hopMarkers[i] = marker;
            }
        }

        if (previous != null && target != null && !targetInRoute)
        {
            AddRouteLine(previous, target, true);
        }

        if (labelIndex >= 0 && labelIndex < snapshot.Hops.Count && labelIndex < ViewModel.Hops.Count &&
            WorldMap.Locate(string.Empty, snapshot.Hops[labelIndex].Geography) is { } labelCoordinate)
        {
            var position = Position(labelCoordinate);
            var row = ViewModel.Hops[labelIndex];
            var content = new StackPanel { Spacing = 3 };
            var heading = new TextBlock { Style = (Style)Resources["NetMapMapLabelStyle"], Tag = nameof(NetMapHopRow.MapHeading), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            heading.SetBinding(TextBlock.TextProperty, new Binding { Source = row, Path = new PropertyPath(nameof(NetMapHopRow.MapHeading)), Mode = BindingMode.OneWay });
            var close = new Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                Style = (Style)Application.Current.Resources["KitCompactIconButtonStyle"],
                Width = 24,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Top,
                Tag = "CloseHopDetails",
            };
            string closeText = NetMapViewModel.Text("CloseDetails");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, closeText);
            ToolTipService.SetToolTip(close, closeText);
            close.Click += (_, _) => HopList.SelectedIndex = -1;
            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(close, 1);
            header.Children.Add(heading);
            header.Children.Add(close);
            foreach (string property in new[] { nameof(NetMapHopRow.MapLocation), nameof(NetMapHopRow.MapAsn), nameof(NetMapHopRow.MapMetrics) })
            {
                var text = new TextBlock { Style = (Style)Resources["NetMapMapLabelStyle"], Tag = property };
                text.SetBinding(TextBlock.TextProperty, new Binding { Source = row, Path = new PropertyPath(property), Mode = BindingMode.OneWay });
                content.Children.Add(text);
            }

            // Keep all details reachable with large fonts or long ASN names, without
            // allowing the card to extend beyond the map or cover its zoom controls.
            var scroll = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Auto,
            };
            var details = new Grid { RowSpacing = 3 };
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(scroll, 1);
            details.Children.Add(header);
            details.Children.Add(scroll);
            var label = new Border
            {
                Child = details,
                Tag = "HopDetails",
                Width = Math.Max(1, Math.Min(320, mapViewport.Width - 16)),
                MaxHeight = Math.Max(1, mapViewport.Height - 96),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(6),
                Style = (Style)Resources["NetMapMapLabelBackgroundStyle"],
            };
            label.SetBinding(ToolTipService.ToolTipProperty, new Binding { Source = row, Path = new PropertyPath(nameof(NetMapHopRow.Detail)), Mode = BindingMode.OneWay });
            label.SizeChanged += (_, _) => PlaceMapLabel(label, position);
            MapPoints.Children.Add(label);
            Canvas.SetZIndex(label, 4);
            label.Measure(new Size(label.Width, label.MaxHeight));
            PlaceMapLabel(label, position);
        }

        MapDescription.Text = selected >= 0 && selected < ViewModel.Hops.Count ? ViewModel.Hops[selected].Detail : descriptions.Count == 0 ? NetMapViewModel.Text("MapEmpty") : string.Join("   /   ", descriptions);
        DrawGeographicLabels(positions);
    }

    private static MapCoordinate EgressPosition(ChannelState channel) => channel.LastSuccess?.Identity is { } identity ? WorldMap.Locate(string.IsNullOrEmpty(identity.CountryCode) ? channel.Geography?.CountryCode ?? string.Empty : identity.CountryCode, channel.Geography) : null;

    private void PlaceMapLabel(FrameworkElement label, Point position)
    {
        double width = label.DesiredSize.Width;
        double height = label.DesiredSize.Height;
        double top = position.Y + 20;
        if (top + height > mapViewport.Height - 40)
        {
            top = position.Y - height - 20;
        }

        Canvas.SetLeft(label, Math.Clamp(position.X - (width / 2), 8, Math.Max(8, mapViewport.Width - width - 8)));
        Canvas.SetTop(label, Math.Clamp(top, 48, Math.Max(48, mapViewport.Height - height - 40)));
    }

    private void FitMap(IReadOnlyList<MapCoordinate> coordinates)
    {
        mapViewport = manualMapViewport ?? MapViewport.Fit(coordinates, MapView.ActualWidth, MapView.ActualHeight, userZoomFactor);
        UpdateMapDetail();
        var matrix = new Matrix(mapViewport.Scale, 0, 0, mapViewport.Scale, mapViewport.OffsetX, mapViewport.OffsetY);
        WorldOutline.RenderTransform = new MatrixTransform { Matrix = matrix };
        WorldGraticule.RenderTransform = new MatrixTransform { Matrix = matrix };
        WorldCoastline.RenderTransform = new MatrixTransform { Matrix = matrix };

        // RenderTransform also scales strokes: compensate so coastlines remain
        // crisp both at world scale and when a single region is enlarged.
        WorldOutline.StrokeThickness = 1.0 / mapViewport.Scale;
        WorldGraticule.StrokeThickness = 0.6 / mapViewport.Scale;
        WorldCoastline.StrokeThickness = 1.2 / mapViewport.Scale;
        UpdateMapScale();
    }

    private void AddRouteLine(MapCoordinate from, MapCoordinate to, bool approximate)
    {
        var start = Position(from);
        var end = Position(to);
        double worldWidth = MapViewport.WorldWidth * mapViewport.Scale;
        if (Math.Abs(end.X - start.X) > worldWidth / 2)
        {
            double offset = end.X < start.X ? worldWidth : -worldWidth;
            AddSegment(start, new(end.X + offset, end.Y));
            AddSegment(new(start.X - offset, start.Y), end);
        }
        else
        {
            AddSegment(start, end);
        }

        void AddSegment(Point first, Point second)
        {
            if (!approximate && ViewModel.IsDetectionOn)
            {
                var glow = new Polyline { Style = (Style)Resources["NetMapRouteGlowStyle"], Opacity = 0.75 };
                glow.Points.Add(first);
                glow.Points.Add(second);
                MapLinesGlow.Children.Add(glow);
            }

            var line = new Polyline { Style = (Style)Resources["NetMapRouteLineStyle"], Opacity = ViewModel.IsDetectionOn ? 0.95 : 0.4, Tag = approximate ? "Approximate" : "Observed" };
            if (approximate)
            {
                line.StrokeDashArray = new DoubleCollection { 4, 3 };
            }

            line.Points.Add(first);
            line.Points.Add(second);
            MapLines.Children.Add(line);

            if (!approximate && ViewModel.IsDetectionOn)
            {
                var flow = new Polyline
                {
                    Style = (Style)Resources["NetMapRouteFlowStyle"],
                    StrokeDashArray = new DoubleCollection { 4, 10 },
                    Opacity = 0.72,
                };
                flow.Points.Add(first);
                flow.Points.Add(second);
                MapLinesGlow.Children.Add(flow);
                if (KitMotionHelper.StartDashFlow(flow, cycleLength: 14d, durationSeconds: 1.7) is { } anim)
                {
                    activeMapAnimations.Add(anim);
                }
            }
        }
    }

    private void DrawEgress(string name, ChannelState channel, bool proxy, List<string> descriptions)
    {
        var identity = channel.LastSuccess?.Identity;
        if (identity == null)
        {
            return;
        }

        string code = string.IsNullOrEmpty(identity.CountryCode) ? channel.Geography?.CountryCode ?? string.Empty : identity.CountryCode;
        var coordinate = WorldMap.Locate(code, channel.Geography);
        bool countryOnly = channel.Geography?.Latitude == null || (!string.IsNullOrEmpty(channel.Geography.CountryCode) && code != channel.Geography.CountryCode);
        string precision = NetMapViewModel.Text(countryOnly ? "CountryPoint" : "ApproxLocation");
        var caption = name + " \u00b7 " + NetMapViewModel.LocationText(identity, channel.Geography) + " \u00b7 " + (coordinate == null ? NetMapViewModel.Text("UnknownLocation") : precision);
        bool stale = !ViewModel.IsDetectionOn || channel.Latest?.Success != true;
        if (stale)
        {
            caption += " \u00b7 " + NetMapViewModel.Text("LastKnown");
        }

        descriptions.Add(caption);
        if (coordinate != null)
        {
            var health = !ViewModel.IsDetectionOn || channel.Latest == null ? ProbeHealth.Unknown : channel.Latest.Success ? ProbeHealth.Good : ProbeHealth.Error;
            AddPoint(coordinate, caption, proxy, stale, proxy ? 12 : 14, health: health, pulse: !stale && proxy);
        }
    }

    private FrameworkElement AddPoint(MapCoordinate coordinate, string label, bool proxy, bool stale, double size, string number = null, ProbeHealth? health = null, bool pulse = false)
    {
        var position = Position(coordinate);
        if (pulse && MapPulseLayer != null)
        {
            var halo = new Ellipse
            {
                Width = size,
                Height = size,
                Style = health.HasValue ? HealthDotStyle(health.Value) : (Style)Resources["NetMapProxyMarkerStyle"],
                Opacity = 0,
            };
            Canvas.SetLeft(halo, position.X - (size / 2));
            Canvas.SetTop(halo, position.Y - (size / 2));
            MapPulseLayer.Children.Add(halo);
            activePulseRings.Add(halo);
            KitMotionHelper.StartPulseRing(halo, size, maxScale: 1.95f, startOpacity: 0.48f, durationMs: 1900);
        }

        Shape mark = proxy ? new Ellipse() : new Rectangle { RadiusX = 4, RadiusY = 4 };
        mark.Style = health.HasValue ? (proxy ? HealthDotStyle(health.Value) : HealthBoxStyle(health.Value)) : (Style)Resources[proxy ? "NetMapProxyMarkerStyle" : "NetMapDirectMarkerStyle"];
        mark.Stroke = ResolvePageBrush("NetMapMarkerHaloStrokeBrush");
        mark.StrokeThickness = 2.2;
        mark.Width = size;
        mark.Height = size;
        mark.Opacity = stale ? 0.5 : 0.96;
        var container = new Grid { Width = size, Height = size };
        container.Children.Add(mark);
        if (number != null)
        {
            container.Children.Add(new TextBlock { Text = number, FontSize = TypographyResources.FontSize(12), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Style = (Style)Resources["NetMapHopNumberStyle"], HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }

        ToolTipService.SetToolTip(container, label);
        Canvas.SetLeft(container, position.X - (size / 2));
        Canvas.SetTop(container, position.Y - (size / 2));
        MapPoints.Children.Add(container);
        return container;
    }

    private Point Position(MapCoordinate coordinate)
    {
        var point = mapViewport.Position(coordinate);
        return new(point.X, point.Y);
    }

    private static Point Project(MapCoordinate coordinate) => new((coordinate.Longitude + 180) * 960 / 360, (90 - coordinate.Latitude) * 480 / 180);
}
