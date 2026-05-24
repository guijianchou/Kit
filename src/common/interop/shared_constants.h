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

    // Path to the events used by PowerDisplay
    const wchar_t TOGGLE_POWER_DISPLAY_EVENT[] = L"Local\\KitPowerDisplay-ToggleEvent-5f1a9c3e-7d2b-4e8f-9a6c-3b5d7e9f1a2c";
    const wchar_t TERMINATE_POWER_DISPLAY_EVENT[] = L"Local\\KitPowerDisplay-TerminateEvent-7b9c2e1f-8a5d-4c3e-9f6b-2a1d8c5e3b7a";
    const wchar_t REFRESH_POWER_DISPLAY_MONITORS_EVENT[] = L"Local\\KitPowerDisplay-RefreshMonitorsEvent-a3f5c8e7-9d1b-4e2f-8c6a-3b5d7e9f1a2c";
    const wchar_t SETTINGS_UPDATED_POWER_DISPLAY_EVENT[] = L"Local\\KitPowerDisplay-SettingsUpdatedEvent-2e4d6f8a-1c3b-5e7f-9a1d-4c6e8f0b2d3e";
    const wchar_t HOTKEY_UPDATED_POWER_DISPLAY_EVENT[] = L"Local\\KitPowerDisplay-HotkeyUpdatedEvent-9d5f3a2b-7e1c-4b8a-6f3d-2a9e5c7b1d4f";

    // IPC messages used in PowerDisplay named pipe communication
    const wchar_t POWER_DISPLAY_TOGGLE_MESSAGE[] = L"Toggle";
    const wchar_t POWER_DISPLAY_APPLY_PROFILE_MESSAGE[] = L"ApplyProfile";
    const wchar_t POWER_DISPLAY_TERMINATE_APP_MESSAGE[] = L"TerminateApp";

    // Path to the events used by LightSwitch to notify PowerDisplay of theme changes
    const wchar_t LIGHT_SWITCH_LIGHT_THEME_EVENT[] = L"Local\\KitLightSwitch-LightThemeEvent-50077121-2ffc-4841-9c86-ab1bd3f9baca";
    const wchar_t LIGHT_SWITCH_DARK_THEME_EVENT[] = L"Local\\KitLightSwitch-DarkThemeEvent-b3a835c0-eaa2-49b0-b8eb-f793e3df3368";

    // Max DWORD for key code to disable keys.
    const DWORD VK_DISABLED = 0x100;
}
