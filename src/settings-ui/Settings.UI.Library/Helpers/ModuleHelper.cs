// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Kit.AiHub.Engine;
using Kit.AiHub.Storage;
using Kit.Settings.UI.Library;
using ManagedCommon;

namespace Kit.Settings.UI.Library.Helpers
{
    public static class ModuleHelper
    {
        public static string GetModuleLabelResourceName(ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => $"{nameof(ModuleType.Awake)}/ModuleTitle",
                ModuleType.LightSwitch => $"{nameof(ModuleType.LightSwitch)}/ModuleTitle",
                ModuleType.Localserver => $"{nameof(ModuleType.Localserver)}/ModuleTitle",
                ModuleType.AiHub => $"{nameof(ModuleType.AiHub)}/ModuleTitle",
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
                ModuleType.Localserver => "ms-appx:///Assets/Settings/Icons/Localserver.png",
                ModuleType.AiHub => "ms-appx:///Assets/Settings/Icons/AiHub.png",
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
                ModuleType.Localserver => generalSettingsConfig.Enabled.Localserver,
                ModuleType.AiHub => GetAiHubEnabled(),
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
                case ModuleType.Localserver: generalSettingsConfig.Enabled.Localserver = isEnabled; break;
                case ModuleType.AiHub:
                    new AiHubSettingsStore().Update(config => config.IsEnabled = isEnabled);
                    AiHubEngine.RaiseStateChanged();
                    break;
                case ModuleType.GeneralSettings: generalSettingsConfig.EnableQuickAccess = isEnabled; break;
            }
        }

        private static bool GetAiHubEnabled()
        {
            try
            {
                return new AiHubSettingsStore().Load().IsEnabled;
            }
            catch (System.Exception)
            {
                return false;
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
                ModuleType.Localserver => LocalserverSettings.ModuleName,
                ModuleType.AiHub => "AiHub",
                ModuleType.GeneralSettings => nameof(ModuleType.GeneralSettings),
                _ => string.Empty,
            };
        }
    }
}
