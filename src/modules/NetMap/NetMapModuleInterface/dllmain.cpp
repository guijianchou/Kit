#include "pch.h"
#include <interface/kit_module_interface.h>
#include <common/SettingsAPI/settings_objects.h>

extern "C" IMAGE_DOS_HEADER __ImageBase;

class NetMapModule final : public KitModuleIface
{
    bool enabled = false;

public:
    const wchar_t* get_name() override { return L"NetMap"; }
    const wchar_t* get_key() override { return L"NetMap"; }
    bool is_enabled_by_default() const override { return false; }
    bool is_enabled() override { return enabled; }
    void enable() override { enabled = true; }
    void disable() override { enabled = false; }
    void destroy() override { disable(); delete this; }

    bool get_config(wchar_t* buffer, int* size) override
    {
        KitSettings::Settings settings(reinterpret_cast<HINSTANCE>(&__ImageBase), get_name());
        settings.set_description(L"Observe direct and proxy egress, endpoint responses and local routes.");
        return settings.serialize_to_buffer(buffer, size);
    }

    void set_config(const wchar_t* config) override
    {
        try
        {
            auto values = KitSettings::PowerToyValues::from_json_string(config, get_key());
            values.save_to_settings_file();
        }
        catch (...)
        {
            // Never propagate malformed settings across the native module boundary.
        }
    }
};

extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create()
{
    return new NetMapModule();
}
