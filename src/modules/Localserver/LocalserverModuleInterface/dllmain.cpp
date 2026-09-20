#include "pch.h"
#include <interface/kit_module_interface.h>
#include <common/SettingsAPI/settings_objects.h>
#include <common/interop/shared_constants.h>
#include "trace.h"
#include "resource.h"
#include "LocalserverConstants.h"
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

const static wchar_t* MODULE_NAME = L"Localserver";
const static wchar_t* MODULE_DESC = L"A module that manages local servers, microservices, and development environments.";

class LocalserverModule : public KitModuleIface
{
    std::wstring app_name;
    std::wstring app_key;
    bool m_enabled = false;

    // Headless supervisor process. It hosts the service runners so health monitoring and
    // restart policy survive the Settings window closing.
    HANDLE m_worker_process{ nullptr };
    std::mutex m_lifecycle_mutex;

    void start_worker_if_needed();
    void stop_worker_if_running();
    void close_worker_handle();

public:
    LocalserverModule()
    {
        app_name = GET_RESOURCE_STRING(IDS_LOCALSERVER_NAME);
        app_key = LocalserverConstants::ModuleKey;
        Logger::info("Localserver module is constructing");
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
        return MODULE_NAME;
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

        Trace::EnableLocalserver(true);
        Logger::info(L"Localserver module enabled");
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

        Trace::EnableLocalserver(false);
        Logger::info(L"Localserver module disabled");
    }

    virtual bool is_enabled() override
    {
        return m_enabled;
    }
};

void LocalserverModule::close_worker_handle()
{
    if (m_worker_process)
    {
        CloseHandle(m_worker_process);
        m_worker_process = nullptr;
    }
}

void LocalserverModule::start_worker_if_needed()
{
    if (m_worker_process && WaitForSingleObject(m_worker_process, 0) == WAIT_TIMEOUT)
    {
        Logger::debug(L"[Localserver] Supervisor already running; skipping start.");
        return;
    }

    close_worker_handle();

    const auto module_path = get_module_filename(reinterpret_cast<HMODULE>(&__ImageBase));
    if (module_path.empty())
    {
        Logger::error(L"[Localserver] Failed to resolve the module path. {}", get_last_error_or_default(GetLastError()));
        return;
    }

    // The worker is deployed next to the module output, following the LightSwitchService
    // convention. It also accepts a data-directory override for a non-default catalog.
    const auto resolved_path = (std::filesystem::path(module_path).parent_path() / L"LocalserverWorker" / L"Kit.LocalserverWorker.exe").wstring();
    if (!std::filesystem::exists(resolved_path))
    {
        Logger::warn(L"[Localserver] Supervisor executable not found at {}; services will only be supervised while Settings is open.", resolved_path);
        return;
    }

    const std::wstring args = L"--pid " + std::to_wstring(GetCurrentProcessId());
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
        Logger::error(L"[Localserver] Failed to launch the supervisor. {}", get_last_error_or_default(GetLastError()));
        return;
    }

    Logger::info(L"[Localserver] Supervisor launched (PID: {}).", pi.dwProcessId);
    m_worker_process = pi.hProcess;
    CloseHandle(pi.hThread);
}

void LocalserverModule::stop_worker_if_running()
{
    if (!m_worker_process)
    {
        return;
    }

    // The worker watches the runner PID and exits on its own. Ask nicely, then fall back
    // to terminating it so a stale supervisor cannot outlive its module.
    DWORD result = WaitForSingleObject(m_worker_process, 0);
    if (result == WAIT_TIMEOUT)
    {
        Logger::info(L"[Localserver] Stopping the supervisor.");
        if (TerminateProcess(m_worker_process, 0))
        {
            result = WaitForSingleObject(m_worker_process, 1500);
        }
    }

    if (result != WAIT_OBJECT_0)
    {
        Logger::warn(L"[Localserver] Supervisor shutdown could not be confirmed.");
        return;
    }

    close_worker_handle();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create()
{
    return new LocalserverModule();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl powertoy_create()
{
    return kit_create();
}
