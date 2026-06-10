#include "pch.h"
#include "Constants.h"
#include "Constants.g.cpp"
#include "shared_constants.h"
#include <ShlObj.h>

namespace winrt::PowerToys::Interop::implementation
{
    uint32_t Constants::VK_WIN_BOTH()
    {
        return CommonSharedConstants::VK_WIN_BOTH;
    }
    hstring Constants::AppDataPath()
    {
        PWSTR local_app_path;
        winrt::check_hresult(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, NULL, &local_app_path));
        winrt::hstring result{ local_app_path };
        CoTaskMemFree(local_app_path);
        result = result + L"\\" + CommonSharedConstants::APPDATA_PATH;
        return result;
    }
    hstring Constants::LightSwitchToggleEvent()
    {
        return CommonSharedConstants::LIGHTSWITCH_TOGGLE_EVENT;
    }
    hstring Constants::AwakeExitEvent()
    {
        return CommonSharedConstants::AWAKE_EXIT_EVENT;
    }
    hstring Constants::KitRunnerTerminateSettingsEvent()
    {
        return CommonSharedConstants::TERMINATE_SETTINGS_SHARED_EVENT;
    }
    hstring Constants::TogglePowerDisplayEvent()
    {
        return CommonSharedConstants::TOGGLE_POWER_DISPLAY_EVENT;
    }
    hstring Constants::TerminatePowerDisplayEvent()
    {
        return CommonSharedConstants::TERMINATE_POWER_DISPLAY_EVENT;
    }
    hstring Constants::RefreshPowerDisplayMonitorsEvent()
    {
        return CommonSharedConstants::REFRESH_POWER_DISPLAY_MONITORS_EVENT;
    }
    hstring Constants::SettingsUpdatedPowerDisplayEvent()
    {
        return CommonSharedConstants::SETTINGS_UPDATED_POWER_DISPLAY_EVENT;
    }
    hstring Constants::HotkeyUpdatedPowerDisplayEvent()
    {
        return CommonSharedConstants::HOTKEY_UPDATED_POWER_DISPLAY_EVENT;
    }
    hstring Constants::PowerDisplayToggleMessage()
    {
        return CommonSharedConstants::POWER_DISPLAY_TOGGLE_MESSAGE;
    }
    hstring Constants::PowerDisplayApplyProfileMessage()
    {
        return CommonSharedConstants::POWER_DISPLAY_APPLY_PROFILE_MESSAGE;
    }
    hstring Constants::PowerDisplayTerminateAppMessage()
    {
        return CommonSharedConstants::POWER_DISPLAY_TERMINATE_APP_MESSAGE;
    }
}
