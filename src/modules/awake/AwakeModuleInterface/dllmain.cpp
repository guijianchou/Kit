#include "pch.h"
#include <interface/kit_module_interface.h>
#include <common/SettingsAPI/settings_objects.h>
#include <common/interop/shared_constants.h>
#include "trace.h"
#include "resource.h"
#include "AwakeConstants.h"
#include <common/logger/logger.h>
#include <common/SettingsAPI/settings_helpers.h>

#include <common/utils/elevation.h>
#include <common/utils/process_path.h>
#include <common/utils/resources.h>
#include <common/utils/os-detect.h>
#include <common/utils/winapi_error.h>

#include <filesystem>
#include <set>

BOOL APIENTRY DllMain(HMODULE /*hModule*/, DWORD ul_reason_for_call, LPVOID /*lpReserved*/)
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

const static wchar_t* MODULE_NAME = L"Awake";
const static wchar_t* MODULE_DESC = L"A module that keeps your computer awake on-demand.";
constexpr DWORD AWAKE_SHUTDOWN_WAIT_MS = 1500;

class Awake : public KitModuleIface
{
    std::wstring app_name;
    std::wstring app_key;

private:
    bool m_enabled = false;
    PROCESS_INFORMATION p_info = {};

    bool is_process_running()
    {
        return p_info.hProcess && WaitForSingleObject(p_info.hProcess, 0) == WAIT_TIMEOUT;
    }

    void close_process_handles()
    {
        if (p_info.hThread)
        {
            CloseHandle(p_info.hThread);
            p_info.hThread = nullptr;
        }

        if (p_info.hProcess)
        {
            CloseHandle(p_info.hProcess);
            p_info.hProcess = nullptr;
        }

        p_info.dwProcessId = 0;
        p_info.dwThreadId = 0;
    }

    void terminate_process_if_running()
    {
        if (!is_process_running())
        {
            return;
        }

        Logger::warn(L"Kit Awake did not exit after shutdown signal; terminating process.");
        if (!TerminateProcess(p_info.hProcess, 1))
        {
            Logger::warn(L"Failed to terminate Kit Awake. {}", get_last_error_or_default(GetLastError()));
            return;
        }

        WaitForSingleObject(p_info.hProcess, AWAKE_SHUTDOWN_WAIT_MS);
    }

    void wait_for_process_shutdown()
    {
        if (!p_info.hProcess)
        {
            return;
        }

        DWORD waitResult = WaitForSingleObject(p_info.hProcess, AWAKE_SHUTDOWN_WAIT_MS);
        if (waitResult == WAIT_TIMEOUT || waitResult == WAIT_FAILED)
        {
            terminate_process_if_running();
        }
    }

    bool launch_process()
    {
        Logger::trace(L"Launching Kit Awake process");
        unsigned long kit_pid = GetCurrentProcessId();

        const auto module_path = get_module_filename(reinterpret_cast<HMODULE>(&__ImageBase));
        if (module_path.empty())
        {
            Logger::error(L"Failed to resolve the Awake module path.");
            return false;
        }

        const auto application_directory = std::filesystem::path(module_path).parent_path().wstring();
        const auto application_path = (std::filesystem::path(application_directory) / L"Kit.Awake.exe").wstring();
        const std::wstring executable_args = L"--use-kit-config --pid " + std::to_wstring(kit_pid);
        std::wstring full_command_path = L"\"" + application_path + L"\" " + executable_args;
        Logger::trace(L"Kit Awake launching with parameters: " + executable_args);

        STARTUPINFO info = { sizeof(info) };

        if (!CreateProcessW(application_path.c_str(), full_command_path.data(), nullptr, nullptr, FALSE, 0, nullptr, application_directory.c_str(), &info, &p_info))
        {
            Logger::error(L"Kit Awake failed to start. {}", get_last_error_or_default(GetLastError()));
            close_process_handles();
            return false;
        }

        return true;
    }

public:
    Awake()
    {
        app_name = GET_RESOURCE_STRING(IDS_AWAKE_NAME);
        app_key = AwakeConstants::ModuleKey;
        std::filesystem::path logFilePath(PTSettingsHelper::get_module_save_folder_location(this->app_key));
        logFilePath.append(LogSettings::awakeLogPath);
        Logger::init(LogSettings::awakeLoggerName, logFilePath.wstring(), PTSettingsHelper::get_log_settings_file_location());
        Logger::info("Awake module is constructing");
    };

    virtual kit_gpo::gpo_rule_configured_t gpo_policy_enabled_configuration() override
    {
        return kit_gpo::getConfiguredAwakeEnabledValue();
    }

    virtual void destroy() override
    {
        disable();
        delete this;
    }

    virtual const wchar_t* get_name() override
    {
        return MODULE_NAME;
    }

    virtual bool get_config(wchar_t* buffer, int* buffer_size) override
    {
        HINSTANCE hinstance = reinterpret_cast<HINSTANCE>(&__ImageBase);

        PowerToysSettings::Settings settings(hinstance, get_name());
        settings.set_description(MODULE_DESC);

        return settings.serialize_to_buffer(buffer, buffer_size);
    }

    virtual const wchar_t* get_key() override
    {
        return app_key.c_str();
    }

    virtual void set_config(const wchar_t* config) override
    {
        try
        {
            PowerToysSettings::PowerToyValues values =
                PowerToysSettings::PowerToyValues::from_json_string(config, get_key());
            values.save_to_settings_file();
        }
        catch (std::exception&)
        {
        }
    }

    virtual void enable() override
    {
        if (m_enabled && is_process_running())
        {
            return;
        }

        close_process_handles();
        m_enabled = launch_process();
        if (m_enabled)
        {
            Trace::EnableAwake(true);
        }
    }

    virtual void disable() override
    {
        if (m_enabled || p_info.hProcess)
        {
            Trace::EnableAwake(false);
            Logger::trace(L"Disabling Awake...");

            auto exitEvent = CreateEvent(nullptr, false, false, CommonSharedConstants::AWAKE_EXIT_EVENT);
            if (!exitEvent)
            {
                Logger::warn(L"Failed to create exit event for Kit Awake. {}", get_last_error_or_default(GetLastError()));
                terminate_process_if_running();
            }
            else
            {
                Logger::trace(L"Signaled exit event for Kit Awake.");
                if (!SetEvent(exitEvent))
                {
                    Logger::warn(L"Failed to signal exit event for Kit Awake. {}", get_last_error_or_default(GetLastError()));
                    terminate_process_if_running();
                }
                CloseHandle(exitEvent);
                wait_for_process_shutdown();
            }
        }

        close_process_handles();
        m_enabled = false;
    }

    virtual bool is_enabled() override
    {
        return m_enabled;
    }
};

extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create()
{
    return new Awake();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl powertoy_create()
{
    return kit_create();
}
