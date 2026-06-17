// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;

using ManagedCommon;

namespace Microsoft.PowerToys.Settings.UI.Library.Helpers
{
    public static class KitModuleCatalog
    {
        public static IReadOnlyList<ModuleType> ActiveModules { get; } =
            new[]
            {
                ModuleType.Awake,
                ModuleType.LightSwitch,
                ModuleType.Monitor,
            };

        public static IReadOnlyList<ModuleType> DashboardModules => ActiveModules;

        public static IReadOnlyList<ModuleType> QuickAccessModules { get; } =
            new[]
            {
                ModuleType.LightSwitch,
                ModuleType.Monitor,
            };

        public static IReadOnlyList<string> ActiveSettingsModuleKeys { get; } =
            new[]
            {
                nameof(GeneralSettings),
                AwakeSettings.ModuleName,
                LightSwitchSettings.ModuleName,
                MonitorSettings.ModuleName,
                "General",
            };

        public static IReadOnlyList<string> ActiveEnabledModuleKeys { get; } =
            new[]
            {
                AwakeSettings.ModuleName,
                LightSwitchSettings.ModuleName,
                MonitorSettings.ModuleName,
            };

        public static bool IsActiveModule(ModuleType moduleType)
        {
            return ActiveModules.Contains(moduleType);
        }
    }
}
