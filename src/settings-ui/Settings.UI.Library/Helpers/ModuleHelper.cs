// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using ManagedCommon;

namespace Microsoft.PowerToys.Settings.UI.Library.Helpers
{
    public static class ModuleHelper
    {
        public static string GetModuleLabelResourceName(ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => $"{nameof(ModuleType.Awake)}/ModuleTitle",
                ModuleType.LightSwitch => $"{nameof(ModuleType.LightSwitch)}/ModuleTitle",
                ModuleType.Monitor => $"{nameof(ModuleType.Monitor)}/ModuleTitle",
                ModuleType.GeneralSettings => "QuickAccessTitle/Title",
                _ => string.Empty,
            };
        }

        public static string GetModuleTypeFluentIconName(ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => "ms-appx:///Assets/Settings/Icons/Awake.png",
                ModuleType.LightSwitch => "ms-appx:///Assets/Settings/Icons/LightSwitch.png",
                ModuleType.Monitor => "ms-appx:///Assets/Settings/Icons/Monitor.png",
                ModuleType.GeneralSettings => "ms-appx:///Assets/Settings/Icons/PowerToys.png",
                _ => string.Empty,
            };
        }

        public static bool GetIsModuleEnabled(GeneralSettings generalSettingsConfig, ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => generalSettingsConfig.Enabled.Awake,
                ModuleType.LightSwitch => generalSettingsConfig.Enabled.LightSwitch,
                ModuleType.Monitor => generalSettingsConfig.Enabled.Monitor,
                ModuleType.GeneralSettings => generalSettingsConfig.EnableQuickAccess,
                _ => false,
            };
        }

        public static void SetIsModuleEnabled(GeneralSettings generalSettingsConfig, ModuleType moduleType, bool isEnabled)
        {
            switch (moduleType)
            {
                case ModuleType.Awake: generalSettingsConfig.Enabled.Awake = isEnabled; break;
                case ModuleType.LightSwitch: generalSettingsConfig.Enabled.LightSwitch = isEnabled; break;
                case ModuleType.Monitor: generalSettingsConfig.Enabled.Monitor = isEnabled; break;
                case ModuleType.GeneralSettings: generalSettingsConfig.EnableQuickAccess = isEnabled; break;
            }
        }

        /// <summary>
        /// Gets the module key name used in IPC messages and settings JSON.
        /// These names match the JsonPropertyName attributes in EnabledModules class.
        /// </summary>
        public static string GetModuleKey(ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => AwakeSettings.ModuleName,
                ModuleType.LightSwitch => LightSwitchSettings.ModuleName,
                ModuleType.Monitor => MonitorSettings.ModuleName,
                ModuleType.GeneralSettings => nameof(ModuleType.GeneralSettings),
                _ => string.Empty,
            };
        }
    }
}
