#include "pch.h"
#include "module_hotkeys.h"
#include "kit_module.h"
#include <common/logger/logger.h>
#include <array>
#include <map>

namespace
{
    struct Binding
    {
        std::wstring module;
        size_t index;
        UINT modifiers;
        UINT key;
    };

    HWND ownerWindow = nullptr;
    DWORD ownerThread = 0;
    int nextId = 0x6000;
    std::map<int, Binding> bindings;

    bool OnOwnerThread()
    {
        return ownerWindow && ownerThread == GetCurrentThreadId();
    }
}

void initialize_module_hotkeys(HWND window)
{
    ownerWindow = window;
    ownerThread = GetCurrentThreadId();
    refresh_module_hotkeys();
}

void clear_module_hotkeys()
{
    if (!OnOwnerThread())
        return;

    for (const auto& [id, binding] : bindings)
        UnregisterHotKey(ownerWindow, id);
    bindings.clear();
}

void refresh_module_hotkeys()
{
    if (!OnOwnerThread())
        return;

    clear_module_hotkeys();
    for (auto& [name, module] : modules())
    {
        try
        {
            if (!module->is_enabled())
                continue;

            std::array<KitModuleIface::Hotkey, 32> hotkeys{};
            const auto count = module->get_hotkeys(hotkeys.data(), hotkeys.size());
            if (count > hotkeys.size())
            {
                Logger::warn(L"Module {} returned too many shortcuts.", name);
                continue;
            }
            for (size_t index = 0; index < count; ++index)
            {
                const auto& hotkey = hotkeys[index];
                if (!hotkey.key)
                    continue;
                const UINT modifiers = (hotkey.win ? MOD_WIN : 0) | (hotkey.ctrl ? MOD_CONTROL : 0) |
                                       (hotkey.shift ? MOD_SHIFT : 0) | (hotkey.alt ? MOD_ALT : 0);
                const int id = nextId++;
                if (nextId > 0xBFFF)
                    nextId = 0x6000;
                if (RegisterHotKey(ownerWindow, id, modifiers | MOD_NOREPEAT, hotkey.key))
                    bindings.emplace(id, Binding{ name, index, modifiers, hotkey.key });
                else
                    Logger::warn(L"Could not register shortcut {} for module {} (error {}).", index, name, GetLastError());
            }
        }
        catch (...)
        {
            Logger::error(L"Could not register shortcuts for module {}.", name);
        }
    }
}

bool dispatch_module_hotkey(WPARAM id, LPARAM keyData)
{
    if (!OnOwnerThread())
        return false;

    const auto binding = bindings.find(static_cast<int>(id));
    if (binding == bindings.end() || LOWORD(keyData) != binding->second.modifiers || HIWORD(keyData) != binding->second.key)
        return false;
    const auto module = modules().find(binding->second.module);
    if (module == modules().end())
        return false;
    try
    {
        return module->second->is_enabled() && module->second->on_hotkey(binding->second.index);
    }
    catch (...)
    {
        Logger::error(L"Module {} shortcut failed.", binding->second.module);
        return false;
    }
}
