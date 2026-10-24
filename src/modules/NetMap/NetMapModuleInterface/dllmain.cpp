#include "pch.h"
#include <interface/kit_module_interface.h>
#include <common/SettingsAPI/settings_objects.h>
#include <common/SettingsAPI/settings_helpers.h>
#include <common/interop/shared_constants.h>
#include <common/logger/logger.h>
#include <common/logger/logger_settings.h>
#include <common/utils/process_path.h>
#include <common/utils/winapi_error.h>
#include <filesystem>
#include <mutex>

extern "C" IMAGE_DOS_HEADER __ImageBase;

class NetMapModule final : public KitModuleIface
{
    bool enabled = false;
    HANDLE m_worker_process{ nullptr };
    std::mutex m_lifecycle_mutex;

    void start_worker_if_needed();
    void stop_worker_if_running();
    void close_worker_handle();

public:
    NetMapModule()
    {
        std::filesystem::path logFilePath(PTSettingsHelper::get_module_save_folder_location(get_key()));
        logFilePath.append(LogSettings::netMapLogPath);
        Logger::init(LogSettings::netMapLoggerName, logFilePath.wstring(), PTSettingsHelper::get_log_settings_file_location());
        Logger::info("NetMap module is constructing");
    }

    const wchar_t* get_name() override { return L"NetMap"; }
    const wchar_t* get_key() override { return L"NetMap"; }
    bool is_enabled_by_default() const override { return false; }
    bool is_enabled() override { return enabled; }

    void enable() override
    {
        if (enabled)
        {
            return;
        }

        {
            std::lock_guard lifecycleLock(m_lifecycle_mutex);
            enabled = true;
            start_worker_if_needed();
        }

        Logger::info(L"NetMap module enabled");
    }

    void disable() override
    {
        if (!enabled)
        {
            return;
        }

        {
            std::lock_guard lifecycleLock(m_lifecycle_mutex);
            enabled = false;
            stop_worker_if_running();
        }

        Logger::info(L"NetMap module disabled");
    }

    void destroy() override
    {
        disable();
        close_worker_handle();
        delete this;
    }

    bool get_config(wchar_t* buffer, int* size) override
    {
        KitSettings::Settings settings(reinterpret_cast<HINSTANCE>(&__ImageBase), get_name());
        settings.set_description(L"Observe direct and proxy egress, endpoint responses and local routes.");
        return settings.serialize_to_buffer(buffer, size);
    }

    void set_config(const wchar_t* config) override
    {
        try
        {
            auto values = KitSettings::PowerToyValues::from_json_string(config, get_key());
            values.save_to_settings_file();

            auto refreshEvent = CreateEventW(nullptr, false, false, CommonSharedConstants::NETMAP_REFRESH_SETTINGS_EVENT);
            if (refreshEvent)
            {
                SetEvent(refreshEvent);
                CloseHandle(refreshEvent);
            }
        }
        catch (...)
        {
            // Never propagate malformed settings across the native module boundary.
        }
    }
};

void NetMapModule::close_worker_handle()
{
    if (m_worker_process)
    {
        CloseHandle(m_worker_process);
        m_worker_process = nullptr;
    }
}

void NetMapModule::start_worker_if_needed()
{
    if (m_worker_process && WaitForSingleObject(m_worker_process, 0) == WAIT_TIMEOUT)
    {
        Logger::debug(L"[NetMap] Service already running; skipping start.");
        return;
    }

    close_worker_handle();

    const auto module_path = get_module_filename(reinterpret_cast<HMODULE>(&__ImageBase));
    if (module_path.empty())
    {
        Logger::error(L"[NetMap] Failed to resolve the module path. {}", get_last_error_or_default(GetLastError()));
        return;
    }

    const auto resolved_path = (std::filesystem::path(module_path).parent_path() / L"NetMapService" / L"Kit.NetMapService.exe").wstring();
    if (!std::filesystem::exists(resolved_path))
    {
        Logger::warn(L"[NetMap] Service executable not found at {}.", resolved_path);
        return;
    }

    const std::wstring args = L"--use-kit-config --pid " + std::to_wstring(GetCurrentProcessId());
    std::wstring command_line = L"\"" + resolved_path + L"\" " + args;

    STARTUPINFO si = { sizeof(si) };
    PROCESS_INFORMATION pi{};

    if (!CreateProcessW(
            resolved_path.c_str(),
            command_line.data(),
            nullptr,
            nullptr,
            FALSE,
            CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
            nullptr,
            nullptr,
            &si,
            &pi))
    {
        Logger::error(L"[NetMap] Failed to launch the service. {}", get_last_error_or_default(GetLastError()));
        return;
    }

    Logger::info(L"[NetMap] Service launched (PID: {}).", pi.dwProcessId);
    m_worker_process = pi.hProcess;
    CloseHandle(pi.hThread);
}

void NetMapModule::stop_worker_if_running()
{
    if (!m_worker_process)
    {
        return;
    }

    DWORD result = WaitForSingleObject(m_worker_process, 0);
    if (result == WAIT_TIMEOUT)
    {
        Logger::info(L"[NetMap] Stopping the service.");

        HANDLE stopEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, CommonSharedConstants::NETMAP_EXIT_EVENT);
        if (stopEvent)
        {
            SetEvent(stopEvent);
            CloseHandle(stopEvent);
            result = WaitForSingleObject(m_worker_process, 2000);
        }

        if (result == WAIT_TIMEOUT && TerminateProcess(m_worker_process, 0))
        {
            result = WaitForSingleObject(m_worker_process, 1500);
        }
    }

    if (result != WAIT_OBJECT_0)
    {
        Logger::warn(L"[NetMap] Service shutdown could not be confirmed.");
        return;
    }

    close_worker_handle();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create()
{
    return new NetMapModule();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl powertoy_create()
{
    return kit_create();
}
