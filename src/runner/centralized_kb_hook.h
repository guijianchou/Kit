#include "pch.h"

#include "../modules/interface/kit_module_interface.h"

namespace CentralizedKeyboardHook
{
    using Hotkey = KitModuleIface::Hotkey;

    void Start() noexcept;
    void Stop() noexcept;
    void SetHotkeyAction(const std::wstring& moduleName, const Hotkey& hotkey, std::function<bool()>&& action) noexcept;
    void ClearModuleHotkeys(const std::wstring& moduleName) noexcept;
};
