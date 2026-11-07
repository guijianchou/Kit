// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace Kit.Settings.UI.Controls
{
    /// <summary>
    /// Draws probe history with three paths and one tooltip instead of a visual
    /// and tooltip for every sample. Samples outside the visible slots have no geometry.
    /// </summary>
    public sealed partial class ProbeHistoryStrip : UserControl
    {
        private const double BarWidth = 5;
        private const double Spacing = 2;
        private readonly Path[] _paths = [CreatePath(), CreatePath(), CreatePath()];
        private readonly ToolTip _toolTip = new();
        private INotifyCollectionChanged? _observedSource;
        private int _firstVisible;
        private int _visibleCount;
        private int _selectedIndex = -1;
        private double _step;
        private object? _renderedSource;
        private long _renderedRevision = -1;
        private Size _renderedSize;
        private ElementTheme _renderedTheme;

        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(object), typeof(ProbeHistoryStrip), new PropertyMetadata(null, OnItemsSourceChanged));

        public ProbeHistoryStrip()
        {
            var canvas = new Canvas { Background = new SolidColorBrush(Colors.Transparent) };
            foreach (var path in _paths)
            {
                canvas.Children.Add(path);
            }

            Content = canvas;
            IsTabStop = true;
            UseSystemFocusVisuals = true;
            ToolTipService.SetToolTip(this, _toolTip);
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += (_, _) => Redraw();

            PointerMoved += OnPointerMoved;
            PointerExited += (_, _) => _toolTip.IsOpen = false;
            KeyDown += OnKeyDown;
            LostFocus += (_, _) => _toolTip.IsOpen = false;
        }

        public object? ItemsSource
        {
            get => GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        private IList<StatusBarViewModel>? Samples => ItemsSource as IList<StatusBarViewModel>;

        protected override AutomationPeer OnCreateAutomationPeer() => new ProbeHistoryStripAutomationPeer(this);

        private static Path CreatePath() => new()
        {
            StrokeThickness = BarWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };

        private static void OnItemsSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var strip = (ProbeHistoryStrip)sender;
            strip.UpdateSourceSubscription();
            strip.Redraw();
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            // Refresh the native event registration when a cached page is reattached.
            // Keeping an ActualThemeChanged subscription across unloads can leave it inactive.
            ((FrameworkElement)Content).ActualThemeChanged -= OnContentThemeChanged;
            ((FrameworkElement)Content).ActualThemeChanged += OnContentThemeChanged;
            UpdateSourceSubscription();
            Redraw();
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            ((FrameworkElement)Content).ActualThemeChanged -= OnContentThemeChanged;
            Unsubscribe();
            _toolTip.IsOpen = false;
        }

        private void OnContentThemeChanged(FrameworkElement sender, object args) => Redraw();

        private void UpdateSourceSubscription()
        {
            Unsubscribe();
            if (IsLoaded && ItemsSource is INotifyCollectionChanged source)
            {
                _observedSource = source;
                source.CollectionChanged += OnCollectionChanged;
            }
        }

        private void Unsubscribe()
        {
            if (_observedSource is not null)
            {
                _observedSource.CollectionChanged -= OnCollectionChanged;
                _observedSource = null;
            }
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => Redraw();

        private void Redraw()
        {
            if (!IsLoaded)
            {
                return;
            }

            var samples = Samples;
            long revision = samples is BoundedObservableCollection<StatusBarViewModel> bounded ? bounded.Revision : -1;
            var size = new Size(ActualWidth, ActualHeight);
            if (revision >= 0 && ReferenceEquals(_renderedSource, samples) &&
                _renderedRevision == revision && _renderedSize == size && _renderedTheme == ActualTheme)
            {
                return;
            }

            // Cached pages keep their bounded geometry while unloaded. Rebuild only
            // when samples, available size or theme changed while the page was away.
            _renderedSource = samples;
            _renderedRevision = revision;
            _renderedSize = size;
            _renderedTheme = ActualTheme;
            _visibleCount = 0;
            foreach (var path in _paths)
            {
                path.Data = null;
            }

            if (samples is null || samples.Count == 0 || ActualWidth < BarWidth || ActualHeight < BarWidth)
            {
                _selectedIndex = -1;
                _toolTip.Content = null;
                _toolTip.IsOpen = false;
                AutomationProperties.SetHelpText(this, string.Empty);
                return;
            }

            int slots = Math.Max(1, (int)Math.Floor((ActualWidth + Spacing) / (BarWidth + Spacing)));
            _visibleCount = Math.Min(samples.Count, slots);
            _firstVisible = samples.Count - _visibleCount;
            _step = slots > 1 ? (ActualWidth - BarWidth) / (slots - 1) : 0;

            // Fill from the left as samples arrive. Once full, discard the oldest
            // visible sample and keep the newest sample at the right edge.
            PathGeometry[] geometries = [new(), new(), new()];
            for (int offset = 0; offset < _visibleCount; offset++)
            {
                var sample = samples[_firstVisible + offset];
                double height = Math.Min(sample.BarHeight, ActualHeight);
                double x = (offset * _step) + (BarWidth / 2);
                var figure = new PathFigure
                {
                    StartPoint = new Point(x, ActualHeight - height + (BarWidth / 2)),
                    IsClosed = false,
                    IsFilled = false,
                };
                figure.Segments.Add(new LineSegment { Point = new Point(x, ActualHeight - (BarWidth / 2)) });

                // FromProbeResult uses 16 / 11 / 6 for success / slow / failed samples.
                int severity = sample.BarHeight > 11 ? 0 : sample.BarHeight > 6 ? 1 : 2;
                geometries[severity].Figures.Add(figure);
            }

            _paths[0].Stroke = ThemeBrushHelper.GetBrush("SystemFillColorSuccessBrush", ThemeBrushHelper.SuccessBrush, ActualTheme);
            _paths[1].Stroke = ThemeBrushHelper.GetBrush("SystemFillColorCautionBrush", ThemeBrushHelper.CautionBrush, ActualTheme);
            _paths[2].Stroke = ThemeBrushHelper.GetBrush("SystemFillColorCriticalBrush", ThemeBrushHelper.CriticalBrush, ActualTheme);
            for (int index = 0; index < _paths.Length; index++)
            {
                _paths[index].Data = geometries[index];
            }

            SelectSample(samples.Count - 1);
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
        {
            if (_visibleCount == 0)
            {
                return;
            }

            double x = args.GetCurrentPoint(this).Position.X;
            int offset = _step > 0 ? (int)Math.Round((x - (BarWidth / 2)) / _step) : 0;
            double left = offset * _step;
            if (offset >= 0 && offset < _visibleCount && x >= left && x <= left + BarWidth)
            {
                SelectSample(_firstVisible + offset);
                _toolTip.IsOpen = true;
            }
            else
            {
                _toolTip.IsOpen = false;
            }
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (_visibleCount == 0)
            {
                return;
            }

            int last = _firstVisible + _visibleCount - 1;
            int index = args.Key switch
            {
                VirtualKey.Left => Math.Max(_firstVisible, _selectedIndex - 1),
                VirtualKey.Right => Math.Min(last, _selectedIndex + 1),
                VirtualKey.Home => _firstVisible,
                VirtualKey.End => last,
                _ => -1,
            };
            if (index >= 0)
            {
                SelectSample(index);
                _toolTip.IsOpen = true;
                args.Handled = true;
            }
        }

        private void SelectSample(int index)
        {
            var samples = Samples;
            if (samples is null || index < 0 || index >= samples.Count)
            {
                return;
            }

            _selectedIndex = index;
            _toolTip.Content = samples[index].Description;
            AutomationProperties.SetHelpText(this, $"{index + 1} / {samples.Count}: {samples[index].Description}");
        }
    }

    internal sealed partial class ProbeHistoryStripAutomationPeer(ProbeHistoryStrip owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ProbeHistoryStrip);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    }
}
