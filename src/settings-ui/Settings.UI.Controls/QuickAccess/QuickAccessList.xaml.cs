// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using ManagedCommon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Controls
{
    public sealed partial class QuickAccessList : UserControl
    {
        public QuickAccessList()
        {
            this.InitializeComponent();
        }

        public object ItemsSource
        {
            get => (object)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(QuickAccessList), new PropertyMetadata(null));

        public double LabelFontSize
        {
            get => (double)GetValue(LabelFontSizeProperty);
            set => SetValue(LabelFontSizeProperty, value);
        }

        public static readonly DependencyProperty LabelFontSizeProperty = DependencyProperty.Register(
            nameof(LabelFontSize), typeof(double), typeof(QuickAccessList), new PropertyMetadata(14d));

        public bool IsCompact
        {
            get => (bool)GetValue(IsCompactProperty);
            set => SetValue(IsCompactProperty, value);
        }

        public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
            nameof(IsCompact), typeof(bool), typeof(QuickAccessList), new PropertyMetadata(false));

        public bool IsTileMode(bool isCompact) => !isCompact;

        public static string GetModuleGlyph(object? module) => module switch
        {
            ModuleType.AIHub => "\uEA80",
            ModuleType.Awake => "\uE708",
            ModuleType.LightSwitch => "\uE706",
            ModuleType.Localserver => "\uE968",
            ModuleType.NetMap => "\uE774",
            ModuleType.UDPtest => "\uE9D9",
            ModuleType.Winget => "\uE7B8",
            _ => "\uE71D",
        };
    }
}
