// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Kit.Settings.UI.Helpers
{
    public static class ThemeBrushHelper
    {
        private static readonly SolidColorBrush DefaultGrayBrush = new(ColorHelper.FromArgb(255, 128, 128, 128));
        private static readonly SolidColorBrush SuccessGreenBrush = new(ColorHelper.FromArgb(255, 16, 124, 65));
        private static readonly SolidColorBrush CautionOrangeBrush = new(ColorHelper.FromArgb(255, 255, 140, 0));
        private static readonly SolidColorBrush CriticalRedBrush = new(ColorHelper.FromArgb(255, 232, 17, 35));
        private static readonly SolidColorBrush AttentionBlueBrush = new(ColorHelper.FromArgb(255, 0, 120, 212));

        public static Brush GetBrush(string resourceKey, Brush fallbackBrush)
        {
            if (Application.Current?.Resources != null &&
                Application.Current.Resources.TryGetValue(resourceKey, out object obj) &&
                obj is Brush brush)
            {
                return brush;
            }

            return fallbackBrush;
        }

        public static Brush SuccessBrush => GetBrush("SystemFillColorSuccessBrush", SuccessGreenBrush);

        public static Brush CautionBrush => GetBrush("SystemFillColorCautionBrush", CautionOrangeBrush);

        public static Brush CriticalBrush => GetBrush("SystemFillColorCriticalBrush", CriticalRedBrush);

        public static Brush AttentionBrush => GetBrush("SystemFillColorAttentionBrush", AttentionBlueBrush);

        public static Brush SecondaryTextBrush => GetBrush("TextFillColorSecondaryBrush", DefaultGrayBrush);

        public static Brush StrokeDefaultBrush => GetBrush("ControlStrongStrokeColorDefaultBrush", DefaultGrayBrush);
    }
}
