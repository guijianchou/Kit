// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Kit.Settings.UI.Controls
{
    public sealed partial class QuickAccessGridPanel : Panel
    {
        public int Columns { get; set; } = 3;

        public double ColumnSpacing { get; set; } = 12.0;

        public double RowSpacing { get; set; } = 12.0;

        protected override Size MeasureOverride(Size availableSize)
        {
            int cols = Math.Max(1, Columns);
            double width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? 384.0 : availableSize.Width;
            double itemWidth = Math.Max(0, (width - ((cols - 1) * ColumnSpacing)) / cols);

            var visibleChildren = new List<UIElement>(Children.Count);
            foreach (UIElement child in Children)
            {
                child.Measure(new Size(itemWidth, availableSize.Height));
                if (child.Visibility == Visibility.Visible && (child.DesiredSize.Width > 0 || child.DesiredSize.Height > 0))
                {
                    visibleChildren.Add(child);
                }
            }

            if (visibleChildren.Count == 0)
            {
                return new Size(0, 0);
            }

            int rows = ((visibleChildren.Count + cols) - 1) / cols;
            double totalHeight = 0;
            for (int r = 0; r < rows; r++)
            {
                double rowHeight = 0;
                int start = r * cols;
                int end = Math.Min(start + cols, visibleChildren.Count);
                for (int i = start; i < end; i++)
                {
                    rowHeight = Math.Max(rowHeight, visibleChildren[i].DesiredSize.Height);
                }

                totalHeight += rowHeight;
                if (r > 0)
                {
                    totalHeight += RowSpacing;
                }
            }

            return new Size(width, totalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int cols = Math.Max(1, Columns);
            double itemWidth = Math.Max(0, (finalSize.Width - ((cols - 1) * ColumnSpacing)) / cols);

            var visibleChildren = new List<UIElement>(Children.Count);
            foreach (UIElement child in Children)
            {
                if (child.Visibility == Visibility.Visible && (child.DesiredSize.Width > 0 || child.DesiredSize.Height > 0))
                {
                    visibleChildren.Add(child);
                }
                else
                {
                    child.Arrange(new Rect(0, 0, 0, 0));
                }
            }

            int rows = ((visibleChildren.Count + cols) - 1) / cols;
            double y = 0;
            for (int r = 0; r < rows; r++)
            {
                double rowHeight = 0;
                int start = r * cols;
                int end = Math.Min(start + cols, visibleChildren.Count);
                for (int i = start; i < end; i++)
                {
                    rowHeight = Math.Max(rowHeight, visibleChildren[i].DesiredSize.Height);
                }

                for (int i = start; i < end; i++)
                {
                    int c = i - start;
                    double x = c * (itemWidth + ColumnSpacing);
                    visibleChildren[i].Arrange(new Rect(x, y, itemWidth, rowHeight));
                }

                y += rowHeight + RowSpacing;
            }

            return finalSize;
        }
    }
}
