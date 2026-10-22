#pragma once
#include <optional>
#include <string>
#include "settings_window_startup.h"

enum class ESettingsWindowNames
{
    Dashboard = 0,
    Overview,
    Awake,
    LightSwitch,
    Localserver,
    UDPtest,
    NetMap,
    AiHub,
};

std::string ESettingsWindowNames_to_string(ESettingsWindowNames value);
ESettingsWindowNames ESettingsWindowNames_from_string(std::string value);

void open_settings_window(std::optional<std::wstring> settings_window);
SettingsWindowStartup::Completion complete_settings_window_startup(bool openSettings, std::optional<std::wstring> settings_window);
void cancel_settings_window_startup();
bool close_settings_window();
