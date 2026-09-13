#pragma once
#include <interface/powertoy_module_interface.h>
#include <string>
#include <memory>
#include <mutex>
#include <vector>
#include <functional>
#include "hotkey_conflict_detector.h"

#include <common/utils/json.h>

struct PowertoyModuleDeleter
{
    void operator()(PowertoyModuleIface* pt_module) const
    {
        if (pt_module)
        {
            pt_module->destroy();
        }
    }
};

struct PowertoyModuleDLLDeleter
{
    using pointer = HMODULE;
    void operator()(HMODULE handle) const
    {
        FreeLibrary(handle);
    }
};

class PowertoyModule
{
public:
    PowertoyModule(PowertoyModuleIface* pt_module, HMODULE handle);

    inline PowertoyModuleIface* operator->()
    {
        return pt_module.get();
    }

    // Kit optimization: Cache frequently accessed metadata to reduce virtual call overhead
    inline const wchar_t* get_name() const { return cached_name.c_str(); }
    inline const wchar_t* get_key() const { return cached_key.c_str(); }
    inline bool is_enabled_by_default() const { return cached_default_enabled; }

    json::JsonObject json_config() const;

    void update_hotkeys();

    void UpdateHotkeyEx();

    inline void remove_hotkey_records()
    {
        hkmng.RemoveHotkeyByModule(pt_module->get_key());
    }

private:
    HotkeyConflictDetector::HotkeyConflictManager& hkmng;
    std::unique_ptr<HMODULE, PowertoyModuleDLLDeleter> handle;
    std::unique_ptr<PowertoyModuleIface, PowertoyModuleDeleter> pt_module;

    // Cached metadata
    std::wstring cached_name;
    std::wstring cached_key;
    bool cached_default_enabled;
};

PowertoyModule load_powertoy(const std::wstring_view filename);
std::map<std::wstring, PowertoyModule>& modules();
