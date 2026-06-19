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

#include <wil/resource.h>

#include <string>
#include <utility>
#include <vector>

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

    enum class MonitorProcessKind
    {
        Background,
        OneShot,
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
    wil::unique_handle m_process;
    std::vector<wil::unique_handle> m_one_shot_processes;

    bool launch_process(const std::wstring& extra_args, const MonitorProcessKind process_kind)
    {
        const bool track_background = process_kind == MonitorProcessKind::Background;
        if (track_background && is_handle_running(m_process.get()))
        {
            Logger::debug(L"Monitor worker is already running.");
            return true;
        }

        if (track_background && m_process.get() != nullptr)
        {
            m_process.reset();
        }
        else if (process_kind == MonitorProcessKind::OneShot)
        {
            prune_one_shot_processes();
        }

        MonitorLaunchTarget launch_target;
        if (!resolve_monitor_launch_target(launch_target))
        {
            return false;
        }

        if (track_background)
        {
            reset_background_exit_event();
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

        wil::unique_handle process_handle{ process_info.hProcess };
        wil::unique_handle thread_handle{ process_info.hThread };

        Logger::info(L"Monitor process launched successfully (PID: {}).", process_info.dwProcessId);
        if (track_background)
        {
            m_process = std::move(process_handle);
        }
        else
        {
            m_one_shot_processes.push_back(std::move(process_handle));
        }

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
        signal_event(create_monitor_exit_event(), L"Monitor exit");
    }

    void signal_background_exit_event()
    {
        signal_event(create_monitor_background_exit_event(), L"Monitor background exit");
    }

    void signal_event(HANDLE exit_event, const wchar_t* event_description)
    {
        if (exit_event == nullptr)
        {
            Logger::warn(L"Failed to create {} event. {}", event_description, get_last_error_or_default(GetLastError()));
            return;
        }

        if (!SetEvent(exit_event))
        {
            Logger::warn(L"Failed to signal {} event. {}", event_description, get_last_error_or_default(GetLastError()));
        }

        CloseHandle(exit_event);
    }

    HANDLE create_monitor_exit_event()
    {
        SECURITY_ATTRIBUTES sa;
        sa.nLength = sizeof(sa);
        sa.bInheritHandle = false;
        sa.lpSecurityDescriptor = NULL;
        return CreateEventW(&sa, TRUE, FALSE, CommonSharedConstants::MONITOR_EXIT_EVENT);
    }

    HANDLE create_monitor_background_exit_event()
    {
        SECURITY_ATTRIBUTES sa;
        sa.nLength = sizeof(sa);
        sa.bInheritHandle = false;
        sa.lpSecurityDescriptor = NULL;
        return CreateEventW(&sa, TRUE, FALSE, CommonSharedConstants::MONITOR_BACKGROUND_EXIT_EVENT);
    }

    void reset_exit_event()
    {
        reset_event(create_monitor_exit_event(), L"Monitor exit");
    }

    void reset_background_exit_event()
    {
        reset_event(create_monitor_background_exit_event(), L"Monitor background exit");
    }

    void reset_event(HANDLE exit_event, const wchar_t* event_description)
    {
        if (exit_event == nullptr)
        {
            Logger::warn(L"Failed to create {} event for reset. {}", event_description, get_last_error_or_default(GetLastError()));
            return;
        }

        if (!ResetEvent(exit_event))
        {
            Logger::warn(L"Failed to reset {} event. {}", event_description, get_last_error_or_default(GetLastError()));
        }

        CloseHandle(exit_event);
    }

    void stop_background_worker(const bool request_exit)
    {
        if (m_process.get() == nullptr)
        {
            return;
        }

        if (is_handle_running(m_process.get()))
        {
            Logger::info("Monitor background worker stopping.");
            if (request_exit)
            {
                signal_background_exit_event();
            }

            constexpr DWORD background_stop_timeout_ms = 10000;
            DWORD wait_result = WaitForSingleObject(m_process.get(), background_stop_timeout_ms);
            if (wait_result == WAIT_TIMEOUT)
            {
                Logger::warn("Monitor worker did not exit in time. Forcing termination.");
                TerminateProcess(m_process.get(), 0);
            }
        }

        m_process.reset();
    }

    void prune_one_shot_processes()
    {
        std::vector<wil::unique_handle> running_processes;
        for (wil::unique_handle& process : m_one_shot_processes)
        {
            if (is_handle_running(process.get()))
            {
                running_processes.push_back(std::move(process));
            }
        }

        m_one_shot_processes = std::move(running_processes);
    }

    void stop_one_shot_workers()
    {
        prune_one_shot_processes();
        if (m_one_shot_processes.empty())
        {
            return;
        }

        Logger::info("Monitor one-shot workers stopping.");
        constexpr DWORD one_shot_stop_timeout_ms = 10000;
        const ULONGLONG one_shot_stop_deadline = GetTickCount64() + one_shot_stop_timeout_ms;
        for (wil::unique_handle& process : m_one_shot_processes)
        {
            if (process.get() == nullptr)
            {
                continue;
            }

            if (is_handle_running(process.get()))
            {
                const ULONGLONG now = GetTickCount64();
                const DWORD wait_timeout_ms = now >= one_shot_stop_deadline ? 0 : static_cast<DWORD>(one_shot_stop_deadline - now);
                DWORD wait_result = WaitForSingleObject(process.get(), wait_timeout_ms);
                if (wait_result == WAIT_TIMEOUT)
                {
                    Logger::warn("Monitor one-shot worker did not exit in time. Forcing termination.");
                    TerminateProcess(process.get(), 0);
                }
            }

            process.reset();
        }

        m_one_shot_processes.clear();
    }

    void stop_monitor_workers()
    {
        signal_exit_event();
        stop_background_worker(false);
        stop_one_shot_workers();
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
                stop_background_worker(true);
            }

            if (!launch_process(L"", MonitorProcessKind::Background))
            {
                Logger::warn("Monitor background worker failed to launch; keeping Monitor enabled so the user's setting is not reset.");
            }

            return;
        }

        stop_background_worker(true);
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
        if (!m_enabled)
        {
            Logger::warn("Monitor custom action ignored because the module is disabled.");
            return;
        }

        try
        {
            auto action_object = PowerToysSettings::CustomActionObject::from_json_string(action);
            if (action_object.get_name() == L"scanNow")
            {
                std::wstring args = L"--scan-once --use-configured-actions";
                std::wstring scan_id = action_object.get_value();
                if (!scan_id.empty())
                {
                    args += L" --scan-id ";
                    args += quote_argument(scan_id);
                }

                launch_process(args, MonitorProcessKind::OneShot);
            }
            else if (action_object.get_name() == L"organizeDownloads")
            {
                launch_process(L"--scan-once --organize", MonitorProcessKind::OneShot);
            }
            else if (action_object.get_name() == L"cleanInstallers")
            {
                launch_process(L"--scan-once --clean-installers", MonitorProcessKind::OneShot);
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
        reset_exit_event();
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

        stop_monitor_workers();
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
