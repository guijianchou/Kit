// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Kit.Settings.UI.Library;
using Microsoft.UI.Xaml.Data;

namespace Kit.Settings.UI.Converters
{
    public sealed partial class AwakeModeToIntConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var mode = (AwakeMode)value;
            return (int)mode;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return (AwakeMode)Enum.ToObject(typeof(AwakeMode), (int)value);
        }
    }
}
