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
    public void LightSwitchShouldIgnoreLegacyShortcutAndPreserveCurrentSchedule()
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

        settings.Properties.DarkTime.Value = 1260;
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(settings, SettingsSerializationContext.Default.LightSwitchSettings));
        var properties = saved.RootElement.GetProperty("properties");
        Assert.IsFalse(properties.TryGetProperty("toggle-theme-hotkey", out _));
        Assert.IsFalse(properties.GetProperty("changeSystem").GetProperty("value").GetBoolean());
        Assert.AreEqual(420, properties.GetProperty("lightTime").GetProperty("value").GetInt32());
        Assert.AreEqual(1260, properties.GetProperty("darkTime").GetProperty("value").GetInt32());
        Assert.AreEqual("FixedHours", properties.GetProperty("scheduleMode").GetProperty("value").GetString());

        var reloaded = JsonSerializer.Deserialize(saved.RootElement, SettingsSerializationContext.Default.LightSwitchSettings);
        Assert.IsNotNull(reloaded);
        Assert.AreEqual(settings.ToJsonString(), reloaded.ToJsonString());
    }

    [TestMethod]
    public void ActiveModulesShouldNotSerializeShortcuts()
    {
        BasePTModuleSettings[] settings =
        {
            new AwakeSettings(),
            new LightSwitchSettings(),
            new LocalserverSettings(),
            new UDPtestSettings(),
            new NetMapSettings(),
            new AIHubSettings(),
            new WingetSettings(),
        };

        foreach (var module in settings)
        {
            var serialized = JsonSerializer.Serialize(module, module.GetType(), SettingsSerializationContext.Default);
            Assert.IsFalse(serialized.Contains("hotkey", StringComparison.OrdinalIgnoreCase), module.Name);
            Assert.IsFalse(serialized.Contains("shortcut", StringComparison.OrdinalIgnoreCase), module.Name);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{\"value\":{\"win\":false,\"ctrl\":false,\"alt\":false,\"shift\":false,\"code\":0}}")]
    [DataRow("\"retired legacy value\"")]
    public void LightSwitchShouldNotReintroduceShortcutsOnRoundtrip(string legacyHotkey)
    {
        string retiredProperty = legacyHotkey == null ? string.Empty : ",\"toggle-theme-hotkey\":" + legacyHotkey;
        string input = "{\"name\":\"LightSwitch\",\"properties\":{\"lightTime\":{\"value\":420}" + retiredProperty + "}}";
        var settings = JsonSerializer.Deserialize(input, SettingsSerializationContext.Default.LightSwitchSettings);
        Assert.IsNotNull(settings);
        using var saved = JsonDocument.Parse(settings.ToJsonString());
        Assert.IsFalse(saved.RootElement.GetProperty("properties").TryGetProperty("toggle-theme-hotkey", out _));
        var reloaded = JsonSerializer.Deserialize(saved.RootElement, SettingsSerializationContext.Default.LightSwitchSettings);
        Assert.IsNotNull(reloaded);
        Assert.AreEqual(420, reloaded.Properties.LightTime.Value);
        Assert.AreEqual(settings.ToJsonString(), reloaded.ToJsonString());
    }
}
