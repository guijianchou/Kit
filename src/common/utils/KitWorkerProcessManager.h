#pragma once

#include <Windows.h>
#include <string>
#include <filesystem>
#include <mutex>
#include <common/logger/logger.h>
#include <common/utils/process_path.h>
#include <common/utils/winapi_error.h>

class KitWorkerProcessManager
{
public:
    KitWorkerProcessManager(
        std::wstring moduleTag,
        std::wstring subFolder,
        std::wstring exeName,
        std::wstring exitEventName) :
        m_moduleTag(std::move(moduleTag)),
        m_subFolder(std::move(subFolder)),
        m_exeName(std::move(exeName)),
        m_exitEventName(std::move(exitEventName))
    {
    }

    ~KitWorkerProcessManager()
    {
        close_handle();
    }

    KitWorkerProcessManager(const KitWorkerProcessManager&) = delete;
    KitWorkerProcessManager& operator=(const KitWorkerProcessManager&) = delete;

    void start(HMODULE moduleInstance, const std::wstring& extraArgs = L"")
    {
        std::lock_guard lock(m_mutex);
        if (m_process && WaitForSingleObject(m_process, 0) == WAIT_TIMEOUT)
        {
            Logger::debug(L"[{}] Worker process already running; skipping start.", m_moduleTag);
            return;
        }

        close_handle_locked();

        const auto module_path = get_module_filename(moduleInstance);
        if (module_path.empty())
        {
            Logger::error(L"[{}] Failed to resolve module path. {}", m_moduleTag, get_last_error_or_default(GetLastError()));
            return;
        }

        auto resolved_path = (std::filesystem::path(module_path).parent_path() / m_exeName).wstring();
        if (!std::filesystem::exists(resolved_path) && !m_subFolder.empty())
        {
            const auto subfolder_path = (std::filesystem::path(module_path).parent_path() / m_subFolder / m_exeName).wstring();
            if (std::filesystem::exists(subfolder_path))
            {
                resolved_path = subfolder_path;
            }
            else
            {
                Logger::warn(L"[{}] Worker executable not found at {} or {}.", m_moduleTag, resolved_path, subfolder_path);
                return;
            }
        }

        std::wstring args = L"--use-kit-config --pid " + std::to_wstring(GetCurrentProcessId());
        if (!extraArgs.empty())
        {
            args += L" " + extraArgs;
        }
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
            Logger::error(L"[{}] Failed to launch worker process. {}", m_moduleTag, get_last_error_or_default(GetLastError()));
            return;
        }

        Logger::info(L"[{}] Worker launched (PID: {}).", m_moduleTag, pi.dwProcessId);
        m_process = pi.hProcess;
        CloseHandle(pi.hThread);
    }

    void stop(DWORD waitTimeoutMs = 2000, DWORD fallbackTimeoutMs = 1500)
    {
        std::lock_guard lock(m_mutex);
        if (!m_process)
        {
            return;
        }

        DWORD result = WaitForSingleObject(m_process, 0);
        if (result == WAIT_TIMEOUT)
        {
            Logger::info(L"[{}] Stopping worker process.", m_moduleTag);
            if (!m_exitEventName.empty())
            {
                HANDLE stopEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, m_exitEventName.c_str());
                if (!stopEvent)
                {
                    stopEvent = CreateEventW(nullptr, FALSE, FALSE, m_exitEventName.c_str());
                }
                if (stopEvent)
                {
                    SetEvent(stopEvent);
                    CloseHandle(stopEvent);
                    result = WaitForSingleObject(m_process, waitTimeoutMs);
                }
            }

            if (result == WAIT_TIMEOUT && TerminateProcess(m_process, 0))
            {
                result = WaitForSingleObject(m_process, fallbackTimeoutMs);
            }
        }

        if (result != WAIT_OBJECT_0)
        {
            Logger::warn(L"[{}] Worker shutdown could not be confirmed.", m_moduleTag);
        }

        close_handle_locked();
    }

    bool is_running()
    {
        std::lock_guard lock(m_mutex);
        return m_process && WaitForSingleObject(m_process, 0) == WAIT_TIMEOUT;
    }

    void close_handle()
    {
        std::lock_guard lock(m_mutex);
        close_handle_locked();
    }

private:
    void close_handle_locked()
    {
        if (m_process)
        {
            CloseHandle(m_process);
            m_process = nullptr;
        }
    }

    std::wstring m_moduleTag;
    std::wstring m_subFolder;
    std::wstring m_exeName;
    std::wstring m_exitEventName;
    HANDLE m_process = nullptr;
    std::mutex m_mutex;
};
