// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Microsoft.PowerToys.Settings.UI.ViewModels
{
    public static class MonitorStatusBrushes
    {
        public static Brush Success { get; } = new SolidColorBrush(Color.FromArgb(255, 16, 124, 16));

        public static Brush Warning { get; } = new SolidColorBrush(Color.FromArgb(255, 202, 80, 16));

        public static Brush Failed { get; } = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28));

        public static Brush Empty { get; } = new SolidColorBrush(Color.FromArgb(255, 138, 138, 138));
    }
}
