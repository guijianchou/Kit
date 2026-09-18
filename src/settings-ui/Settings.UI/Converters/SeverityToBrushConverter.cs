// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Kit.Settings.UI.Converters
{
    public sealed partial class SeverityToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush HighBrush = new(ColorHelper.FromArgb(255, 232, 17, 35));
        private static readonly SolidColorBrush MediumBrush = new(ColorHelper.FromArgb(255, 255, 140, 0));
        private static readonly SolidColorBrush LowBrush = new(ColorHelper.FromArgb(255, 0, 120, 212));
        private static readonly SolidColorBrush DefaultBrush = new(ColorHelper.FromArgb(255, 128, 128, 128));

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string severity)
            {
                if (string.Equals(severity, "High", StringComparison.OrdinalIgnoreCase))
                {
                    return HighBrush;
                }

                if (string.Equals(severity, "Medium", StringComparison.OrdinalIgnoreCase))
                {
                    return MediumBrush;
                }

                if (string.Equals(severity, "Low", StringComparison.OrdinalIgnoreCase))
                {
                    return LowBrush;
                }
            }

            return DefaultBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
