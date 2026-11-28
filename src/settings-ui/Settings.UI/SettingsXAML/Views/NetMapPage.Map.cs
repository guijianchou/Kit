// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetMapLib;
using Windows.Foundation;

namespace Kit.Settings.UI.Views;

public sealed partial class NetMapPage
{
    private static readonly double[] MapScaleSteps = [1.0, 2.0, 5.0];
    private PathGeometry overviewGeometry;
    private PathGeometry detailGeometry;
    private PathGeometry coastlineGeometry;
    private Rect detailBounds;
    private MapViewport manualMapViewport;
    private long mapGeneration = long.MinValue;
    private Point? mapDragPosition;
    private bool mapPanQueued;
    private int graticuleStep = 30;

    private static PathGeometry BuildMapGeometry(IEnumerable<MapCoordinate[]> rings, bool closed, Rect? visible)
    {
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        foreach (var ring in rings)
        {
            if (ring.Length < (closed ? 3 : 2))
            {
                continue;
            }

            var points = ring.Select(Project).ToArray();
            double left = points.Min(point => point.X);
            double right = points.Max(point => point.X);
            double top = points.Min(point => point.Y);
            double bottom = points.Max(point => point.Y);
            foreach (double offset in new[] { -MapViewport.WorldWidth, 0, MapViewport.WorldWidth })
            {
                if (visible is { } bounds && (right + offset < bounds.Left || left + offset > bounds.Right || bottom < bounds.Top || top > bounds.Bottom))
                {
                    continue;
                }

                var figure = new PathFigure { StartPoint = new(points[0].X + offset, points[0].Y), IsClosed = closed, IsFilled = closed };
                var segment = new PolyLineSegment();
                for (int i = 1; i < points.Length; i++)
                {
                    segment.Points.Add(new(points[i].X + offset, points[i].Y));
                }

                figure.Segments.Add(segment);
                geometry.Figures.Add(figure);
            }
        }

        return geometry;
    }

    private void UpdateMapDetail()
    {
        bool detailed = mapViewport.Scale >= 1.4;
        WorldOutline.Stroke = ResolvePageBrush(detailed ? "NetMapCountryBorderBrush" : "NetMapLandStrokeBrush");
        if (detailed)
        {
            var view = new Rect(-mapViewport.OffsetX / mapViewport.Scale, -mapViewport.OffsetY / mapViewport.Scale, mapViewport.Width / mapViewport.Scale, mapViewport.Height / mapViewport.Scale);
            if (detailGeometry == null || view.Left < detailBounds.Left || view.Right > detailBounds.Right || view.Top < detailBounds.Top || view.Bottom > detailBounds.Bottom)
            {
                // Cache a generous region around the viewport. Drag frames reuse the
                // geometry until its edge is reached; off-screen islands stay unbuilt.
                detailBounds = new(view.X - view.Width, view.Y - view.Height, view.Width * 3, view.Height * 3);
                detailGeometry = BuildMapGeometry(WorldMap.DetailedCountries.SelectMany(country => country.Rings), closed: true, detailBounds);
                coastlineGeometry = BuildMapGeometry(WorldMap.Coastlines, closed: false, detailBounds);
            }

            WorldOutline.Data = detailGeometry;
            WorldCoastline.Data = coastlineGeometry;
        }
        else
        {
            WorldOutline.Data = overviewGeometry;
            WorldCoastline.Data = null;
        }

        int step = mapViewport.Scale >= 10 ? 2 : mapViewport.Scale >= 4 ? 5 : detailed ? 10 : 30;
        if (step != graticuleStep)
        {
            graticuleStep = step;
            WorldGraticule.Data = BuildGraticuleGeometry(step);
        }

        ToolTipService.SetToolTip(MapView, NetMapViewModel.Text("MapNavigationHint"));
    }

    private void UpdateMapScale()
    {
        double centerLatitude = 90 - ((((mapViewport.Height / 2) - mapViewport.OffsetY) / mapViewport.Scale) * 180 / MapViewport.WorldHeight);
        double kilometersPerDip = 40075.017 * Math.Max(0.01, Math.Cos(centerLatitude * Math.PI / 180)) / (MapViewport.WorldWidth * mapViewport.Scale);
        double target = kilometersPerDip * Math.Min(80, mapViewport.Width / 4);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double distance = MapScaleSteps.Select(value => value * magnitude).LastOrDefault(value => value <= target);
        if (distance <= 0)
        {
            distance = magnitude;
        }

        MapScaleText.Text = distance >= 1 ? $"≈ {distance:0.#} km" : $"≈ {distance * 1000:0} m";
        MapScaleBar.Width = Math.Max(1, distance / kilometersPerDip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MapScaleText, NetMapViewModel.Text("MapScaleHint") + ": " + MapScaleText.Text);
    }

    private void DrawGeographicLabels(IReadOnlyList<MapCoordinate> route)
    {
        MapLabels.Children.Clear();
        var occupied = route.Select(point => mapViewport.Position(point)).Select(point => new Rect(point.X - 17, point.Y - 17, 34, 34)).ToList();
        foreach (var card in MapPoints.Children.OfType<Border>().Where(element => element.Tag is "HopDetails"))
        {
            occupied.Add(new(Canvas.GetLeft(card), Canvas.GetTop(card), card.DesiredSize.Width, card.DesiredSize.Height));
        }

        bool chinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        int count = 0;
        var routeCountries = ViewModel.Snapshot.Hops.Select(hop => hop.Geography?.CountryCode)
            .Concat(new[] { ViewModel.Snapshot.Direct.Geography?.CountryCode, ViewModel.Snapshot.Proxy.Geography?.CountryCode })
            .Where(code => !string.IsNullOrEmpty(code)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var country in WorldMap.DetailedCountries.OrderByDescending(country => routeCountries.Contains(country.Code)).ThenBy(country => country.LabelRank))
        {
            if ((mapViewport.Scale < 1.4 && country.LabelRank > 2) || (mapViewport.Scale < 3 && country.LabelRank > 4))
            {
                continue;
            }

            string name = chinese && !string.IsNullOrWhiteSpace(country.ChineseName) ? country.ChineseName : country.Name;
            TryLabel(name, country.Label, country: true, capital: false);
            if (count >= 14)
            {
                break;
            }
        }

        if (mapViewport.Scale >= 1.4)
        {
            foreach (var city in WorldMap.Cities.OrderByDescending(city => routeCountries.Contains(city.CountryCode)).ThenBy(city => city.Rank).ThenByDescending(city => city.Capital))
            {
                if ((mapViewport.Scale < 3 && city.Rank > 1) || (mapViewport.Scale < 5 && city.Rank > 3))
                {
                    continue;
                }

                string name = chinese && !string.IsNullOrWhiteSpace(city.ChineseName) ? city.ChineseName : city.Name;
                TryLabel(name, city.Location, country: false, city.Capital);
                if (count >= 42)
                {
                    break;
                }
            }
        }

        void TryLabel(string name, MapCoordinate location, bool country, bool capital)
        {
            var point = mapViewport.Position(location);
            if (point.X < 4 || point.X > mapViewport.Width - 4 || point.Y < 44 || point.Y > mapViewport.Height - 38)
            {
                return;
            }

            var text = new TextBlock
            {
                Text = country ? name : (capital ? "◉ " : "· ") + name,
                FontSize = TypographyResources.FontSize(country ? 11 : 10),
                FontWeight = country ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ResolvePageBrush("NetMapGeographicTextBrush"),
                MaxLines = 1,
            };
            var label = new Border { Child = text, Padding = new Thickness(3, 1, 3, 1), CornerRadius = new CornerRadius(2), Background = ResolvePageBrush("NetMapLabelSurfaceBrush"), Tag = country ? "CountryLabel" : "CityLabel" };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double left = country ? point.X - (label.DesiredSize.Width / 2) : point.X + 4;
            double top = point.Y - (label.DesiredSize.Height / 2);
            var bounds = new Rect(left - 3, top - 2, label.DesiredSize.Width + 6, label.DesiredSize.Height + 4);
            if (bounds.Left < 4 || bounds.Right > mapViewport.Width - 4 || bounds.Top < 42 || bounds.Bottom > mapViewport.Height - 36 || occupied.Any(other => Overlaps(bounds, other)))
            {
                return;
            }

            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
            MapLabels.Children.Add(label);
            occupied.Add(bounds);
            count++;
        }
    }

    private static bool Overlaps(Rect a, Rect b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private void ZoomMap(double factor, MapPoint anchor)
    {
        double zoom = Math.Clamp(userZoomFactor * factor, 1, 8);
        if (Math.Abs(zoom - userZoomFactor) < 0.001)
        {
            return;
        }

        // A queued drag frame may already have a newer position than the last draw.
        manualMapViewport = (manualMapViewport ?? mapViewport).ZoomAt(zoom / userZoomFactor, anchor);
        userZoomFactor = zoom;
        renderedSnapshot = null;
        DrawPoints();
    }

    private void MapView_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (!args.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            return;
        }

        var point = args.GetCurrentPoint(MapView);
        ZoomMap(Math.Pow(1.2, point.Properties.MouseWheelDelta / 120.0), new(point.Position.X, point.Position.Y));
        args.Handled = true;
    }

    private void MapView_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(MapView);
        if (args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Leave marker taps and the selected card's scrolling/close button intact.
        for (DependencyObject source = args.OriginalSource as DependencyObject; source != null && source != MapView; source = VisualTreeHelper.GetParent(source))
        {
            if (source == MapPoints)
            {
                return;
            }
        }

        if (MapView.CapturePointer(args.Pointer))
        {
            mapDragPosition = point.Position;
            args.Handled = true;
        }
    }

    private void MapView_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (mapDragPosition is not { } previous)
        {
            return;
        }

        var position = args.GetCurrentPoint(MapView).Position;
        PanMap(position.X - previous.X, position.Y - previous.Y);
        mapDragPosition = position;
        args.Handled = true;
    }

    private void PanMap(double deltaX, double deltaY)
    {
        manualMapViewport = (manualMapViewport ?? mapViewport).Pan(deltaX, deltaY);
        renderedSnapshot = null;
        if (!mapPanQueued)
        {
            mapPanQueued = true;
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                mapPanQueued = false;
                DrawPoints();
            }))
            {
                mapPanQueued = false;
            }
        }
    }

    private void MapView_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        mapDragPosition = null;
        MapView.ReleasePointerCapture(args.Pointer);
    }

    private void MapView_PointerCaptureLost(object sender, PointerRoutedEventArgs args) => mapDragPosition = null;
}
