#pragma once

#include <cstdint>

namespace CommonSharedConstants
{
    // Fake key code to represent VK_WIN.
    inline const DWORD VK_WIN_BOTH = 0x104;

    const wchar_t APPDATA_PATH[] = L"Kit";

    // Path to the event used by runner to terminate Settings app
    const wchar_t TERMINATE_SETTINGS_SHARED_EVENT[] = L"Local\\KitRunnerTerminateSettingsEvent-c34cb661-2e69-4613-a1f8-4e39c25d7ef6";

    // Path to the event used by Awake
    const wchar_t AWAKE_EXIT_EVENT[] = L"Local\\KitAwakeExitEvent-c0d5e305-35fc-4fb5-83ec-f6070cfaf7fe";

    // Path to the event used by LightSwitch
    const wchar_t LIGHTSWITCH_TOGGLE_EVENT[] = L"Local\\Kit-LightSwitch-ToggleEvent-d8dc2f29-8c94-4ca1-8c5f-3e2b1e3c4f5a";

    // Path to events used by Localserver
    const wchar_t LOCALSERVER_EXIT_EVENT[] = L"Local\\Kit_Localserver_Exit_Event_3d8a1b21";
    const wchar_t LOCALSERVER_REFRESH_SETTINGS_EVENT[] = L"Local\\Kit_Localserver_RefreshSettings_Event_3d8a1b22";

    // Path to events used by AIHub
    const wchar_t AIHUB_EXIT_EVENT[] = L"Local\\KitAIHubWorkerStopEvent-3f8c1a52-6d47-4b9e-8a11-2c7d5e9f4b60";
    const wchar_t AIHUB_REFRESH_SETTINGS_EVENT[] = L"Local\\Kit_AIHub_RefreshSettings_Event_a1b2c3d6";

    // Path to events used by UDPtest
    const wchar_t UDPTEST_EXIT_EVENT[] = L"Local\\Kit_UDPtest_Stop_Event_c9d0e1f2";
    const wchar_t UDPTEST_REFRESH_SETTINGS_EVENT[] = L"Local\\Kit_UDPtest_RefreshSettings_Event_1a2b3c4d";

    // Path to events used by NetMap
    const wchar_t NETMAP_EXIT_EVENT[] = L"Local\\Kit_NetMap_Exit_Event_7f8e9d0d";
    const wchar_t NETMAP_REFRESH_SETTINGS_EVENT[] = L"Local\\Kit_NetMap_RefreshSettings_Event_7f8e9d0e";

    // Max DWORD for key code to disable keys.
    const DWORD VK_DISABLED = 0x100;
}
