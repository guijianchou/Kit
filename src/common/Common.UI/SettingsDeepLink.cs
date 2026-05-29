// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using ManagedCommon;

namespace Common.UI
{
    public static class SettingsDeepLink
    {
        public enum SettingsWindow
        {
            Dashboard = 0,
            Overview,
            Awake,
            LightSwitch,
            Monitor,
            PowerDisplay,
        }

        private static string SettingsWindowNameToString(SettingsWindow value)
        {
            switch (value)
            {
                case SettingsWindow.Dashboard:
                    return "Dashboard";
                case SettingsWindow.Overview:
                    return "Overview";
                case SettingsWindow.Awake:
                    return "Awake";
                case SettingsWindow.LightSwitch:
                    return "LightSwitch";
                case SettingsWindow.Monitor:
                    return "Monitor";
                case SettingsWindow.PowerDisplay:
                    return "PowerDisplay";
                default:
                    {
                        return string.Empty;
                    }
            }
        }

        // What about debug build? Should also consider debug build, maybe tray window message?
        public static void OpenSettings(SettingsWindow window)
        {
            try
            {
                var installPath = PowerToysPathResolver.GetKitInstallPath();
                if (string.IsNullOrEmpty(installPath))
                {
                    Logger.LogError("Failed to find Kit install path");
                    return;
                }

                var exePath = Path.Combine(installPath, "Kit.exe");
                if (!File.Exists(exePath))
                {
                    Logger.LogError($"Failed to find Kit.exe path, {exePath}");
                    return;
                }

                var args = "--open-settings=" + SettingsWindowNameToString(window);

                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex.Message);
            }
        }
    }
}
