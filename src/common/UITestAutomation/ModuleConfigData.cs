// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UITestBase")]
[assembly: InternalsVisibleTo("Session")]

namespace Microsoft.PowerToys.UITest
{
    /// <summary>
    /// This file manages the configuration of Kit modules for UI tests.
    /// </summary>
    /// <remarks>
    /// How to add a new module:
    /// 1. Define the new module in the PowerToysModule enum.
    /// 2. Add the exe window name to the ModuleWindowName dictionary in the ModuleConfigData constructor.
    /// 3. Add the exe path to the ModulePath dictionary in the ModuleConfigData constructor.
    /// </remarks>

    /// <summary>
    /// Represents the active Kit launch targets used by UI tests.
    /// </summary>
    public enum PowerToysModule
    {
        PowerToysSettings,
        Runner,
        Awake,
        LightSwitch,
        Monitor,
    }

    /// <summary>
    /// Represents the window size for the UI test.
    /// </summary>
    public enum WindowSize
    {
        /// <summary>
        /// Unspecified window size, won't make any size change
        /// </summary>
        UnSpecified,

        /// <summary>
        /// Small window size, 640 * 480
        /// </summary>
        Small,

        /// <summary>
        /// Small window size, 480 * 640
        /// </summary>
        Small_Vertical,

        /// <summary>
        /// Medium window size, 1024 * 768
        /// </summary>
        Medium,

        /// <summary>
        /// Medium window size, 768 * 1024
        /// </summary>
        Medium_Vertical,

        /// <summary>
        /// Large window size, 1920 * 1080
        /// </summary>
        Large,

        /// <summary>
        /// Large window size, 1080 * 1920
        /// </summary>
        Large_Vertical,
    }

    internal class ModuleConfigData
    {
        private Dictionary<PowerToysModule, ModuleInfo> ModuleInfo { get; }

        // Singleton instance of ModuleConfigData.
        private static readonly Lazy<ModuleConfigData> SingletonInstance = new Lazy<ModuleConfigData>(() => new ModuleConfigData());

        public static ModuleConfigData Instance => SingletonInstance.Value;

        public const string WindowsApplicationDriverUrl = "http://127.0.0.1:4723";

        private bool UseInstallerForTest { get; }

        private ModuleConfigData()
        {
            // Check if we should use installer paths from environment variable
            UseInstallerForTest = EnvironmentConfig.UseInstallerForTest;

            // Module information including executable name, window name, and optional subdirectory
            ModuleInfo = new Dictionary<PowerToysModule, ModuleInfo>
            {
                [PowerToysModule.PowerToysSettings] = new ModuleInfo("PowerToys.Settings.exe", "Kit", "WinUI3Apps"),
                [PowerToysModule.Runner] = new ModuleInfo("Kit.exe", "Kit"),
                [PowerToysModule.Awake] = new ModuleInfo("PowerToys.Awake.exe", "PowerToys Awake"),
                [PowerToysModule.LightSwitch] = new ModuleInfo("PowerToys.LightSwitchService.exe", "PowerToys.LightSwitchService", "LightSwitchService"),
                [PowerToysModule.Monitor] = new ModuleInfo("PowerToys.Monitor.exe", "Kit Monitor"),
            };
        }

        private string GetKitInstallPath()
        {
            // Try common installation paths
            string[] possiblePaths =
            {
                @"C:\Program Files\Kit",
                @"C:\Program Files (x86)\Kit",
                Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Kit"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Kit"),
            };

            foreach (string path in possiblePaths)
            {
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "Kit.exe")))
                {
                    return path;
                }
            }

            // Fallback to Program Files if not found
            return @"C:\Program Files\Kit";
        }

        public string GetModulePath(PowerToysModule scope)
        {
            var moduleInfo = ModuleInfo[scope];

            if (UseInstallerForTest)
            {
                string kitInstallPath = GetKitInstallPath();
                string installedPath = moduleInfo.GetInstalledPath(kitInstallPath);

                if (File.Exists(installedPath))
                {
                    return installedPath;
                }
                else
                {
                    Console.WriteLine($"Warning: Installed module not found at {installedPath}, using development path");
                }
            }

            return moduleInfo.GetDevelopmentPath();
        }

        public string GetWindowsApplicationDriverUrl() => WindowsApplicationDriverUrl;

        public string GetModuleWindowName(PowerToysModule scope) => ModuleInfo[scope].WindowName;
    }
}
