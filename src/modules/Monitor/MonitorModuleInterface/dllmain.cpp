#include "pch.h"
#include <interface/powertoy_module_interface.h>
#include <common/SettingsAPI/settings_objects.h>
#include <common/SettingsAPI/settings_helpers.h>
#include <common/interop/shared_constants.h>
#include <common/logger/logger.h>
#include <common/utils/logger_helper.h>
#include <common/utils/process_path.h>
#include <common/utils/resources.h>
#include <common/utils/winapi_error.h>
#include "resource.h"
#include "trace.h"

#include <string>

extern "C" IMAGE_DOS_HEADER __ImageBase;

namespace
{
    const wchar_t* const MODULE_NAME = L"Monitor";
    const wchar_t* const MODULE_DESC = L"Monitor keeps the Downloads folder indexed, organized, and ready for duplicate or installer cleanup workflows.";
    const wchar_t* const MONITOR_PROCESS_NAME = L"PowerToys.Monitor.exe";
    const wchar_t* const MONITOR_PROCESS_DLL_NAME = L"PowerToys.Monitor.dll";
    const wchar_t* const DOTNET_PROCESS_NAME = L"dotnet.exe";

    struct MonitorLaunchTarget
    {
        std::wstring application_path;
        std::wstring command_prefix;
        std::wstring working_directory;
    };

    bool is_handle_running(HANDLE process)
    {
        return process != nullptr && WaitForSingleObject(process, 0) == WAIT_TIMEOUT;
    }

    bool is_existing_file(const std::wstring& path)
    {
        DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    std::wstring combine_path(std::wstring directory, const wchar_t* file_name)
    {
        if (!directory.empty() && directory.back() != L'\\' && directory.back() != L'/')
        {
            directory += L'\\';
        }

        directory += file_name;
        return directory;
    }

    std::wstring quote_argument(const std::wstring& value)
    {
        return L"\"" + value + L"\"";
    }

    bool search_process_path(const wchar_t* process_name, std::wstring& resolved_path)
    {
        std::wstring search_result(MAX_PATH, L'\0');
        DWORD result = SearchPathW(
            nullptr,
            process_name,
            nullptr,
            static_cast<DWORD>(search_result.size()),
            search_result.data(),
            nullptr);

        if (result == 0 || result >= search_result.size())
        {
            return false;
        }

        search_result.resize(result);
        resolved_path = std::move(search_result);
        return true;
    }

    bool resolve_monitor_launch_target(MonitorLaunchTarget& target)
    {
        std::wstring module_folder = get_module_folderpath(reinterpret_cast<HMODULE>(&__ImageBase));
        std::wstring monitor_exe_path = combine_path(module_folder, MONITOR_PROCESS_NAME);
        if (is_existing_file(monitor_exe_path))
        {
            target.application_path = monitor_exe_path;
            target.command_prefix = quote_argument(monitor_exe_path);
            target.working_directory = module_folder;
            return true;
        }

        if (search_process_path(MONITOR_PROCESS_NAME, monitor_exe_path))
        {
            target.application_path = monitor_exe_path;
            target.command_prefix = quote_argument(monitor_exe_path);
            target.working_directory = module_folder;
            return true;
        }

        std::wstring monitor_dll_path = combine_path(module_folder, MONITOR_PROCESS_DLL_NAME);
        if (!is_existing_file(monitor_dll_path))
        {
            Logger::error(L"Failed to locate Monitor worker. Missing '{}' and '{}'.", MONITOR_PROCESS_NAME, monitor_dll_path);
            return false;
        }

        std::wstring dotnet_path;
        if (!search_process_path(DOTNET_PROCESS_NAME, dotnet_path))
        {
            Logger::error(L"Failed to locate '{}' for Monitor worker fallback. {}", DOTNET_PROCESS_NAME, get_last_error_or_default(GetLastError()));
            return false;
        }

        target.application_path = dotnet_path;
        target.command_prefix = quote_argument(dotnet_path) + L" " + quote_argument(monitor_dll_path);
        target.working_directory = module_folder;
        return true;
    }
}

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

class MonitorModuleInterface : public PowertoyModuleIface
{
private:
    bool m_enabled = false;
    bool m_run_in_background = false;
    HANDLE m_process = nullptr;

    bool launch_process(const std::wstring& extra_args, const bool track_process)
    {
        if (track_process && is_handle_running(m_process))
        {
            Logger::debug(L"Monitor worker is already running.");
            return true;
        }

        if (track_process && m_process != nullptr)
        {
            CloseHandle(m_process);
            m_process = nullptr;
        }

        MonitorLaunchTarget launch_target;
        if (!resolve_monitor_launch_target(launch_target))
        {
            return false;
        }

        std::wstring args = L"--pid " + std::to_wstring(GetCurrentProcessId());
        if (!extra_args.empty())
        {
            args += L" ";
            args += extra_args;
        }

        std::wstring command_line = launch_target.command_prefix + L" " + args;
        STARTUPINFO startup_info = { sizeof(startup_info) };
        startup_info.dwFlags = STARTF_USESHOWWINDOW;
        startup_info.wShowWindow = SW_HIDE;
        PROCESS_INFORMATION process_info = {};

        if (!CreateProcessW(
                launch_target.application_path.c_str(),
                command_line.data(),
                nullptr,
                nullptr,
                FALSE,
                CREATE_NO_WINDOW,
                nullptr,
                launch_target.working_directory.empty() ? nullptr : launch_target.working_directory.c_str(),
                &startup_info,
                &process_info))
        {
            Logger::error(L"Failed to launch Monitor process. {}", get_last_error_or_default(GetLastError()));
            return false;
        }

        Logger::info(L"Monitor process launched successfully (PID: {}).", process_info.dwProcessId);
        if (track_process)
        {
            m_process = process_info.hProcess;
        }
        else
        {
            CloseHandle(process_info.hProcess);
        }

        CloseHandle(process_info.hThread);
        return true;
    }

    void load_run_in_background_setting()
    {
        try
        {
            PowerToysSettings::PowerToyValues values = PowerToysSettings::PowerToyValues::load_from_settings_file(get_key());
            if (auto value = values.get_bool_value(L"runInBackground"))
            {
                m_run_in_background = *value;
            }
        }
        catch (const std::exception&)
        {
            Logger::warn("Monitor failed to load runInBackground setting; keeping the current value.");
        }
    }

    void signal_exit_event()
    {
        HANDLE exit_event = CreateDefaultEvent(CommonSharedConstants::MONITOR_EXIT_EVENT);
        if (exit_event == nullptr)
        {
            Logger::warn(L"Failed to create Monitor exit event. {}", get_last_error_or_default(GetLastError()));
            return;
        }

        if (!SetEvent(exit_event))
        {
            Logger::warn(L"Failed to signal Monitor exit event. {}", get_last_error_or_default(GetLastError()));
        }

        CloseHandle(exit_event);
    }

    void stop_background_worker()
    {
        if (m_process == nullptr)
        {
            return;
        }

        if (is_handle_running(m_process))
        {
            Logger::info("Monitor background worker stopping.");
            signal_exit_event();

            constexpr DWORD timeout_ms = 1500;
            DWORD wait_result = WaitForSingleObject(m_process, timeout_ms);
            if (wait_result == WAIT_TIMEOUT)
            {
                Logger::warn("Monitor worker did not exit in time. Forcing termination.");
                TerminateProcess(m_process, 0);
            }
        }

        CloseHandle(m_process);
        m_process = nullptr;
    }

    void sync_background_worker(const bool restart_running_worker)
    {
        if (!m_enabled)
        {
            return;
        }

        if (m_run_in_background)
        {
            if (restart_running_worker)
            {
                stop_background_worker();
            }

            if (!launch_process(L"", true))
            {
                Logger::warn("Monitor background worker failed to launch; keeping Monitor enabled so the user's setting is not reset.");
            }

            return;
        }

        stop_background_worker();
    }

public:
    MonitorModuleInterface()
    {
        LoggerHelpers::init_logger(L"Monitor", L"ModuleInterface", "monitor");
        Logger::info("Monitor module interface is constructing.");
    }

    const wchar_t* get_name() override
    {
        return MODULE_NAME;
    }

    const wchar_t* get_key() override
    {
        return MODULE_NAME;
    }

    bool get_config(wchar_t* buffer, int* buffer_size) override
    {
        HINSTANCE hinstance = reinterpret_cast<HINSTANCE>(&__ImageBase);
        PowerToysSettings::Settings settings(hinstance, get_name());
        settings.set_description(MODULE_DESC);

        return settings.serialize_to_buffer(buffer, buffer_size);
    }

    void set_config(const wchar_t* config) override
    {
        try
        {
            PowerToysSettings::PowerToyValues values = PowerToysSettings::PowerToyValues::from_json_string(config, get_key());
            if (auto value = values.get_bool_value(L"runInBackground"))
            {
                m_run_in_background = *value;
            }

            values.save_to_settings_file();
            sync_background_worker(true);
        }
        catch (const std::exception&)
        {
            Logger::error("Monitor set_config failed to parse or save config.");
        }
    }

    void call_custom_action(const wchar_t* action) override
    {
        try
        {
            auto action_object = PowerToysSettings::CustomActionObject::from_json_string(action);
            if (action_object.get_name() == L"scanNow")
            {
                launch_process(L"--scan-once --use-configured-actions", false);
            }
            else if (action_object.get_name() == L"organizeDownloads")
            {
                launch_process(L"--scan-once --organize", false);
            }
            else if (action_object.get_name() == L"cleanInstallers")
            {
                launch_process(L"--scan-once --clean-installers", false);
            }
        }
        catch (const std::exception&)
        {
            Logger::error("Monitor custom action failed to parse.");
        }
    }

    void enable() override
    {
        Trace::EnableMonitor(true);
        m_enabled = true;
        load_run_in_background_setting();
        sync_background_worker(false);
    }

    void disable() override
    {
        if (m_enabled)
        {
            Logger::info("Monitor module disabling.");
        }

        stop_background_worker();
        Trace::EnableMonitor(false);
        m_enabled = false;
    }

    bool is_enabled() override
    {
        return m_enabled;
    }

    bool is_enabled_by_default() const override
    {
        return false;
    }

    powertoys_gpo::gpo_rule_configured_t gpo_policy_enabled_configuration() override
    {
        return powertoys_gpo::gpo_rule_configured_not_configured;
    }

    void destroy() override
    {
        disable();
        delete this;
    }
};

extern "C" __declspec(dllexport) PowertoyModuleIface* __cdecl powertoy_create()
{
    return new MonitorModuleInterface();
}
