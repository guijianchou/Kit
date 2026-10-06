// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Kit.Settings.UI.Helpers;

public sealed partial class UsageProgressBar : ProgressBar
{
    public static readonly DependencyProperty TargetValueProperty = DependencyProperty.Register(
        nameof(TargetValue), typeof(double), typeof(UsageProgressBar), new PropertyMetadata(0d, OnTargetValueChanged));

    private readonly UISettings uiSettings = new();
    private Storyboard transition;

    public UsageProgressBar()
    {
        Unloaded += (_, _) => SetUsage(animate: false);
    }

    public double TargetValue
    {
        get => (double)GetValue(TargetValueProperty);
        set => SetValue(TargetValueProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("LayoutRoot") is FrameworkElement root)
        {
            // The native update transition slides the whole indicator. Our Value animation
            // already changes its width; keep the left edge fixed while it does so.
            foreach (var group in VisualStateManager.GetVisualStateGroups(root))
            {
                for (int index = group.Transitions.Count - 1; index >= 0; index--)
                {
                    var update = group.Transitions[index];
                    if ((update.From == "Updating" && update.To == "Determinate") ||
                        (update.From == "UpdatingError" && update.To == "Error"))
                    {
                        update.Storyboard?.Stop();
                        group.Transitions.RemoveAt(index);
                    }
                }
            }
        }
    }

    private static void OnTargetValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var bar = (UsageProgressBar)sender;
        bar.SetUsage(bar.IsLoaded && bar.uiSettings.AnimationsEnabled);
    }

    private void SetUsage(bool animate)
    {
        double target = double.IsFinite(TargetValue) ? Math.Clamp(TargetValue, Minimum, Maximum) : Minimum;

        // Retarget from the currently displayed value, including an unfinished transition.
        double current = Value;
        transition?.Stop();
        transition = null;
        Value = target;
        if (!animate || Math.Abs(target - current) < 0.01)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = TimeSpan.FromMilliseconds(450),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, this);
        Storyboard.SetTargetProperty(animation, nameof(Value));
        transition = new Storyboard();
        transition.Children.Add(animation);
        transition.Begin();
    }
}
