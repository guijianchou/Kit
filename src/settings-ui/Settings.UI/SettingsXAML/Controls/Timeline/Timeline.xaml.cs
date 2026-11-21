// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using Kit.Settings.UI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Kit.Settings.UI.Controls
{
    public sealed partial class Timeline : UserControl
    {
        private const int TickHourStep = 2; // every 2 hours
        private const double MinBandIconWidth = 28.0;
        private const double EdgeLabelPadding = 6.0;

        public TimeSpan StartTime
        {
            get => (TimeSpan)GetValue(StartTimeProperty);
            set => SetValue(StartTimeProperty, value);
        }

        public static readonly DependencyProperty StartTimeProperty = DependencyProperty.Register(nameof(StartTime), typeof(TimeSpan), typeof(Timeline), new PropertyMetadata(defaultValue: new TimeSpan(22, 0, 0), OnTimeChanged));

        public TimeSpan EndTime
        {
            get => (TimeSpan)GetValue(EndTimeProperty);
            set => SetValue(EndTimeProperty, value);
        }

        public static readonly DependencyProperty EndTimeProperty = DependencyProperty.Register(nameof(EndTime), typeof(TimeSpan), typeof(Timeline), new PropertyMetadata(defaultValue: new TimeSpan(7, 0, 0), OnTimeChanged));

        public TimeSpan? Sunrise
        {
            get => (TimeSpan?)GetValue(SunriseProperty);
            set => SetValue(SunriseProperty, value);
        }

        public static readonly DependencyProperty SunriseProperty = DependencyProperty.Register(nameof(Sunrise), typeof(TimeSpan?), typeof(Timeline), new PropertyMetadata(defaultValue: null, OnTimeChanged));

        public TimeSpan? Sunset
        {
            get => (TimeSpan?)GetValue(SunsetProperty);
            set => SetValue(SunsetProperty, value);
        }

        public static readonly DependencyProperty SunsetProperty = DependencyProperty.Register(nameof(Sunset), typeof(TimeSpan?), typeof(Timeline), new PropertyMetadata(defaultValue: null, OnTimeChanged));

        private readonly List<int> _tickHours = new();
        private readonly RectangleGeometry _canvasClip = new();

        // Visuals
        private readonly List<Line> _ticks = new();
        private readonly List<TextBlock> _majorTickBottomLabels = new(); // 00,06,12,18,24 (below)

        private readonly List<Border> _dayRects = new(); // Light mode window (StartTime -> EndTime, up to 2 if wrapped)
        private readonly List<Border> _nightRects = new(); // Dark mode window (complement, up to 2)

        private TextBlock _startEdgeLabel; // top-of-chart
        private TextBlock _endEdgeLabel;

        private Line _sunriseTick;
        private Line _sunsetTick;
        private Line _currentTimeTick;

        private StackPanel _sunrisePanel;  // icon + time (below chart)
        private StackPanel _sunsetPanel;

        private bool _sizeEventsSubscribed;

        public Timeline()
        {
            this.InitializeComponent();
            TimelineCanvas.Clip = _canvasClip;
            this.Loaded += Timeline_Loaded;
            this.IsEnabledChanged += Timeline_IsEnabledChanged;
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new TimelineAutomationPeer(this);
        }

        private void Timeline_Loaded(object sender, RoutedEventArgs e)
        {
            CheckEnabledState();
        }

        private void Timeline_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            CheckEnabledState();
        }

        private void CheckEnabledState()
        {
            this.Opacity = IsEnabled ? 1.0 : 0.4;
        }

        private static void OnTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((Timeline)d).Setup();
        }

        private void Setup()
        {
            EnsureBands();
            EnsureTicks();
            EnsureStartEndEdgeLabels();
            EnsureSunriseSunsetTicks();
            EnsureCurrentTimeIndicator();
            EnsureSunPanels();
            EnsureMajorTickLabels();
            UpdateAll();
        }

        private void TimelineCanvas_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_sizeEventsSubscribed)
            {
                HeaderCanvas.SizeChanged += OnCanvasSizeChanged;
                TimelineCanvas.SizeChanged += OnCanvasSizeChanged;
                AnnotationCanvas.SizeChanged += OnCanvasSizeChanged;
                _sizeEventsSubscribed = true;
            }

            Setup();
        }

        private void TimelineCanvas_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_sizeEventsSubscribed)
            {
                HeaderCanvas.SizeChanged -= OnCanvasSizeChanged;
                TimelineCanvas.SizeChanged -= OnCanvasSizeChanged;
                AnnotationCanvas.SizeChanged -= OnCanvasSizeChanged;
                _sizeEventsSubscribed = false;
            }
        }

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateAll();
        }

        private void UpdateAll()
        {
            UpdateBandsLayout();
            UpdateTicksLayout();
            UpdateStartEndEdgeLabelsLayout();
            UpdateSunriseSunsetTicksLayout();
            UpdateCurrentTimeIndicatorLayout();
            UpdateSunPanelsLayout();
            UpdateMajorTickLabelsLayout();
            AutomationProperties.SetHelpText(
                this,
                $"Start={StartTime};End={EndTime};Sunrise={Sunrise};Sunset={Sunset}");
        }

        // ===== Ticks =====
        private void EnsureTicks()
        {
            if (_ticks.Count > 0)
            {
                return;
            }

            _tickHours.Clear();

            // Build ticks at 0,2,4,...,24 but skip the first/last MAJOR ticks (0 and 24)
            for (int hour = 0; hour <= 24; hour += TickHourStep)
            {
                bool isMajor = hour % 6 == 0;
                if (isMajor && (hour == 0 || hour == 24))
                {
                    continue; // skip first/last major ticks
                }

                var line = new Line
                {
                    Style = (Style)this.Resources[isMajor ? "MajorHourTickStyle" : "HourTickStyle"],
                };

                Canvas.SetZIndex(line, 0);

                _ticks.Add(line);
                _tickHours.Add(hour);

                AnnotationCanvas.Children.Add(line);
            }
        }

        private void UpdateTicksLayout()
        {
            double w = TimelineCanvas.ActualWidth;
            double h = TimelineCanvas.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            double minorLen = h * 0.1;
            double majorLen = h * 0.2;

            for (int i = 0; i < _ticks.Count; i++)
            {
                int hour = _tickHours[i];
                double x = Math.Round((hour / 24.0) * w);

                var line = _ticks[i];
                double len = (hour % 6 == 0) ? majorLen : minorLen;

                line.X1 = x;
                line.Y1 = 0;
                line.X2 = x;
                line.Y2 = len;
            }
        }

        // ===== Bands (Light + Dark) =====
        private void EnsureBands()
        {
            if (_dayRects.Count == 0)
            {
                _dayRects.Add(MakeBandRect(isDark: false));
                _dayRects.Add(MakeBandRect(isDark: false));
            }

            if (_nightRects.Count == 0)
            {
                _nightRects.Add(MakeBandRect(isDark: true));
                _nightRects.Add(MakeBandRect(isDark: true));
            }
        }

        private Border MakeBandRect(bool isDark)
        {
            var r = new Border();
            var icon = new FontIcon();
            if (isDark)
            {
                r.Style = (Style)this.Resources["DarkBandStyle"];
                icon.Style = (Style)this.Resources["DarkBandIconStyle"];
            }
            else
            {
                r.Style = (Style)this.Resources["LightBandStyle"];
                icon.Style = (Style)this.Resources["LightBandIconStyle"];
            }

            r.Child = icon;
            ToolTipService.SetToolTip(r, ResourceLoaderInstance.ResourceLoader.GetString(isDark ? "LightSwitch_Timeline_DarkBandToolTip" : "LightSwitch_Timeline_LightBandToolTip"));
            Canvas.SetZIndex(r, 5); // below ticks/labels
            TimelineCanvas.Children.Add(r);
            return r;
        }

        private void UpdateBandsLayout()
        {
            double w = TimelineCanvas.ActualWidth;
            double h = TimelineCanvas.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            _canvasClip.Rect = new Rect(0, 0, w, h);

            foreach (var r in _dayRects)
            {
                r.Height = h;
                Canvas.SetTop(r, 0);
            }

            foreach (var r in _nightRects)
            {
                r.Height = h;
                Canvas.SetTop(r, 0);
            }

            var dayRanges = ToRanges(StartTime, EndTime);       // 1 or 2 segments (Light mode window)
            var nightRanges = ComplementRanges(dayRanges);      // 0..2 segments (Dark mode window)

            LayoutRangeRects(_dayRects, dayRanges, w);
            LayoutRangeRects(_nightRects, nightRanges, w);
        }

        private static void LayoutRangeRects(List<Border> rects, List<(TimeSpan Start, TimeSpan End)> ranges, double width)
        {
            for (int i = 0; i < rects.Count; i++)
            {
                if (i < ranges.Count)
                {
                    var (start, end) = ranges[i];
                    double x = Math.Round((start.TotalHours / 24.0) * width);
                    double x2 = Math.Round((end.TotalHours / 24.0) * width);
                    double bandWidth = Math.Max(0, x2 - x);

                    var r = rects[i];
                    Canvas.SetLeft(r, x);
                    r.Width = bandWidth;
                    r.Visibility = Visibility.Visible;

                    if (r.Child is UIElement icon)
                    {
                        icon.Visibility = bandWidth >= MinBandIconWidth ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                else
                {
                    rects[i].Visibility = Visibility.Collapsed;
                }
            }
        }

        private static List<(TimeSpan Start, TimeSpan End)> ToRanges(TimeSpan start, TimeSpan end)
        {
            // Full day
            if (start == end)
            {
                return new() { (TimeSpan.Zero, TimeSpan.FromHours(24)) };
            }

            if (start < end)
            {
                return new() { (start, end) };
            }

            // Wraps midnight
            return new()
            {
                (start, TimeSpan.FromHours(24)),
                (TimeSpan.Zero, end),
            };
        }

        private static List<(TimeSpan Start, TimeSpan End)> ComplementRanges(List<(TimeSpan Start, TimeSpan End)> primary)
        {
            var res = new List<(TimeSpan, TimeSpan)>();

            // If primary covers the full day, there is no complement
            if (primary.Count == 1 && primary[0].Start == TimeSpan.Zero && primary[0].End == TimeSpan.FromHours(24))
            {
                return res;
            }

            if (primary.Count == 1)
            {
                var (ds, de) = primary[0];
                if (ds > TimeSpan.Zero)
                {
                    res.Add((TimeSpan.Zero, ds));
                }

                if (de < TimeSpan.FromHours(24))
                {
                    res.Add((de, TimeSpan.FromHours(24)));
                }
            }
            else
            {
                // primary[0] = [a,24), primary[1] = [0,b) => single complement [b,a)
                var a = primary[0].Start;
                var b = primary[1].End;
                res.Add((b, a));
            }

            return res;
        }

        // ===== Start & End labels (TOP of chart, ABOVE rectangles) =====
        private void EnsureStartEndEdgeLabels()
        {
            if (_startEdgeLabel == null)
            {
                _startEdgeLabel = new TextBlock { Style = (Style)this.Resources["EdgeLabelStyle"] };
                HeaderCanvas.Children.Add(_startEdgeLabel);
                Canvas.SetZIndex(_startEdgeLabel, 25);
            }

            if (_endEdgeLabel == null)
            {
                _endEdgeLabel = new TextBlock { Style = (Style)this.Resources["EdgeLabelStyle"] };
                HeaderCanvas.Children.Add(_endEdgeLabel);
                Canvas.SetZIndex(_endEdgeLabel, 25);
            }
        }

        private void UpdateStartEndEdgeLabelsLayout()
        {
            double w = TimelineCanvas.ActualWidth;
            if (w <= 0)
            {
                return;
            }

            _startEdgeLabel.Text = TimeSpanHelper.Convert(StartTime);
            _endEdgeLabel.Text = TimeSpanHelper.Convert(EndTime);

            double startW = MeasureTextWidth(_startEdgeLabel);
            double startX = Math.Round((StartTime.TotalHours / 24.0) * w);
            double startLeft = Clamp(startX - (startW / 2.0), 0, Math.Max(0, w - startW));

            Canvas.SetTop(_startEdgeLabel, 0);
            _startEdgeLabel.Visibility = Visibility.Visible;

            if (StartTime == EndTime)
            {
                Canvas.SetLeft(_startEdgeLabel, startLeft);
                _endEdgeLabel.Visibility = Visibility.Collapsed;
                return;
            }

            double endW = MeasureTextWidth(_endEdgeLabel);
            double endX = Math.Round((EndTime.TotalHours / 24.0) * w);
            double endLeft = Clamp(endX - (endW / 2.0), 0, Math.Max(0, w - endW));

            // Nudge horizontally if the two labels overlap
            if (startLeft <= endLeft)
            {
                double overlap = (startLeft + startW + EdgeLabelPadding) - endLeft;
                if (overlap > 0)
                {
                    double half = overlap / 2.0;
                    startLeft = Clamp(startLeft - half, 0, Math.Max(0, w - startW));
                    endLeft = Clamp(Math.Max(endLeft + half, startLeft + startW + EdgeLabelPadding), 0, Math.Max(0, w - endW));
                    if (startLeft + startW + EdgeLabelPadding > endLeft)
                    {
                        startLeft = Clamp(endLeft - startW - EdgeLabelPadding, 0, Math.Max(0, w - startW));
                    }
                }
            }
            else
            {
                double overlap = (endLeft + endW + EdgeLabelPadding) - startLeft;
                if (overlap > 0)
                {
                    double half = overlap / 2.0;
                    endLeft = Clamp(endLeft - half, 0, Math.Max(0, w - endW));
                    startLeft = Clamp(Math.Max(startLeft + half, endLeft + endW + EdgeLabelPadding), 0, Math.Max(0, w - startW));
                    if (endLeft + endW + EdgeLabelPadding > startLeft)
                    {
                        endLeft = Clamp(startLeft - endW - EdgeLabelPadding, 0, Math.Max(0, w - endW));
                    }
                }
            }

            Canvas.SetLeft(_startEdgeLabel, startLeft);
            Canvas.SetLeft(_endEdgeLabel, endLeft);
            Canvas.SetTop(_endEdgeLabel, 0);
            _endEdgeLabel.Visibility = Visibility.Visible;
        }

        // ===== Sunrise/Sunset ticks on chart =====
        private void EnsureSunriseSunsetTicks()
        {
            if (_sunriseTick == null)
            {
                _sunriseTick = new Line { Style = (Style)this.Resources["SunRiseMarkerTickStyle"] };
                TimelineCanvas.Children.Add(_sunriseTick);
                Canvas.SetZIndex(_sunriseTick, 12);
            }

            if (_sunsetTick == null)
            {
                _sunsetTick = new Line { Style = (Style)this.Resources["SunSetMarkerTickStyle"] };
                TimelineCanvas.Children.Add(_sunsetTick);
                Canvas.SetZIndex(_sunsetTick, 12);
            }
        }

        private void UpdateSunriseSunsetTicksLayout()
        {
            double w = TimelineCanvas.ActualWidth;
            double h = TimelineCanvas.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            void Place(Line tick, TimeSpan t)
            {
                double x = Math.Round((t.TotalHours / 24.0) * w);
                tick.X1 = x;
                tick.X2 = x;
                tick.Y1 = 0;
                tick.Y2 = h;
            }

            if (_sunriseTick != null)
            {
                if (Sunrise.HasValue)
                {
                    Place(_sunriseTick, Sunrise.Value);
                    _sunriseTick.Visibility = Visibility.Visible;
                }
                else
                {
                    _sunriseTick.Visibility = Visibility.Collapsed;
                }
            }

            if (_sunsetTick != null)
            {
                if (Sunset.HasValue)
                {
                    Place(_sunsetTick, Sunset.Value);
                    _sunsetTick.Visibility = Visibility.Visible;
                }
                else
                {
                    _sunsetTick.Visibility = Visibility.Collapsed;
                }
            }
        }

        // ===== Current-time indicator =====
        private void EnsureCurrentTimeIndicator()
        {
            if (_currentTimeTick == null)
            {
                _currentTimeTick = new Line { Style = (Style)this.Resources["CurrentTimeIndicatorStyle"] };
                TimelineCanvas.Children.Add(_currentTimeTick);
                Canvas.SetZIndex(_currentTimeTick, 15);
            }
        }

        private void UpdateCurrentTimeIndicatorLayout()
        {
            if (_currentTimeTick == null)
            {
                return;
            }

            double w = TimelineCanvas.ActualWidth;
            double h = TimelineCanvas.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                _currentTimeTick.Visibility = Visibility.Collapsed;
                return;
            }

            double x = Clamp(Math.Round((DateTime.Now.TimeOfDay.TotalHours / 24.0) * w), 1, Math.Max(1, w - 1));
            _currentTimeTick.X1 = x;
            _currentTimeTick.X2 = x;
            _currentTimeTick.Y1 = 2;
            _currentTimeTick.Y2 = Math.Max(2, h - 2);
            _currentTimeTick.Visibility = Visibility.Visible;
        }

        // ===== Sunrise/Sunset panels (below chart) =====
        private void EnsureSunPanels()
        {
            if (_sunrisePanel == null)
            {
                _sunrisePanel = MakeSunPanel("\uEC8A");
                AnnotationCanvas.Children.Add(_sunrisePanel);
            }

            if (_sunsetPanel == null)
            {
                _sunsetPanel = MakeSunPanel("\uED3A");
                AnnotationCanvas.Children.Add(_sunsetPanel);
            }
        }

        private StackPanel MakeSunPanel(string iconEmoji)
        {
            var icon = new FontIcon { Glyph = iconEmoji, Style = (Style)this.Resources["SunIconStyle"] };
            var sp = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
            sp.Children.Add(icon);
            return sp;
        }

        private void UpdateSunPanelsLayout()
        {
            double timelineW = TimelineCanvas.ActualWidth;
            double annotationW = AnnotationCanvas.ActualWidth;
            if (annotationW <= 0)
            {
                annotationW = timelineW;
            }

            if (timelineW <= 0 || annotationW <= 0)
            {
                return;
            }

            void Place(StackPanel sp, TimeSpan t)
            {
                double panelW = MeasureElementWidth(sp);
                double xTimeline = Math.Round((t.TotalHours / 24.0) * timelineW);
                double left = Clamp(xTimeline - (panelW / 2.0), 0, Math.Max(0, annotationW - panelW));
                Canvas.SetLeft(sp, left);
                Canvas.SetTop(sp, 8);
            }

            if (_sunrisePanel != null)
            {
                if (Sunrise.HasValue)
                {
                    ToolTipService.SetToolTip(_sunrisePanel, string.Format(CultureInfo.CurrentCulture, ResourceLoaderInstance.ResourceLoader.GetString("LightSwitch_Timeline_SunriseToolTip"), TimeSpanHelper.Convert(Sunrise.Value)));
                    _sunrisePanel.Visibility = Visibility.Visible;
                    Place(_sunrisePanel, Sunrise.Value);
                }
                else
                {
                    ToolTipService.SetToolTip(_sunrisePanel, null);
                    _sunrisePanel.Visibility = Visibility.Collapsed;
                }
            }

            if (_sunsetPanel != null)
            {
                if (Sunset.HasValue)
                {
                    ToolTipService.SetToolTip(_sunsetPanel, string.Format(CultureInfo.CurrentCulture, ResourceLoaderInstance.ResourceLoader.GetString("LightSwitch_Timeline_SunsetToolTip"), TimeSpanHelper.Convert(Sunset.Value)));
                    _sunsetPanel.Visibility = Visibility.Visible;
                    Place(_sunsetPanel, Sunset.Value);
                }
                else
                {
                    ToolTipService.SetToolTip(_sunsetPanel, null);
                    _sunsetPanel.Visibility = Visibility.Collapsed;
                }
            }
        }

        // ===== Major labels BELOW chart (00,06,12,18,24) =====
        private void EnsureMajorTickLabels()
        {
            if (_majorTickBottomLabels.Count > 0)
            {
                return;
            }

            // Includes 24:00 at end
            for (int i = 0; i < 5; i++)
            {
                var tb = new TextBlock { Style = (Style)this.Resources["MajorTickLabelStyle"] };
                Canvas.SetZIndex(tb, 5); // on annotation canvas
                _majorTickBottomLabels.Add(tb);
                AnnotationCanvas.Children.Add(tb);
            }
        }

        private void UpdateMajorTickLabelsLayout()
        {
            double timelineW = TimelineCanvas.ActualWidth;
            double annotationW = AnnotationCanvas.ActualWidth;
            if (annotationW <= 0)
            {
                annotationW = timelineW;
            }

            if (timelineW <= 0 || annotationW <= 0)
            {
                return;
            }

            int[] hours = { 0, 6, 12, 18, 24 };

            // 1) Place labels first
            for (int i = 0; i < hours.Length; i++)
            {
                var tb = _majorTickBottomLabels[i];
                var t = TimeSpan.FromHours(hours[i]);
                tb.Text = TimeSpanHelper.Convert(t);

                double xTimeline = Math.Round((t.TotalHours / 24.0) * timelineW);
                double textW = MeasureTextWidth(tb);
                double left = xTimeline - (textW / 2.0);

                // Center interior ticks (06, 12, 18) and clamp all labels inside the canvas bounds
                Canvas.SetLeft(tb, Clamp(left, 0, Math.Max(0, annotationW - textW)));
                Canvas.SetTop(tb, 8);
                tb.Visibility = Visibility.Visible;
            }

            // 2) Compute sunrise/sunset occupied horizontal ranges (if present)
            (double Left, double Right)? sunriseBounds = null;
            (double Left, double Right)? sunsetBounds = null;

            if (Sunrise.HasValue && _sunrisePanel != null)
            {
                sunriseBounds = GetAnnotationBoundsForTime(Sunrise.Value, timelineW, annotationW, _sunrisePanel);
            }

            if (Sunset.HasValue && _sunsetPanel != null)
            {
                sunsetBounds = GetAnnotationBoundsForTime(Sunset.Value, timelineW, annotationW, _sunsetPanel);
            }

            // 3) Hide any label that intersects the sunrise/sunset panel bounds or overlaps a neighbor on narrow widths
            (double Left, double Right)? lastVisibleBounds = null;
            for (int i = 0; i < hours.Length; i++)
            {
                var tb = _majorTickBottomLabels[i];
                if (tb.Visibility != Visibility.Visible)
                {
                    continue;
                }

                var lbl = GetLabelBounds(tb);

                bool hide =
                    (sunriseBounds.HasValue && Intersects(lbl, sunriseBounds.Value)) ||
                    (sunsetBounds.HasValue && Intersects(lbl, sunsetBounds.Value)) ||
                    (lastVisibleBounds.HasValue && Intersects(lastVisibleBounds.Value, lbl));

                if (hide)
                {
                    tb.Visibility = Visibility.Collapsed;
                }
                else
                {
                    tb.Visibility = Visibility.Visible;
                    lastVisibleBounds = lbl;
                }
            }
        }

        // ===== Utilities =====
        private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

        private static double MeasureElementWidth(FrameworkElement el)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return el.DesiredSize.Width;
        }

        private static double MeasureTextWidth(TextBlock tb)
        {
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return tb.DesiredSize.Width;
        }

        private static bool Intersects((double Left, double Right) a, (double Left, double Right) b, double pad = 4)
        {
            // Horizontal overlap with padding
            return !(a.Right + pad <= b.Left || b.Right + pad <= a.Left);
        }

        private (double Left, double Right) GetAnnotationBoundsForTime(TimeSpan t, double timelineW, double annotationW, FrameworkElement element)
        {
            // Compute the *actual* left/right the panel will occupy in AnnotationCanvas
            double panelW = MeasureElementWidth(element);
            double xTimeline = Math.Round((t.TotalHours / 24.0) * timelineW);
            double left = Clamp(xTimeline - (panelW / 2.0), 0, Math.Max(0, annotationW - panelW));
            return (left, left + panelW);
        }

        private (double Left, double Right) GetLabelBounds(TextBlock tb)
        {
            double w = MeasureTextWidth(tb);
            double left = Canvas.GetLeft(tb);
            return (left, left + w);
        }
    }
}
