#include "pch.h"
#include "kit_module.h"
#include "module_lifecycle.h"
#include "module_hotkeys.h"
#include "tray_icon.h"
#include "settings_window.h"
#include <array>
#include <common/logger/logger.h>
#include <common/utils/process_path.h>
#include <common/utils/winapi_error.h>
#include <filesystem>
#include <chrono>

std::map<std::wstring, KitModule>& modules()
{
    static std::map<std::wstring, KitModule> modules_map;
    return modules_map;
}

KitModule load_kit_module(const std::wstring_view filename)
{
    using clock = std::chrono::steady_clock;
    const auto elapsed_ms = [](clock::time_point from) {
        return std::chrono::duration_cast<std::chrono::milliseconds>(clock::now() - from).count();
    };

    const auto runnerDirectory = std::filesystem::path(get_module_filename()).parent_path();
    const auto modulePath = runnerDirectory / filename;
    auto stageStart = clock::now();
    std::unique_ptr<HMODULE, KitModuleDLLDeleter> handle(winrt::check_pointer(LoadLibraryExW(
        modulePath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)));
    const auto loadMs = elapsed_ms(stageStart);

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
    stageStart = clock::now();
    auto pt_module = create();
    if (!pt_module)
    {
        winrt::throw_hresult(winrt::hresult(E_POINTER));
    }
    const auto createMs = elapsed_ms(stageStart);

    KitModule module(pt_module, handle.release());
    Logger::debug(L"Module {}: LoadLibrary {}ms, kit_create {}ms",
                  filename,
                  loadMs,
                  createMs);
    return module;
}

static std::wstring read_module_config(KitModuleIface* module)
{
    int size = 0;
    module->get_config(nullptr, &size);
    constexpr int maximumConfigCharacters = 1024 * 1024;
    if (size <= 1)
    {
        return L"{}";
    }

    for (int attempt = 0; attempt < 2; ++attempt)
    {
        if (size < 1 || size > maximumConfigCharacters)
        {
            winrt::throw_hresult(E_INVALIDARG);
        }

        const int capacity = size;
        std::wstring result(static_cast<size_t>(capacity), L'\0');
        if (module->get_config(result.data(), &size))
        {
            if (size < 1 || size > capacity || result.find(L'\0') == std::wstring::npos)
            {
                winrt::throw_hresult(E_INVALIDARG);
            }

            result.resize(result.find(L'\0'));
            return result;
        }
    }

    winrt::throw_hresult(E_FAIL);
}

std::wstring_view get_module_filename_for_key(std::wstring_view key)
{
    for (const auto& entry : KitModuleCatalog)
    {
        if (entry.key == key)
        {
            return entry.filename;
        }
    }
    return {};
}

std::wstring_view get_module_key_for_filename(std::wstring_view filename)
{
    for (const auto& entry : KitModuleCatalog)
    {
        if (entry.filename == filename)
        {
            return entry.key;
        }
    }
    return {};
}

struct KitModule::State
{
    // Declaration order guarantees destroy() precedes FreeLibrary().
    std::unique_ptr<HMODULE, KitModuleDLLDeleter> library;
    std::unique_ptr<KitModuleIface, KitModuleDeleter> module;
    const std::wstring key;
    const std::wstring name;
    const bool enabledByDefault;
    const kit_gpo::gpo_rule_configured_t policy;
    std::mutex snapshots;
    std::wstring config;
    std::vector<KitModuleIface::Hotkey> hotkeys;
    bool lastRunning = false;
    std::atomic<size_t> pendingConfigurations{ 0 };
    bool notifyAfterConfig = false;
    std::unique_ptr<ModuleLifecycle> lifecycle;

    State(KitModuleIface* instance, HMODULE handle) :
        library(handle), module(instance), key(instance->get_key()), name(instance->get_name()),
        enabledByDefault(instance->is_enabled_by_default()), policy(instance->gpo_policy_enabled_configuration())
    {
        refresh(false, true);
        lifecycle = std::make_unique<ModuleLifecycle>(ModuleLifecycle::Callbacks{
            [] { winrt::init_apartment(winrt::apartment_type::multi_threaded); },
            [] { winrt::uninit_apartment(); },
            [this] { module->enable(); },
            [this] { module->disable(); },
            [this] { return module->is_enabled(); },
            [this](bool configurationChanged) { refresh(true, configurationChanged); },
            [this] { Logger::error(L"Module {} lifecycle operation failed; retry remains bounded.", key); }
        });
    }

    ~State()
    {
        if (lifecycle) lifecycle->shutdown();
        // The joined worker is the only thread allowed to enter module methods.
        // No queued UI notification contains this State or its DLL pointer.
    }

    void refresh(bool notify, bool configurationChanged)
    {
        std::wstring nextConfig;
        std::vector<KitModuleIface::Hotkey> nextHotkeys;
        if (configurationChanged)
        {
            nextConfig = read_module_config(module.get());
            std::array<KitModuleIface::Hotkey, 32> buffer{};
            const size_t count = module->get_hotkeys(buffer.data(), buffer.size());
            if (count > buffer.size()) throw std::runtime_error("Module returned too many hotkeys");
            nextHotkeys.assign(buffer.begin(), buffer.begin() + count);
        }
        const bool running = module->is_enabled();
        bool changed;
        {
            std::lock_guard lock(snapshots);
            changed = lastRunning != running;
            if (configurationChanged)
            {
                changed = changed || config != nextConfig || hotkeys != nextHotkeys;
                config = std::move(nextConfig);
                hotkeys = std::move(nextHotkeys);
            }
            lastRunning = running;
        }
        if (notify && (changed || notifyAfterConfig))
        {
            notifyAfterConfig = false;
            // No State pointer crosses to the UI. A final config completion also sends
            // unchanged settings (e.g. rejected input) after all module writes have drained.
            dispatch_run_on_main_ui_thread([](PVOID) {
                refresh_module_hotkeys();
                send_all_settings_to_settings();
            }, nullptr);
        }
    }

    void submit(std::function<void()> operation)
    {
        if (!lifecycle->submit(std::move(operation)))
            throw std::runtime_error("Module lifecycle queue is stopping or full");
    }
};

KitModule::KitModule(KitModuleIface* module, HMODULE handle)
{
    if (!module)
    {
        if (handle) FreeLibrary(handle);
        throw std::runtime_error("Kit module not initialized");
    }
    state = std::make_unique<State>(module, handle);
}

KitModule::~KitModule() = default;
KitModule::KitModule(KitModule&&) noexcept = default;
KitModule& KitModule::operator=(KitModule&&) noexcept = default;
const wchar_t* KitModule::get_key() const { return state->key.c_str(); }
const wchar_t* KitModule::get_name() const { return state->name.c_str(); }
bool KitModule::is_enabled_by_default() const { return state->enabledByDefault; }
kit_gpo::gpo_rule_configured_t KitModule::gpo_policy_enabled_configuration() const { return state->policy; }
bool KitModule::is_enabled() const { return state->lifecycle->desired(); }
bool KitModule::is_running() const { return state->lifecycle->running(); }
bool KitModule::configuration_pending() const noexcept { return state->pendingConfigurations.load() != 0; }

void KitModule::enable()
{
    if (!state->lifecycle->request(true)) throw std::runtime_error("Module lifecycle queue is stopping or full");
}

void KitModule::disable()
{
    if (!state->lifecycle->request(false)) throw std::runtime_error("Module lifecycle queue is stopping or full");
}

void KitModule::begin_shutdown() noexcept
{
    state->lifecycle->begin_shutdown();
}

void begin_module_shutdown() noexcept
{
    for (auto& [key, module] : modules()) module.begin_shutdown();
}

bool module_configuration_pending() noexcept
{
    for (const auto& [key, module] : modules())
        if (module.configuration_pending()) return true;
    return false;
}

void KitModule::set_config(const wchar_t* config)
{
    std::wstring value(config ? config : L"");
    State* owner = state.get();
    // A queued command can also be discarded if COM initialization fails. Its lease
    // releases pending on either path, so future Settings responses cannot stay blocked.
    ++owner->pendingConfigurations;
    auto pending = std::shared_ptr<State>(owner, [](State* value) { --value->pendingConfigurations; });
    try
    {
        state->submit([owner, pending = std::move(pending), value = std::move(value)]() mutable {
            auto completed = wil::scope_exit([owner, &pending] {
                // Publish the new snapshot before clearing pending; another module's
                // completion may already be queued on the UI thread.
                try { owner->refresh(false, true); }
                catch (...) { Logger::warn(L"Module {} config snapshot refresh failed.", owner->key); }
                pending.reset();
                owner->notifyAfterConfig = true;
            });
            owner->module->set_config(value.c_str());
        });
    }
    catch (...)
    {
        // The rejected command's pending lease is released during unwinding.
        throw;
    }
}

void KitModule::call_custom_action(const wchar_t* action)
{
    std::wstring value(action ? action : L"");
    State* owner = state.get();
    state->submit([owner, value = std::move(value)] {
        if (owner->lifecycle->desired()) owner->module->call_custom_action(value.c_str());
    });
}

size_t KitModule::get_hotkeys(KitModuleIface::Hotkey* buffer, size_t size) const
{
    std::lock_guard lock(state->snapshots);
    if (buffer && size >= state->hotkeys.size()) std::copy(state->hotkeys.begin(), state->hotkeys.end(), buffer);
    return state->hotkeys.size();
}

bool KitModule::on_hotkey(size_t index)
{
    if (!is_enabled()) return false;
    State* owner = state.get();
    return state->lifecycle->submit([owner, index] {
        if (owner->lifecycle->desired() && owner->module->is_enabled()) owner->module->on_hotkey(index);
    });
}

bool KitModule::get_config(wchar_t* buffer, int* size) const
{
    if (!size) return false;
    std::lock_guard lock(state->snapshots);
    const int required = static_cast<int>(state->config.size() + 1);
    if (!buffer || *size < required)
    {
        *size = required;
        return false;
    }
    wcscpy_s(buffer, *size, state->config.c_str());
    *size = required;
    return true;
}

json::JsonObject KitModule::json_config() const
{
    std::wstring snapshot;
    {
        std::lock_guard lock(state->snapshots);
        snapshot = state->config;
    }
    return json::JsonObject::Parse(snapshot);
}
