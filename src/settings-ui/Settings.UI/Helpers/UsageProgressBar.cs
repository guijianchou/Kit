// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
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
        Loaded += (_, _) => UpdateIndicatorColor(TargetValue);
        ActualThemeChanged += (_, _) => UpdateIndicatorColor(TargetValue);
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
        UpdateIndicatorColor(target);
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

    private void UpdateIndicatorColor(double value)
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        string category = DetectCategory();
        Color color = category switch
        {
            "Cpu" => isDark ? Color.FromArgb(0xFF, 0x38, 0xB6, 0xFF) : Color.FromArgb(0xFF, 0x00, 0x78, 0xD4),
            "Memory" => isDark ? Color.FromArgb(0xFF, 0xB1, 0x46, 0xC2) : Color.FromArgb(0xFF, 0x74, 0x4D, 0xA9),
            "Disk" => isDark ? Color.FromArgb(0xFF, 0x38, 0xAD, 0x62) : Color.FromArgb(0xFF, 0x0E, 0x7A, 0x0D),
            "Gpu" => isDark ? Color.FromArgb(0xFF, 0xFF, 0x8C, 0x00) : Color.FromArgb(0xFF, 0xD8, 0x3B, 0x01),
            "Temperature" => value >= 80d
                ? (isDark ? Color.FromArgb(0xFF, 0xFF, 0x52, 0x52) : Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C))
                : value >= 65d
                    ? (isDark ? Color.FromArgb(0xFF, 0xFF, 0x8C, 0x00) : Color.FromArgb(0xFF, 0xD8, 0x3B, 0x01))
                    : (isDark ? Color.FromArgb(0xFF, 0xFB, 0xB0, 0x3B) : Color.FromArgb(0xFF, 0xCA, 0x50, 0x10)),
            _ => isDark ? Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF) : Color.FromArgb(0xFF, 0x00, 0x78, 0xD4),
        };

        Foreground = new SolidColorBrush(color);
    }

    private string DetectCategory()
    {
        string name = AutomationProperties.GetName(this) ?? string.Empty;
        if (name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("处理器", StringComparison.OrdinalIgnoreCase))
        {
            return "Cpu";
        }

        if (name.Contains("内存", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("RAM", StringComparison.OrdinalIgnoreCase))
        {
            return "Memory";
        }

        if (name.Contains(":\\", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("磁盘", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Disk", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("存储", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Storage", StringComparison.OrdinalIgnoreCase))
        {
            return "Disk";
        }

        if (name.Contains("Temp", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("温度", StringComparison.OrdinalIgnoreCase))
        {
            return "Temperature";
        }

        if (name.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("显存", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VRAM", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("显卡", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Usage", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("使用率", StringComparison.OrdinalIgnoreCase))
        {
            return "Gpu";
        }

        return "Default";
    }
}
