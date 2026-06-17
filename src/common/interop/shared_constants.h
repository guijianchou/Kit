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

    // Path to the event used by Monitor
    const wchar_t MONITOR_EXIT_EVENT[] = L"Local\\KitMonitorExitEvent-0b94f553-2821-4690-a940-76d04c3ef7e8";

    // Path to the event used by Monitor to report one-shot scan completion
    const wchar_t MONITOR_SCAN_COMPLETED_EVENT[] = L"Local\\KitMonitorScanCompletedEvent-b7fb014b-c1fd-46c4-9d33-b517ef54824c";

    // Path to the event used by LightSwitch
    const wchar_t LIGHTSWITCH_TOGGLE_EVENT[] = L"Local\\Kit-LightSwitch-ToggleEvent-d8dc2f29-8c94-4ca1-8c5f-3e2b1e3c4f5a";

    // Max DWORD for key code to disable keys.
    const DWORD VK_DISABLED = 0x100;
}
