// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.IO;
using ManagedCommon;

namespace PowerDisplay.Helpers
{
    public static class SettingsDeepLink
    {
        public static void OpenSettings(bool mainExecutableIsOnTheParentFolder)
        {
            try
            {
                var installPath = PowerToysPathResolver.GetPowerToysInstallPath();
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

                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--open-settings=PowerDisplay",
                    UseShellExecute = false,
                });
            }
            catch (System.Exception ex)
            {
                Logger.LogError(ex.Message);
            }
        }
    }
}
