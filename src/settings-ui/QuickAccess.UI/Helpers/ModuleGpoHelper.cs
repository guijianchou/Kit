// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using global::Kit.GPOWrapper;
using Kit.Settings.UI.Library;
using ManagedCommon;

namespace Kit.QuickAccess.Helpers;

using GPOWrapper = global::Kit.GPOWrapper.GPOWrapper;

internal static class ModuleGpoHelper
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
}
