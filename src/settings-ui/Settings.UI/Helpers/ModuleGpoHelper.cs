// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using global::PowerToys.GPOWrapper;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Views;

namespace Microsoft.PowerToys.Settings.UI.Helpers
{
    internal sealed class ModuleGpoHelper
    {
        public static GpoRuleConfigured GetModuleGpoConfiguration(ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => GPOWrapper.GetConfiguredAwakeEnabledValue(),
                ModuleType.LightSwitch => GPOWrapper.GetConfiguredLightSwitchEnabledValue(),
                ModuleType.PowerDisplay => GPOWrapper.GetConfiguredPowerDisplayEnabledValue(),
                _ => GpoRuleConfigured.Unavailable,
            };
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
