// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
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
        private static readonly Dictionary<(ElementTheme Theme, string ResourceKey), Border> BrushProbes = new();

        public static Brush GetBrush(string resourceKey, Brush fallbackBrush, ElementTheme? theme = null)
        {
            if (!IsResourceKeyValid(resourceKey))
            {
                return fallbackBrush;
            }

            try
            {
                ElementTheme effectiveTheme = theme is ElementTheme.Light or ElementTheme.Dark
                    ? theme.Value
                    : (App.GetSettingsWindow()?.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
                if (Application.Current != null)
                {
                    if (effectiveTheme == ElementTheme.Default)
                    {
                        effectiveTheme = Application.Current.RequestedTheme == ApplicationTheme.Light
                            ? ElementTheme.Light : ElementTheme.Dark;
                    }

                    var cacheKey = (effectiveTheme, resourceKey);
                    if (!BrushProbes.TryGetValue(cacheKey, out Border probe))
                    {
                        // Let WinUI resolve the same ThemeResource expression as a visible
                        // element. Native resource dictionaries do not support enumerating
                        // every packaged resource through their managed Keys projection.
                        probe = (Border)XamlReader.Load(
                            "<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" RequestedTheme=\""
                            + effectiveTheme + "\" Background=\"{ThemeResource " + resourceKey + "}\" />");
                        BrushProbes.Add(cacheKey, probe);
                    }

                    // Keep the element and its live resource expression, not a brush
                    // snapshot, so system palette/high-contrast updates remain native.
                    return probe.Background ?? fallbackBrush;
                }
            }
            catch
            {
            }

            return fallbackBrush;
        }

        public static Brush SuccessBrush => GetBrush("SystemFillColorSuccessBrush", SuccessGreenBrush);

        public static Brush CautionBrush => GetBrush("SystemFillColorCautionBrush", CautionOrangeBrush);

        public static Brush CriticalBrush => GetBrush("SystemFillColorCriticalBrush", CriticalRedBrush);

        public static Brush AttentionBrush => GetBrush("SystemFillColorAttentionBrush", AttentionBlueBrush);

        public static Brush SecondaryTextBrush => GetBrush("TextFillColorSecondaryBrush", DefaultGrayBrush);

        public static Brush StrokeDefaultBrush => GetBrush("ControlStrongStrokeColorDefaultBrush", DefaultGrayBrush);

        private static bool IsResourceKeyValid(string resourceKey)
        {
            if (string.IsNullOrEmpty(resourceKey))
            {
                return false;
            }

            // Internal resource names are identifiers. Reject XAML/markup delimiters
            // instead of permitting a resource key to alter the probe's expression.
            foreach (char character in resourceKey)
            {
                if (!char.IsLetterOrDigit(character) && character != '_' && character != '.' && character != '-')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
