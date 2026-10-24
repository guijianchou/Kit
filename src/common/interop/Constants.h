#pragma once
#include "Constants.g.h"
namespace winrt::Kit::Interop::implementation
{
    struct Constants : ConstantsT<Constants>
    {
        Constants() = default;

        static uint32_t VK_WIN_BOTH();
        static hstring AppDataPath();
        static hstring LightSwitchToggleEvent();
        static hstring AwakeExitEvent();
        static hstring KitRunnerTerminateSettingsEvent();
        static hstring LocalserverExitEvent();
        static hstring LocalserverRefreshSettingsEvent();
        static hstring AIHubExitEvent();
        static hstring AIHubRefreshSettingsEvent();
        static hstring UDPtestExitEvent();
        static hstring UDPtestRefreshSettingsEvent();
        static hstring NetMapExitEvent();
        static hstring NetMapRefreshSettingsEvent();
    };
}

namespace winrt::Kit::Interop::factory_implementation
{
    struct Constants : ConstantsT<Constants, implementation::Constants>
    {
    };
}
