// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

using Microsoft.PowerToys.Settings.UI.Library;

namespace Microsoft.PowerToys.Settings.UI.Services
{
    public static class MonitorSettingsStoragePaths
    {
        private const string MonitorProgressFileName = "scan-progress.json";
        private const string MonitorStatusDatabaseFileName = "monitor-status.db";

        public static string ResolveProgressPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kit",
                MonitorSettings.ModuleName,
                MonitorProgressFileName);
        }

        public static string ResolveStatusDatabasePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kit",
                MonitorSettings.ModuleName,
                MonitorStatusDatabaseFileName);
        }
    }
}
