// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using ManagedCommon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Kit.Settings.UI.Controls
{
    public sealed partial class QuickAccessList : UserControl
    {
        private bool _wasUnloaded;

        public QuickAccessList()
        {
            this.InitializeComponent();
            Loaded += OnQuickAccessListLoaded;
            Unloaded += OnQuickAccessListUnloaded;
            ActualThemeChanged += OnActualThemeChanged;
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

        private void QuickAccessIcon_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Image image)
            {
                ApplyFreshIconSource(image);
            }
        }

        private void QuickAccessIcon_Unloaded(object sender, RoutedEventArgs e)
        {
            // Release cached-page BitmapImage before leaving the live tree so returning to a
            // cached page (NavigationCacheMode=Required) always binds a fresh DComp surface.
            if (sender is Image image)
            {
                image.Source = null;
            }
        }

        private void QuickAccessIcon_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is Image image && image.IsLoaded)
            {
                ApplyFreshIconSource(image);
            }
        }

        private void OnQuickAccessListLoaded(object sender, RoutedEventArgs e)
        {
            if (!_wasUnloaded)
            {
                return;
            }

            _wasUnloaded = false;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (IsLoaded)
                {
                    RefreshVisualIcons(this);
                }
            });
        }

        private void OnQuickAccessListUnloaded(object sender, RoutedEventArgs e)
        {
            _wasUnloaded = true;
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            if (IsLoaded)
            {
                RefreshVisualIcons(this);
            }
        }

        private static void RefreshVisualIcons(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is Image image && (image.Name == "QuickAccessIcon" || image.Name == "QuickAccessTileIcon"))
                {
                    ApplyFreshIconSource(image);
                }
                else
                {
                    RefreshVisualIcons(child);
                }
            }
        }

        private static void ApplyFreshIconSource(Image image)
        {
            string? iconPath = (image.DataContext as QuickAccessItem)?.Icon;
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                iconPath = image.Tag as string;
            }

            if (string.IsNullOrWhiteSpace(iconPath) || !Uri.TryCreate(iconPath, UriKind.Absolute, out Uri? iconUri))
            {
                image.Source = null;
                return;
            }

            // Use a fresh BitmapImage without DecodePixelWidth and ignore evicted cache entries so
            // WinUI 3 always uploads a valid DirectComposition texture on cached-page re-entry.
            var bitmap = new BitmapImage
            {
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                UriSource = iconUri,
            };
            image.Source = null;
            image.Source = bitmap;
        }

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
