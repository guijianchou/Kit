// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.UI.Xaml.Data;

namespace Kit.Settings.UI.Converters
{
    public sealed partial class BoolToDisabledOpacityConverter : IValueConverter
    {
        public double EnabledOpacity { get; set; } = 1.0;

        public double DisabledOpacity { get; set; } = 0.38;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isEnabled)
            {
                return isEnabled ? EnabledOpacity : DisabledOpacity;
            }

            return EnabledOpacity;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
