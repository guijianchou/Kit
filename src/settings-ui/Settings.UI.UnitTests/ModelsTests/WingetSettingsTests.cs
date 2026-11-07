// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using ManagedCommon;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
public class WingetSettingsTests
{
    [TestMethod]
    public void EnabledDefaultsOffAndPartialRunnerReplyPreservesSavedState()
    {
        var enabled = new EnabledModules();
        Assert.IsFalse(enabled.Winget);
        enabled.Winget = true;
        enabled.MergeFrom(JsonSerializer.Deserialize("{\"UDPtest\":false}", SettingsSerializationContext.Default.EnabledModules));
        Assert.IsTrue(enabled.Winget);
        enabled.MergeFrom(JsonSerializer.Deserialize("{\"Winget\":false}", SettingsSerializationContext.Default.EnabledModules));
        Assert.IsFalse(enabled.Winget);
        enabled.MergeFrom(JsonSerializer.Deserialize("{\"Winget\":true}", SettingsSerializationContext.Default.EnabledModules));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(enabled, SettingsSerializationContext.Default.EnabledModules));
        Assert.IsTrue(json.RootElement.GetProperty("Winget").GetBoolean());
    }

    [TestMethod]
    public void SettingsDefaultsCloneAndIpcPreserveIndependentPreferences()
    {
        var settings = new WingetSettings();
        Assert.IsTrue(settings.Properties.DailyCheckEnabled.Value);
        Assert.IsTrue(settings.Properties.NotifyUpdates.Value);
        settings.Properties.DailyCheckEnabled.Value = false;
        var clone = (WingetSettings)settings.Clone();
        clone.Properties.NotifyUpdates.Value = false;
        Assert.IsTrue(settings.Properties.NotifyUpdates.Value);
        Assert.IsFalse(clone.Properties.DailyCheckEnabled.Value);

        var restored = JsonSerializer.Deserialize(clone.ToJsonString(), SettingsSerializationContext.Default.WingetSettings);
        Assert.IsFalse(restored.Properties.DailyCheckEnabled.Value);
        Assert.IsFalse(restored.Properties.NotifyUpdates.Value);
        var ipc = new SndModuleSettings<SndWingetSettings>(new SndWingetSettings(restored));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ipc, SettingsSerializationContext.Default.SndModuleSettingsSndWingetSettings));
        var module = document.RootElement.GetProperty("powertoys").GetProperty("Winget");
        Assert.AreEqual("Winget", module.GetProperty("name").GetString());
        Assert.IsFalse(module.GetProperty("properties").GetProperty("daily_check_enabled").GetProperty("value").GetBoolean());
        Assert.IsFalse(module.GetProperty("properties").GetProperty("notify_updates").GetProperty("value").GetBoolean());
    }

    [TestMethod]
    public void CatalogAndHelpersExposeWingetWithItsOwnKeyAndIcon()
    {
        Assert.IsTrue(KitModuleCatalog.IsActiveModule(ModuleType.Winget));
        CollectionAssert.Contains(KitModuleCatalog.ActiveSettingsModuleKeys.ToArray(), WingetSettings.ModuleName);
        CollectionAssert.Contains(KitModuleCatalog.ActiveEnabledModuleKeys.ToArray(), WingetSettings.ModuleName);
        var general = new GeneralSettings();
        ModuleHelper.SetIsModuleEnabled(general, ModuleType.Winget, true);
        Assert.IsTrue(general.Enabled.Winget);
        Assert.IsTrue(ModuleHelper.GetIsModuleEnabled(general, ModuleType.Winget));
        Assert.AreEqual("Winget", ModuleHelper.GetModuleKey(ModuleType.Winget));
        Assert.AreEqual("Winget/ModuleTitle", ModuleHelper.GetModuleLabelResourceName(ModuleType.Winget));
        Assert.AreEqual("ms-appx:///Assets/Settings/Icons/Winget.png", ModuleHelper.GetModuleTypeFluentIconName(ModuleType.Winget));
    }

    [TestMethod]
    public void WingetResourcesHaveMatchingEnglishAndChineseKeysAndFormatArguments()
    {
        var english = ReadResources("en-us");
        var chinese = ReadResources("zh-CN");
        Assert.IsTrue(english.Count > 0, "Winget must expose localized resources.");
        CollectionAssert.AreEquivalent(english.Keys.ToArray(), chinese.Keys.ToArray());
        foreach (var item in english)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.Value), $"English resource {item.Key} is empty.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(chinese[item.Key]), $"Chinese resource {item.Key} is empty.");
            CollectionAssert.AreEquivalent(FormatArguments(item.Value), FormatArguments(chinese[item.Key]), $"Resource format arguments differ: {item.Key}");
        }

        var page = XDocument.Load(FindSource("SettingsXAML", "Views", "WingetPage.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string uid in page.Descendants().Select(element => (string)element.Attribute(xaml + "Uid")).Where(uid => uid?.StartsWith("Winget", StringComparison.Ordinal) == true))
        {
            Assert.IsTrue(english.Keys.Any(key => key.StartsWith(uid + ".", StringComparison.Ordinal)), $"XAML resource UID has no localized values: {uid}");
        }
    }

    private static System.Collections.Generic.Dictionary<string, string> ReadResources(string language) =>
        XDocument.Load(FindSource("Strings", language, "Resources.resw")).Root.Elements("data")
            .Where(item => ((string)item.Attribute("name")).StartsWith("Winget", StringComparison.Ordinal) || ((string)item.Attribute("name")).StartsWith("Shell_Winget", StringComparison.Ordinal))
            .ToDictionary(item => (string)item.Attribute("name"), item => (string)item.Element("value"), StringComparer.Ordinal);

    private static string[] FormatArguments(string text) => Regex.Matches(text, @"\{(\d+)(?:[,:][^}]*)?\}")
        .Select(match => match.Groups[1].Value).ToArray();

    private static string FindSource(params string[] relativeParts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(new[] { directory.FullName, "src", "settings-ui", "Settings.UI" }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Winget resource source was not found.", Path.Combine(relativeParts));
    }
}
