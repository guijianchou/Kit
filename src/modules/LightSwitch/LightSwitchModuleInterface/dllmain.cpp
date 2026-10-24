#include "pch.h"
#include <interface/kit_module_interface.h>
#include "trace.h"
#include <common/logger/logger.h>
#include <common/SettingsAPI/settings_objects.h>
#include <common/SettingsAPI/settings_helpers.h>
#include <common/interop/shared_constants.h>
#include <locale>
#include <codecvt>
#include <common/utils/logger_helper.h>
#include <common/utils/process_path.h>
#include "ThemeHelper.h"
#include <thread>
#include <atomic>
#include <climits>
#include <mutex>
#include <filesystem>

extern "C" IMAGE_DOS_HEADER __ImageBase;

namespace
{
    const wchar_t KIT_LIGHTSWITCH_SERVICE_STOP[] = L"Local\\KitLightSwitchServiceStopEvent-09b983c3-01df-4490-9f84-9f6e5c52c7d5";
    constexpr DWORD LIGHTSWITCH_SHUTDOWN_WAIT_MS = 1500;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        Trace::RegisterProvider();
        break;
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
        break;
    case DLL_PROCESS_DETACH:
        Trace::UnregisterProvider();
        break;
    }
    return TRUE;
}

// The PowerToy name that will be shown in the settings.
const static wchar_t* MODULE_NAME = L"LightSwitch";
// Add a description that will we shown in the module settings page.
const static wchar_t* MODULE_DESC = L"This is a module that allows you to control light/dark theming via set times, sun rise, or directly invoking the change.";

enum class ScheduleMode
{
    Off,
    FixedHours,
    SunsetToSunrise,
    FollowNightLight,
    // add more later
};

inline std::wstring ToString(ScheduleMode mode)
{
    switch (mode)
    {
    case ScheduleMode::SunsetToSunrise:
        return L"SunsetToSunrise";
    case ScheduleMode::FixedHours:
        return L"FixedHours";
    case ScheduleMode::FollowNightLight:
        return L"FollowNightLight";
    default:
        return L"Off";
    }
}

inline ScheduleMode FromString(const std::wstring& str)
{
    if (str == L"SunsetToSunrise")
        return ScheduleMode::SunsetToSunrise;
    if (str == L"FixedHours")
        return ScheduleMode::FixedHours;
    if (str == L"FollowNightLight")
        return ScheduleMode::FollowNightLight;
    return ScheduleMode::Off;
}

// These are the properties shown in the Settings page.
struct ModuleSettings
{
    bool m_changeSystem = true;
    bool m_changeApps = true;
    ScheduleMode m_scheduleMode = ScheduleMode::Off;
    int m_lightTime = 480;
    int m_darkTime = 1200;
    int m_sunrise_offset = 0;
    int m_sunset_offset = 0;
    std::wstring m_latitude = L"0.0";
    std::wstring m_longitude = L"0.0";
} g_settings;

class LightSwitchInterface : public KitModuleIface
{
private:
    // Listener ownership is separate so disable() can join without blocking a
    // request that is already waiting for the service lock.
    std::mutex m_lifecycle_mutex;
    std::mutex m_service_mutex;
    bool m_enabled = false;

    HANDLE m_process{ nullptr };
    HANDLE m_service_stop_event_handle{ nullptr };
    HANDLE m_toggle_request_semaphore{ nullptr };
    HANDLE m_toggle_event_handle{ nullptr };
    std::thread m_toggle_thread;
    std::atomic<bool> m_toggle_thread_running{ false };

    void init_settings();
    bool EnsureServiceRunningLocked();
    void ToggleTheme();
    void StartToggleListener();
    void StopToggleListener();

public:
    LightSwitchInterface()
    {
        LoggerHelpers::init_logger(L"LightSwitch", L"ModuleInterface", LogSettings::lightSwitchLoggerName);

        m_service_stop_event_handle = CreateEventW(nullptr, TRUE, FALSE, KIT_LIGHTSWITCH_SERVICE_STOP);
        m_toggle_request_semaphore = CreateSemaphoreW(nullptr, 0, LONG_MAX, LIGHT_SWITCH_TOGGLE_REQUEST_SEMAPHORE);
        m_toggle_event_handle = CreateDefaultEvent(CommonSharedConstants::LIGHTSWITCH_TOGGLE_EVENT);

        init_settings();
    };

    virtual const wchar_t* get_key() override
    {
        return L"LightSwitch";
    }

    // Destroy the powertoy and free memory
    virtual void destroy() override
    {
        // Ensure worker threads/process handles are cleaned up before destruction
        disable();
        if (m_toggle_event_handle)
        {
            CloseHandle(m_toggle_event_handle);
            m_toggle_event_handle = nullptr;
        }
        if (m_service_stop_event_handle)
        {
            CloseHandle(m_service_stop_event_handle);
            m_service_stop_event_handle = nullptr;
        }
        delete this;
    }

    // Return the display name of the powertoy, this will be cached by the runner
    virtual const wchar_t* get_name() override
    {
        return MODULE_NAME;
    }

    // Return the configured status for the gpo policy for the module
    virtual kit_gpo::gpo_rule_configured_t gpo_policy_enabled_configuration() override
    {
        return kit_gpo::getConfiguredLightSwitchEnabledValue();
    }

    // Return JSON with the configuration options.
    virtual bool get_config(wchar_t* buffer, int* buffer_size) override
    {
        HINSTANCE hinstance = reinterpret_cast<HINSTANCE>(&__ImageBase);

        // Create a Settings object with your module name
        KitSettings::Settings settings(hinstance, get_name());
        settings.set_description(MODULE_DESC);
        settings.set_overview_link(L"https://github.com/guijianchou/Kit");

        // Boolean toggles
        settings.add_bool_toggle(
            L"changeSystem",
            L"Change System Theme",
            g_settings.m_changeSystem);

        settings.add_bool_toggle(
            L"changeApps",
            L"Change Apps Theme",
            g_settings.m_changeApps);

        settings.add_choice_group(
            L"scheduleMode",
            L"Theme schedule mode",
            ToString(g_settings.m_scheduleMode),
            { { L"Off", L"Disable the schedule" },
              { L"FixedHours", L"Set hours manually" },
              { L"SunsetToSunrise", L"Use sunrise/sunset times" },
              { L"FollowNightLight", L"Follow Windows Night Light state" }
            });

        // Integer spinners
        settings.add_int_spinner(
            L"lightTime",
            L"Time to switch to light theme (minutes after midnight).",
            g_settings.m_lightTime,
            0,
            1439,
            1);

        settings.add_int_spinner(
            L"darkTime",
            L"Time to switch to dark theme (minutes after midnight).",
            g_settings.m_darkTime,
            0,
            1439,
            1);

        settings.add_int_spinner(
            L"sunrise_offset",
            L"Time to offset turning on your light theme.",
            g_settings.m_sunrise_offset,
            0,
            1439,
            1);

        settings.add_int_spinner(
            L"sunset_offset",
            L"Time to offset turning on your dark theme.",
            g_settings.m_sunset_offset,
            0,
            1439,
            1);

        // Strings for latitude and longitude
        settings.add_string(
            L"latitude",
            L"Your latitude in decimal degrees (e.g. 39.95).",
            g_settings.m_latitude);

        settings.add_string(
            L"longitude",
            L"Your longitude in decimal degrees (e.g. -75.16).",
            g_settings.m_longitude);

        // Serialize to buffer for the Kit runner
        return settings.serialize_to_buffer(buffer, buffer_size);
    }

    // Signal from the Settings editor to call a custom action.
    // This can be used to spawn more complex editors.
    void call_custom_action(const wchar_t* action) override
    {
        UNREFERENCED_PARAMETER(action);
    }

    // Called by the runner to pass the updated settings values as a serialized JSON.
    virtual void set_config(const wchar_t* config) override
    {
        try
        {
            auto values = KitSettings::PowerToyValues::from_json_string(config, get_key());
            bool scheduleModeChanged = false;

            if (auto v = values.get_bool_value(L"changeSystem"))
            {
                g_settings.m_changeSystem = *v;
            }

            if (auto v = values.get_bool_value(L"changeApps"))
            {
                g_settings.m_changeApps = *v;
            }

            if (auto v = values.get_string_value(L"scheduleMode"))
            {
                auto newMode = FromString(*v);
                if (newMode != g_settings.m_scheduleMode)
                {
                    Logger::info(L"[LightSwitchInterface] Schedule mode changed from {} to {}",
                                 ToString(g_settings.m_scheduleMode),
                                 ToString(newMode));
                    g_settings.m_scheduleMode = newMode;
                    scheduleModeChanged = true;
                }
            }

            if (auto v = values.get_int_value(L"lightTime"))
            {
                g_settings.m_lightTime = *v;
            }

            if (auto v = values.get_int_value(L"darkTime"))
            {
                g_settings.m_darkTime = *v;
            }

            if (auto v = values.get_int_value(L"sunrise_offset"))
            {
                g_settings.m_sunrise_offset = *v;
            }

            if (auto v = values.get_int_value(L"sunset_offset"))
            {
                g_settings.m_sunset_offset = *v;
            }

            if (auto v = values.get_string_value(L"latitude"))
            {
                g_settings.m_latitude = *v;
            }
            if (auto v = values.get_string_value(L"longitude"))
            {
                g_settings.m_longitude = *v;
            }

            values.save_to_settings_file();

            if (scheduleModeChanged)
            {
                start_service_if_needed();
            }
        }
        catch (...)
        {
            Logger::error("[Light Switch] set_config: Failed to parse or apply config.");
        }
    }

    virtual void start_service_if_needed()
    {
        std::lock_guard lock(m_service_mutex);
        EnsureServiceRunningLocked();
    }

    virtual void enable()
    {
        std::lock_guard lifecycle_lock(m_lifecycle_mutex);
        {
            std::lock_guard service_lock(m_service_mutex);
            m_enabled = true;
            Logger::info(L"Enabling Light Switch module...");
            Trace::Enable(true);
            EnsureServiceRunningLocked();
        }

        // Keep accepting requests even if this launch failed, so the next
        // Command Palette request can retry without toggling the module off/on.
        StartToggleListener();
    }

    // Disable the powertoy
    virtual void disable()
    {
        std::lock_guard lifecycle_lock(m_lifecycle_mutex);
        Logger::info("Light Switch disabling");
        {
            std::lock_guard service_lock(m_service_mutex);
            m_enabled = false;
            Trace::Enable(false);
        }

        // A listener request may need the service lock before it can exit.
        StopToggleListener();

        std::lock_guard service_lock(m_service_mutex);
        if (m_process)
        {
            if (m_service_stop_event_handle)
            {
                SetEvent(m_service_stop_event_handle);
            }

            DWORD result = WaitForSingleObject(m_process, LIGHTSWITCH_SHUTDOWN_WAIT_MS);

            if (result == WAIT_TIMEOUT)
            {
                Logger::warn("Light Switch: Process didn't exit in time. Forcing termination.");
                if (!TerminateProcess(m_process, 0))
                {
                    Logger::error(L"[Light Switch] Could not terminate the service process (error: {}).", GetLastError());
                }
                result = WaitForSingleObject(m_process, LIGHTSWITCH_SHUTDOWN_WAIT_MS);
            }

            if (result != WAIT_OBJECT_0)
            {
                // Preserve ownership while the process may still be using the
                // semaphore; a later enable must not launch a second worker.
                Logger::error(L"[Light Switch] Service process has not exited; retaining its handles.");
                return;
            }

            CloseHandle(m_process);
            m_process = nullptr;
            if (m_service_stop_event_handle)
            {
                ResetEvent(m_service_stop_event_handle);
            }
        }

        if (m_toggle_request_semaphore)
        {
            CloseHandle(m_toggle_request_semaphore);
            m_toggle_request_semaphore = nullptr;
        }
    }

    // Returns if the powertoys is enabled
    virtual bool is_enabled() override
    {
        std::lock_guard lock(m_service_mutex);
        return m_enabled;
    }

    // Returns whether the PowerToys should be enabled by default
    virtual bool is_enabled_by_default() const override
    {
        return false;
    }
};

bool LightSwitchInterface::EnsureServiceRunningLocked()
{
    if (!m_enabled || gpo_policy_enabled_configuration() == kit_gpo::gpo_rule_configured_disabled)
    {
        return false;
    }

    if (m_process)
    {
        const DWORD wait_result = WaitForSingleObject(m_process, 0);
        if (wait_result == WAIT_TIMEOUT)
        {
            return true;
        }
        if (wait_result != WAIT_OBJECT_0)
        {
            Logger::error(L"[Light Switch] Could not check the service process (error: {}).", GetLastError());
            return false;
        }

        CloseHandle(m_process);
        m_process = nullptr;
    }

    if (!m_service_stop_event_handle)
    {
        m_service_stop_event_handle = CreateEventW(nullptr, TRUE, FALSE, KIT_LIGHTSWITCH_SERVICE_STOP);
        if (!m_service_stop_event_handle)
        {
            Logger::error(L"[Light Switch] Could not create service stop event (error: {}).", GetLastError());
            return false;
        }
    }
    ResetEvent(m_service_stop_event_handle);

    if (!m_toggle_request_semaphore)
    {
        m_toggle_request_semaphore = CreateSemaphoreW(nullptr, 0, LONG_MAX, LIGHT_SWITCH_TOGGLE_REQUEST_SEMAPHORE);
        if (!m_toggle_request_semaphore)
        {
            Logger::error(L"[Light Switch] Could not create toggle request semaphore (error: {}).", GetLastError());
            return false;
        }
    }

    unsigned long kit_pid = GetCurrentProcessId();
    std::wstring args = L"--pid " + std::to_wstring(kit_pid);
    const auto module_path = get_module_filename(reinterpret_cast<HMODULE>(&__ImageBase));
    if (module_path.empty())
    {
        Logger::error(L"Failed to resolve the Light Switch module path. {}", get_last_error_or_default(GetLastError()));
        return false;
    }

    const auto resolved_path = (std::filesystem::path(module_path).parent_path() / L"LightSwitchService" / L"Kit.LightSwitchService.exe").wstring();
    Logger::debug(L"Resolved executable path: {}", resolved_path);

    std::wstring command_line = L"\"" + resolved_path + L"\" " + args;

    STARTUPINFO si = { sizeof(si) };
    PROCESS_INFORMATION pi{};

    if (!CreateProcessW(
            resolved_path.c_str(),
            command_line.data(),
            nullptr,
            nullptr,
            TRUE,
            0,
            nullptr,
            nullptr,
            &si,
            &pi))
    {
        Logger::error(L"Failed to launch Light Switch process. {}", get_last_error_or_default(GetLastError()));
        return false;
    }

    Logger::info(L"Light Switch process launched successfully (PID: {}).", pi.dwProcessId);
    m_process = pi.hProcess;
    CloseHandle(pi.hThread);
    return true;
}

void LightSwitchInterface::ToggleTheme()
{
    std::lock_guard lock(m_service_mutex);
    if (!EnsureServiceRunningLocked())
    {
        return;
    }

    // The service applies the theme and updates its scheduler state as one operation.
    // A semaphore retains every toggle request while an earlier theme change is running.
    if (!m_toggle_request_semaphore || !ReleaseSemaphore(m_toggle_request_semaphore, 1, nullptr))
    {
        Logger::warn(L"[Light Switch] Could not queue a toggle request (error: {}).", m_toggle_request_semaphore ? GetLastError() : ERROR_INVALID_HANDLE);
    }
}

void LightSwitchInterface::StartToggleListener()
{
    if (m_toggle_thread_running || !m_toggle_event_handle)
    {
        return;
    }

    m_toggle_thread_running = true;
    m_toggle_thread = std::thread([this]() {
        while (m_toggle_thread_running)
        {
            const DWORD wait_result = WaitForSingleObject(m_toggle_event_handle, 500);
            if (!m_toggle_thread_running)
            {
                break;
            }

            if (wait_result == WAIT_OBJECT_0)
            {
                Trace::ShortcutInvoked();
                ToggleTheme();
            }
        }
    });
}

void LightSwitchInterface::StopToggleListener()
{
    if (!m_toggle_thread_running)
    {
        return;
    }

    m_toggle_thread_running = false;
    if (m_toggle_event_handle)
    {
        SetEvent(m_toggle_event_handle);
    }
    if (m_toggle_thread.joinable())
    {
        m_toggle_thread.join();
    }
    if (m_toggle_event_handle)
    {
        // Clear the stop wake-up if the listener exited before consuming it.
        ResetEvent(m_toggle_event_handle);
    }
}

std::wstring utf8_to_wstring(const std::string& str)
{
    if (str.empty())
        return std::wstring();

    int size_needed = MultiByteToWideChar(
        CP_UTF8,
        0,
        str.c_str(),
        static_cast<int>(str.size()),
        nullptr,
        0);

    std::wstring wstr(size_needed, 0);

    MultiByteToWideChar(
        CP_UTF8,
        0,
        str.c_str(),
        static_cast<int>(str.size()),
        &wstr[0],
        size_needed);

    return wstr;
}

// Load the settings file.
void LightSwitchInterface::init_settings()
{
    Logger::info(L"[Light Switch] init_settings: starting to load settings for module");

    try
    {
        KitSettings::PowerToyValues settings =
            KitSettings::PowerToyValues::load_from_settings_file(get_name());

        if (auto v = settings.get_bool_value(L"changeSystem"))
            g_settings.m_changeSystem = *v;
        if (auto v = settings.get_bool_value(L"changeApps"))
            g_settings.m_changeApps = *v;
        if (auto v = settings.get_string_value(L"scheduleMode"))
            g_settings.m_scheduleMode = FromString(*v);
        if (auto v = settings.get_int_value(L"lightTime"))
            g_settings.m_lightTime = *v;
        if (auto v = settings.get_int_value(L"darkTime"))
            g_settings.m_darkTime = *v;
        if (auto v = settings.get_int_value(L"sunrise_offset"))
            g_settings.m_sunrise_offset = *v;
        if (auto v = settings.get_int_value(L"sunset_offset"))
            g_settings.m_sunset_offset = *v;
        if (auto v = settings.get_string_value(L"latitude"))
            g_settings.m_latitude = *v;
        if (auto v = settings.get_string_value(L"longitude"))
            g_settings.m_longitude = *v;

        Logger::info(L"[Light Switch] init_settings: loaded successfully");
    }
    catch (const winrt::hresult_error& e)
    {
        const auto errorCode = static_cast<unsigned int>(static_cast<HRESULT>(e.code()));
        Logger::error(L"[Light Switch] init_settings: hresult_error 0x{:08X} - {}", errorCode, e.message().c_str());
    }
    catch (const std::exception& e)
    {
        std::wstring whatStr = utf8_to_wstring(e.what());
        Logger::error(L"[Light Switch] init_settings: std::exception - {}", whatStr);
    }
    catch (...)
    {
        Logger::error(L"[Light Switch] init_settings: unknown exception while loading settings");
    }
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create()
{
    return new LightSwitchInterface();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl powertoy_create()
{
    return kit_create();
}
