// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.Settings.UI.Converters;

using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

/// <summary>
/// Maps an audit trend intensity bucket (0-4) to the heatmap cell brush.
/// </summary>
/// <remarks>
/// Lives in the UI layer on purpose: the trends aggregation is a headless data model and
/// must not reference WinUI types.
/// </remarks>
public sealed partial class TrendIntensityBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Transparent = new(Microsoft.UI.Colors.Transparent);
    private static readonly SolidColorBrush Light = new(Color.FromArgb(90, 108, 203, 95));
    private static readonly SolidColorBrush Medium = new(Color.FromArgb(150, 108, 203, 95));
    private static readonly SolidColorBrush Strong = new(Color.FromArgb(210, 255, 185, 0));
    private static readonly SolidColorBrush Critical = new(Color.FromArgb(230, 255, 99, 71));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            0 => Transparent,
            1 => Light,
            2 => Medium,
            3 => Strong,
            4 => Critical,
            _ => Transparent,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
