#pragma once

#include <compare>
#include <optional>
#include <string>
#include <Windows.h>
#include <common/utils/gpo.h>

/*
  DLL Interface for Kit. The kit_create() (see below) must return
  an object that implements this interface.

  The Kit runner will, for each Kit module DLL:
    - load the DLL,
    - call kit_create() to create the Kit module.

  On the received object, the runner will call:
    - get_key() to get the non-localized ID of the module,
    - enable() to initialize the module.

  While running, the runner might call the following methods between kit_create()
  and destroy():
    - disable() / enable() / is_enabled() to change or query enabled state,
    - get_config() to get the available configuration settings JSON schema,
    - set_config() to update settings values,
    - call_custom_action() when a UI custom action is triggered.

  Keyboard-related types and virtual slots below are retained only for binary
  compatibility with existing modules. Kit does not register module hotkeys or
  call their keyboard callbacks.

  When terminating, the runner will:
    - call destroy() which must free all resources and delete the module instance,
    - unload the DLL.
*/

class KitModuleIface
{
public:
    /* Legacy ABI data layout; Kit does not use this to register hotkeys. */
    struct Hotkey
    {
        bool win = false;
        bool ctrl = false;
        bool shift = false;
        bool alt = false;
        unsigned char key = 0;
        // Legacy identity field; retained to preserve the structure layout.
        int id = 0;
        // Legacy visibility field; retained to preserve the structure layout.
        bool isShown = true;

        std::strong_ordering operator<=>(const Hotkey& other) const
        {
            if (auto cmp = (win <=> other.win); cmp != 0)
                return cmp;
            if (auto cmp = (ctrl <=> other.ctrl); cmp != 0)
                return cmp;
            if (auto cmp = (shift <=> other.shift); cmp != 0)
                return cmp;
            if (auto cmp = (alt <=> other.alt); cmp != 0)
                return cmp;

            return key <=> other.key;
        }

        bool operator==(const Hotkey& other) const
        {
            return win == other.win &&
                   ctrl == other.ctrl &&
                   shift == other.shift &&
                   alt == other.alt &&
                   key == other.key;
        }
    };

    // Legacy ABI data layout for the optional hotkey slot.
    struct HotkeyEx
    {
        WORD modifiersMask = 0;
        WORD vkCode = 0;
        int id = 0;
    };

    /* Returns the localized name of the module */
    virtual const wchar_t* get_name() = 0;

    /* Returns non-localized unique key of the module (e.g. L"Awake", L"LightSwitch", L"Localserver") */
    virtual const wchar_t* get_key() = 0;

    /* Fills a buffer with the available configuration settings JSON schema.
     * If 'buffer' is null or buffer size is insufficient, sets required buffer size
     * in 'buffer_size' and returns false. Returns true if successful.
     */
    virtual bool get_config(wchar_t* buffer, int* buffer_size) = 0;

    /* Sets the configuration values from JSON */
    virtual void set_config(const wchar_t* config) = 0;

    /* Call custom action from settings UI */
    virtual void call_custom_action(const wchar_t* /*action*/) {}

    /* Enables the module */
    virtual void enable() = 0;

    /* Disables the module, releasing background resources */
    virtual void disable() = 0;

    /* Returns true if the module is enabled */
    virtual bool is_enabled() = 0;

    /* Destroy the module and free all memory */
    virtual void destroy() = 0;

    /* Inert legacy vtable slot. Kit does not query or register module hotkeys. */
    virtual size_t get_hotkeys(Hotkey* /*buffer*/, size_t /*buffer_size*/)
    {
        return 0;
    }

    /* Inert legacy vtable slot; retain its position for binary compatibility. */
    virtual std::optional<HotkeyEx> GetHotkeyEx()
    {
        return std::nullopt;
    }

    /* Inert legacy callback; the Kit runner never invokes it. */
    virtual void OnHotkeyEx()
    {
    }

    /* Inert legacy callback; the Kit runner never invokes it. */
    virtual bool on_hotkey(size_t /*hotkeyId*/)
    {
        return false;
    }

    // Legacy constants retained for module source compatibility.
    static constexpr size_t WIN_KEY_HOLD_HOTKEY_ID = static_cast<size_t>(-1);

    // Preserve the upstream vtable slots. Kit does not poll the legacy Win-key hold path.
    virtual bool keep_track_of_pressed_win_key() { return false; }
    virtual UINT milliseconds_win_key_must_be_pressed() { return 0; }

    virtual void send_settings_telemetry()
    {
    }

    virtual bool is_enabled_by_default() const { return true; }

    /* Provides the GPO configuration value for the module */
    virtual kit_gpo::gpo_rule_configured_t gpo_policy_enabled_configuration()
    {
        return kit_gpo::gpo_rule_configured_not_configured;
    }

    // Legacy constant; Kit does not install the centralized keyboard hook.
    const static inline ULONG_PTR CENTRALIZED_KEYBOARD_HOOK_DONT_TRIGGER_FLAG = 0x110;

protected:
    HANDLE CreateDefaultEvent(const wchar_t* eventName)
    {
        SECURITY_ATTRIBUTES sa;
        sa.nLength = sizeof(sa);
        sa.bInheritHandle = false;
        sa.lpSecurityDescriptor = NULL;
        return CreateEventW(&sa, FALSE, FALSE, eventName);
    }
};

/*
  Typedef of the factory function that creates the Kit module object.
  Must be exported by the DLL as kit_create(), e.g.:

  extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create();
*/
typedef KitModuleIface*(__cdecl* kit_create_func)();

// Transitional compatibility aliases
using PowertoyModuleIface = KitModuleIface;
typedef kit_create_func powertoy_create_func;
