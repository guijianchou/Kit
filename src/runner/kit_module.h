#pragma once
#include <interface/kit_module_interface.h>
#include <string>
#include <memory>
#include <mutex>
#include <vector>
#include <functional>
#include <map>
#include "hotkey_conflict_detector.h"

#include <common/utils/json.h>

struct KitModuleDeleter
{
    void operator()(KitModuleIface* pt_module) const
    {
        if (pt_module)
        {
            pt_module->destroy();
        }
    }
};

struct KitModuleDLLDeleter
{
    using pointer = HMODULE;
    void operator()(HMODULE handle) const
    {
        FreeLibrary(handle);
    }
};

class KitModule
{
public:
    KitModule(KitModuleIface* pt_module, HMODULE handle);

    inline KitModuleIface* operator->()
    {
        return pt_module.get();
    }

    json::JsonObject json_config() const;

    void update_hotkeys();

    void UpdateHotkeyEx();

    inline void remove_hotkey_records()
    {
        hkmng.RemoveHotkeyByModule(pt_module->get_key());
    }

private:
    HotkeyConflictDetector::HotkeyConflictManager& hkmng;
    std::unique_ptr<HMODULE, KitModuleDLLDeleter> handle;
    std::unique_ptr<KitModuleIface, KitModuleDeleter> pt_module;
};

std::map<std::wstring, KitModule>& modules();
KitModule load_kit_module(const std::wstring_view filename);

// Transitional compatibility aliases
using PowertoyModule = KitModule;
using PowertoyModuleDeleter = KitModuleDeleter;
using PowertoyModuleDLLDeleter = KitModuleDLLDeleter;
inline KitModule load_powertoy(const std::wstring_view filename)
{
    return load_kit_module(filename);
}
