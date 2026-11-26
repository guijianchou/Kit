// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.ObjectModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Controls
{
    public sealed partial class SettingsPageControl : UserControl
    {
        public SettingsPageControl()
        {
            this.InitializeComponent();
            PrimaryLinks = new ObservableCollection<PageLink>();
            SecondaryLinks = new ObservableCollection<PageLink>();
        }

        public string ModuleTitle
        {
            get { return (string)GetValue(ModuleTitleProperty); }
            set { SetValue(ModuleTitleProperty, value); }
        }

        public string ModuleDescription
        {
            get => (string)GetValue(ModuleDescriptionProperty);
            set => SetValue(ModuleDescriptionProperty, value);
        }

        public Uri ModuleImageSource
        {
            get => (Uri)GetValue(ModuleImageSourceProperty);
            set => SetValue(ModuleImageSourceProperty, value);
        }

        public ObservableCollection<PageLink> PrimaryLinks
        {
            get => (ObservableCollection<PageLink>)GetValue(PrimaryLinksProperty);
            set => SetValue(PrimaryLinksProperty, value);
        }

        public string SecondaryLinksHeader
        {
            get { return (string)GetValue(SecondaryLinksHeaderProperty); }
            set { SetValue(SecondaryLinksHeaderProperty, value); }
        }

        public ObservableCollection<PageLink> SecondaryLinks
        {
            get => (ObservableCollection<PageLink>)GetValue(SecondaryLinksProperty);
            set => SetValue(SecondaryLinksProperty, value);
        }

        public object ModuleContent
        {
            get { return (object)GetValue(ModuleContentProperty); }
            set { SetValue(ModuleContentProperty, value); }
        }

        public bool CompactHeader
        {
            get => (bool)GetValue(CompactHeaderProperty);
            set => SetValue(CompactHeaderProperty, value);
        }

        public object HeaderAction
        {
            get => GetValue(HeaderActionProperty);
            set => SetValue(HeaderActionProperty, value);
        }

        public object HeaderSubContent
        {
            get => GetValue(HeaderSubContentProperty);
            set => SetValue(HeaderSubContentProperty, value);
        }

        public static readonly DependencyProperty ModuleTitleProperty = DependencyProperty.Register(nameof(ModuleTitle), typeof(string), typeof(SettingsPageControl), new PropertyMetadata(defaultValue: null));
        public static readonly DependencyProperty ModuleDescriptionProperty = DependencyProperty.Register(nameof(ModuleDescription), typeof(string), typeof(SettingsPageControl), new PropertyMetadata(defaultValue: null));
        public static readonly DependencyProperty ModuleImageSourceProperty = DependencyProperty.Register(nameof(ModuleImageSource), typeof(Uri), typeof(SettingsPageControl), new PropertyMetadata(null));
        public static readonly DependencyProperty PrimaryLinksProperty = DependencyProperty.Register(nameof(PrimaryLinks), typeof(ObservableCollection<PageLink>), typeof(SettingsPageControl), new PropertyMetadata(null));
        public static readonly DependencyProperty SecondaryLinksHeaderProperty = DependencyProperty.Register(nameof(SecondaryLinksHeader), typeof(string), typeof(SettingsPageControl), new PropertyMetadata(default(string)));
        public static readonly DependencyProperty SecondaryLinksProperty = DependencyProperty.Register(nameof(SecondaryLinks), typeof(ObservableCollection<PageLink>), typeof(SettingsPageControl), new PropertyMetadata(null));
        public static readonly DependencyProperty ModuleContentProperty = DependencyProperty.Register(nameof(ModuleContent), typeof(object), typeof(SettingsPageControl), new PropertyMetadata(null));
        public static readonly DependencyProperty CompactHeaderProperty = DependencyProperty.Register(nameof(CompactHeader), typeof(bool), typeof(SettingsPageControl), new PropertyMetadata(false));
        public static readonly DependencyProperty HeaderActionProperty = DependencyProperty.Register(nameof(HeaderAction), typeof(object), typeof(SettingsPageControl), new PropertyMetadata(null));
        public static readonly DependencyProperty HeaderSubContentProperty = DependencyProperty.Register(nameof(HeaderSubContent), typeof(object), typeof(SettingsPageControl), new PropertyMetadata(null));

        private Visibility GetHeaderSubContentVisibility(object content)
        {
            return content != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private Visibility GetCompactSubtitleVisibility(bool compactHeader, string description, int primaryLinkCount)
        {
            return compactHeader && (!string.IsNullOrWhiteSpace(description) || primaryLinkCount > 0)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private Visibility GetLegacyIntroVisibility(bool compactHeader, Uri imageSource, string description)
        {
            if (compactHeader)
            {
                return Visibility.Collapsed;
            }

            if (imageSource == null && string.IsNullOrWhiteSpace(description) && (PrimaryLinks == null || PrimaryLinks.Count == 0))
            {
                return Visibility.Collapsed;
            }

            return Visibility.Visible;
        }
    }
}
