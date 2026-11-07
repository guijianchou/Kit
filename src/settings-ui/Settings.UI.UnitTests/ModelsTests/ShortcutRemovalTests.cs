// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text.Json;
using Kit.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Settings.UI.UnitTests.ModelsTests;

[TestClass]
public class ShortcutRemovalTests
{
    [TestMethod]
    public void GeneralSettingsShouldReadLegacyShortcutsWithoutWritingThemBack()
    {
        const string legacySettings = """
            {
                "startup": true,
                "enable_quick_access": false,
                "theme": "dark",
                "quick_access_shortcut": { "win": true, "ctrl": true, "code": 81 },
                "ignored_conflict_properties": { "ignored_shortcuts": [{ "code": 81 }] },
                "enabled": { "Awake": false, "LightSwitch": true, "NetMap": false }
            }
            """;

        var settings = JsonSerializer.Deserialize(legacySettings, SettingsSerializationContext.Default.GeneralSettings);
        Assert.IsNotNull(settings);
        Assert.IsTrue(settings.Startup);
        Assert.IsFalse(settings.EnableQuickAccess);
        Assert.AreEqual("dark", settings.Theme);
        Assert.IsFalse(settings.Enabled.Awake);
        Assert.IsTrue(settings.Enabled.LightSwitch);
        Assert.IsFalse(settings.Enabled.NetMap);

        // Saving another setting must also stop persisting the retired shortcut keys.
        settings.EnableQuickAccess = true;
        using var saved = JsonDocument.Parse(settings.ToJsonString());
        Assert.IsTrue(saved.RootElement.GetProperty("enable_quick_access").GetBoolean());
        Assert.IsFalse(saved.RootElement.TryGetProperty("quick_access_shortcut", out _));
        Assert.IsFalse(saved.RootElement.TryGetProperty("ignored_conflict_properties", out _));
        Assert.AreEqual("dark", saved.RootElement.GetProperty("theme").GetString());
    }

    [TestMethod]
    public void LightSwitchShouldReadLegacyShortcutAlongsideCurrentSchedule()
    {
        const string legacySettings = """
            {
                "name": "LightSwitch",
                "version": "1.0",
                "properties": {
                    "toggle-theme-hotkey": { "value": { "win": true, "code": 68 } },
                    "changeSystem": { "value": false },
                    "lightTime": { "value": 420 },
                    "scheduleMode": { "value": "FixedHours" }
                }
            }
            """;

        var settings = JsonSerializer.Deserialize(legacySettings, SettingsSerializationContext.Default.LightSwitchSettings);
        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.Properties.ChangeSystem.Value);
        Assert.AreEqual(420, settings.Properties.LightTime.Value);
        Assert.AreEqual("FixedHours", settings.Properties.ScheduleMode.Value);

        var saved = JsonSerializer.Serialize(settings, SettingsSerializationContext.Default.LightSwitchSettings);
        Assert.IsFalse(saved.Contains("hotkey", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(saved.Contains("shortcut", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ActiveModulesShouldSerializeWithoutShortcutSettings()
    {
        BasePTModuleSettings[] settings =
        {
            new AwakeSettings(),
            new LightSwitchSettings(),
            new LocalserverSettings(),
            new UDPtestSettings(),
            new NetMapSettings(),
            new AIHubSettings(),
        };

        foreach (var module in settings)
        {
            var serialized = JsonSerializer.Serialize(module, module.GetType(), SettingsSerializationContext.Default);
            Assert.IsFalse(serialized.Contains("hotkey", StringComparison.OrdinalIgnoreCase), module.Name);
            Assert.IsFalse(serialized.Contains("shortcut", StringComparison.OrdinalIgnoreCase), module.Name);
        }
    }
}
