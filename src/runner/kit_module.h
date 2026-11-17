#pragma once
#include <interface/kit_module_interface.h>
#include <string>
#include <memory>
#include <mutex>
#include <vector>
#include <functional>
#include <map>

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
    KitModule(KitModuleIface* module, HMODULE handle);
    ~KitModule();
    KitModule(KitModule&&) noexcept;
    KitModule& operator=(KitModule&&) noexcept;
    KitModule(const KitModule&) = delete;
    KitModule& operator=(const KitModule&) = delete;

    // Keep existing call sites, but never expose the raw DLL object to UI code.
    KitModule* operator->() { return this; }
    const KitModule* operator->() const { return this; }
    const wchar_t* get_key() const;
    const wchar_t* get_name() const;
    bool is_enabled_by_default() const;
    kit_gpo::gpo_rule_configured_t gpo_policy_enabled_configuration() const;
    // Accepted user intent persists through pending startup and recoverable crashes.
    bool is_enabled() const;
    // Last confirmed process state, distinct from the persisted switch.
    bool is_running() const;
    bool configuration_pending() const noexcept;
    void enable();
    void disable();
    void begin_shutdown() noexcept;
    void set_config(const wchar_t* config);
    void call_custom_action(const wchar_t* action);
    size_t get_hotkeys(KitModuleIface::Hotkey* buffer, size_t size) const;
    bool on_hotkey(size_t index);
    bool get_config(wchar_t* buffer, int* size) const;
    json::JsonObject json_config() const;

private:
    struct State;
    std::unique_ptr<State> state;
};

std::map<std::wstring, KitModule>& modules();
// Stop admission/retries for every module before clearing the registry joins each worker.
void begin_module_shutdown() noexcept;
bool module_configuration_pending() noexcept;
KitModule load_kit_module(const std::wstring_view filename);

struct KitModuleCatalogEntry
{
    std::wstring_view key;
    std::wstring_view filename;
};

constexpr KitModuleCatalogEntry KitModuleCatalog[] = {
    { L"Awake", L"Kit.AwakeModuleInterface.dll" },
    { L"LightSwitch", L"Kit.LightSwitchModuleInterface.dll" },
    { L"Localserver", L"Kit.LocalserverModuleInterface.dll" },
    { L"UDPtest", L"Kit.UDPtestModuleInterface.dll" },
    { L"NetMap", L"Kit.NetMapModuleInterface.dll" },
    { L"AIHub", L"Kit.AIHubModuleInterface.dll" },
    { L"Winget", L"Kit.WingetModuleInterface.dll" },
};

std::wstring_view get_module_filename_for_key(std::wstring_view key);
std::wstring_view get_module_key_for_filename(std::wstring_view filename);

// Transitional compatibility aliases
using PowertoyModule = KitModule;
using PowertoyModuleDeleter = KitModuleDeleter;
using PowertoyModuleDLLDeleter = KitModuleDLLDeleter;
inline KitModule load_powertoy(const std::wstring_view filename)
{
    return load_kit_module(filename);
}
