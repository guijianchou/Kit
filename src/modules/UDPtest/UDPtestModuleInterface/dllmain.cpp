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

public:
    UDPtestModule()
    {
        app_name = GET_RESOURCE_STRING(IDS_UDPTEST_NAME);
        app_key = UDPtestConstants::ModuleKey;
        Logger::info("UDPtest module is constructing");
    }

    virtual kit_gpo::gpo_rule_configured_t gpo_policy_enabled_configuration() override
    {
        return kit_gpo::gpo_rule_configured_not_configured;
    }

    virtual void destroy() override
    {
        disable();
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

        m_enabled = true;
        Trace::EnableUDPtest(true);
        Logger::info(L"UDPtest module enabled");
    }

    virtual void disable() override
    {
        if (!m_enabled)
        {
            return;
        }

        m_enabled = false;
        Trace::EnableUDPtest(false);
        Logger::info(L"UDPtest module disabled");
    }

    virtual bool is_enabled() override
    {
        return m_enabled;
    }
};

extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create()
{
    return new UDPtestModule();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl powertoy_create()
{
    return kit_create();
}
