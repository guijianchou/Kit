// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Controls;

[TemplatePart(Name = PartContentHost, Type = typeof(ContentControl))]
public sealed partial class GPOInfoControl : ContentControl
{
    private const string PartContentHost = "PART_ContentHost";
    private ContentControl _contentHost;

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
        UpdateContentEnabledState();
    }

    private void UpdateContentEnabledState()
    {
        if (_contentHost != null)
        {
            _contentHost.IsEnabled = !ShowWarning;
        }
    }
}
