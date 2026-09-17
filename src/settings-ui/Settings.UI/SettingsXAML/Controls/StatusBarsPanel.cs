// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Kit.Settings.UI.Controls
{
    public sealed partial class StatusBarsPanel : Panel
    {
        public static readonly DependencyProperty BarWidthProperty =
            DependencyProperty.Register(
                nameof(BarWidth),
                typeof(double),
                typeof(StatusBarsPanel),
                new PropertyMetadata(3.0, OnLayoutPropertyChanged));

        public static readonly DependencyProperty SpacingProperty =
            DependencyProperty.Register(
                nameof(Spacing),
                typeof(double),
                typeof(StatusBarsPanel),
                new PropertyMetadata(2.0, OnLayoutPropertyChanged));

        public static readonly DependencyProperty MaxSlotsProperty =
            DependencyProperty.Register(
                nameof(MaxSlots),
                typeof(int),
                typeof(StatusBarsPanel),
                new PropertyMetadata(60, OnLayoutPropertyChanged));

        public double BarWidth
        {
            get => (double)GetValue(BarWidthProperty);
            set => SetValue(BarWidthProperty, value);
        }

        public double Spacing
        {
            get => (double)GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        public int MaxSlots
        {
            get => (int)GetValue(MaxSlotsProperty);
            set => SetValue(MaxSlotsProperty, value);
        }

        private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StatusBarsPanel panel)
            {
                panel.InvalidateMeasure();
                panel.InvalidateArrange();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            int count = Children.Count;
            if (count == 0)
            {
                return new Size(0, 0);
            }

            double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
            double barWidth = BarWidth;
            double maxHeight = 0;

            foreach (UIElement child in Children)
            {
                child.Measure(new Size(barWidth, availableSize.Height));
                maxHeight = Math.Max(maxHeight, child.DesiredSize.Height);
            }

            return new Size(width, maxHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int count = Children.Count;
            if (count == 0)
            {
                return finalSize;
            }

            int slots = Math.Max(MaxSlots > 1 ? MaxSlots : 1, count);
            double barWidth = BarWidth;
            double step = slots > 1 ? Math.Max(0, (finalSize.Width - barWidth) / (slots - 1)) : 0;

            for (int i = 0; i < count; i++)
            {
                double x = i * step;
                Children[i].Arrange(new Rect(x, 0, barWidth, finalSize.Height));
            }

            return finalSize;
        }
    }
}
