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
                ModuleType.PowerDisplay => $"{nameof(ModuleType.PowerDisplay)}/ModuleTitle",
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
                ModuleType.Monitor => "ms-appx:///Assets/Settings/Icons/PowerToys.png",
                ModuleType.PowerDisplay => "ms-appx:///Assets/Settings/Icons/PowerDisplay.png",
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
                ModuleType.PowerDisplay => generalSettingsConfig.Enabled.PowerDisplay,
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
                case ModuleType.PowerDisplay: generalSettingsConfig.Enabled.PowerDisplay = isEnabled; break;
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
                ModuleType.AdvancedPaste => AdvancedPasteSettings.ModuleName,
                ModuleType.AlwaysOnTop => AlwaysOnTopSettings.ModuleName,
                ModuleType.Awake => AwakeSettings.ModuleName,
                ModuleType.CmdPal => "CmdPal",
                ModuleType.ColorPicker => ColorPickerSettings.ModuleName,
                ModuleType.CropAndLock => CropAndLockSettings.ModuleName,
                ModuleType.CursorWrap => CursorWrapSettings.ModuleName,
                ModuleType.EnvironmentVariables => EnvironmentVariablesSettings.ModuleName,
                ModuleType.FancyZones => FancyZonesSettings.ModuleName,
                ModuleType.FileLocksmith => FileLocksmithSettings.ModuleName,
                ModuleType.FindMyMouse => FindMyMouseSettings.ModuleName,
                ModuleType.Hosts => HostsSettings.ModuleName,
                ModuleType.ImageResizer => ImageResizerSettings.ModuleName,
                ModuleType.KeyboardManager => KeyboardManagerSettings.ModuleName,
                ModuleType.LightSwitch => LightSwitchSettings.ModuleName,
                ModuleType.Monitor => MonitorSettings.ModuleName,
                ModuleType.MouseHighlighter => MouseHighlighterSettings.ModuleName,
                ModuleType.MouseJump => "MouseJump",
                ModuleType.MousePointerCrosshairs => MousePointerCrosshairsSettings.ModuleName,
                ModuleType.MouseWithoutBorders => MouseWithoutBordersSettings.ModuleName,
                ModuleType.NewPlus => NewPlusSettings.ModuleName,
                ModuleType.Peek => PeekSettings.ModuleName,
                ModuleType.PowerDisplay => PowerDisplaySettings.ModuleName,
                ModuleType.PowerRename => PowerRenameSettings.ModuleName,
                ModuleType.PowerLauncher => PowerLauncherSettings.ModuleName,
                ModuleType.PowerAccent => PowerAccentSettings.ModuleName,
                ModuleType.RegistryPreview => RegistryPreviewSettings.ModuleName,
                ModuleType.MeasureTool => MeasureToolSettings.ModuleName,
                ModuleType.ShortcutGuide => ShortcutGuideSettings.ModuleName,
                ModuleType.PowerOCR => PowerOcrSettings.ModuleName,
                ModuleType.Workspaces => WorkspacesSettings.ModuleName,
                ModuleType.GrabAndMove => GrabAndMoveSettings.ModuleName,
                ModuleType.ZoomIt => ZoomItSettings.ModuleName,
                ModuleType.GeneralSettings => nameof(ModuleType.GeneralSettings),
                _ => string.Empty,
            };
        }
    }
}
