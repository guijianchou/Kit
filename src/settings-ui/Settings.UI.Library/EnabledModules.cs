// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.PowerToys.Settings.UI.Library
{
    [JsonConverter(typeof(EnabledModulesJsonConverter))]
    public class EnabledModules
    {
        private Action notifyEnabledChangedAction;

        // Default values for enabled modules should match their expected "enabled by default" values.
        // Otherwise, a run of DSC on clean settings will not match the expected default result.
        public EnabledModules()
        {
        }

        private bool fancyZones = true;

        [JsonPropertyName("FancyZones")]
        public bool FancyZones
        {
            get => fancyZones;
            set
            {
                if (fancyZones != value)
                {
                    fancyZones = value;
                    NotifyChange();
                }
            }
        }

        private bool imageResizer = true;

        [JsonPropertyName("Image Resizer")]
        public bool ImageResizer
        {
            get => imageResizer;
            set
            {
                if (imageResizer != value)
                {
                    imageResizer = value;
                }
            }
        }

        private bool fileExplorerPreview = true;

        [JsonPropertyName("File Explorer Preview")]
        public bool PowerPreview
        {
            get => fileExplorerPreview;
            set
            {
                if (fileExplorerPreview != value)
                {
                    fileExplorerPreview = value;
                }
            }
        }

        private bool shortcutGuide = true;

        [JsonPropertyName("Shortcut Guide")]
        public bool ShortcutGuide
        {
            get => shortcutGuide;
            set
            {
                if (shortcutGuide != value)
                {
                    shortcutGuide = value;
                    NotifyChange();
                }
            }
        }

        private bool powerRename = true;

        public bool PowerRename
        {
            get => powerRename;
            set
            {
                if (powerRename != value)
                {
                    powerRename = value;
                }
            }
        }

        private bool keyboardManager; // defaulting to off

        [JsonPropertyName("Keyboard Manager")]
        public bool KeyboardManager
        {
            get => keyboardManager;
            set
            {
                if (keyboardManager != value)
                {
                    keyboardManager = value;
                }
            }
        }

        private bool powerLauncher; // defaulting to off

        [JsonPropertyName("PowerToys Run")]
        public bool PowerLauncher
        {
            get => powerLauncher;
            set
            {
                if (powerLauncher != value)
                {
                    powerLauncher = value;
                    NotifyChange();
                }
            }
        }

        private bool colorPicker = true;

        [JsonPropertyName("ColorPicker")]
        public bool ColorPicker
        {
            get => colorPicker;
            set
            {
                if (colorPicker != value)
                {
                    colorPicker = value;
                    NotifyChange();
                }
            }
        }

        private bool cropAndLock; // defaulting to off

        [JsonPropertyName("CropAndLock")]
        public bool CropAndLock
        {
            get => cropAndLock;
            set
            {
                if (cropAndLock != value)
                {
                    cropAndLock = value;
                    NotifyChange();
                }
            }
        }

        private bool awake = true;

        [JsonPropertyName("Awake")]
        public bool Awake
        {
            get => awake;
            set
            {
                if (awake != value)
                {
                    awake = value;
                    NotifyChange();
                }
            }
        }

        private bool mouseWithoutBorders; // defaulting to off

        [JsonPropertyName("MouseWithoutBorders")]
        public bool MouseWithoutBorders
        {
            get => mouseWithoutBorders;
            set
            {
                if (mouseWithoutBorders != value)
                {
                    mouseWithoutBorders = value;
                }
            }
        }

        private bool findMyMouse = true;

        [JsonPropertyName("FindMyMouse")]
        public bool FindMyMouse
        {
            get => findMyMouse;
            set
            {
                if (findMyMouse != value)
                {
                    findMyMouse = value;
                }
            }
        }

        private bool mouseHighlighter = true;

        [JsonPropertyName("MouseHighlighter")]
        public bool MouseHighlighter
        {
            get => mouseHighlighter;
            set
            {
                if (mouseHighlighter != value)
                {
                    mouseHighlighter = value;
                }
            }
        }

        private bool mouseJump; // defaulting to off

        [JsonPropertyName("MouseJump")]
        public bool MouseJump
        {
            get => mouseJump;
            set
            {
                if (mouseJump != value)
                {
                    mouseJump = value;
                }
            }
        }

        private bool alwaysOnTop = true;

        [JsonPropertyName("AlwaysOnTop")]
        public bool AlwaysOnTop
        {
            get => alwaysOnTop;
            set
            {
                if (alwaysOnTop != value)
                {
                    alwaysOnTop = value;
                }
            }
        }

        private bool mousePointerCrosshairs; // defaulting to off

        [JsonPropertyName("MousePointerCrosshairs")]
        public bool MousePointerCrosshairs
        {
            get => mousePointerCrosshairs;
            set
            {
                if (mousePointerCrosshairs != value)
                {
                    mousePointerCrosshairs = value;
                }
            }
        }

        private bool powerAccent; // defaulting to off

        [JsonPropertyName("QuickAccent")]
        public bool PowerAccent
        {
            get => powerAccent;
            set
            {
                if (powerAccent != value)
                {
                    powerAccent = value;
                }
            }
        }

        private bool powerOCR; // defaulting to off

        [JsonPropertyName("TextExtractor")]
        public bool PowerOcr
        {
            get => powerOCR;
            set
            {
                if (powerOCR != value)
                {
                    powerOCR = value;
                    NotifyChange();
                }
            }
        }

        private bool advancedPaste; // defaulting to off

        [JsonPropertyName("AdvancedPaste")]
        public bool AdvancedPaste
        {
            get => advancedPaste;
            set
            {
                if (advancedPaste != value)
                {
                    advancedPaste = value;
                    NotifyChange();
                }
            }
        }

        private bool measureTool = true;

        [JsonPropertyName("Measure Tool")]
        public bool MeasureTool
        {
            get => measureTool;
            set
            {
                if (measureTool != value)
                {
                    measureTool = value;
                    NotifyChange();
                }
            }
        }

        private bool hosts; // defaulting to off

        [JsonPropertyName("Hosts")]
        public bool Hosts
        {
            get => hosts;
            set
            {
                if (hosts != value)
                {
                    hosts = value;
                    NotifyChange();
                }
            }
        }

        private bool fileLocksmith = true;

        [JsonPropertyName("File Locksmith")]
        public bool FileLocksmith
        {
            get => fileLocksmith;
            set
            {
                if (fileLocksmith != value)
                {
                    fileLocksmith = value;
                }
            }
        }

        private bool peek = true;

        [JsonPropertyName("Peek")]
        public bool Peek
        {
            get => peek;
            set
            {
                if (peek != value)
                {
                    peek = value;
                }
            }
        }

        private bool registryPreview; // defaulting to off

        [JsonPropertyName("RegistryPreview")]
        public bool RegistryPreview
        {
            get => registryPreview;
            set
            {
                if (registryPreview != value)
                {
                    registryPreview = value;
                }
            }
        }

        private bool cmdNotFound = true;

        [JsonPropertyName("CmdNotFound")]
        public bool CmdNotFound
        {
            get => cmdNotFound;
            set
            {
                if (cmdNotFound != value)
                {
                    cmdNotFound = value;
                    NotifyChange();
                }
            }
        }

        private bool environmentVariables; // defaulting to off

        [JsonPropertyName("EnvironmentVariables")]
        public bool EnvironmentVariables
        {
            get => environmentVariables;
            set
            {
                if (environmentVariables != value)
                {
                    environmentVariables = value;
                }
            }
        }

        private bool newPlus;

        [JsonPropertyName("NewPlus")] // This key must match newplus::constants::non_localizable
        public bool NewPlus
        {
            get => newPlus;
            set
            {
                if (newPlus != value)
                {
                    newPlus = value;
                }
            }
        }

        private bool workspaces; // defaulting to off

        [JsonPropertyName("Workspaces")]
        public bool Workspaces
        {
            get => workspaces;
            set
            {
                if (workspaces != value)
                {
                    workspaces = value;
                    NotifyChange();
                }
            }
        }

        private bool cmdPal = true;

        [JsonPropertyName("CmdPal")]
        public bool CmdPal
        {
            get => cmdPal;
            set
            {
                if (cmdPal != value)
                {
                    cmdPal = value;
                }
            }
        }

        private bool zoomIt;

        [JsonPropertyName("ZoomIt")]
        public bool ZoomIt
        {
            get => zoomIt;
            set
            {
                if (zoomIt != value)
                {
                    zoomIt = value;
                    NotifyChange();
                }
            }
        }

        private bool cursorWrap; // defaulting to off

        [JsonPropertyName("CursorWrap")]
        public bool CursorWrap
        {
            get => cursorWrap;
            set
            {
                if (cursorWrap != value)
                {
                    cursorWrap = value;
                }
            }
        }

        private bool lightSwitch = true;

        [JsonPropertyName("LightSwitch")]
        public bool LightSwitch
        {
            get => lightSwitch;
            set
            {
                if (lightSwitch != value)
                {
                    lightSwitch = value;
                    NotifyChange();
                }
            }
        }

        private bool monitor;

        [JsonPropertyName("Monitor")]
        public bool Monitor
        {
            get => monitor;
            set
            {
                if (monitor != value)
                {
                    monitor = value;
                    NotifyChange();
                }
            }
        }

        private bool grabAndMove;

        [JsonPropertyName("GrabAndMove")]
        public bool GrabAndMove
        {
            get => grabAndMove;
            set
            {
                if (grabAndMove != value)
                {
                    grabAndMove = value;
                    NotifyChange();
                }
            }
        }

        private void NotifyChange()
        {
            notifyEnabledChangedAction?.Invoke();
        }

        public string ToJsonString()
        {
            return JsonSerializer.Serialize(this);
        }

        internal void AddEnabledModuleChangeNotification(Action callBack)
        {
            notifyEnabledChangedAction = callBack;
        }
    }
}
