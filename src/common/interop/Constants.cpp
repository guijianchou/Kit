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
}
