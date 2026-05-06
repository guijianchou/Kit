// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using global::PowerToys.GPOWrapper;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Views;
using Windows.UI;

namespace Microsoft.PowerToys.Settings.UI.Helpers
{
    internal sealed class ModuleGpoHelper
    {
        public static GpoRuleConfigured GetModuleGpoConfiguration(ModuleType moduleType)
        {
            switch (moduleType)
            {
                case ModuleType.AdvancedPaste: return GPOWrapper.GetConfiguredAdvancedPasteEnabledValue();
                case ModuleType.AlwaysOnTop: return GPOWrapper.GetConfiguredAlwaysOnTopEnabledValue();
                case ModuleType.Awake: return GPOWrapper.GetConfiguredAwakeEnabledValue();
                case ModuleType.CmdPal: return GPOWrapper.GetConfiguredCmdPalEnabledValue();
                case ModuleType.ColorPicker: return GPOWrapper.GetConfiguredColorPickerEnabledValue();
                case ModuleType.CropAndLock: return GPOWrapper.GetConfiguredCropAndLockEnabledValue();
                case ModuleType.CursorWrap: return GPOWrapper.GetConfiguredCursorWrapEnabledValue();
                case ModuleType.EnvironmentVariables: return GPOWrapper.GetConfiguredEnvironmentVariablesEnabledValue();
                case ModuleType.FancyZones: return GPOWrapper.GetConfiguredFancyZonesEnabledValue();
                case ModuleType.FileLocksmith: return GPOWrapper.GetConfiguredFileLocksmithEnabledValue();
                case ModuleType.FindMyMouse: return GPOWrapper.GetConfiguredFindMyMouseEnabledValue();
                case ModuleType.Hosts: return GPOWrapper.GetConfiguredHostsFileEditorEnabledValue();
                case ModuleType.ImageResizer: return GPOWrapper.GetConfiguredImageResizerEnabledValue();
                case ModuleType.KeyboardManager: return GPOWrapper.GetConfiguredKeyboardManagerEnabledValue();
                case ModuleType.LightSwitch: return GPOWrapper.GetConfiguredLightSwitchEnabledValue();
                case ModuleType.MouseHighlighter: return GPOWrapper.GetConfiguredMouseHighlighterEnabledValue();
                case ModuleType.MouseJump: return GPOWrapper.GetConfiguredMouseJumpEnabledValue();
                case ModuleType.MousePointerCrosshairs: return GPOWrapper.GetConfiguredMousePointerCrosshairsEnabledValue();
                case ModuleType.MouseWithoutBorders: return GPOWrapper.GetConfiguredMouseWithoutBordersEnabledValue();
                case ModuleType.NewPlus: return GPOWrapper.GetConfiguredNewPlusEnabledValue();
                case ModuleType.Peek: return GPOWrapper.GetConfiguredPeekEnabledValue();
                case ModuleType.PowerRename: return GPOWrapper.GetConfiguredPowerRenameEnabledValue();
                case ModuleType.PowerLauncher: return GPOWrapper.GetConfiguredPowerLauncherEnabledValue();
                case ModuleType.PowerAccent: return GPOWrapper.GetConfiguredQuickAccentEnabledValue();
                case ModuleType.Workspaces: return GPOWrapper.GetConfiguredWorkspacesEnabledValue();
                case ModuleType.RegistryPreview: return GPOWrapper.GetConfiguredRegistryPreviewEnabledValue();
                case ModuleType.MeasureTool: return GPOWrapper.GetConfiguredScreenRulerEnabledValue();
                case ModuleType.ShortcutGuide: return GPOWrapper.GetConfiguredShortcutGuideEnabledValue();
                case ModuleType.PowerOCR: return GPOWrapper.GetConfiguredTextExtractorEnabledValue();
                case ModuleType.PowerDisplay: return GPOWrapper.GetConfiguredPowerDisplayEnabledValue();
                case ModuleType.ZoomIt: return GPOWrapper.GetConfiguredZoomItEnabledValue();
                case ModuleType.GrabAndMove: return GPOWrapper.GetConfiguredGrabAndMoveEnabledValue();
                default: return GpoRuleConfigured.Unavailable;
            }
        }

        public static System.Type GetModulePageType(ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => typeof(AwakePage),
                ModuleType.LightSwitch => typeof(LightSwitchPage),
                ModuleType.Monitor => typeof(MonitorPage),
                ModuleType.PowerDisplay => typeof(PowerDisplayPage),
                _ => typeof(GeneralPage),
            };
        }
    }
}
