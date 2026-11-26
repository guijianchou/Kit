// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Kit.Settings.UI.Views
{
    public sealed partial class UDPtestPage : NavigablePage, IRefreshablePage, ILiveLocalizable
    {
        public UDPtestViewModel ViewModel { get; }

        public UDPtestPage()
        {
            var settingsUtils = SettingsUtils.Default;
            var generalSettingsRepository = SettingsRepository<GeneralSettings>.GetInstance(settingsUtils);
            var moduleSettingsRepository = SettingsRepository<UDPtestSettings>.GetInstance(settingsUtils);

            ViewModel = new UDPtestViewModel(
                generalSettingsRepository,
                moduleSettingsRepository,
                ShellPage.SendDefaultIPCMessage);
            DataContext = ViewModel;
            InitializeComponent();

            _settingsWindow = App.GetSettingsWindow();
            _settingsWindow.Closed += OnSettingsWindowClosed;
            _settingsWindow.Activated += OnSettingsWindowActivated;
            Loaded += OnUDPtestLoaded;
            Unloaded += OnUDPtestUnloaded;

            ViewModel.HealthChartNeedsRedraw += OnHealthChartNeedsRedraw;
            TypographyResources.TypographyChanged += OnTypographyChanged;
        }

        private readonly MainWindow _settingsWindow;
        private int _chartRedrawQueued;
        private ElementTheme? _lastAppliedTheme;
        private (long Version, string? LineId, int Period, double Width, double Height, long WindowSecond)? _lastChartKey;

        private void OnUDPtestLoaded(object sender, RoutedEventArgs e)
        {
            // WinUI's native theme subscription can become inactive when a cached
            // page leaves the tree. Re-register on attachment, as ProbeHistoryStrip does.
            ActualThemeChanged -= OnUDPtestActualThemeChanged;
            ActualThemeChanged += OnUDPtestActualThemeChanged;
            RefreshEnabledState();
            ApplyCurrentTheme();
            ViewModel.SetPageActive(true);
        }

        private void OnUDPtestUnloaded(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
            {
                return;
            }

            ActualThemeChanged -= OnUDPtestActualThemeChanged;
            InfoTeachingTip.IsOpen = false;
            ViewModel.SetPageActive(false);
        }

        private void OnUDPtestActualThemeChanged(FrameworkElement sender, object args)
        {
            if (IsLoaded)
            {
                ApplyCurrentTheme();
            }
        }

        private void ApplyCurrentTheme()
        {
            // Cached navigation retains the existing row bindings. Republish their
            // brushes only on first load or when a theme actually changed while hidden.
            if (_lastAppliedTheme == ActualTheme)
            {
                return;
            }

            _lastAppliedTheme = ActualTheme;
            ViewModel.RefreshThemeBrushes(ActualTheme);
        }

        private void OnSettingsWindowClosed(object sender, WindowEventArgs args)
        {
            if (args.Handled)
            {
                if (!_settingsWindow.AppWindow.IsVisible)
                {
                    ViewModel.SetPageActive(false);
                }

                return;
            }

            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow.Activated -= OnSettingsWindowActivated;
            ActualThemeChanged -= OnUDPtestActualThemeChanged;
            ViewModel.HealthChartNeedsRedraw -= OnHealthChartNeedsRedraw;
            TypographyResources.TypographyChanged -= OnTypographyChanged;
            ViewModel.Dispose();
        }

        private void OnSettingsWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated && IsLoaded)
            {
                ViewModel.SetPageActive(true);
            }
        }

        public void RefreshEnabledState()
        {
            ViewModel.RefreshEnabledState();
        }

        public void RefreshLocalization()
        {
            _lastChartKey = null;
            OnHealthChartNeedsRedraw();
        }

        private void OnTypographyChanged(object? sender, EventArgs e)
        {
            ViewModel.RefreshTypography();
        }

        private async void OnStartClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                await ViewModel.StartAsync();
            }
            catch (System.Exception ex)
            {
                ManagedCommon.Logger.LogError("Settings action OnStartClicked failed.", ex);
            }
        }

        private async void OnStopClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                await ViewModel.StopAsync();
            }
            catch (System.Exception ex)
            {
                ManagedCommon.Logger.LogError("Settings action OnStopClicked failed.", ex);
            }
        }

        private async void OnClearClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                await ViewModel.ClearHistoryAsync();
            }
            catch (System.Exception ex)
            {
                ManagedCommon.Logger.LogError("Settings action OnClearClicked failed.", ex);
            }
        }

        private void OnAddUdpLineClicked(object sender, RoutedEventArgs e)
        {
            ViewModel.AddUdpLine();
        }

        private void OnIntervalMenuOpening(object sender, object e)
        {
            int current = ViewModel.RefreshInterval;
            Interval500Item.IsChecked = current == 500;
            Interval1000Item.IsChecked = current == 1000;
            Interval2000Item.IsChecked = current == 2000;
            Interval5000Item.IsChecked = current == 5000;
        }

        private void OnIntervalItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string tag && int.TryParse(tag, out int val))
            {
                ViewModel.RefreshInterval = val;
            }
        }

        private void OnInfoButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement target)
            {
                string? tipText = ToolTipService.GetToolTip(target)?.ToString();
                if (!string.IsNullOrWhiteSpace(tipText))
                {
                    InfoTeachingTip.Target = target;
                    InfoTeachingTip.Subtitle = tipText;
                    InfoTeachingTip.IsOpen = true;
                }
            }
        }

        private void OnProbeRowTapped(object sender, TappedRoutedEventArgs e)
        {
            if (IsInsideInteractiveChild(e.OriginalSource as DependencyObject, sender as DependencyObject))
            {
                return;
            }

            if (sender is FrameworkElement element && element.Tag is UDPtestLineRowViewModel row)
            {
                ViewModel.SelectedLine = row;
            }
        }

        private static bool IsInsideInteractiveChild(DependencyObject? source, DependencyObject? stopAt)
        {
            DependencyObject? current = source;
            while (current != null && !ReferenceEquals(current, stopAt))
            {
                if (current is ButtonBase or ToggleSwitch or TextBox or ComboBox or Controls.ProbeHistoryStrip)
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private void ToggleConfigExpanded_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is UDPtestLineRowViewModel row)
            {
                row.ToggleConfigExpanded();
            }
        }

        private void SaveLine_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is UDPtestLineRowViewModel row)
            {
                row.SaveConfig();
            }
        }

        private void CancelEditLine_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is UDPtestLineRowViewModel row)
            {
                row.ResetEditFields();
                row.IsConfigExpanded = false;
            }
        }

        private void DeleteLine_Click(object sender, RoutedEventArgs e)
        {
            DismissParentFlyout(sender);
            if (sender is FrameworkElement element && element.DataContext is UDPtestLineRowViewModel row)
            {
                row.DeleteConfig();
            }
        }

        private void CancelDeleteLine_Click(object sender, RoutedEventArgs e)
        {
            DismissParentFlyout(sender);
        }

        private static void DismissParentFlyout(object sender)
        {
            DependencyObject? current = sender as DependencyObject;
            while (current != null)
            {
                if (current is FlyoutPresenter && VisualTreeHelper.GetParent(current) is Popup popup)
                {
                    popup.IsOpen = false;
                    return;
                }

                current = VisualTreeHelper.GetParent(current);
            }
        }

        private void OnHealthChartSizeChanged(object sender, SizeChangedEventArgs e)
        {
            OnHealthChartNeedsRedraw();
        }

        private void OnHealthChartNeedsRedraw()
        {
            if (Interlocked.Exchange(ref _chartRedrawQueued, 1) != 0)
            {
                return;
            }

            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                Interlocked.Exchange(ref _chartRedrawQueued, 0);
                RedrawHealthChart();
            }))
            {
                Interlocked.Exchange(ref _chartRedrawQueued, 0);
            }
        }

        private void RedrawHealthChart()
        {
            if (!IsLoaded || !_settingsWindow.AppWindow.IsVisible)
            {
                return;
            }

            double width = HealthChartCanvas.ActualWidth;
            double height = HealthChartCanvas.ActualHeight;

            if (width <= 0 || height <= 0)
            {
                return;
            }

            // Layout and a snapshot can request the same chart several times in one frame.
            // Include the time window so revisiting a stopped session still expires old points.
            long windowSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var key = (ViewModel.ChartDataVersion, ViewModel.SelectedLine?.Id, ViewModel.SelectedPeriodSeconds, width, height, windowSecond);
            if (_lastChartKey == key)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(windowSecond);
            var points = ViewModel.GetCurrentChartPoints(now);
            _lastChartKey = key;
            HealthCurvePath.Data = null;
            HealthCurveFillPath.Data = null;
            HealthFailuresPath.Data = null;
            HealthChartMaxLabel.Visibility = Visibility.Collapsed;

            if (points.Count == 0)
            {
                string emptyText = ResourceLoaderInstance.ResourceLoader.GetString("UDPtest_NoSamplesLabel/Text");
                HealthChartEmptyText.Text = string.IsNullOrEmpty(emptyText) ? "No samples in this period" : emptyText;
                HealthChartEmptyText.Visibility = Visibility.Visible;
                return;
            }

            var successes = points.Where(p => p.IsSuccess).ToList();
            string noSuccessText = ResourceLoaderInstance.ResourceLoader.GetString("UDPtest_NoSuccessfulRttLabel");
            HealthChartEmptyText.Text = successes.Count == 0
                ? (string.IsNullOrEmpty(noSuccessText) ? "No successful RTT in this period" : noSuccessText)
                : string.Empty;
            HealthChartEmptyText.Visibility = successes.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            double plotBottom = Math.Max(8, height - 8);
            double plotTop = 6;

            int bucketCount = Math.Clamp((int)Math.Floor(width / 10d), 24, 72);
            List<double>?[] successBuckets = new List<double>?[bucketCount];
            bool[] failureBuckets = new bool[bucketCount];

            DateTimeOffset start = now.AddSeconds(-ViewModel.SelectedPeriodSeconds);
            DateTimeOffset end = now;
            double totalSeconds = Math.Max(1, (end - start).TotalSeconds);

            foreach (var sample in points)
            {
                double elapsedSeconds = Math.Clamp((sample.Timestamp - start).TotalSeconds, 0, totalSeconds);
                int bucketIndex = Math.Min(
                    bucketCount - 1,
                    (int)Math.Floor(elapsedSeconds / totalSeconds * bucketCount));
                if (sample.IsSuccess)
                {
                    (successBuckets[bucketIndex] ??= []).Add(sample.Rtt);
                }
                else
                {
                    failureBuckets[bucketIndex] = true;
                }
            }

            double?[] bucketRtts = new double?[bucketCount];
            for (int index = 0; index < bucketCount; index++)
            {
                if (successBuckets[index] is { Count: > 0 } values)
                {
                    bucketRtts[index] = GetMedian(values);
                }
            }

            double?[] smoothedRtts = new double?[bucketCount];
            for (int index = 0; index < bucketCount; index++)
            {
                if (bucketRtts[index] is not double current)
                {
                    continue;
                }

                double weightedTotal = current * 2d;
                double weight = 2d;
                if (index > 0 && bucketRtts[index - 1] is double previous)
                {
                    weightedTotal += previous;
                    weight += 1d;
                }
                if (index + 1 < bucketCount && bucketRtts[index + 1] is double next)
                {
                    weightedTotal += next;
                    weight += 1d;
                }
                smoothedRtts[index] = weightedTotal / weight;
            }

            double[] plottedRtts = smoothedRtts
                .Where(static value => value.HasValue)
                .Select(static value => value!.Value)
                .ToArray();

            if (plottedRtts.Length > 0)
            {
                double percentile95 = GetPercentile(plottedRtts, 0.95d);
                double upperBound = Math.Ceiling(Math.Max(25d, percentile95 * 1.2d) / 10d) * 10d;
                List<Point> chartPoints = [];
                for (int index = 0; index < bucketCount; index++)
                {
                    if (smoothedRtts[index] is not double rtt)
                    {
                        continue;
                    }

                    double x = (index + 0.5d) / bucketCount * Math.Max(0, width - 2);
                    double normalized = Math.Clamp(rtt / upperBound, 0d, 1d);
                    double y = plotBottom - normalized * (plotBottom - plotTop);
                    chartPoints.Add(new Point(x, y));
                }

                HealthCurvePath.Data = CreateSmoothHealthPath(chartPoints);
                HealthCurveFillPath.Data = CreateSmoothFillPath(chartPoints, plotBottom);
                HealthChartMaxLabel.Text = string.Create(CultureInfo.InvariantCulture, $"{upperBound:0} ms");
                HealthChartMaxLabel.Visibility = Visibility.Visible;
            }

            PathGeometry failureGeometry = new();
            for (int index = 0; index < bucketCount; index++)
            {
                if (!failureBuckets[index])
                {
                    continue;
                }

                double x = (index + 0.5d) / bucketCount * Math.Max(0, width - 2);
                PathFigure marker = new()
                {
                    StartPoint = new Point(x, plotBottom),
                    IsClosed = false,
                    IsFilled = false,
                };
                marker.Segments.Add(new LineSegment { Point = new Point(x, plotBottom - 5) });
                failureGeometry.Figures.Add(marker);
            }
            HealthFailuresPath.Data = failureGeometry;
        }

        private static double GetMedian(IReadOnlyCollection<double> values)
        {
            double[] sorted = [.. values];
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) / 2d
                : sorted[middle];
        }

        private static double GetPercentile(IReadOnlyCollection<double> values, double percentile)
        {
            double[] sorted = [.. values];
            Array.Sort(sorted);
            double position = Math.Clamp(percentile, 0d, 1d) * (sorted.Length - 1);
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper)
            {
                return sorted[lower];
            }

            double fraction = position - lower;
            return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
        }

        private static PathGeometry CreateSmoothHealthPath(IReadOnlyList<Point> points)
        {
            PathGeometry geometry = new();
            AppendSmoothFigure(geometry, points, closeToBottomY: null);
            return geometry;
        }

        private static PathGeometry CreateSmoothFillPath(IReadOnlyList<Point> points, double bottomY)
        {
            PathGeometry geometry = new();
            if (points.Count >= 2)
            {
                AppendSmoothFigure(geometry, points, closeToBottomY: bottomY);
            }

            return geometry;
        }

        private static void AppendSmoothFigure(PathGeometry geometry, IReadOnlyList<Point> points, double? closeToBottomY)
        {
            if (points.Count == 0)
            {
                return;
            }

            bool filled = closeToBottomY.HasValue;
            PathFigure figure = new()
            {
                StartPoint = points[0],
                IsClosed = filled,
                IsFilled = filled,
            };
            if (points.Count == 1)
            {
                figure.Segments.Add(new LineSegment
                {
                    Point = new Point(points[0].X + 1, points[0].Y),
                });
            }
            else if (points.Count == 2)
            {
                figure.Segments.Add(new LineSegment { Point = points[1] });
            }
            else
            {
                for (int index = 1; index < points.Count - 1; index++)
                {
                    Point midpoint = new(
                        (points[index].X + points[index + 1].X) / 2d,
                        (points[index].Y + points[index + 1].Y) / 2d);
                    figure.Segments.Add(new QuadraticBezierSegment
                    {
                        Point1 = points[index],
                        Point2 = midpoint,
                    });
                }

                Point last = points[^1];
                figure.Segments.Add(new QuadraticBezierSegment
                {
                    Point1 = last,
                    Point2 = last,
                });
            }

            if (closeToBottomY is double bottomY)
            {
                figure.Segments.Add(new LineSegment { Point = new Point(points[^1].X, bottomY) });
                figure.Segments.Add(new LineSegment { Point = new Point(points[0].X, bottomY) });
            }

            geometry.Figures.Add(figure);
        }
    }
}
