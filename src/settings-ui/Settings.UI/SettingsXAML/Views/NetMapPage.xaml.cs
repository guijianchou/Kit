// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetMapLib;
using Windows.Foundation;
using Windows.Storage.Pickers;

namespace Kit.Settings.UI.Views;

public sealed partial class NetMapPage : NavigablePage, IRefreshablePage
{
    private readonly MainWindow settingsWindow;
    private readonly Dictionary<int, FrameworkElement> hopMarkers = new();
    private bool windowSubscribed;
    private bool isCurrentPage;
    private bool mapLoaded;
    private NetMapSnapshot renderedSnapshot;
    private int renderedSelection = -1;
    private double mapScale = 1;
    private double mapCenterX = 480;
    private double mapOffsetX;
    private double mapOffsetY;

    public NetMapPage()
    {
        ViewModel = new NetMapViewModel();
        InitializeComponent();
        DataContext = ViewModel;
        settingsWindow = App.GetSettingsWindow();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
        ActualThemeChanged += Page_ActualThemeChanged;
        ViewModel.MapChanged += DrawPoints;
    }

    public NetMapViewModel ViewModel { get; }

    public void RefreshEnabledState() => ViewModel.RefreshEnabledState();

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        isCurrentPage = true;

        // Returning during a transition can reuse a page without another Loaded event.
        ViewModel.RefreshEnabledState();
        UpdateVisibility();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        // Cached pages can remain loaded during the navigation transition.
        isCurrentPage = false;
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
            mapLoaded = true;
        }

        DrawPoints();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SetPageActive(false);
        UnsubscribeWindow();
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
        bool visible = isCurrentPage && IsLoaded && settingsWindow.AppWindow.IsVisible &&
            !(settingsWindow.AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized);
        ViewModel.SetPageActive(visible);
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        ViewModel.SetPageActive(false);
        if (!args.Handled)
        {
            UnsubscribeWindow();
            ViewModel.MapChanged -= DrawPoints;
            ViewModel.Dispose();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => ViewModel.IsDetectionOn = false;

    private void Start_Click(object sender, RoutedEventArgs e) => ViewModel.IsDetectionOn = true;

    private void Apply_Click(object sender, RoutedEventArgs e) => ViewModel.ApplySettings();

    private async void UpdateAsn_Click(object sender, RoutedEventArgs e) => await ViewModel.UpdateAsnAsync();

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
            MapDescription.Text = NetMapViewModel.Text("PickerFailed");
        }
    }

    private void EgressGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 650;
        Grid.SetColumn(ProxyCard, narrow ? 0 : 1);
        Grid.SetRow(ProxyCard, narrow ? 1 : 0);
        EgressGrid.RowSpacing = narrow ? 12 : 0;
        EgressGrid.ColumnSpacing = narrow ? 0 : 12;
        EgressGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    private void Page_ActualThemeChanged(FrameworkElement sender, object args) => DispatcherQueue.TryEnqueue(DrawPoints);

    private void MapBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 880;
        Grid.SetColumn(RoutePanel, narrow ? 0 : 1);
        Grid.SetRow(RoutePanel, narrow ? 1 : 0);
        MapBody.ColumnSpacing = narrow ? 0 : 12;
        MapBody.RowSpacing = narrow ? 12 : 0;
        MapBody.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 440);
    }

    private void HopList_SelectionChanged(object sender, SelectionChangedEventArgs e) => DispatcherQueue.TryEnqueue(DrawPoints);

    private void DrawPoints()
    {
        if (MapPoints == null || MapDescription == null)
        {
            return;
        }

        var snapshot = ViewModel.Snapshot;
        int selected = HopList.SelectedIndex;
        bool redraw = renderedSnapshot == null || renderedSelection != selected || renderedSnapshot.Generation != snapshot.Generation || renderedSnapshot.Running != snapshot.Running ||
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
                        shape.Style = (Style)((ResourceDictionary)Resources["NetMapHealthStyles"])[ViewModel.Hops[marker.Key].Health + "Dot"];
                    }
                }
            }

            if (selected >= 0 && selected < ViewModel.Hops.Count)
            {
                MapDescription.Text = ViewModel.Hops[selected].Detail;
            }

            return;
        }

        MapPoints.Children.Clear();
        MapLines.Children.Clear();
        hopMarkers.Clear();
        renderedSnapshot = snapshot;
        renderedSelection = selected;
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
                var marker = AddPoint(point, ViewModel.Hops[i].Detail, true, !snapshot.Running, selected == i ? 44 : 34, ViewModel.Hops[i].Ttl, ViewModel.Hops[i].Health);
                marker.Tapped += (_, _) => HopList.SelectedIndex = index;
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
            var text = new TextBlock { FontSize = 28, Style = (Style)Resources["NetMapMapLabelStyle"] };
            text.SetBinding(TextBlock.TextProperty, new Binding { Source = ViewModel.Hops[labelIndex], Path = new PropertyPath(nameof(NetMapHopRow.MapLabel)), Mode = BindingMode.OneWay });
            var label = new Border { Child = text, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(4), Style = (Style)Resources["NetMapMapLabelBackgroundStyle"] };
            Canvas.SetLeft(label, Math.Clamp(position.X - 100, 8, 620));
            Canvas.SetTop(label, Math.Clamp(position.Y + 28, 8, 428));
            MapPoints.Children.Add(label);
        }

        MapDescription.Text = selected >= 0 && selected < ViewModel.Hops.Count ? ViewModel.Hops[selected].Detail : descriptions.Count == 0 ? NetMapViewModel.Text("MapEmpty") : string.Join("   /   ", descriptions);
    }

    private static MapCoordinate EgressPosition(ChannelState channel) => channel.LastSuccess?.Identity is { } identity ? WorldMap.Locate(string.IsNullOrEmpty(identity.CountryCode) ? channel.Geography?.CountryCode ?? string.Empty : identity.CountryCode, channel.Geography) : null;

    private void FitMap(IReadOnlyList<MapCoordinate> coordinates)
    {
        mapScale = 1;
        mapCenterX = 480;
        mapOffsetX = mapOffsetY = 0;
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
            mapScale = Math.Clamp(Math.Min(800 / Math.Max(width, 40), 340 / Math.Max(height, 30)), 1, 4);
            mapOffsetX = 480 - (mapCenterX * mapScale);
            mapOffsetY = Math.Clamp(240 - ((top + (height / 2)) * mapScale), 480 - (480 * mapScale), 0);
        }

        WorldOutline.RenderTransform = new MatrixTransform { Matrix = new Matrix(mapScale, 0, 0, mapScale, mapOffsetX, mapOffsetY) };
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
            var line = new Polyline { Style = (Style)Resources["NetMapRouteLineStyle"], StrokeThickness = 3, Opacity = ViewModel.IsDetectionOn ? 0.95 : 0.4, Tag = approximate ? "Approximate" : "Observed" };
            if (approximate)
            {
                line.StrokeDashArray = new DoubleCollection { 4, 3 };
            }

            line.Points.Add(first);
            line.Points.Add(second);
            MapLines.Children.Add(line);
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
            AddPoint(coordinate, caption, proxy, stale, proxy ? 16 : 20, health: health);
        }
    }

    private FrameworkElement AddPoint(MapCoordinate coordinate, string label, bool proxy, bool stale, double size, string number = null, ProbeHealth? health = null)
    {
        var position = Position(coordinate);
        Shape mark = proxy ? new Ellipse() : new Rectangle { RadiusX = 3, RadiusY = 3 };
        mark.Style = health.HasValue ? (Style)((ResourceDictionary)Resources["NetMapHealthStyles"])[health + (proxy ? "Dot" : "Box")] : (Style)Resources[proxy ? "NetMapProxyMarkerStyle" : "NetMapDirectMarkerStyle"];
        mark.Width = size;
        mark.Height = size;
        mark.Opacity = stale ? 0.45 : 0.9;
        var container = new Grid { Width = size, Height = size };
        container.Children.Add(mark);
        if (number != null)
        {
            container.Children.Add(new TextBlock { Text = number, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Style = (Style)Resources["NetMapHopNumberStyle"], HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
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
