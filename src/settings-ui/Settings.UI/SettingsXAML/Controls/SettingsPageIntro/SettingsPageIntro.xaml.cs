// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Controls;

public sealed partial class SettingsPageIntro : UserControl
{
    public SettingsPageIntro()
    {
        PrimaryLinks = new ObservableCollection<PageLink>();
        InitializeComponent();
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

    public static readonly DependencyProperty ModuleDescriptionProperty = DependencyProperty.Register(
        nameof(ModuleDescription), typeof(string), typeof(SettingsPageIntro), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ModuleImageSourceProperty = DependencyProperty.Register(
        nameof(ModuleImageSource), typeof(Uri), typeof(SettingsPageIntro), new PropertyMetadata(null));

    public static readonly DependencyProperty PrimaryLinksProperty = DependencyProperty.Register(
        nameof(PrimaryLinks), typeof(ObservableCollection<PageLink>), typeof(SettingsPageIntro), new PropertyMetadata(null));

    public bool FocusPrimaryLinks() => PrimaryLinksControl.Focus(FocusState.Programmatic);
}
