#include "theme_listener.h"

#define HKEY_WINDOWS_THEME L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"

// disabling warning 4702 - unreachable code
// prevent the warning after the call off an infinite loop function
#pragma warning(push)
#pragma warning(disable : 4702)
DWORD WINAPI _checkTheme(LPVOID lpParam)
{
    auto listener = static_cast<ThemeListener*>(lpParam);
    listener->CheckTheme();
    return 0;
}
#pragma warning(pop)

void ThemeListener::AddChangedHandler(THEME_HANDLE handle)
{
    std::lock_guard<std::mutex> lock(handlesMutex);
    handles.push_back(handle);
}

void ThemeListener::DelChangedHandler(THEME_HANDLE handle)
{
    std::lock_guard<std::mutex> lock(handlesMutex);
    auto it = std::find(handles.begin(), handles.end(), handle);
    if (it != handles.end())
    {
        handles.erase(it);
    }
}

void ThemeListener::AddAppThemeChangedHandler(THEME_HANDLE handle)
{
    std::lock_guard<std::mutex> lock(handlesMutex);
    appThemeHandles.push_back(handle);
}

void ThemeListener::DelAppThemeChangedHandler(THEME_HANDLE handle)
{
    std::lock_guard<std::mutex> lock(handlesMutex);
    auto it = std::find(appThemeHandles.begin(), appThemeHandles.end(), handle);
    if (it != appThemeHandles.end())
    {
        appThemeHandles.erase(it);
    }
}

void ThemeListener::AddSystemThemeChangedHandler(THEME_HANDLE handle)
{
    std::lock_guard<std::mutex> lock(handlesMutex);
    systemThemeHandles.push_back(handle);
}

void ThemeListener::DelSystemThemeChangedHandler(THEME_HANDLE handle)
{
    std::lock_guard<std::mutex> lock(handlesMutex);
    auto it = std::find(systemThemeHandles.begin(), systemThemeHandles.end(), handle);
    if (it != systemThemeHandles.end())
    {
        systemThemeHandles.erase(it);
    }
}

void ThemeListener::CheckTheme()
{
    HKEY hKey = nullptr;

    // Open the Key to listen
    if (RegOpenKeyExW(HKEY_CURRENT_USER, HKEY_WINDOWS_THEME, 0, KEY_NOTIFY, &hKey) != ERROR_SUCCESS)
    {
        return;
    }

    HANDLE hEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (hEvent == nullptr)
    {
        RegCloseKey(hKey);
        return;
    }

    auto checkAndDispatch = [&]() {
        const auto _appTheme = ThemeHelpers::GetAppTheme();
        const auto _systemTheme = ThemeHelpers::GetSystemTheme();

        const bool appThemeChanged = (AppTheme != _appTheme);
        const bool systemThemeChanged = (SystemTheme != _systemTheme);

        if (!appThemeChanged && !systemThemeChanged)
        {
            return;
        }

        AppTheme = _appTheme;
        SystemTheme = _systemTheme;

        // Copy handlers under lock, then invoke outside lock to avoid deadlock
        std::vector<THEME_HANDLE> handlesCopy;
        std::vector<THEME_HANDLE> appThemeHandlesCopy;
        std::vector<THEME_HANDLE> systemThemeHandlesCopy;

        {
            std::lock_guard<std::mutex> lock(handlesMutex);
            handlesCopy = handles;
            if (appThemeChanged)
            {
                appThemeHandlesCopy = appThemeHandles;
            }
            if (systemThemeChanged)
            {
                systemThemeHandlesCopy = systemThemeHandles;
            }
        }

        // Call generic handlers (backward compatible)
        for (const auto& handler : handlesCopy)
        {
            handler();
        }

        // Call app theme specific handlers
        for (const auto& handler : appThemeHandlesCopy)
        {
            handler();
        }

        // Call system theme specific handlers
        for (const auto& handler : systemThemeHandlesCopy)
        {
            handler();
        }
    };

    while (true)
    {
        // Arm the notification BEFORE reading the registry so changes that arrive
        // between startup/handler execution and WaitForSingleObject are never missed.
        if (RegNotifyChangeKeyValue(hKey,
                                    FALSE,
                                    REG_NOTIFY_CHANGE_LAST_SET,
                                    hEvent,
                                    TRUE) != ERROR_SUCCESS)
        {
            break;
        }

        checkAndDispatch();

        if (WaitForSingleObject(hEvent, INFINITE) != WAIT_OBJECT_0)
        {
            break;
        }

        checkAndDispatch();
    }

    CloseHandle(hEvent);
    RegCloseKey(hKey);
}