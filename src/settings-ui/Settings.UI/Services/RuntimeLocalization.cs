// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Kit.Settings.UI.Helpers;
using ManagedCommon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Kit.Settings.UI.Services;

/// <summary>Reapplies the resources owned by x:Uid to existing controls.</summary>
public sealed class RuntimeLocalization : DependencyObject
{
    private static readonly List<WeakReference<DependencyObject>> Targets = new();
    private static readonly Dictionary<string, List<KeyValuePair<string, string>>> Values = new(StringComparer.Ordinal);
    private static readonly string SystemLanguage = CultureInfo.CurrentUICulture.Name;
    private static ResourceManager manager;
    private static ResourceContext context;
    private static ResourceMap resources;

    public static readonly DependencyProperty UidProperty = DependencyProperty.RegisterAttached(
        "Uid", typeof(string), typeof(RuntimeLocalization), new PropertyMetadata(string.Empty, OnUidChanged));

    public static event EventHandler LanguageChanged;

    public static string AppliedLanguage { get; private set; } = string.Empty;

    public static string EffectiveLanguage { get; private set; } = "en-US";

    public static string GetUid(DependencyObject target) => (string)target.GetValue(UidProperty);

    public static void SetUid(DependencyObject target, string value) => target.SetValue(UidProperty, value);

    internal static void Initialize(string language)
    {
        AppliedLanguage = language ?? string.Empty;
        string selected = string.IsNullOrEmpty(AppliedLanguage) ? SystemLanguage : AppliedLanguage;
        EffectiveLanguage = selected.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase)
            || selected.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || selected.Equals("zh-SG", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";

        // MRT Core rejects an empty override. Keep the persisted empty value for
        // "Windows default", and resolve it to the system language for this process.
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = EffectiveLanguage;
        var culture = CultureInfo.GetCultureInfo(EffectiveLanguage);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        ResourceLoaderInstance.Refresh();
        manager = new ResourceManager("Kit.Settings.pri");
        context = manager.CreateResourceContext();
        context.QualifierValues["Language"] = EffectiveLanguage;
        resources = manager.MainResourceMap.GetSubtree("Resources");
        Values.Clear();
    }

    internal static void ApplyLanguage(string language)
    {
        Initialize(language);
        for (int index = Targets.Count - 1; index >= 0; index--)
        {
            if (Targets[index].TryGetTarget(out var target))
            {
                Apply(target);
            }
            else
            {
                Targets.RemoveAt(index);
            }
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void OnUidChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty((string)args.OldValue))
        {
            Targets.Add(new WeakReference<DependencyObject>(target));
            if (target is FrameworkElement element)
            {
                element.Loaded += OnElementLoaded;
            }
        }

        Apply(target);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs args) => Apply((DependencyObject)sender);

    internal static void Apply(DependencyObject target)
    {
        string uid = GetUid(target);
        if (resources == null || string.IsNullOrEmpty(uid))
        {
            return;
        }

        if (!Values.TryGetValue(uid, out var properties))
        {
            properties = new List<KeyValuePair<string, string>>();
            var subtree = resources.TryGetSubtree(uid);
            if (subtree != null)
            {
                for (uint index = 0; index < subtree.ResourceCount; index++)
                {
                    var entry = subtree.GetValueByIndex(index, context);
                    string key = entry.Key;
                    int prefix = key.IndexOf(uid + "/", StringComparison.Ordinal);
                    if (prefix >= 0)
                    {
                        key = key[(prefix + uid.Length + 1)..];
                    }

                    properties.Add(new KeyValuePair<string, string>(key.Replace('/', '.'), entry.Value.ValueAsString));
                }
            }

            Values[uid] = properties;
        }

        foreach (var property in properties)
        {
            try
            {
                SetLocalizedProperty(target, property.Key, property.Value);
            }
            catch (Exception ex) when (ex is ArgumentException or TargetInvocationException or InvalidCastException)
            {
                Logger.LogError($"Could not refresh localized property {uid}/{property.Key}.", ex);
            }
        }
    }

    private static void SetLocalizedProperty(DependencyObject target, string name, string text)
    {
        // PRI keeps the XAML namespace qualifier on attached properties.
        if (name.StartsWith('[') && name.IndexOf(']') is int qualifierEnd && qualifierEnd >= 0)
        {
            name = name[(qualifierEnd + 1)..];
        }

        switch (name)
        {
            case "AutomationProperties.Name":
                AutomationProperties.SetName(target, text);
                return;
            case "AutomationProperties.HelpText":
                AutomationProperties.SetHelpText(target, text);
                return;
            case "ToolTipService.ToolTip":
                ToolTipService.SetToolTip(target, text);
                return;
        }

        var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property?.CanWrite != true)
        {
            return;
        }

        object value = property.PropertyType == typeof(string) || property.PropertyType == typeof(object)
            ? text : XamlBindingHelper.ConvertValue(property.PropertyType, text);
        if (!Equals(property.GetValue(target), value))
        {
            property.SetValue(target, value);
        }
    }
}
