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
                new PropertyMetadata(5.0, OnLayoutPropertyChanged));

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
                new PropertyMetadata(0, OnLayoutPropertyChanged));

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
            double barWidth = BarWidth > 0 ? BarWidth : 5.0;
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
            if (count == 0 || finalSize.Width <= 0 || finalSize.Height <= 0)
            {
                return finalSize;
            }

            double barWidth = BarWidth > 0 ? BarWidth : 5.0;
            double targetSpacing = Spacing > 0 ? Spacing : 2.0;
            double pitch = barWidth + targetSpacing;

            // Compute how many slots fit across the available width with ~targetSpacing
            int slots = Math.Max(1, (int)Math.Round((finalSize.Width + targetSpacing) / pitch));
            if (MaxSlots > 0 && slots > MaxSlots)
            {
                slots = MaxSlots;
            }

            // Step to ensure slot 0 is at x = 0 and slot (slots - 1) ends exactly at finalSize.Width - barWidth
            double step = slots > 1 ? (finalSize.Width - barWidth) / (slots - 1) : 0;

            // If we have more children than available slots, only show the most recent 'slots' items
            int startIndex = Math.Max(0, count - slots);

            for (int i = 0; i < count; i++)
            {
                if (i < startIndex)
                {
                    Children[i].Arrange(new Rect(0, 0, 0, 0));
                }
                else
                {
                    int slotIndex = i - startIndex;
                    double x = slotIndex * step;
                    Children[i].Arrange(new Rect(x, 0, barWidth, finalSize.Height));
                }
            }

            return finalSize;
        }
    }
}
