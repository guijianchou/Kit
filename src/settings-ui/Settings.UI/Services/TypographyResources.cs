// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Kit.Settings.UI.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Kit.Settings.UI.Services;

/// <summary>
/// Keeps font resources and explicitly marked text sizes in sync in the current window.
/// </summary>
public sealed class TypographyResources : ResourceDictionary
{
    private static readonly IReadOnlyDictionary<string, double> NativeFontSizes = new Dictionary<string, double>
    {
        ["ControlContentThemeFontSize"] = 14,
        ["ContentControlFontSize"] = 14,
        ["BodyTextBlockFontSize"] = 14,
        ["BodyStrongTextBlockFontSize"] = 14,
        ["CaptionTextBlockFontSize"] = 12,
        ["BodyLargeTextBlockFontSize"] = 18,
        ["SubtitleTextBlockFontSize"] = 20,
        ["TitleTextBlockFontSize"] = 28,
        ["TitleLargeTextBlockFontSize"] = 40,
        ["DisplayTextBlockFontSize"] = 68,
        ["ToolTipContentThemeFontSize"] = 12,
        ["InfoBarTitleFontSize"] = 14,
        ["InfoBarMessageFontSize"] = 14,
        ["InfoBarHyperlinkButtonFontSize"] = 14,
        ["SettingsCardDescriptionFontSize"] = 12,
    };

    private static readonly Dictionary<string, Style> OriginalNativeStyles = new(StringComparer.Ordinal);
    private static readonly Dictionary<Style, double> NativeStyleBaselines = new();
    private static readonly ConditionalWeakTable<ResourceDictionary, object> InitializedNativeResources = new();
    private static readonly ConditionalWeakTable<DependencyObject, TextBaseline> Baselines = new();
    private static readonly ConditionalWeakTable<DependencyObject, object> MarkedTargets = new();
    private static readonly List<WeakReference<DependencyObject>> MarkedElements = new();

    public static readonly DependencyProperty BaseFontSizeProperty = RegisterSize("BaseFontSize");
    public static readonly DependencyProperty BaseLineHeightProperty = RegisterSize("BaseLineHeight");
    public static readonly DependencyProperty BaseTitleFontSizeProperty = RegisterSize("BaseTitleFontSize");
    public static readonly DependencyProperty BaseLabelFontSizeProperty = RegisterSize("BaseLabelFontSize");
    public static readonly DependencyProperty BaseDescriptionFontSizeProperty = RegisterSize("BaseDescriptionFontSize");

    private static double scale = 1;
    private static TypographyResources current;

    public static event EventHandler TypographyChanged;

    public static string AppliedFontFamily { get; private set; } = GeneralSettings.DefaultUiFontFamily;

    public static int AppliedFontSize { get; private set; } = GeneralSettings.DefaultUiFontSize;

    public TypographyResources()
    {
        current = this;
        var settings = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default).SettingsConfig;
        Populate(settings.UiFontFamily, settings.UiFontSize);
    }

    public static double GetBaseFontSize(DependencyObject target) => (double)target.GetValue(BaseFontSizeProperty);

    public static void SetBaseFontSize(DependencyObject target, double value) => target.SetValue(BaseFontSizeProperty, value);

    public static double GetBaseLineHeight(DependencyObject target) => (double)target.GetValue(BaseLineHeightProperty);

    public static void SetBaseLineHeight(DependencyObject target, double value) => target.SetValue(BaseLineHeightProperty, value);

    public static double GetBaseTitleFontSize(DependencyObject target) => (double)target.GetValue(BaseTitleFontSizeProperty);

    public static void SetBaseTitleFontSize(DependencyObject target, double value) => target.SetValue(BaseTitleFontSizeProperty, value);

    public static double GetBaseLabelFontSize(DependencyObject target) => (double)target.GetValue(BaseLabelFontSizeProperty);

    public static void SetBaseLabelFontSize(DependencyObject target, double value) => target.SetValue(BaseLabelFontSizeProperty, value);

    public static double GetBaseDescriptionFontSize(DependencyObject target) => (double)target.GetValue(BaseDescriptionFontSizeProperty);

    public static void SetBaseDescriptionFontSize(DependencyObject target, double value) => target.SetValue(BaseDescriptionFontSizeProperty, value);

    private void Populate(string fontFamily, int fontSize)
    {
        AppliedFontFamily = fontFamily == GeneralSettings.NotoSansScUiFontFamily ? fontFamily : GeneralSettings.DefaultUiFontFamily;
        AppliedFontSize = fontSize is >= GeneralSettings.MinimumUiFontSize and <= GeneralSettings.MaximumUiFontSize
            ? fontSize : GeneralSettings.DefaultUiFontSize;
        scale = AppliedFontSize / (double)GeneralSettings.DefaultUiFontSize;
        var family = new FontFamily(AppliedFontFamily == GeneralSettings.NotoSansScUiFontFamily
            ? "ms-appx:///Assets/Fonts/NotoSansSC-Variable.ttf#Noto Sans SC"
            : "XamlAutoFontFamily");

        this["KitUiFontFamily"] = family;
        this["ContentControlThemeFontFamily"] = family;
        this["KeyTipFontFamily"] = family;
        this["DashboardDetailLabelWidth"] = new GridLength(FontSize(92));
        this["DashboardUsageValueWidth"] = new GridLength(FontSize(144));
        this["DashboardTwoColumnThreshold"] = FontSize(720);

        foreach (double size in new double[] { 9, 10, 11, 12, 12.5, 13, 13.5, 14, 15, 16, 17, 18, 20, 22, 24, 26, 28, 30, 32, 34, 40, 46, 68 })
        {
            this["KitFontSize" + size.ToString(CultureInfo.InvariantCulture).Replace('.', '_')] = FontSize(size);
        }

        foreach (var token in NativeFontSizes)
        {
            this[token.Key] = FontSize(token.Value);
        }
    }

    public static double FontSize(double baseline) => Math.Round(baseline * scale, 3);

    internal static void Apply(string family, int size)
    {
        current.Populate(family, size);

        // This dictionary is already merged into Application.Resources. Updating
        // it directly avoids creating application-level entries WinUI then seals.
        // WinUI seals styles after controls use them. Runtime changes update the
        // live trees; native style wrappers are installed only before window creation.
        for (int index = MarkedElements.Count - 1; index >= 0; index--)
        {
            if (MarkedElements[index].TryGetTarget(out var target))
            {
                ApplyElement(target);
            }
            else
            {
                MarkedElements.RemoveAt(index);
            }
        }

        TypographyChanged?.Invoke(null, EventArgs.Empty);
    }

    private static DependencyProperty RegisterSize(string name) => DependencyProperty.RegisterAttached(
        name, typeof(double), typeof(TypographyResources), new PropertyMetadata(0d, OnBaseSizeChanged));

    private static void OnBaseSizeChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (!MarkedTargets.TryGetValue(target, out _))
        {
            MarkedTargets.Add(target, new object());
            MarkedElements.Add(new WeakReference<DependencyObject>(target));
        }

        // A marker can be applied by a Style while WinUI is still constructing
        // its setters. Writing the styled properties inside that callback can
        // re-enter native style resolution. Wait until the element is loaded.
        if (target is FrameworkElement element && !element.IsLoaded)
        {
            element.Loaded -= OnMarkedElementLoaded;
            element.Loaded += OnMarkedElementLoaded;
            return;
        }

        ApplyElement(target);
    }

    private static void OnMarkedElementLoaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        element.Loaded -= OnMarkedElementLoaded;
        ApplyElement(element);
    }

    internal static void TrackElement(DependencyObject target)
    {
        if (IsTypographyTarget(target))
        {
            GetBaseline(target);
        }
    }

    private static bool IsTypographyTarget(DependencyObject target) =>
        target is TextBlock or RichTextBlock or Control or ContentPresenter || MarkedTargets.TryGetValue(target, out _);

    internal static void ApplyElement(DependencyObject target)
    {
        if (target is FontIcon or SymbolIcon || !IsTypographyTarget(target))
        {
            return;
        }

        var baseline = GetBaseline(target);
        string existingFamily = GetFamily(target);
        if (baseline.Symbol || IsSymbol(existingFamily))
        {
            return;
        }

        double marked = GetBaseFontSize(target);

        // Shared controls bind their label size to the owner's live size property.
        // A local FontSize assignment would replace that binding and capture its
        // temporary fallback while a cached page is detached from the tree.
        bool boundFontSize = target is TextBlock boundText && boundText.GetBindingExpression(TextBlock.FontSizeProperty) != null;
        if (!boundFontSize && (marked > 0 || baseline.Size > 0))
        {
            SetFontSize(target, FontSize(marked > 0 ? marked : baseline.Size));
        }

        if (!baseline.Monospace && !IsMonospace(existingFamily) && current != null)
        {
            var family = (FontFamily)current["KitUiFontFamily"];
            switch (target)
            {
                case TextBlock text when text.FontFamily.Source != family.Source:
                    text.FontFamily = family;
                    break;
                case RichTextBlock richText when richText.FontFamily.Source != family.Source:
                    richText.FontFamily = family;
                    break;
                case Control control when control.FontFamily.Source != family.Source:
                    control.FontFamily = family;
                    break;
                case ContentPresenter presenter when presenter.FontFamily.Source != family.Source:
                    presenter.FontFamily = family;
                    break;
            }
        }

        double lineHeight = GetBaseLineHeight(target);
        if (lineHeight > 0)
        {
            SetDouble(target, "LineHeight", FontSize(lineHeight));
        }

        ApplyMarkedSize(target, BaseTitleFontSizeProperty, "TitleFontSize");
        ApplyMarkedSize(target, BaseLabelFontSizeProperty, "LabelFontSize");
        ApplyMarkedSize(target, BaseDescriptionFontSizeProperty, "DescriptionFontSize");
    }

    private static void ApplyMarkedSize(DependencyObject target, DependencyProperty marker, string property)
    {
        if ((double)target.GetValue(marker) is > 0 and var baseline)
        {
            SetDouble(target, property, FontSize(baseline));
        }
    }

    private static TextBaseline GetBaseline(DependencyObject target) => Baselines.GetValue(target, element =>
    {
        (double size, string family) = element switch
        {
            TextBlock text => (text.FontSize, text.FontFamily.Source),
            RichTextBlock text => (text.FontSize, text.FontFamily.Source),
            Control control => (control.FontSize, control.FontFamily.Source),
            ContentPresenter presenter => (presenter.FontSize, presenter.FontFamily.Source),
            _ => (0, string.Empty),
        };
        bool symbol = element is IconElement || IsSymbol(family);
        bool mono = IsMonospace(family);
        return new TextBaseline(ResolveBaseSize(element, size), mono, symbol);
    });

    private static double ResolveBaseSize(DependencyObject element, double size)
    {
        if (GetBaseFontSize(element) is > 0 and var marked)
        {
            return marked;
        }

        // Toolkit templates can keep the original caption style, resolved before
        // the appearance changed. A newly materialized description is still 12pt.
        for (DependencyObject ancestor = element; ancestor != null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is FrameworkElement { Name: "PART_DescriptionPresenter" })
            {
                return 12;
            }
        }

        if (element is FrameworkElement frameworkElement)
        {
            for (Style style = frameworkElement.Style; style != null; style = style.BasedOn)
            {
                if (NativeStyleBaselines.TryGetValue(style, out double nativeSize))
                {
                    return nativeSize;
                }

                foreach (var setter in style.Setters)
                {
                    if (setter is Setter { Property: var property, Value: double value } && property == BaseFontSizeProperty && value > 0)
                    {
                        return value;
                    }
                }
            }
        }

        // Template-bound text inherits the owner's already known base size.
        // This also covers controls materialized after a switch to a larger font.
        for (DependencyObject ancestor = VisualTreeHelper.GetParent(element); ancestor != null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (Baselines.TryGetValue(ancestor, out var baseline) && Math.Abs(GetFontSize(ancestor) - size) < 0.0001)
            {
                double ancestorMarked = GetBaseFontSize(ancestor);
                return ancestorMarked > 0 ? ancestorMarked : baseline.Size;
            }
        }

        if (element is Control control && control.ReadLocalValue(Control.FontSizeProperty) == DependencyProperty.UnsetValue)
        {
            return control is ToolTip ? 12 : 14;
        }

        return size / scale;
    }

    private static double GetFontSize(DependencyObject element) => element switch
    {
        TextBlock text => text.FontSize,
        RichTextBlock text => text.FontSize,
        Control control => control.FontSize,
        ContentPresenter presenter => presenter.FontSize,
        _ => 0,
    };

    private static string GetFamily(DependencyObject target) => target switch
    {
        TextBlock text => text.FontFamily.Source,
        RichTextBlock text => text.FontFamily.Source,
        Control control => control.FontFamily.Source,
        ContentPresenter presenter => presenter.FontFamily.Source,
        _ => string.Empty,
    };

    private static bool IsSymbol(string family) => family.Contains("Icons", StringComparison.OrdinalIgnoreCase)
        || family.Contains("Symbol", StringComparison.OrdinalIgnoreCase) || family.Contains("MDL2", StringComparison.OrdinalIgnoreCase);

    private static bool IsMonospace(string family) => family.Contains("Cascadia", StringComparison.OrdinalIgnoreCase)
        || family.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || family.Contains("Courier", StringComparison.OrdinalIgnoreCase);

    private static void SetFontSize(DependencyObject target, double value)
    {
        var property = target switch
        {
            TextBlock => TextBlock.FontSizeProperty,
            RichTextBlock => RichTextBlock.FontSizeProperty,
            Control => Control.FontSizeProperty,
            ContentPresenter => ContentPresenter.FontSizeProperty,
            _ => null,
        };

        if (property != null)
        {
            if (Math.Abs((double)target.GetValue(property) - value) > 0.0001)
            {
                target.SetValue(property, value);
            }
        }
        else
        {
            SetDouble(target, "FontSize", value);
        }
    }

    private static void SetDouble(DependencyObject target, string name, double value)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType == typeof(double) && property.CanWrite &&
            Math.Abs((double)property.GetValue(target) - value) > 0.0001)
        {
            property.SetValue(target, value);
        }
    }

    /// <summary>
    /// Native text styles contain resolved font values. Preserve their layout and
    /// weights while overriding typography before any window creates its controls.
    /// </summary>
    public static void ApplyNativeTextStyles(ResourceDictionary resources)
    {
        if (InitializedNativeResources.TryGetValue(resources, out _))
        {
            return;
        }

        var family = (FontFamily)resources["KitUiFontFamily"];
        foreach (var entry in new Dictionary<string, double>
        {
            ["BaseTextBlockStyle"] = 14,
            ["BodyTextBlockStyle"] = 14,
            ["BodyStrongTextBlockStyle"] = 14,
            ["CaptionTextBlockStyle"] = 12,
            ["BodyLargeTextBlockStyle"] = 18,
            ["SubtitleTextBlockStyle"] = 20,
            ["TitleTextBlockStyle"] = 28,
            ["TitleLargeTextBlockStyle"] = 40,
            ["DisplayTextBlockStyle"] = 68,
            ["HeaderTextBlockStyle"] = 46,
            ["SubheaderTextBlockStyle"] = 34,
        })
        {
            if (resources.TryGetValue(entry.Key, out var original) && original is Style originalStyle)
            {
                if (!OriginalNativeStyles.TryGetValue(entry.Key, out var baseStyle))
                {
                    OriginalNativeStyles[entry.Key] = baseStyle = originalStyle;
                }

                NativeStyleBaselines[baseStyle] = entry.Value;

                var style = new Style(typeof(TextBlock)) { BasedOn = baseStyle };
                style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, family));
                style.Setters.Add(new Setter(TextBlock.FontSizeProperty, FontSize(entry.Value)));
                style.Setters.Add(new Setter(BaseFontSizeProperty, entry.Value));
                NativeStyleBaselines[style] = entry.Value;
                resources[entry.Key] = style;
            }
        }

        foreach (string key in new[] { "BaseRichTextBlockStyle", "BodyRichTextBlockStyle" })
        {
            if (resources.TryGetValue(key, out var original) && original is Style originalStyle)
            {
                if (!OriginalNativeStyles.TryGetValue(key, out var baseStyle))
                {
                    OriginalNativeStyles[key] = baseStyle = originalStyle;
                }

                NativeStyleBaselines[baseStyle] = 14;

                var style = new Style(typeof(RichTextBlock)) { BasedOn = baseStyle };
                style.Setters.Add(new Setter(RichTextBlock.FontFamilyProperty, family));
                style.Setters.Add(new Setter(RichTextBlock.FontSizeProperty, FontSize(14)));
                style.Setters.Add(new Setter(BaseFontSizeProperty, 14d));
                NativeStyleBaselines[style] = 14;
                resources[key] = style;
            }
        }

        InitializedNativeResources.Add(resources, new object());
    }

    private sealed record TextBaseline(double Size, bool Monospace, bool Symbol);
}
