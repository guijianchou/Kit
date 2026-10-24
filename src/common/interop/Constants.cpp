#include "pch.h"
#include "Constants.h"
#include "Constants.g.cpp"
#include "shared_constants.h"
#include <ShlObj.h>

namespace winrt::Kit::Interop::implementation
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
    hstring Constants::LocalserverExitEvent()
    {
        return CommonSharedConstants::LOCALSERVER_EXIT_EVENT;
    }
    hstring Constants::LocalserverRefreshSettingsEvent()
    {
        return CommonSharedConstants::LOCALSERVER_REFRESH_SETTINGS_EVENT;
    }
    hstring Constants::AIHubExitEvent()
    {
        return CommonSharedConstants::AIHUB_EXIT_EVENT;
    }
    hstring Constants::AIHubRefreshSettingsEvent()
    {
        return CommonSharedConstants::AIHUB_REFRESH_SETTINGS_EVENT;
    }
    hstring Constants::UDPtestExitEvent()
    {
        return CommonSharedConstants::UDPTEST_EXIT_EVENT;
    }
    hstring Constants::UDPtestRefreshSettingsEvent()
    {
        return CommonSharedConstants::UDPTEST_REFRESH_SETTINGS_EVENT;
    }
    hstring Constants::NetMapExitEvent()
    {
        return CommonSharedConstants::NETMAP_EXIT_EVENT;
    }
    hstring Constants::NetMapRefreshSettingsEvent()
    {
        return CommonSharedConstants::NETMAP_REFRESH_SETTINGS_EVENT;
    }
}
