#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cwchar>
#include <iostream>

#if defined(KIT_UPSTREAM_ABI_FIXTURE)
#include <modules/interface/powertoy_module_interface.h>

class UpstreamFixture final : public PowertoyModuleIface
{
    bool enabled = false;
    bool notified = false;

public:
    const wchar_t* get_name() override { return notified ? L"notified" : L"upstream"; }
    const wchar_t* get_key() override { return L"abi-fixture"; }
    bool get_config(wchar_t* buffer, int* size) override
    {
        if (!size) return false;
        if (!buffer || *size < 3) { *size = 3; return false; }
        wcscpy_s(buffer, *size, L"{}");
        return true;
    }
    void set_config(const wchar_t*) override {}
    void call_custom_action(const wchar_t*) override { enabled = false; }
    void enable() override { enabled = true; }
    void disable() override { enabled = false; }
    bool is_enabled() override { return enabled; }
    void destroy() override { delete this; }
    size_t get_hotkeys(Hotkey* buffer, size_t size) override
    {
        if (buffer && size > 0) buffer[0] = { true, false, true, false, 65, 37, false };
        return 1;
    }
    std::optional<HotkeyEx> GetHotkeyEx() override { return HotkeyEx{ 2, 66, 38 }; }
    void OnHotkeyEx() override { enabled = false; }
    bool on_hotkey(size_t id) override { return id == 37; }
    bool keep_track_of_pressed_win_key() override { return true; }
    UINT milliseconds_win_key_must_be_pressed() override { return 1234; }
    void send_settings_telemetry() override { notified = true; }
    bool is_enabled_by_default() const override { return false; }
    powertoys_gpo::gpo_rule_configured_t gpo_policy_enabled_configuration() override
    {
        return powertoys_gpo::gpo_rule_configured_enabled;
    }
};

extern "C" __declspec(dllexport) PowertoyModuleIface* __cdecl powertoy_create()
{
    return new UpstreamFixture();
}
#else
#include <interface/kit_module_interface.h>

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) return 2;
    HMODULE library = LoadLibraryExW(argv[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!library) return 3;
    auto create = reinterpret_cast<kit_create_func>(GetProcAddress(library, "powertoy_create"));
    if (!create || GetProcAddress(library, "kit_create")) return 4;
    KitModuleIface* module = create();
    bool valid = module && !module->is_enabled() && std::wcscmp(module->get_key(), L"abi-fixture") == 0;
    if (valid)
    {
        int capacity = 0;
        valid = !module->get_config(nullptr, &capacity) && capacity == 3;
        wchar_t config[3]{};
        valid &= module->get_config(config, &capacity) && std::wcscmp(config, L"{}") == 0;
        module->set_config(L"{}");
        module->enable();
        valid &= module->is_enabled();
        module->call_custom_action(L"{}");
        valid &= !module->is_enabled();
        KitModuleIface::Hotkey hotkey{};
        valid &= module->get_hotkeys(&hotkey, 1) == 1 && hotkey.win && hotkey.shift && !hotkey.isShown && hotkey.id == 37;
        auto extended = module->GetHotkeyEx();
        valid &= extended && extended->modifiersMask == 2 && extended->vkCode == 66 && extended->id == 38;
        module->enable();
        module->OnHotkeyEx();
        valid &= !module->is_enabled() && module->on_hotkey(37) && !module->on_hotkey(38);
        valid &= module->keep_track_of_pressed_win_key() && module->milliseconds_win_key_must_be_pressed() == 1234;
        module->send_settings_telemetry();
        valid &= std::wcscmp(module->get_name(), L"notified") == 0 && !module->is_enabled_by_default();
        valid &= module->gpo_policy_enabled_configuration() == kit_gpo::gpo_rule_configured_enabled;
        module->disable();
        valid &= !module->is_enabled();
    }
    if (module) module->destroy();
    FreeLibrary(library);
    std::cout << (valid ? "PASS" : "FAIL") << " Upstream-compiled DLL: all virtual slots, hotkey structs, legacy export, and Kit-side destruction.\n";
    return valid ? 0 : 1;
}
#endif
