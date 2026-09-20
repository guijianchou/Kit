#include "pch.h"
#include <interface/kit_module_interface.h>
#include <common/SettingsAPI/settings_objects.h>
#include <common/interop/shared_constants.h>
#include "trace.h"
#include "resource.h"
#include "AIHubConstants.h"
#include <common/logger/logger.h>
#include <common/SettingsAPI/settings_helpers.h>

#include <common/utils/elevation.h>
#include <common/utils/process_path.h>
#include <common/utils/resources.h>
#include <common/utils/os-detect.h>
#include <common/utils/winapi_error.h>

#include <filesystem>
#include <mutex>

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

const static wchar_t* MODULE_NAME = L"AI Hub";
const static wchar_t* MODULE_DESC = L"AI Hub provides local security audit and system optimization pipelines using Codex and Pi CLI.";

namespace
{
    // Must match the name the worker listens on so disable() can request a clean stop.
    const wchar_t AIHUB_WORKER_STOP_EVENT[] = L"Local\\KitAIHubWorkerStopEvent-3f8c1a52-6d47-4b9e-8a11-2c7d5e9f4b60";
}

class AIHubModule : public KitModuleIface
{
    std::wstring app_name;
    std::wstring app_key;
    bool m_enabled = false;

    // Headless audit host. It owns the scheduled rule-based audit so the cadence keeps
    // running after the Settings window closes.
    HANDLE m_worker_process{ nullptr };
    std::mutex m_lifecycle_mutex;

    void start_worker_if_needed();
    void stop_worker_if_running();
    void close_worker_handle();

public:
    AIHubModule()
    {
        app_name = GET_RESOURCE_STRING_FALLBACK(IDS_AIHUB_NAME, L"AI Hub");
        app_key = AIHubConstants::ModuleKey;
        Logger::info("AIHub module is constructing");
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

        Trace::EnableAIHub(true);
        Logger::info(L"AIHub module enabled");
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

        Trace::EnableAIHub(false);
        Logger::info(L"AIHub module disabled");
    }

    virtual bool is_enabled() override
    {
        return m_enabled;
    }
};

void AIHubModule::close_worker_handle()
{
    if (m_worker_process)
    {
        CloseHandle(m_worker_process);
        m_worker_process = nullptr;
    }
}

void AIHubModule::start_worker_if_needed()
{
    if (m_worker_process && WaitForSingleObject(m_worker_process, 0) == WAIT_TIMEOUT)
    {
        Logger::debug(L"[AIHub] Audit worker already running; skipping start.");
        return;
    }

    close_worker_handle();

    const auto module_path = get_module_filename(reinterpret_cast<HMODULE>(&__ImageBase));
    if (module_path.empty())
    {
        Logger::error(L"[AIHub] Failed to resolve the module path. {}", get_last_error_or_default(GetLastError()));
        return;
    }

    // Deployed next to the module output, following the LightSwitchService convention.
    const auto resolved_path = (std::filesystem::path(module_path).parent_path() / L"AIHubWorker" / L"Kit.AIHubWorker.exe").wstring();
    if (!std::filesystem::exists(resolved_path))
    {
        Logger::warn(L"[AIHub] Audit worker not found at {}; scheduled audits run only while Settings is open.", resolved_path);
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
        Logger::error(L"[AIHub] Failed to launch the audit worker. {}", get_last_error_or_default(GetLastError()));
        return;
    }

    Logger::info(L"[AIHub] Audit worker launched (PID: {}).", pi.dwProcessId);
    m_worker_process = pi.hProcess;
    CloseHandle(pi.hThread);
}

void AIHubModule::stop_worker_if_running()
{
    if (!m_worker_process)
    {
        return;
    }

    // The worker watches the runner PID and exits on its own; terminate as a fallback so a
    // stale supervisor cannot outlive its module.
    DWORD result = WaitForSingleObject(m_worker_process, 0);
    if (result == WAIT_TIMEOUT)
    {
        Logger::info(L"[AIHub] Stopping the audit worker.");

        // Ask the worker to drain and exit first, then terminate as a fallback so a stuck
        // worker cannot outlive the module.
        HANDLE stopEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, AIHUB_WORKER_STOP_EVENT);
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
        Logger::warn(L"[AIHub] Audit worker shutdown could not be confirmed.");
        return;
    }

    close_worker_handle();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create()
{
    return new AIHubModule();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl powertoy_create()
{
    return kit_create();
}
