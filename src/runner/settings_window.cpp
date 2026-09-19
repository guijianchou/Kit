#include "pch.h"
#include <WinSafer.h>
#include <Sddl.h>
#include <atomic>
#include <exception>
#include <sstream>
#include <thread>
#include <utility>
#include <aclapi.h>

#include "powertoy_module.h"
#include <common/interop/two_way_pipe_message_ipc.h>
#include <common/interop/shared_constants.h>
#include "tray_icon.h"
#include "UpdateUtils.h"
#include "general_settings.h"
#include "restart_elevated.h"
#include "centralized_kb_hook.h"
#include "Generated files/resource.h"
#include "hotkey_conflict_detector.h"

#include <common/utils/json.h>
#include <common/SettingsAPI/settings_helpers.cpp>
#include <common/version/version.h>
#include <common/version/helper.h>
#include <common/logger/logger.h>
#include <common/utils/resources.h>
#include <common/utils/elevation.h>
#include <common/utils/process_path.h>
#include <common/utils/timeutil.h>
#include <common/utils/winapi_error.h>
#include <common/themes/windows_colors.h>
#include "settings_window.h"
#include "ai_hub_ipc.h"

#define BUFSIZE 1024

TwoWayPipeMessageIPC* current_settings_ipc = NULL;
std::mutex ipc_mutex;
std::atomic_bool g_isLaunchInProgress = false;
std::atomic_bool isUpdateCheckThreadRunning = false;
HANDLE g_terminateSettingsEvent = CreateEventW(nullptr, false, false, CommonSharedConstants::TERMINATE_SETTINGS_SHARED_EVENT);
std::mutex settings_launch_mutex;
bool g_settings_shutdown_requested = false;
wil::unique_handle settings_shutdown_event;
// The Runner UI thread owns open/close and reaps this lifecycle thread.
// The worker takes settings_launch_mutex only until IPC and the PID are published.
std::thread settings_thread;

json::JsonObject get_power_toys_settings()
{
    json::JsonObject result;
    for (const auto& [name, powertoy] : modules())
    {
        try
        {
            result.SetNamedValue(name, powertoy.json_config());
        }
        catch (...)
        {
            Logger::error(L"get_power_toys_settings(): got malformed json for {} module", name);
        }
    }
    return result;
}

json::JsonObject get_all_settings()
{
    json::JsonObject result;

    result.SetNamedValue(L"general", get_general_settings().to_json());
    const auto moduleSettings = get_power_toys_settings();
    result.SetNamedValue(L"kit", moduleSettings);
    result.SetNamedValue(L"powertoys", moduleSettings);
    return result;
}

std::optional<std::wstring> dispatch_json_action_to_module(const json::JsonObject& powertoys_configs)
{
    std::optional<std::wstring> result;
    for (const auto& powertoy_element : powertoys_configs)
    {
        const std::wstring name{ powertoy_element.Key().c_str() };
        if (name == L"general")
        {
            try
            {
                const auto value = powertoy_element.Value().GetObjectW();
                const auto action = value.GetNamedString(L"action_name");
                if (action == L"restart_elevation")
                {
                    if (is_process_elevated())
                    {
                        schedule_restart_as_non_elevated();
                        PostQuitMessage(0);
                    }
                    else
                    {
                        schedule_restart_as_elevated(true);
                        PostQuitMessage(0);
                    }
                }
                else if (action == L"restart_maintain_elevation")
                {
                    // this was added to restart and maintain elevation, which is needed after settings are change from outside the normal process.
                    // since a normal PostQuitMessage(0) would usually cause this process to save its in memory settings to disk, we need to
                    // send a PostQuitMessage(1) and check for that on exit, and skip the settings-flush.
                    if (is_process_elevated())
                    {
                        schedule_restart_as_elevated(true);
                        PostQuitMessage(1);
                    }
                    else
                    {
                        schedule_restart_as_non_elevated(true);
                        PostQuitMessage(1);
                    }
                }
                else if (action == L"check_for_updates")
                {
                    bool expected_isUpdateCheckThreadRunning = false;
                    if (isUpdateCheckThreadRunning.compare_exchange_strong(expected_isUpdateCheckThreadRunning, true))
                    {
                        std::thread([]() {
                            try
                            {
                                CheckForUpdatesCallback();
                            }
                            catch (...)
                            {
                                Logger::error("Manual Kit update check failed before completion.");
                            }

                            isUpdateCheckThreadRunning.store(false);
                        }).detach();
                    }
                }
            }
            catch (...)
            {
            }
        }
        else if (modules().find(name) != modules().end())
        {
            const auto element = powertoy_element.Value().Stringify();
            modules().at(name)->call_custom_action(element.c_str());
        }
    }

    return result;
}

void send_json_config_to_module(const std::wstring& module_key, const std::wstring& settings, bool hotkeyUpdated)
{
    auto moduleIt = modules().find(module_key);
    if (moduleIt != modules().end())
    {
        moduleIt->second->set_config(settings.c_str());

        if (hotkeyUpdated)
        {
            moduleIt->second.remove_hotkey_records();
            moduleIt->second.update_hotkeys();
            moduleIt->second.UpdateHotkeyEx();
        }
    }
}

void dispatch_json_config_to_modules(const json::JsonObject& powertoys_configs)
{
    for (const auto& powertoy_element : powertoys_configs)
    {
        const auto element = powertoy_element.Value().Stringify();

        /* Some retained compatibility settings can opt out of hotkey refreshes unless
         * hotkey properties change, avoiding incorrect conflict detection for modules
         * whose hotkeys are not runner-registered.
         */
        auto settings = powertoy_element.Value().GetObjectW();
        bool hotkeyUpdated = true;
        if (settings.HasKey(L"properties"))
        {
            const auto properties = settings.GetNamedObject(L"properties");

            // Retained compatibility settings can use this property to avoid unnecessary hotkey refreshes.
            if (properties.HasKey(L"hotkey_changed"))
            {
                json::get(properties, L"hotkey_changed", hotkeyUpdated, true);
            }
        }
        
        send_json_config_to_module(powertoy_element.Key().c_str(), element.c_str(), hotkeyUpdated);
    }
};

void dispatch_received_json(const std::wstring& json_to_parse)
{
    json::JsonObject j;
    const bool ok = json::JsonObject::TryParse(json_to_parse, j);
    if (!ok)
    {
        Logger::error(L"dispatch_received_json: got malformed json: {}", json_to_parse);
        return;
    }

    Logger::info(L"dispatch_received_json: {}", json_to_parse);

    for (const auto& base_element : j)
    {
        const auto name = base_element.Key();
        const auto value = base_element.Value();

        if (name == L"general")
        {
            apply_general_settings(value.GetObjectW());
            // const std::wstring settings_string{ get_all_settings().Stringify().c_str() };
            // {
            //     std::unique_lock lock{ ipc_mutex };
            //     if (current_settings_ipc)
            //         current_settings_ipc->send(settings_string);
            // }
        }
        else if (name == L"module_status")
        {
            // Handle single module enable/disable update
            // Expected format: {"module_status": {"ModuleName": true/false}}
            apply_module_status_update(value.GetObjectW());
        }
        else if (name == L"powertoys" || name == L"kit")
        {
            dispatch_json_config_to_modules(value.GetObjectW());
            const std::wstring settings_string{ get_all_settings().Stringify().c_str() };
            {
                std::unique_lock lock{ ipc_mutex };
                if (current_settings_ipc)
                    current_settings_ipc->send(settings_string);
            }
        }
        else if (name == L"refresh")
        {
            const std::wstring settings_string{ get_all_settings().Stringify().c_str() };
            {
                std::unique_lock lock{ ipc_mutex };
                if (current_settings_ipc)
                    current_settings_ipc->send(settings_string);
            }
        }
        else if (name == L"action")
        {
            auto result = dispatch_json_action_to_module(value.GetObjectW());
            if (result.has_value())
            {
                {
                    std::unique_lock lock{ ipc_mutex };
                    if (current_settings_ipc)
                        current_settings_ipc->send(result.value());
                }
            }
        }
        else if (name == L"killrunner")
        {
            const auto pt_main_window = FindWindowW(pt_tray_icon_window_class, nullptr);
            if (pt_main_window != nullptr)
            {
                PostMessageW(pt_main_window, WM_CLOSE, 0, 0);
            }
        }
        else if (name == L"language")
        {
            constexpr const wchar_t* language_filename = L"\\language.json";
            const std::wstring save_file_location = PTSettingsHelper::get_root_save_folder_location() + language_filename;
            json::to_file(save_file_location, j);
        }
        else if (name == L"check_hotkey_conflict")
        {
            try
            {
                PowertoyModuleIface::Hotkey hotkey;
                hotkey.win = value.GetObjectW().GetNamedBoolean(L"win", false);
                hotkey.ctrl = value.GetObjectW().GetNamedBoolean(L"ctrl", false);
                hotkey.shift = value.GetObjectW().GetNamedBoolean(L"shift", false);
                hotkey.alt = value.GetObjectW().GetNamedBoolean(L"alt", false);
                hotkey.key = static_cast<unsigned char>(value.GetObjectW().GetNamedNumber(L"key", 0));

                std::wstring requestId = value.GetObjectW().GetNamedString(L"request_id", L"").c_str();

                auto& hkmng = HotkeyConflictDetector::HotkeyConflictManager::GetInstance();
                bool hasConflict = hkmng.HasConflict(hotkey);

                json::JsonObject response;
                response.SetNamedValue(L"response_type", json::JsonValue::CreateStringValue(L"hotkey_conflict_result"));
                response.SetNamedValue(L"request_id", json::JsonValue::CreateStringValue(requestId));
                response.SetNamedValue(L"has_conflict", json::JsonValue::CreateBooleanValue(hasConflict));

                if (hasConflict)
                {
                    auto conflicts = hkmng.GetAllConflicts(hotkey);
                    if (!conflicts.empty())
                    {
                        // Include all conflicts in the response
                        json::JsonArray allConflicts;
                        for (const auto& conflict : conflicts)
                        {
                            json::JsonObject conflictObj;
                            conflictObj.SetNamedValue(L"module", json::JsonValue::CreateStringValue(conflict.moduleName));
                            conflictObj.SetNamedValue(L"hotkeyID", json::JsonValue::CreateNumberValue(conflict.hotkeyID));
                            allConflicts.Append(conflictObj);
                        }
                        response.SetNamedValue(L"all_conflicts", allConflicts);
                    }
                }

                std::unique_lock lock{ ipc_mutex };
                if (current_settings_ipc)
                {
                    current_settings_ipc->send(response.Stringify().c_str());
                }
            }
            catch (...)
            {
                Logger::error(L"Failed to process hotkey conflict check request");
            }
        }
        else if (name == L"get_all_hotkey_conflicts")
        {
            try
            {
                auto& hkmng = HotkeyConflictDetector::HotkeyConflictManager::GetInstance();
                auto conflictsJson = hkmng.GetHotkeyConflictsAsJson();

                // Add response type identifier
                conflictsJson.SetNamedValue(L"response_type", json::JsonValue::CreateStringValue(L"all_hotkey_conflicts"));

                std::unique_lock lock{ ipc_mutex };
                if (current_settings_ipc)
                {
                    current_settings_ipc->send(conflictsJson.Stringify().c_str());
                }
            }
            catch (...)
            {
                Logger::error(L"Failed to process get all hotkey conflicts request");
            }
        }
    }
    return;
}

void dispatch_received_json_callback(PVOID data)
{
    std::wstring* msg = static_cast<std::wstring*>(data);
    dispatch_received_json(*msg);
    delete msg;
}

void receive_json_send_to_main_thread(const std::wstring& msg)
{
    if (try_complete_ai_hub_response(msg))
    {
        return;
    }

    std::wstring* copy = new std::wstring(msg);
    dispatch_run_on_main_ui_thread(dispatch_received_json_callback, copy);
}

// Try to run the Settings process with non-elevated privileges.
BOOL run_settings_non_elevated(LPCWSTR executable_path, LPWSTR executable_args, PROCESS_INFORMATION* process_info)
{
    HWND hwnd = GetShellWindow();
    if (!hwnd)
    {
        return false;
    }

    DWORD pid;
    GetWindowThreadProcessId(hwnd, &pid);

    winrt::handle process{ OpenProcess(PROCESS_CREATE_PROCESS, FALSE, pid) };
    if (!process)
    {
        return false;
    }

    SIZE_T size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
    auto pproc_buffer = std::unique_ptr<char[]>{ new (std::nothrow) char[size] };
    auto pptal = reinterpret_cast<PPROC_THREAD_ATTRIBUTE_LIST>(pproc_buffer.get());
    if (!pptal)
    {
        return false;
    }

    if (!InitializeProcThreadAttributeList(pptal, 1, 0, &size))
    {
        return false;
    }

    if (!UpdateProcThreadAttribute(pptal,
                                   0,
                                   PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                                   &process,
                                   sizeof(process),
                                   nullptr,
                                   nullptr))
    {
        return false;
    }

    STARTUPINFOEX siex = { 0 };
    siex.lpAttributeList = pptal;
    siex.StartupInfo.cb = sizeof(siex);

    BOOL process_created = CreateProcessW(executable_path,
                                          executable_args,
                                          nullptr,
                                          nullptr,
                                          FALSE,
                                          EXTENDED_STARTUPINFO_PRESENT,
                                          nullptr,
                                          nullptr,
                                          &siex.StartupInfo,
                                          process_info);
    return process_created;
}

DWORD g_settings_process_id = 0;
bool g_settings_process_closed = true;

void end_settings_ipc()
{
    std::unique_ptr<TwoWayPipeMessageIPC> settings_ipc;
    {
        std::unique_lock lock{ ipc_mutex };
        settings_ipc.reset(current_settings_ipc);
        current_settings_ipc = nullptr;
    }
    cancel_ai_hub_requests();
    if (settings_ipc)
    {
        // Input callbacks only post to the main thread. Join on the Settings
        // lifecycle thread, without holding the lock used by UI senders.
        settings_ipc->end();
    }
}

bool terminate_created_settings_process(PROCESS_INFORMATION& process_info)
{
    if (!process_info.hProcess)
    {
        return true;
    }

    DWORD wait_result = WaitForSingleObject(process_info.hProcess, 0);
    if (wait_result == WAIT_OBJECT_0)
    {
        return true;
    }
    if (wait_result == WAIT_FAILED)
    {
        Logger::warn(L"Cannot query Settings process state. {}", get_last_error_or_default(GetLastError()));
        return false;
    }

    SetEvent(g_terminateSettingsEvent);
    auto reset_exit_event = wil::scope_exit([] { ResetEvent(g_terminateSettingsEvent); });

    constexpr DWORD timeout_ms = 1500;
    wait_result = WaitForSingleObject(process_info.hProcess, timeout_ms);
    if (wait_result == WAIT_TIMEOUT)
    {
        if (!TerminateProcess(process_info.hProcess, 0))
        {
            Logger::warn(L"Failed to terminate Settings. {}", get_last_error_or_default(GetLastError()));
            return false;
        }
        wait_result = WaitForSingleObject(process_info.hProcess, timeout_ms);
    }
    if (wait_result != WAIT_OBJECT_0)
    {
        Logger::warn(L"Settings did not finish shutting down; wait result={}", wait_result);
        return false;
    }
    return true;
}

void run_settings_window(std::optional<std::wstring> settings_window)
{
    PROCESS_INFORMATION process_info = { 0 };
    HANDLE hToken = nullptr;
    wchar_t* uuid_chars = nullptr;
    bool settings_process_closed = true;
    std::unique_lock launch_lock{ settings_launch_mutex, std::defer_lock };

    try
    {
        // Keep shutdown and process creation ordered, including the interval
        // before this worker first gets scheduled and before IPC publishes the PID.
        launch_lock.lock();
        if (g_settings_shutdown_requested)
        {
            goto LExit;
        }

        // Arguments for calling the settings executable:
        // "C:\kit_path\Kit.Settings.exe" kit_pipe settings_pipe kit_pid settings_theme
        // kit_pipe: Kit pipe server.
        // settings_pipe : Settings pipe server.
        // kit_pid : Kit process pid.
        // settings_theme: pass "dark" to start the settings window in dark mode

        // Arg 1: executable path.
        std::wstring executable_path = get_module_folderpath() + L"\\WinUI3Apps\\Kit.Settings.exe";

        // Args 2,3: pipe server. Generate unique names for the pipes, if getting a UUID is possible.
        std::wstring runner_pipe_name(L"\\\\.\\pipe\\kit_runner_");
        std::wstring settings_pipe_name(L"\\\\.\\pipe\\kit_settings_");
        UUID temp_uuid;
        if (UuidCreate(&temp_uuid) == RPC_S_UUID_NO_ADDRESS)
        {
            auto val = get_last_error_message(GetLastError());
            Logger::warn(L"UuidCreate cannot create guid. {}", val.has_value() ? val.value() : L"");
        }
        else if (UuidToString(&temp_uuid, reinterpret_cast<RPC_WSTR*>(&uuid_chars)) != RPC_S_OK)
        {
            auto val = get_last_error_message(GetLastError());
            Logger::warn(L"UuidToString cannot convert to string. {}", val.has_value() ? val.value() : L"");
        }

        if (uuid_chars != nullptr)
        {
            runner_pipe_name += std::wstring(uuid_chars);
            settings_pipe_name += std::wstring(uuid_chars);
            RpcStringFree(reinterpret_cast<RPC_WSTR*>(&uuid_chars));
            uuid_chars = nullptr;
        }

        // Arg 4: process pid.
        DWORD powertoys_pid = GetCurrentProcessId();

        GeneralSettings save_settings = get_general_settings();

        // Arg 5: settings theme.
        const std::wstring settings_theme_setting{ save_settings.theme };
        std::wstring settings_theme = L"system";
        if (settings_theme_setting == L"dark" || (settings_theme_setting == L"system" && WindowsColors::is_dark_mode()))
        {
            settings_theme = L"dark";
        }

        // Arg 6: elevated status
        bool isElevated{ save_settings.isElevated };
        std::wstring settings_elevatedStatus = isElevated ? L"true" : L"false";

        // Arg 7: is user an admin
        bool isAdmin{ save_settings.isAdmin };
        std::wstring settings_isUserAnAdmin = isAdmin ? L"true" : L"false";

        // Arg 8: contains if there's a settings window argument. If true, will add one extra argument with the value to the call.
        std::wstring settings_containsSettingsWindow = settings_window.has_value() ? L"true" : L"false";

        // Args 9, .... : Optional arguments depending on the options presented before. All by the same value.

        // create general settings file to initialize the settings file with installation configurations like :
        // 1. Run on start up.
        PTSettingsHelper::save_general_settings(save_settings.to_json());

        std::wstring executable_args = fmt::format(L"\"{}\" {} {} {} {} {} {} {}",
                                                   executable_path,
                                                   runner_pipe_name,
                                                   settings_pipe_name,
                                                   std::to_wstring(powertoys_pid),
                                                   settings_theme,
                                                   settings_elevatedStatus,
                                                   settings_isUserAnAdmin,
                                                   settings_containsSettingsWindow);

        if (settings_window.has_value())
        {
            executable_args.append(L" ");
            executable_args.append(settings_window.value());
        }

        BOOL process_created = false;

        // Commented out to fix #22659
        // Running settings non-elevated and modules elevated when PowerToys is running elevated results
        // in settings making changes in one file (non-elevated user dir) and modules are reading settings
        // from different (elevated user) dir
        //if (is_process_elevated())
        //{

        //    auto res = RunNonElevatedFailsafe(executable_path, executable_args, get_module_folderpath());
        //    process_created = res.has_value();
        //    if (process_created)
        //    {
        //        process_info.dwProcessId = res->processID;
        //        process_info.hProcess = res->processHandle.release();
        //        g_isLaunchInProgress = false;
        //    }
        //}

        if (FALSE == process_created)
        {
            // The runner is not elevated or we failed to create the process using the
            // attribute list from Windows Explorer (this happens when PowerToys is executed
            // as Administrator from a non-Administrator user or an error occur trying).
            // In the second case the Settings process will run elevated.
            STARTUPINFO startup_info = { sizeof(startup_info) };
            if (!CreateProcessW(executable_path.c_str(),
                                executable_args.data(),
                                nullptr,
                                nullptr,
                                FALSE,
                                0,
                                nullptr,
                                nullptr,
                                &startup_info,
                                &process_info))
            {
                Logger::error(L"Failed to start Settings. {}", get_last_error_or_default(GetLastError()));
                goto LExit;
            }
        }
        settings_process_closed = false;
        Logger::info(L"run_settings_window: Settings process created with PID={}", process_info.dwProcessId);

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken))
        {
            Logger::error(L"Failed to open the Settings IPC token. {}", get_last_error_or_default(GetLastError()));
            goto LExit;
        }

        {
            std::unique_lock lock{ ipc_mutex };
            current_settings_ipc = new TwoWayPipeMessageIPC(runner_pipe_name, settings_pipe_name, receive_json_send_to_main_thread);
            current_settings_ipc->start(hToken);
            g_settings_process_id = process_info.dwProcessId;
            g_isLaunchInProgress = false;
        }
        launch_lock.unlock();

        // The lifecycle owner keeps the original process handle and wakes on
        // either a Settings exit or Runner shutdown, without polling.
        const HANDLE wait_handles[] = { process_info.hProcess, settings_shutdown_event.get() };
        const DWORD wait_result = WaitForMultipleObjects(2, wait_handles, FALSE, INFINITE);
        Logger::info("run_settings_window: WaitForMultipleObjects returned {}", wait_result);
        if (wait_result == WAIT_OBJECT_0)
        {
            settings_process_closed = true;
        }
        else if (wait_result != WAIT_OBJECT_0 + 1)
        {
            Logger::warn(L"Cannot wait for the Settings lifecycle; wait result={}", wait_result);
        }
    }
    catch (const std::exception& ex)
    {
        Logger::error("Settings launch setup failed. {}", ex.what());
    }
    catch (...)
    {
        Logger::error(L"Settings launch setup failed with unknown exception.");
    }

LExit:

    // Never wait for Settings or join its IPC threads while holding either lock.
    if (launch_lock.owns_lock())
    {
        launch_lock.unlock();
    }
    if (!settings_process_closed)
    {
        settings_process_closed = terminate_created_settings_process(process_info);
    }
    end_settings_ipc();

    if (uuid_chars)
    {
        RpcStringFree(reinterpret_cast<RPC_WSTR*>(&uuid_chars));
    }
    if (hToken)
    {
        CloseHandle(hToken);
    }

    {
        std::unique_lock lock{ ipc_mutex };
        g_settings_process_id = 0;
        g_isLaunchInProgress = false;
        g_settings_process_closed = settings_process_closed;
    }
    Logger::info("run_settings_window: lifecycle ended, settings_process_closed={}", settings_process_closed);

    if (process_info.hProcess)
    {
        CloseHandle(process_info.hProcess);
    }

    if (process_info.hThread)
    {
        CloseHandle(process_info.hThread);
    }
}

#define MAX_TITLE_LENGTH 100
void bring_settings_to_front()
{
    DWORD settings_process_id;
    {
        std::unique_lock lock{ ipc_mutex };
        settings_process_id = g_settings_process_id;
    }
    if (settings_process_id == 0)
    {
        return;
    }

    auto callback = [](HWND hwnd, LPARAM data) -> BOOL {
        DWORD processId;
        if (GetWindowThreadProcessId(hwnd, &processId) && processId == static_cast<DWORD>(data))
        {
            std::wstring windowTitle = L"Kit";

            WCHAR title[MAX_TITLE_LENGTH];
            int len = GetWindowTextW(hwnd, title, MAX_TITLE_LENGTH);
            if (len <= 0)
            {
                return TRUE;
            }
            if (wcsncmp(title, windowTitle.c_str(), len) == 0)
            {
                auto lStyles = GetWindowLong(hwnd, GWL_STYLE);

                if (lStyles & WS_MAXIMIZE)
                {
                    ShowWindow(hwnd, SW_MAXIMIZE);
                }
                else
                {
                    ShowWindow(hwnd, SW_RESTORE);
                }

                SetForegroundWindow(hwnd);
                return FALSE;
            }
        }

        return TRUE;
    };

    EnumWindows(callback, static_cast<LPARAM>(settings_process_id));
}

void open_settings_window(std::optional<std::wstring> settings_window)
{
    Logger::info(L"open_settings_window: called with target={}", settings_window.value_or(L"<none>"));
    std::unique_lock launch_lock{ settings_launch_mutex };
    if (g_settings_shutdown_requested)
    {
        Logger::warn("open_settings_window: g_settings_shutdown_requested is true, returning");
        return;
    }

    {
        std::unique_lock lock{ ipc_mutex };
        if (g_settings_process_id != 0)
        {
            Logger::info("open_settings_window: Settings process already running with PID={}, sending ShowYourself", g_settings_process_id);
            if (current_settings_ipc)
            {
                if (settings_window.has_value())
                {
                    std::wstring msg = L"{\"ShowYourself\":\"" + settings_window.value() + L"\"}";
                    current_settings_ipc->send(msg);
                }
                else
                {
                    current_settings_ipc->send(L"{\"ShowYourself\":\"Dashboard\"}");
                }
            }
            return;
        }
        if (g_isLaunchInProgress)
        {
            Logger::info("open_settings_window: g_isLaunchInProgress is true, returning");
            return;
        }
    }

    // A previous Settings crash or setup failure has finished cleanup. Its worker
    // no longer needs settings_launch_mutex; never hold ipc_mutex while joining.
    if (settings_thread.joinable())
    {
        Logger::info("open_settings_window: joining previous settings_thread...");
        settings_thread.join();
        Logger::info("open_settings_window: previous settings_thread joined.");
    }

    if (!settings_shutdown_event)
    {
        settings_shutdown_event.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!settings_shutdown_event)
        {
            Logger::error(L"Failed to create the Settings shutdown event. {}", get_last_error_or_default(GetLastError()));
            return;
        }
    }

    {
        std::unique_lock lock{ ipc_mutex };
        if (!g_settings_process_closed)
        {
            Logger::warn(L"Cannot reopen Settings because the previous process exit was not confirmed.");
            return;
        }
        bool expected_isLaunchInProgress = false;
        if (!g_isLaunchInProgress.compare_exchange_strong(expected_isLaunchInProgress, true))
        {
            Logger::warn("open_settings_window: compare_exchange on g_isLaunchInProgress failed, returning");
            return;
        }
    }

    Logger::info("open_settings_window: launching run_settings_window thread...");
    try
    {
        settings_thread = std::thread([settings_window = std::move(settings_window)]() mutable {
            run_settings_window(std::move(settings_window));
        });
    }
    catch (const std::exception& ex)
    {
        std::unique_lock lock{ ipc_mutex };
        g_isLaunchInProgress = false;
        Logger::error("Failed to create the Settings lifecycle thread. {}", ex.what());
    }
    catch (...)
    {
        std::unique_lock lock{ ipc_mutex };
        g_isLaunchInProgress = false;
        Logger::error(L"Failed to create the Settings lifecycle thread.");
    }
}

bool close_settings_window()
{
    {
        std::unique_lock launch_lock{ settings_launch_mutex };
        g_settings_shutdown_requested = true;
        if (settings_shutdown_event)
        {
            SetEvent(settings_shutdown_event.get());
        }
    }

    // Release the launch lock so an unscheduled worker can observe cancellation.
    // Joining also waits for child-process cleanup and all Settings IPC threads.
    if (settings_thread.joinable())
    {
        settings_thread.join();
    }

    std::unique_lock lock{ ipc_mutex };
    return g_settings_process_closed && g_settings_process_id == 0 && !g_isLaunchInProgress && current_settings_ipc == nullptr;
}

std::string ESettingsWindowNames_to_string(ESettingsWindowNames value)
{
    switch (value)
    {
    case ESettingsWindowNames::Dashboard:
        return "Dashboard";
    case ESettingsWindowNames::Overview:
        return "Overview";
    case ESettingsWindowNames::Awake:
        return "Awake";
    case ESettingsWindowNames::LightSwitch:
        return "LightSwitch";
    case ESettingsWindowNames::Localserver:
        return "Localserver";
    case ESettingsWindowNames::UDPtest:
        return "UDPtest";
    case ESettingsWindowNames::AiHub:
        return "AiHub";
    default:
    {
        Logger::error(L"Can't convert ESettingsWindowNames value={} to string", static_cast<int>(value));
        assert(false);
    }
    }
    return "";
}

ESettingsWindowNames ESettingsWindowNames_from_string(std::string value)
{
    if (value == "Dashboard")
    {
        return ESettingsWindowNames::Dashboard;
    }
    else if (value == "Overview")
    {
        return ESettingsWindowNames::Overview;
    }
    else if (value == "Awake")
    {
        return ESettingsWindowNames::Awake;
    }
    else if (value == "LightSwitch")
    {
        return ESettingsWindowNames::LightSwitch;
    }
    else if (value == "Localserver")
    {
        return ESettingsWindowNames::Localserver;
    }
    else if (value == "UDPtest")
    {
        return ESettingsWindowNames::UDPtest;
    }
    else if (value == "AiHub" || value == "AIHub")
    {
        return ESettingsWindowNames::AiHub;
    }
    else
    {
        Logger::error(L"Can't convert string value={} to ESettingsWindowNames", winrt::to_hstring(value));
        assert(false);
    }

    return ESettingsWindowNames::Dashboard;
}
