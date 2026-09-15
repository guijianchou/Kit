#pragma once
#include "HotkeyManager.g.h"

namespace winrt::Kit::Interop::implementation
{
    struct HotkeyManager : HotkeyManagerT<HotkeyManager>
    {
        HotkeyManager();

        uint16_t RegisterHotkey(winrt::Kit::Interop::Hotkey const& _hotkey, winrt::Kit::Interop::HotkeyCallback const& _callback);
        void UnregisterHotkey(uint16_t _handle);
        void Close();

    private:
        KeyboardHook keyboardHook{ nullptr };
        std::map<uint16_t, HotkeyCallback> hotkeys;
        Hotkey pressedKeys{ };
        KeyboardEventCallback keyboardEventCallback;
        IsActiveCallback isActiveCallback;
        FilterKeyboardEvent filterKeyboardCallback;

        void KeyboardEventProc(KeyboardEvent ev);
        bool IsActiveProc();
        bool FilterKeyboardProc(KeyboardEvent ev);
        uint16_t GetHotkeyHandle(Hotkey hotkey);
    };
}
namespace winrt::Kit::Interop::factory_implementation
{
    struct HotkeyManager : HotkeyManagerT<HotkeyManager, implementation::HotkeyManager>
    {
    };
}
