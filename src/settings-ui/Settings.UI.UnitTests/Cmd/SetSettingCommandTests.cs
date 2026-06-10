// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;

using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static Microsoft.PowerToys.Settings.UI.Library.SetSettingCommandLineCommand;

namespace Settings.UI.UnitTests.Cmd;

[TestClass]
public class SetSettingCommandTests
{
    private SettingsUtils settingsUtils;

    [TestInitialize]
    public void Setup()
    {
        settingsUtils = new SettingsUtils(new MockFileSystem());
    }

    private void SetSetting(Type moduleSettingsType, string settingName, string newValueStr)
    {
        var settings = CommandLineUtils.GetSettingsConfigFor(moduleSettingsType, settingsUtils);
        var defaultValue = CommandLineUtils.GetPropertyValue(settingName, settings);
        var qualifiedName = moduleSettingsType.Name.Replace("Settings", string.Empty) + "." + settingName;
        var type = CommandLineUtils.GetSettingPropertyInfo(settingName, settings).PropertyType;
        var newValue = ICmdLineRepresentable.ParseFor(type, newValueStr);

        Execute(qualifiedName, newValueStr, settingsUtils);

        Assert.AreNotEqual(defaultValue, newValue);
        Assert.AreEqual(newValue, CommandLineUtils.GetPropertyValue(settingName, settings));
    }

    // Each setting has a different type.
    [TestMethod]
    [DataRow(typeof(AwakeSettings), nameof(AwakeProperties.Mode), "EXPIRABLE")]
    [DataRow(typeof(AwakeSettings), nameof(AwakeProperties.ExpirationDateTime), "March 31, 2020 15:00 +00:00")]
    [DataRow(typeof(LightSwitchSettings), nameof(LightSwitchProperties.LightTime), "600")]
    [DataRow(typeof(LightSwitchSettings), nameof(LightSwitchProperties.EnableDarkModeProfile), "true")]
    [DataRow(typeof(MonitorSettings), nameof(MonitorProperties.ScanIntervalSeconds), "3600")]
    [DataRow(typeof(PowerDisplaySettings), nameof(PowerDisplayProperties.MonitorRefreshDelay), "10")]
    public void SetModuleSetting(Type moduleSettingsType, string settingName, string newValueStr)
    {
        SetSetting(moduleSettingsType, settingName, newValueStr);
    }

    [DataRow(typeof(GeneralSettings), "Enabled.Monitor", "true")]
    [DataRow(typeof(GeneralSettings), nameof(GeneralSettings.AutoDownloadUpdates), "true")]
    [TestMethod]
    public void SetGeneralSetting(Type moduleSettingsType, string settingName, string newValueStr)
    {
        SetSetting(moduleSettingsType, settingName, newValueStr);
    }

    [TestMethod]
    [DataRow("FancyZones.FancyzonesBorderColor", "#00FF00")]
    [DataRow("PowerLauncher.MaximumNumberOfResults", "322")]
    [DataRow("GeneralSettings.Enabled.MouseWithoutBorders", "true")]
    public void SetSettingShouldRejectInactiveModuleSurface(string settingName, string settingValue)
    {
        Assert.ThrowsException<ArgumentException>(() => Execute(settingName, settingValue, settingsUtils));
    }

    [TestMethod]
    public void GetSettingShouldAcceptGeneralAliasAndActiveModuleSurface()
    {
        var requestedSettings = new Dictionary<string, List<string>>
        {
            ["General"] = [nameof(GeneralSettings.AutoDownloadUpdates)],
            [MonitorSettings.ModuleName] = [nameof(MonitorProperties.ScanIntervalSeconds)],
        };

        var result = GetSettingCommandLineCommand.Execute(requestedSettings);

        StringAssert.Contains(result, "\"General\"");
        StringAssert.Contains(result, $"\"{MonitorSettings.ModuleName}\"");
        StringAssert.Contains(result, nameof(GeneralSettings.AutoDownloadUpdates));
        StringAssert.Contains(result, nameof(MonitorProperties.ScanIntervalSeconds));
    }

    [TestMethod]
    [DataRow("FancyZones", nameof(FZConfigProperties.FancyzonesBorderColor))]
    [DataRow("MouseWithoutBorders", "UseService")]
    [DataRow("BogusModule", "Setting")]
    public void GetSettingShouldRejectInactiveModuleSurface(string moduleName, string settingName)
    {
        var requestedSettings = new Dictionary<string, List<string>>
        {
            [moduleName] = [settingName],
        };

        Assert.ThrowsException<ArgumentException>(() => GetSettingCommandLineCommand.Execute(requestedSettings));
    }
}
