// Port Based on https://stackoverflow.com/a/62811758/5001796
#include "theme_helpers.h"
#include "dwmapi.h"
#include <windows.h>
#include <vector>
#pragma comment(lib, "Dwmapi.lib")

#define DWMWA_USE_IMMERSIVE_DARK_MODE 20
#define HKEY_WINDOWS_THEME L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"

// based on https://stackoverflow.com/questions/51334674/how-to-detect-windows-10-light-dark-mode-in-win32-application
Theme ThemeHelpers::GetAppTheme()
{
    return ThemeRegistryHelper(L"AppsUseLightTheme");
}

Theme ThemeHelpers::GetSystemTheme()
{
    return ThemeRegistryHelper(L"SystemUsesLightTheme");
}

Theme ThemeHelpers::ThemeRegistryHelper(LPCWSTR theme_key)
{
    DWORD value = 0;
    DWORD cbData = sizeof(value);
    const auto res = RegGetValueW(
        HKEY_CURRENT_USER,
        HKEY_WINDOWS_THEME,
        theme_key,
        RRF_RT_REG_DWORD,
        nullptr,
        &value,
        &cbData);

    if (res != ERROR_SUCCESS)
    {
        // Defaulting to Light
        return Theme::Light;
    }

    return value == 0 ? Theme::Dark : Theme::Light;
}

void ThemeHelpers::SetImmersiveDarkMode(HWND window, bool enabled)
{
    int useImmersiveDarkMode = enabled ? 1 : 0;
    DwmSetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, &useImmersiveDarkMode, sizeof(useImmersiveDarkMode));
}
