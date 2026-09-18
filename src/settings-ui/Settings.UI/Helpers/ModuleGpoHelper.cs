// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using global::Kit.GPOWrapper;
using Kit.Settings.UI.Views;
using ManagedCommon;

namespace Kit.Settings.UI.Helpers
{
    using GPOWrapper = global::Kit.GPOWrapper.GPOWrapper;

    internal sealed class ModuleGpoHelper
    {
        public static GpoRuleConfigured GetModuleGpoConfiguration(ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => GPOWrapper.GetConfiguredAwakeEnabledValue(),
                ModuleType.LightSwitch => GPOWrapper.GetConfiguredLightSwitchEnabledValue(),
                _ => GpoRuleConfigured.Unavailable,
            };
        }

        public static System.Type GetModulePageType(ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => typeof(AwakePage),
                ModuleType.LightSwitch => typeof(LightSwitchPage),
                ModuleType.Localserver => typeof(LocalserverPage),
                ModuleType.UDPtest => typeof(UDPtestPage),
                ModuleType.AIHub => typeof(AIHubPage),
                _ => typeof(GeneralPage),
            };
        }
    }
}
