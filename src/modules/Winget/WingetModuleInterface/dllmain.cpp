#include "pch.h"
#include "WingetConstants.h"
#include "resource.h"
#include <interface/kit_module_interface.h>
#include <common/SettingsAPI/settings_objects.h>
#include <common/SettingsAPI/settings_helpers.h>
#include <common/logger/logger.h>
#include <common/utils/process_path.h>
#include <common/utils/resources.h>
#include <filesystem>
#include <mutex>
#include <thread>

extern "C" IMAGE_DOS_HEADER __ImageBase;

BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID)
{
    return TRUE;
}

class WingetModule final : public KitModuleIface
{
    const std::wstring m_name = GET_RESOURCE_STRING_FALLBACK(IDS_WINGET_NAME, L"Winget");
    std::mutex m_lifecycle;
    std::thread m_startup;
    HANDLE m_process = nullptr;
    HANDLE m_exit = nullptr;
    bool m_enabled = false;

    void close_process()
    {
        if (m_process)
        {
            CloseHandle(m_process);
            m_process = nullptr;
        }
    }

    void start_worker()
    {
        try
        {
            auto modulePath = get_module_filename(reinterpret_cast<HMODULE>(&__ImageBase));
            if (modulePath.empty())
            {
                Logger::error(L"[Winget] Cannot resolve the module directory.");
                return;
            }

            const auto executable = std::filesystem::path(modulePath).parent_path() / L"Kit.WingetService.exe";
            const auto directory = executable.parent_path().wstring();
            std::wstring command = L"\"" + executable.wstring() + L"\" --use-kit-config --pid " + std::to_wstring(GetCurrentProcessId());
            STARTUPINFOW startup{ sizeof(startup) };
            PROCESS_INFORMATION process{};
            // Inherit the Runner's current token. No elevation, shell lookup, or service
            // installation is used; individual installers requiring admin remain manual.
            if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
                                CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, nullptr, directory.c_str(), &startup, &process))
            {
                Logger::error(L"[Winget] Cannot start service (Win32 error {}).", GetLastError());
                return;
            }

            m_process = process.hProcess;
            CloseHandle(process.hThread);
            Logger::info(L"[Winget] Service started (PID {}).", process.dwProcessId);
        }
        catch (...)
        {
            Logger::error(L"[Winget] Service startup failed.");
        }
    }

    void stop_worker()
    {
        if (m_exit)
        {
            SetEvent(m_exit);
        }

        if (m_process)
        {
            // The service cancels its active WinGet operation before exiting. Keep the
            // fallback bounded so a broken worker cannot survive a disabled module.
            DWORD wait = WaitForSingleObject(m_process, 5000);
            if (wait == WAIT_TIMEOUT)
            {
                Logger::warn(L"[Winget] Service did not drain after cancellation; terminating its host.");
                TerminateProcess(m_process, 0);
                WaitForSingleObject(m_process, 1000);
            }

            close_process();
        }
    }

public:
    WingetModule()
    {
        auto path = std::filesystem::path(PTSettingsHelper::get_module_save_folder_location(WingetConstants::ModuleKey)) / L"Logs" / L"winget-log.log";
        Logger::init("winget", path.wstring(), PTSettingsHelper::get_log_settings_file_location());
    }

    const wchar_t* get_name() override { return m_name.c_str(); }
    const wchar_t* get_key() override { return WingetConstants::ModuleKey; }
    bool is_enabled_by_default() const override { return false; }

    bool get_config(wchar_t* buffer, int* bufferSize) override
    {
        KitSettings::Settings settings(reinterpret_cast<HINSTANCE>(&__ImageBase), get_name());
        settings.set_description(L"Review application updates with Windows Package Manager.");
        return settings.serialize_to_buffer(buffer, bufferSize);
    }

    void set_config(const wchar_t* config) override
    {
        try
        {
            auto values = KitSettings::PowerToyValues::from_json_string(config, get_key());
            values.save_to_settings_file();
            HANDLE refresh = CreateEventW(nullptr, FALSE, FALSE, WingetConstants::RefreshEvent);
            if (refresh)
            {
                SetEvent(refresh);
                CloseHandle(refresh);
            }
        }
        catch (...)
        {
            Logger::warn(L"[Winget] Invalid settings were ignored.");
        }
    }

    void enable() override
    {
        std::lock_guard lock(m_lifecycle);
        if (m_startup.joinable())
        {
            m_startup.join();
        }

        if (m_enabled && m_process && WaitForSingleObject(m_process, 0) == WAIT_TIMEOUT)
        {
            return;
        }

        close_process();
        if (!m_exit)
        {
            m_exit = CreateEventW(nullptr, TRUE, FALSE, WingetConstants::ExitEvent);
        }

        if (!m_exit)
        {
            Logger::error(L"[Winget] Cannot create the service exit event (Win32 error {}).", GetLastError());
            return;
        }

        ResetEvent(m_exit);
        try
        {
            m_startup = std::thread([this] { start_worker(); });
            m_enabled = true;
        }
        catch (...)
        {
            m_enabled = false;
            Logger::error(L"[Winget] Cannot schedule service startup.");
        }
    }

    void disable() override
    {
        std::lock_guard lock(m_lifecycle);
        m_enabled = false;
        if (m_startup.joinable())
        {
            m_startup.join();
        }

        stop_worker();
    }

    bool is_enabled() override
    {
        std::lock_guard lock(m_lifecycle);
        return m_enabled;
    }

    void destroy() override
    {
        disable();
        if (m_exit)
        {
            CloseHandle(m_exit);
            m_exit = nullptr;
        }

        delete this;
    }
};

extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create()
{
    return new WingetModule();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl powertoy_create()
{
    return kit_create();
}
