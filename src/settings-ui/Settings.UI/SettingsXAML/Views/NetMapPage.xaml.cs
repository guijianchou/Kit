// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
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

public sealed partial class NetMapPage : NavigablePage, IRefreshablePage
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
    private const double MapFitMaxWidth = 800;
    private const double MapFitMaxHeight = 340;
    private const double MapFitMinScale = 1;
    private const double MapFitMaxScale = 4;
    private double userZoomFactor = 1;
    private double mapScale = 1;
    private double mapCenterX = 480;
    private double mapOffsetX;
    private double mapOffsetY;

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
        KitMotionHelper.AttachHoverLift(DirectCard, -1.5f);
        KitMotionHelper.AttachHoverLift(ProxyCard, -1.5f);
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
        ActualThemeChanged += Page_ActualThemeChanged;
        ViewModel.MapChanged += DrawPoints;
    }

    public NetMapViewModel ViewModel { get; }

    public void RefreshEnabledState() => ViewModel.RefreshEnabledState();

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
            var geometry = new PathGeometry();
            foreach (var country in WorldMap.Countries)
            {
                foreach (var ring in country.Rings)
                {
                    if (ring.Length < 3)
                    {
                        continue;
                    }

                    // Adjacent world copies keep the basemap continuous when the route crosses the date line.
                    foreach (double offset in new[] { -960.0, 0, 960 })
                    {
                        var start = Project(ring[0]);
                        var figure = new PathFigure { StartPoint = new(start.X + offset, start.Y), IsClosed = true, IsFilled = true };
                        var segment = new PolyLineSegment();
                        for (int i = 1; i < ring.Length; i++)
                        {
                            var point = Project(ring[i]);
                            segment.Points.Add(new(point.X + offset, point.Y));
                        }

                        figure.Segments.Add(segment);
                        geometry.Figures.Add(figure);
                    }
                }
            }

            WorldOutline.Data = geometry;
            WorldGraticule.Data = BuildGraticuleGeometry();
            mapLoaded = true;
        }

        UpdateHeroPulse();
        DrawPoints();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
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
        userZoomFactor = Math.Clamp(userZoomFactor * 1.35, 1.0, 4.0);
        DrawPoints();
    }

    private void MapZoomOut_Click(object sender, RoutedEventArgs e)
    {
        userZoomFactor = Math.Clamp(userZoomFactor / 1.35, 1.0, 4.0);
        DrawPoints();
    }

    private void MapZoomReset_Click(object sender, RoutedEventArgs e)
    {
        userZoomFactor = 1.0;
        DrawPoints();
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
            ViewModel.ReportMessage(NetMapViewModel.Text("PickerFailed"), InfoBarSeverity.Error);
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

    private static PathGeometry BuildGraticuleGeometry()
    {
        var geometry = new PathGeometry();
        foreach (double offset in new[] { -960.0, 0.0, 960.0 })
        {
            for (int lat = -60; lat <= 60; lat += 30)
            {
                double y = (90 - lat) * 480.0 / 180.0;
                var fig = new PathFigure { StartPoint = new(offset, y), IsClosed = false, IsFilled = false };
                fig.Segments.Add(new LineSegment { Point = new(offset + 960.0, y) });
                geometry.Figures.Add(fig);
            }

            for (int lon = -135; lon <= 180; lon += 45)
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
        if (!isCurrentPage || !IsLoaded || MapPoints == null || MapDescription == null)
        {
            return;
        }

        var snapshot = ViewModel.Snapshot;
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
        var positions = snapshot.Hops.Where(hop => hop.Geography is { Latitude: not null, Longitude: not null }).Select(hop => WorldMap.Locate(string.Empty, hop.Geography)).ToList();
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
            if (previous != null)
            {
                AddRouteLine(previous, point, previousIsOrigin || hop.Ttl != previousTtl + 1);
            }

            targetInRoute = hop.Ip == snapshot.Proxy.LastSuccess?.Identity?.Ip;
            if (targetInRoute && selected < 0)
            {
                labelIndex = i;
            }

            previous = point;
            previousTtl = hop.Ttl;
            previousIsOrigin = false;
            if (i < ViewModel.Hops.Count)
            {
                int index = i;
                bool highlight = selected == i || (targetInRoute && selected < 0);
                var marker = AddPoint(point, ViewModel.Hops[i].Detail, true, !snapshot.Running, selected == i ? 42 : 32, ViewModel.Hops[i].Ttl, ViewModel.Hops[i].Health, pulse: snapshot.Running && highlight);
                marker.Tapped += (_, _) => HopList.SelectedIndex = HopList.SelectedIndex == index ? -1 : index;
                hopMarkers[i] = marker;
            }
        }

        if (previous != null && target != null && !targetInRoute)
        {
            AddRouteLine(previous, target, true);
        }

        if (labelIndex >= 0 && labelIndex < snapshot.Hops.Count && labelIndex < ViewModel.Hops.Count && snapshot.Hops[labelIndex].Geography is { Latitude: not null, Longitude: not null } labelGeo)
        {
            var position = Position(WorldMap.Locate(string.Empty, labelGeo));
            var text = new TextBlock { Style = (Style)Resources["NetMapMapLabelStyle"] };
            text.SetBinding(TextBlock.TextProperty, new Binding { Source = ViewModel.Hops[labelIndex], Path = new PropertyPath(nameof(NetMapHopRow.MapLabel)), Mode = BindingMode.OneWay });
            var label = new Border { Child = text, Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(8), Style = (Style)Resources["NetMapMapLabelBackgroundStyle"] };
            Canvas.SetLeft(label, Math.Clamp(position.X - 100, 8, 620));
            Canvas.SetTop(label, Math.Clamp(position.Y + 28, 8, 424));
            MapPoints.Children.Add(label);
        }

        MapDescription.Text = selected >= 0 && selected < ViewModel.Hops.Count ? ViewModel.Hops[selected].Detail : descriptions.Count == 0 ? NetMapViewModel.Text("MapEmpty") : string.Join("   /   ", descriptions);
    }

    private static MapCoordinate EgressPosition(ChannelState channel) => channel.LastSuccess?.Identity is { } identity ? WorldMap.Locate(string.IsNullOrEmpty(identity.CountryCode) ? channel.Geography?.CountryCode ?? string.Empty : identity.CountryCode, channel.Geography) : null;

    private void FitMap(IReadOnlyList<MapCoordinate> coordinates)
    {
        double baseScale = 1;
        double centerY = 240;
        mapCenterX = 480;
        if (coordinates.Count >= 2)
        {
            var points = coordinates.Select(Project).ToArray();
            var longitudes = points.Select(point => point.X).Order().ToArray();
            double largestGap = -1;
            double left = 0;

            // Put the seam in the largest empty longitude interval, outside the occupied arc.
            for (int i = 0; i < longitudes.Length; i++)
            {
                double next = i + 1 < longitudes.Length ? longitudes[i + 1] : longitudes[0] + 960;
                double gap = next - longitudes[i];
                if (gap > largestGap)
                {
                    largestGap = gap;
                    left = next % 960;
                }
            }

            double width = 960 - largestGap;
            mapCenterX = (left + (width / 2)) % 960;
            double top = points.Min(point => point.Y);
            double height = points.Max(point => point.Y) - top;
            centerY = top + (height / 2);
            baseScale = Math.Clamp(Math.Min(MapFitMaxWidth / Math.Max(width, 40), MapFitMaxHeight / Math.Max(height, 30)), MapFitMinScale, MapFitMaxScale);
        }

        mapScale = Math.Clamp(baseScale * userZoomFactor, MapFitMinScale, MapFitMaxScale * 2);
        mapOffsetX = 480 - (mapCenterX * mapScale);
        mapOffsetY = Math.Clamp(240 - (centerY * mapScale), 480 - (480 * mapScale), 0);

        var matrix = new Matrix(mapScale, 0, 0, mapScale, mapOffsetX, mapOffsetY);
        WorldOutline.RenderTransform = new MatrixTransform { Matrix = matrix };
        WorldGraticule.RenderTransform = new MatrixTransform { Matrix = matrix };
    }

    private void AddRouteLine(MapCoordinate from, MapCoordinate to, bool approximate)
    {
        var start = Position(from);
        var end = Position(to);
        double worldWidth = 960 * mapScale;
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
            AddPoint(coordinate, caption, proxy, stale, proxy ? 18 : 20, health: health, pulse: !stale && proxy);
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
            container.Children.Add(new TextBlock { Text = number, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Style = (Style)Resources["NetMapHopNumberStyle"], HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }

        ToolTipService.SetToolTip(container, label);
        Canvas.SetLeft(container, position.X - (size / 2));
        Canvas.SetTop(container, position.Y - (size / 2));
        MapPoints.Children.Add(container);
        return container;
    }

    private Point Position(MapCoordinate coordinate)
    {
        var point = Project(coordinate);
        double x = mapCenterX + (((point.X - mapCenterX + 1440) % 960) - 480);
        return new((x * mapScale) + mapOffsetX, (point.Y * mapScale) + mapOffsetY);
    }

    private static Point Project(MapCoordinate coordinate) => new((coordinate.Longitude + 180) * 960 / 360, (90 - coordinate.Latitude) * 480 / 180);
}
