// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Kit.Settings.UI.Views
{
    public sealed partial class UDPtestPage : NavigablePage
    {
        public UDPtestViewModel ViewModel { get; }

        public UDPtestPage()
        {
            ViewModel = new UDPtestViewModel();
            DataContext = ViewModel;
            InitializeComponent();

            ViewModel.HealthChartNeedsRedraw += OnHealthChartNeedsRedraw;
            UpdateHealthPeriodButtons();
        }

        private async void OnStartClicked(object sender, RoutedEventArgs e)
        {
            await ViewModel.StartAsync();
        }

        private async void OnStopClicked(object sender, RoutedEventArgs e)
        {
            await ViewModel.StopAsync();
        }

        private async void OnClearClicked(object sender, RoutedEventArgs e)
        {
            await ViewModel.ClearHistoryAsync();
        }

        private void OnAddUdpLineClicked(object sender, RoutedEventArgs e)
        {
            ViewModel.AddUdpLine();
        }

        private void OnRefreshIntervalLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                int current = ViewModel.RefreshInterval;
                foreach (ComboBoxItem item in cb.Items)
                {
                    if (int.TryParse(item.Tag?.ToString(), out int val) && val == current)
                    {
                        cb.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private void OnRefreshIntervalChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int val))
            {
                ViewModel.RefreshInterval = val;
            }
        }

        private void OnPeriodClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string tagStr && int.TryParse(tagStr, out int seconds))
            {
                ViewModel.SelectedPeriodSeconds = seconds;
                UpdateHealthPeriodButtons();
            }
        }

        private void UpdateHealthPeriodButtons()
        {
            Button[] buttons =
            [
                HealthPeriod1mButton,
                HealthPeriod5mButton,
                HealthPeriod30mButton,
                HealthPeriod1hButton,
            ];
            int selectedSeconds = ViewModel.SelectedPeriodSeconds;
            foreach (Button button in buttons)
            {
                if (button == null)
                {
                    continue;
                }

                bool selected = int.TryParse(button.Tag?.ToString(), out int seconds)
                    && seconds == selectedSeconds;
                button.Style = selected
                    ? (Resources.TryGetValue("AccentHealthButtonStyle", out object s) ? s as Style : null)
                    : null;
            }
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

        private void DeleteLine_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is UDPtestLineRowViewModel row)
            {
                row.DeleteConfig();
            }
        }

        private void OnHealthChartSizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawHealthChart();
        }

        private void OnHealthChartNeedsRedraw()
        {
            DispatcherQueue.TryEnqueue(RedrawHealthChart);
        }

        private void RedrawHealthChart()
        {
            HealthCurvePath.Data = null;
            HealthFailuresPath.Data = null;

            double width = HealthChartCanvas.ActualWidth;
            double height = HealthChartCanvas.ActualHeight;

            if (width <= 0 || height <= 0)
            {
                return;
            }

            var points = ViewModel.GetCurrentChartPoints();
            if (points.Count == 0)
            {
                HealthChartEmptyText.Text = "No samples in this period";
                HealthChartEmptyText.Visibility = Visibility.Visible;
                return;
            }

            var successes = points.Where(p => p.IsSuccess).ToList();
            HealthChartEmptyText.Text = successes.Count == 0
                ? "No successful RTT in this period"
                : string.Empty;
            HealthChartEmptyText.Visibility = successes.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            double plotBottom = Math.Max(8, height - 8);
            double plotTop = 6;

            int bucketCount = Math.Clamp((int)Math.Floor(width / 10d), 24, 72);
            List<double>?[] successBuckets = new List<double>?[bucketCount];
            bool[] failureBuckets = new bool[bucketCount];

            DateTimeOffset now = DateTimeOffset.UtcNow;
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
            AppendSmoothFigure(geometry, points);
            return geometry;
        }

        private static void AppendSmoothFigure(PathGeometry geometry, IReadOnlyList<Point> points)
        {
            if (points.Count == 0)
            {
                return;
            }

            PathFigure figure = new()
            {
                StartPoint = points[0],
                IsClosed = false,
                IsFilled = false,
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

            geometry.Figures.Add(figure);
        }
    }
}
