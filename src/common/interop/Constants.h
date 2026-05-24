#pragma once
#include "Constants.g.h"
namespace winrt::PowerToys::Interop::implementation
{
    struct Constants : ConstantsT<Constants>
    {
        Constants() = default;

        static uint32_t VK_WIN_BOTH();
        static hstring AppDataPath();
        static hstring LightSwitchToggleEvent();
        static hstring AwakeExitEvent();
        static hstring KitRunnerTerminateSettingsEvent();
        static hstring TogglePowerDisplayEvent();
        static hstring TerminatePowerDisplayEvent();
        static hstring RefreshPowerDisplayMonitorsEvent();
        static hstring SettingsUpdatedPowerDisplayEvent();
        static hstring HotkeyUpdatedPowerDisplayEvent();
        static hstring PowerDisplayToggleMessage();
        static hstring PowerDisplayApplyProfileMessage();
        static hstring PowerDisplayTerminateAppMessage();
    };
}

namespace winrt::PowerToys::Interop::factory_implementation
{
    struct Constants : ConstantsT<Constants, implementation::Constants>
    {
    };
}
