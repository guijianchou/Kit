#include "pch.h"
#include "kit_module.h"
#include "centralized_kb_hook.h"
#include "centralized_hotkeys.h"
#include <common/logger/logger.h>
#include <common/utils/process_path.h>
#include <common/utils/winapi_error.h>
#include <filesystem>

std::map<std::wstring, KitModule>& modules()
{
    static std::map<std::wstring, KitModule> modules_map;
    return modules_map;
}

KitModule load_kit_module(const std::wstring_view filename)
{
    const auto runnerDirectory = std::filesystem::path(get_module_filename()).parent_path();
    const auto modulePath = runnerDirectory / filename;
    std::unique_ptr<HMODULE, KitModuleDLLDeleter> handle(winrt::check_pointer(LoadLibraryExW(
        modulePath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)));

    // Prefer kit_create export, fall back to legacy powertoy_create for smooth migration
    auto create = reinterpret_cast<kit_create_func>(GetProcAddress(handle.get(), "kit_create"));
    if (!create)
    {
        create = reinterpret_cast<kit_create_func>(GetProcAddress(handle.get(), "powertoy_create"));
    }

    if (!create)
    {
        winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND));
    }
    auto pt_module = create();
    if (!pt_module)
    {
        winrt::throw_hresult(winrt::hresult(E_POINTER));
    }
    return KitModule(pt_module, handle.release());
}

json::JsonObject KitModule::json_config() const
{
    int size = 0;
    pt_module->get_config(nullptr, &size);
    constexpr int maximumConfigCharacters = 1024 * 1024;
    if (size <= 1)
    {
        return json::JsonObject();
    }

    for (int attempt = 0; attempt < 2; ++attempt)
    {
        if (size < 1 || size > maximumConfigCharacters)
        {
            winrt::throw_hresult(E_INVALIDARG);
        }

        const int capacity = size;
        std::wstring result(static_cast<size_t>(capacity), L'\0');
        if (pt_module->get_config(result.data(), &size))
        {
            if (size < 1 || size > capacity || result.find(L'\0') == std::wstring::npos)
            {
                winrt::throw_hresult(E_INVALIDARG);
            }

            result.resize(result.find(L'\0'));
            return json::JsonObject::Parse(result);
        }
    }

    winrt::throw_hresult(E_FAIL);
}

KitModule::KitModule(KitModuleIface* pt_module, HMODULE handle) :
    handle(handle), pt_module(pt_module), hkmng(HotkeyConflictDetector::HotkeyConflictManager::GetInstance())
{
    if (!pt_module)
    {
        throw std::runtime_error("Kit module not initialized");
    }

    remove_hotkey_records();
    update_hotkeys();
    UpdateHotkeyEx();
}

void KitModule::update_hotkeys()
{
    CentralizedKeyboardHook::ClearModuleHotkeys(pt_module->get_key());

    size_t hotkeyCount = pt_module->get_hotkeys(nullptr, 0);
    std::vector<KitModuleIface::Hotkey> hotkeys(hotkeyCount);
    pt_module->get_hotkeys(hotkeys.data(), hotkeyCount);

    auto modulePtr = pt_module.get();

    for (size_t i = 0; i < hotkeyCount; i++)
    {
        if (hotkeys[i].isShown)
        {
            hkmng.AddHotkey(hotkeys[i], pt_module->get_key(), static_cast<int>(i), pt_module->is_enabled());

        }

        CentralizedKeyboardHook::SetHotkeyAction(pt_module->get_key(), hotkeys[i], [modulePtr, i] {
            Logger::trace(L"{} hotkey is invoked from Centralized keyboard hook", modulePtr->get_key());
            return modulePtr->on_hotkey(i);
        });
    }
}

void KitModule::UpdateHotkeyEx()
{
    CentralizedHotkeys::UnregisterHotkeysForModule(pt_module->get_key());

    auto container = pt_module->GetHotkeyEx();
    if (container.has_value() && pt_module->is_enabled())
    {
        hkmng.RemoveHotkeyByModule(pt_module->get_key());

        auto hotkey = container.value();
        auto modulePtr = pt_module.get();
        auto action = [modulePtr](WORD /*modifiersMask*/, WORD /*vkCode*/) {
            Logger::trace(L"{} hotkey Ex is invoked from Centralized keyboard hook", modulePtr->get_key());
            modulePtr->OnHotkeyEx();
        };

        HotkeyConflictDetector::Hotkey _hotkey = HotkeyConflictDetector::ShortcutToHotkey({ hotkey.modifiersMask, hotkey.vkCode });
        hkmng.AddHotkey(_hotkey, pt_module->get_key(), 0, pt_module->is_enabled());

        CentralizedHotkeys::AddHotkeyAction({ hotkey.modifiersMask, hotkey.vkCode }, { pt_module->get_key(), action });
    }
}
