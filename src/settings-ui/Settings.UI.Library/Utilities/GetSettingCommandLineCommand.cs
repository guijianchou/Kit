// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using Kit.Settings.UI.Library.Attributes;
using Kit.Settings.UI.Library.Interfaces;

namespace Kit.Settings.UI.Library;

/// <summary>
/// Queries Kit settings from the command line using a JSON file listing modules and properties.
/// Example: Kit.Settings.exe get %TEMP%\properties.json
/// `properties.json` file contents:
/// {
///   "Awake": ["KeepDisplayOn", "IntervalMinutes"]
/// }
///
/// The command replaces `properties.json` with the current values, for example:
/// {
///   "Awake": {
///     "Enabled": true,
///     "KeepDisplayOn": "False",
///     "IntervalMinutes": "1"
///   }
/// }
/// </summary>
public sealed class GetSettingCommandLineCommand
{
    private static JsonSerializerOptions _serializerOptions = new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Execute(Dictionary<string, List<string>> settingNamesForModules)
    {
        var modulesSettings = new Dictionary<string, Dictionary<string, object>>();

        var settingsAssembly = CommandLineUtils.GetSettingsAssembly();
        var settingsUtils = SettingsUtils.Default;

        var enabledModules = SettingsRepository<GeneralSettings>.GetInstance(settingsUtils).SettingsConfig.Enabled;

        foreach (var (moduleName, settings) in settingNamesForModules)
        {
            CommandLineUtils.EnsureActiveSettingsModule(moduleName);

            var moduleSettings = new Dictionary<string, object>();
            if (!CommandLineUtils.IsGeneralSettingsModule(moduleName))
            {
                CommandLineUtils.EnsureActiveEnabledModule(moduleName);
                moduleSettings.Add("Enabled", typeof(EnabledModules).GetProperty(moduleName).GetValue(enabledModules));
            }

            var settingsConfig = CommandLineUtils.GetSettingsConfigFor(moduleName, settingsUtils, settingsAssembly);
            foreach (var settingName in settings)
            {
                var value = CommandLineUtils.GetPropertyValue(settingName, settingsConfig);
                if (value != null)
                {
                    var cmdReprValue = ICmdLineRepresentable.ToCmdRepr(value.GetType(), value);
                    moduleSettings.Add(settingName, cmdReprValue);
                }
            }

            modulesSettings.Add(moduleName, moduleSettings);
        }

        return JsonSerializer.Serialize(modulesSettings, _serializerOptions);
    }
}
