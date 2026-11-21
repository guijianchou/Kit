// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Controls;

[TemplatePart(Name = PartContentHost, Type = typeof(ContentControl))]
[TemplatePart(Name = PartWarningBar, Type = typeof(InfoBar))]
public sealed partial class GPOInfoControl : ContentControl
{
    private const string PartContentHost = "PART_ContentHost";
    private const string PartWarningBar = "PART_WarningBar";
    private ContentControl _contentHost;
    private InfoBar _warningBar;

    public static readonly DependencyProperty ShowWarningProperty =
      DependencyProperty.Register(
          nameof(ShowWarning),
          typeof(bool),
          typeof(GPOInfoControl),
          new PropertyMetadata(false, OnShowWarningPropertyChanged));

    public bool ShowWarning
    {
        get => (bool)GetValue(ShowWarningProperty);
        set => SetValue(ShowWarningProperty, value);
    }

    private static void OnShowWarningPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GPOInfoControl gpoInfoControl)
        {
            gpoInfoControl.UpdateContentEnabledState();
        }
    }

    public GPOInfoControl()
    {
        DefaultStyleKey = typeof(GPOInfoControl);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _contentHost = GetTemplateChild(PartContentHost) as ContentControl;
        _warningBar = GetTemplateChild(PartWarningBar) as InfoBar;
        UpdateContentEnabledState();
    }

    private void UpdateContentEnabledState()
    {
        if (_contentHost != null)
        {
            _contentHost.IsEnabled = !ShowWarning;
        }

        if (_warningBar != null)
        {
            _warningBar.Visibility = ShowWarning ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
