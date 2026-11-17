#include "pch.h"
#include "quick_access_host.h"

#include <mutex>
#include <string>
#include <vector>
#include <rpc.h>
#include <new>
#include <memory>
#include <atomic>
#include <condition_variable>
#include <thread>

#include <common/logger/logger.h>
#include <common/utils/process_path.h>
#include <common/utils/json.h>
#include <common/interop/two_way_pipe_message_ipc.h>
#include <wil/resource.h>

extern void receive_json_send_to_main_thread(const std::wstring& msg);

namespace
{
    void receive_quick_access_message(const std::wstring& message)
    {
        if (message.size() > 1024 * 1024) return;
        try
        {
            json::JsonObject request;
            if (!json::JsonObject::TryParse(message, request) || request.Size() != 1) return;
            if (json::has(request, L"module_status", json::JsonValueType::Object))
            {
                receive_json_send_to_main_thread(message);
            }
            else if (json::has(request, L"general", json::JsonValueType::Object))
            {
                const auto general = request.GetNamedObject(L"general");
                if (general.Size() == 1 && json::has(general, L"dashboard_sort_order", json::JsonValueType::Number))
                {
                    receive_json_send_to_main_thread(message);
                }
            }
        }
        catch (...) { Logger::warn(L"Rejected malformed Quick Access request."); }
    }

    wil::unique_handle quick_access_process;
    wil::unique_handle quick_access_job;
    wil::unique_handle show_event;
    wil::unique_handle exit_event;
    std::wstring show_event_name;
    std::wstring exit_event_name;
    std::wstring runner_pipe_name;
    std::wstring app_pipe_name;
    std::unique_ptr<TwoWayPipeMessageIPC> quick_access_ipc;
    std::mutex quick_access_mutex;
    std::atomic_bool process_running = false;

    bool is_process_active_locked()
    {
        if (!quick_access_process)
        {
            return false;
        }

        DWORD exit_code = 0;
        if (!GetExitCodeProcess(quick_access_process.get(), &exit_code))
        {
            Logger::warn(L"QuickAccessHost: failed to read Quick Access process exit code. error={}.", GetLastError());
            return false;
        }

        return exit_code == STILL_ACTIVE;
    }

    void reset_state_locked()
    {
        process_running = false;
        if (quick_access_ipc)
        {
            quick_access_ipc->end();
            quick_access_ipc.reset();
        }

        quick_access_process.reset();
        quick_access_job.reset();
        show_event.reset();
        exit_event.reset();
        show_event_name.clear();
        exit_event_name.clear();
        runner_pipe_name.clear();
        app_pipe_name.clear();
    }

    std::wstring build_event_name(const wchar_t* suffix)
    {
        std::wstring name = L"Local\\KitQuickAccess_";
        name += std::to_wstring(GetCurrentProcessId());
        if (suffix)
        {
            name += suffix;
        }
        return name;
    }

    std::wstring build_command_line(const std::wstring& exe_path)
    {
        std::wstring command_line = L"\"";
        command_line += exe_path;
        command_line += L"\" --show-event=\"";
        command_line += show_event_name;
        command_line += L"\" --exit-event=\"";
        command_line += exit_event_name;
        command_line += L"\"";
        if (!runner_pipe_name.empty())
        {
            command_line.append(L" --runner-pipe=\"");
            command_line += runner_pipe_name;
            command_line += L"\"";
        }
        if (!app_pipe_name.empty())
        {
            command_line.append(L" --app-pipe=\"");
            command_line += app_pipe_name;
            command_line += L"\"";
        }
        return command_line;
    }
    void start_process()
    {
        Logger::info(L"QuickAccessHost::start() called");
        std::scoped_lock lock(quick_access_mutex);
        if (is_process_active_locked())
        {
            process_running = true;
            Logger::info(L"QuickAccessHost::start: process already active");
            return;
        }

        reset_state_locked();

        show_event_name = build_event_name(L"_Show");
        exit_event_name = build_event_name(L"_Exit");

        show_event.reset(CreateEventW(nullptr, FALSE, FALSE, show_event_name.c_str()));
        if (!show_event)
        {
            Logger::error(L"QuickAccessHost: failed to create show event. error={}.", GetLastError());
            reset_state_locked();
            return;
        }

        exit_event.reset(CreateEventW(nullptr, FALSE, FALSE, exit_event_name.c_str()));
        if (!exit_event)
        {
            Logger::error(L"QuickAccessHost: failed to create exit event. error={}.", GetLastError());
            reset_state_locked();
            return;
        }

        runner_pipe_name = L"\\\\.\\pipe\\kit_quick_access_runner_";
        app_pipe_name = L"\\\\.\\pipe\\kit_quick_access_ui_";
        UUID temp_uuid;
        wchar_t* uuid_chars = nullptr;
        if (UuidCreate(&temp_uuid) == RPC_S_UUID_NO_ADDRESS)
        {
            Logger::warn(L"QuickAccessHost: failed to create UUID for pipe names. error={}.", GetLastError());
        }
        else if (UuidToString(&temp_uuid, reinterpret_cast<RPC_WSTR*>(&uuid_chars)) != RPC_S_OK)
        {
            Logger::warn(L"QuickAccessHost: failed to convert UUID to string. error={}.", GetLastError());
        }

        if (uuid_chars != nullptr)
        {
            runner_pipe_name += std::wstring(uuid_chars);
            app_pipe_name += std::wstring(uuid_chars);
            RpcStringFree(reinterpret_cast<RPC_WSTR*>(&uuid_chars));
            uuid_chars = nullptr;
        }
        else
        {
            const std::wstring fallback_suffix = std::to_wstring(GetTickCount64());
            runner_pipe_name += fallback_suffix;
            app_pipe_name += fallback_suffix;
        }

        HANDLE token_handle = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token_handle))
        {
            Logger::error(L"QuickAccessHost: failed to open process token. error={}.", GetLastError());
            reset_state_locked();
            return;
        }

        wil::unique_handle token(token_handle);
        quick_access_ipc.reset(new (std::nothrow) TwoWayPipeMessageIPC(runner_pipe_name, app_pipe_name, receive_quick_access_message));
        if (!quick_access_ipc)
        {
            Logger::error(L"QuickAccessHost: failed to allocate IPC instance.");
            reset_state_locked();
            return;
        }

        const std::wstring exe_path = get_module_folderpath() + L"\\WinUI3Apps\\Kit.QuickAccess.exe";
        if (GetFileAttributesW(exe_path.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            Logger::warn(L"QuickAccessHost: missing Quick Access executable at {}", exe_path);
            reset_state_locked();
            return;
        }

        const std::wstring command_line = build_command_line(exe_path);
        std::vector<wchar_t> command_line_buffer(command_line.begin(), command_line.end());
        command_line_buffer.push_back(L'\0');
        STARTUPINFOW startup_info{};
        startup_info.cb = sizeof(startup_info);
        PROCESS_INFORMATION process_info{};

        BOOL created = CreateProcessW(exe_path.c_str(), command_line_buffer.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr, nullptr, &startup_info, &process_info);
        if (!created)
        {
            Logger::error(L"QuickAccessHost: failed to launch Quick Access host. error={}.", GetLastError());
            reset_state_locked();
            return;
        }

        quick_access_process.reset(process_info.hProcess);
        const wil::unique_handle quick_access_thread{ process_info.hThread };

        try
        {
            interop_auth::CallerPolicy policy;
            policy.enabled = true;
            policy.expectedClientPid = process_info.dwProcessId;
            policy.expectedDirectory = get_module_folderpath() + L"\\WinUI3Apps";
            policy.allowedBasenames = { L"Kit.QuickAccess.exe" };
            policy.expectedVersion = interop_auth::GetOwnModuleVersion();
            policy.requireMicrosoftSignature = false; // Locally built Kit; exact child PID is pinned.
            quick_access_ipc->start(token.get(), policy);
        }
        catch (...)
        {
            TerminateProcess(quick_access_process.get(), 1);
            reset_state_locked();
            Logger::error(L"QuickAccessHost: failed to start authenticated IPC.");
            return;
        }

        // Assign to job object to ensure the process is killed if the runner exits unexpectedly (e.g. debugging stop)
        quick_access_job.reset(CreateJobObjectW(nullptr, nullptr));
        if (quick_access_job)
        {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION jeli = { 0 };
            jeli.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!SetInformationJobObject(quick_access_job.get(), JobObjectExtendedLimitInformation, &jeli, sizeof(jeli)))
            {
                Logger::warn(L"QuickAccessHost: failed to set job object information. error={}", GetLastError());
            }
            else
            {
                if (!AssignProcessToJobObject(quick_access_job.get(), quick_access_process.get()))
                {
                    Logger::warn(L"QuickAccessHost: failed to assign process to job object. error={}", GetLastError());
                }
            }
        }
        else
        {
            Logger::warn(L"QuickAccessHost: failed to create job object. error={}", GetLastError());
        }

        if (ResumeThread(quick_access_thread.get()) == static_cast<DWORD>(-1))
        {
            TerminateProcess(quick_access_process.get(), 1);
            reset_state_locked();
        }
        else
        {
            process_running = true;
        }
    }

    void stop_process(bool wait_for_process, HANDLE fast_shutdown_event)
    {
        Logger::info(L"QuickAccessHost::stop() called");
        std::unique_lock lock(quick_access_mutex);
        process_running = false;
        if (exit_event)
        {
            SetEvent(exit_event.get());
        }

        if (quick_access_process)
        {
            HANDLE wait_handles[] = { quick_access_process.get(), fast_shutdown_event };
            const auto wait_for_exit = [&](DWORD timeout) {
                return WaitForMultipleObjects(fast_shutdown_event ? 2 : 1, wait_handles, FALSE, wait_for_process ? timeout : 0);
            };
            const DWORD wait_result = wait_for_exit(2000);
            Logger::info(L"QuickAccessHost::stop: process wait result={}", wait_result);
            if (wait_result == WAIT_TIMEOUT || wait_result == WAIT_OBJECT_0 + 1)
            {
                Logger::warn(L"QuickAccessHost: Quick Access process did not exit in time, terminating.");
                if (!TerminateProcess(quick_access_process.get(), 0))
                {
                    Logger::error(L"QuickAccessHost: failed to terminate Quick Access process. error={}.", GetLastError());
                }
                else
                {
                    Logger::info(L"QuickAccessHost: TerminateProcess succeeded.");
                    wait_for_exit(5000);
                }
            }
            else if (wait_result == WAIT_FAILED)
            {
                Logger::error(L"QuickAccessHost: failed while waiting for Quick Access process. error={}.", GetLastError());
            }

            if (WaitForSingleObject(quick_access_process.get(), 0) != WAIT_OBJECT_0 &&
                (!fast_shutdown_event || WaitForSingleObject(fast_shutdown_event, 0) != WAIT_OBJECT_0))
            {
                // Retain ownership if bounded shutdown failed. A subsequent show
                // must not launch a second host while this child is still alive.
                process_running = is_process_active_locked();
                Logger::warn(L"QuickAccessHost: retaining process ownership after incomplete shutdown.");
                return;
            }
        }

        reset_state_locked();
    }

    // Declared after process state so its destructor joins before handles are destroyed.
    using QuickAccessHost::detail::Request;
    QuickAccessHost::detail::LifecycleWorker lifecycle({
        start_process,
        [] {
            std::scoped_lock lock(quick_access_mutex);
            if (show_event && !SetEvent(show_event.get()))
                Logger::warn(L"QuickAccessHost: failed to signal show event. error={}.", GetLastError());
        },
        [](HANDLE fast_shutdown_event) { stop_process(true, fast_shutdown_event); },
        [] { Logger::error(L"QuickAccessHost: lifecycle request failed."); }
    });
}

namespace QuickAccessHost
{
    bool is_running()
    {
        // State queries must not inherit the child shutdown grace period.
        std::unique_lock lock(quick_access_mutex, std::try_to_lock);
        if (!lock.owns_lock()) return process_running.load();
        process_running = is_process_active_locked();
        return process_running.load();
    }

    void start()
    {
        try { lifecycle.request(Request::Start); }
        catch (...) { Logger::error(L"QuickAccessHost: failed to queue startup."); }
    }

    void show()
    {
        try { lifecycle.request(Request::Show); }
        catch (...) { Logger::error(L"QuickAccessHost: failed to queue show."); }
    }

    void request_stop()
    {
        try { lifecycle.request(Request::Stop); }
        catch (...) { Logger::error(L"QuickAccessHost: failed to queue stop."); }
    }

    void stop(bool wait_for_process)
    {
        lifecycle.shutdown(wait_for_process);
    }
}
