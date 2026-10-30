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
#include <common/utils/KitWorkerProcessManager.h>

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

    KitWorkerProcessManager m_worker{ L"UDPtest", L"UDPtestService", L"Kit.UDPtestService.exe", CommonSharedConstants::UDPTEST_EXIT_EVENT };
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

    virtual bool is_enabled_by_default() const override
    {
        return false;
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
    m_worker.close_handle();
}

void UDPtestModule::start_worker_if_needed()
{
    m_worker.start(reinterpret_cast<HMODULE>(&__ImageBase));
}

void UDPtestModule::stop_worker_if_running()
{
    m_worker.stop();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create()
{
    return new UDPtestModule();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl powertoy_create()
{
    return kit_create();
}
