#include "pch.h"
#include <interface/kit_module_interface.h>
#include <common/SettingsAPI/settings_objects.h>
#include <common/interop/shared_constants.h>
#include "trace.h"
#include "resource.h"
#include "UDPtestConstants.h"
#include <common/logger/logger.h>
#include <common/SettingsAPI/settings_helpers.h>

#include <common/utils/elevation.h>
#include <common/utils/process_path.h>
#include <common/utils/resources.h>
#include <common/utils/os-detect.h>
#include <common/utils/winapi_error.h>

#include <filesystem>
#include <mutex>
#include <set>

extern "C" IMAGE_DOS_HEADER __ImageBase;

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

const static wchar_t* MODULE_NAME = L"UDPtest";
const static wchar_t* MODULE_DESC = L"A module that monitors UDP and TCP connectivity, link latency, jitter, packet loss, and NAT types.";

class UDPtestModule : public KitModuleIface
{
    std::wstring app_name;
    std::wstring app_key;
    bool m_enabled = false;

    HANDLE m_worker_process{ nullptr };
    std::mutex m_lifecycle_mutex;

    void start_worker_if_needed();
    void stop_worker_if_running();
    void close_worker_handle();

public:
    UDPtestModule()
    {
        app_name = GET_RESOURCE_STRING(IDS_UDPTEST_NAME);
        app_key = UDPtestConstants::ModuleKey;
        std::filesystem::path logFilePath(PTSettingsHelper::get_module_save_folder_location(this->app_key));
        logFilePath.append(LogSettings::udptestLogPath);
        Logger::init(LogSettings::udptestLoggerName, logFilePath.wstring(), PTSettingsHelper::get_log_settings_file_location());
        Logger::info("UDPtest module is constructing");
    }

    virtual kit_gpo::gpo_rule_configured_t gpo_policy_enabled_configuration() override
    {
        return kit_gpo::gpo_rule_configured_not_configured;
    }

    virtual void destroy() override
    {
        disable();
        close_worker_handle();
        delete this;
    }

    virtual const wchar_t* get_name() override
    {
        return app_name.c_str();
    }

    virtual bool get_config(wchar_t* buffer, int* buffer_size) override
    {
        HINSTANCE hinstance = reinterpret_cast<HINSTANCE>(&__ImageBase);

        KitSettings::Settings settings(hinstance, get_name());
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
            KitSettings::PowerToyValues values =
                KitSettings::PowerToyValues::from_json_string(config, get_key());
            values.save_to_settings_file();

            auto refreshEvent = CreateEventW(nullptr, false, false, CommonSharedConstants::UDPTEST_REFRESH_SETTINGS_EVENT);
            if (refreshEvent)
            {
                SetEvent(refreshEvent);
                CloseHandle(refreshEvent);
            }
        }
        catch (std::exception&)
        {
        }
    }

    virtual void enable() override
    {
        if (m_enabled)
        {
            return;
        }

        {
            std::lock_guard lifecycleLock(m_lifecycle_mutex);
            m_enabled = true;
            start_worker_if_needed();
        }

        Trace::EnableUDPtest(true);
        Logger::info(L"UDPtest module enabled");
    }

    virtual void disable() override
    {
        if (!m_enabled)
        {
            return;
        }

        {
            std::lock_guard lifecycleLock(m_lifecycle_mutex);
            m_enabled = false;
            stop_worker_if_running();
        }

        Trace::EnableUDPtest(false);
        Logger::info(L"UDPtest module disabled");
    }

    virtual bool is_enabled() override
    {
        return m_enabled;
    }
};

void UDPtestModule::close_worker_handle()
{
    if (m_worker_process)
    {
        CloseHandle(m_worker_process);
        m_worker_process = nullptr;
    }
}

void UDPtestModule::start_worker_if_needed()
{
    if (m_worker_process && WaitForSingleObject(m_worker_process, 0) == WAIT_TIMEOUT)
    {
        Logger::debug(L"[UDPtest] Service already running; skipping start.");
        return;
    }

    close_worker_handle();

    const auto module_path = get_module_filename(reinterpret_cast<HMODULE>(&__ImageBase));
    if (module_path.empty())
    {
        Logger::error(L"[UDPtest] Failed to resolve the module path. {}", get_last_error_or_default(GetLastError()));
        return;
    }

    const auto resolved_path = (std::filesystem::path(module_path).parent_path() / L"UDPtestService" / L"Kit.UDPtestService.exe").wstring();
    if (!std::filesystem::exists(resolved_path))
    {
        Logger::warn(L"[UDPtest] Service executable not found at {}.", resolved_path);
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
        Logger::error(L"[UDPtest] Failed to launch the service. {}", get_last_error_or_default(GetLastError()));
        return;
    }

    Logger::info(L"[UDPtest] Service launched (PID: {}).", pi.dwProcessId);
    m_worker_process = pi.hProcess;
    CloseHandle(pi.hThread);
}

void UDPtestModule::stop_worker_if_running()
{
    if (!m_worker_process)
    {
        return;
    }

    DWORD result = WaitForSingleObject(m_worker_process, 0);
    if (result == WAIT_TIMEOUT)
    {
        Logger::info(L"[UDPtest] Stopping the service.");

        HANDLE stopEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, CommonSharedConstants::UDPTEST_EXIT_EVENT);
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
        Logger::warn(L"[UDPtest] Service shutdown could not be confirmed.");
        return;
    }

    close_worker_handle();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create()
{
    return new UDPtestModule();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl powertoy_create()
{
    return kit_create();
}
