#include "pch.h"
#include <interface/kit_module_interface.h>
#include <common/SettingsAPI/settings_objects.h>
#include <common/SettingsAPI/settings_helpers.h>
#include <common/interop/shared_constants.h>
#include <common/logger/logger.h>
#include <common/logger/logger_settings.h>
#include <common/utils/process_path.h>
#include <common/utils/winapi_error.h>
#include <common/utils/KitWorkerProcessManager.h>
#include <filesystem>
#include <mutex>

extern "C" IMAGE_DOS_HEADER __ImageBase;

class NetMapModule final : public KitModuleIface
{
    bool enabled = false;
    KitWorkerProcessManager m_worker{ L"NetMap", L"NetMapService", L"Kit.NetMapService.exe", CommonSharedConstants::NETMAP_EXIT_EVENT };
    std::mutex m_lifecycle_mutex;

    void start_worker_if_needed();
    void stop_worker_if_running();
    void close_worker_handle();

public:
    NetMapModule()
    {
        std::filesystem::path logFilePath(PTSettingsHelper::get_module_save_folder_location(get_key()));
        logFilePath.append(LogSettings::netMapLogPath);
        Logger::init(LogSettings::netMapLoggerName, logFilePath.wstring(), PTSettingsHelper::get_log_settings_file_location());
        Logger::info("NetMap module is constructing");
    }

    const wchar_t* get_name() override { return L"NetMap"; }
    const wchar_t* get_key() override { return L"NetMap"; }
    bool is_enabled_by_default() const override { return false; }
    bool is_enabled() override { return enabled; }

    void enable() override
    {
        if (enabled)
        {
            return;
        }

        {
            std::lock_guard lifecycleLock(m_lifecycle_mutex);
            enabled = true;
            start_worker_if_needed();
        }

        Logger::info(L"NetMap module enabled");
    }

    void disable() override
    {
        if (!enabled)
        {
            return;
        }

        {
            std::lock_guard lifecycleLock(m_lifecycle_mutex);
            enabled = false;
            stop_worker_if_running();
        }

        Logger::info(L"NetMap module disabled");
    }

    void destroy() override
    {
        disable();
        close_worker_handle();
        delete this;
    }

    bool get_config(wchar_t* buffer, int* size) override
    {
        KitSettings::Settings settings(reinterpret_cast<HINSTANCE>(&__ImageBase), get_name());
        settings.set_description(L"Observe direct and proxy egress, endpoint responses and local routes.");
        return settings.serialize_to_buffer(buffer, size);
    }

    void set_config(const wchar_t* config) override
    {
        try
        {
            auto values = KitSettings::PowerToyValues::from_json_string(config, get_key());
            values.save_to_settings_file();

            auto refreshEvent = CreateEventW(nullptr, false, false, CommonSharedConstants::NETMAP_REFRESH_SETTINGS_EVENT);
            if (refreshEvent)
            {
                SetEvent(refreshEvent);
                CloseHandle(refreshEvent);
            }
        }
        catch (...)
        {
            // Never propagate malformed settings across the native module boundary.
        }
    }
};

void NetMapModule::close_worker_handle()
{
    m_worker.close_handle();
}

void NetMapModule::start_worker_if_needed()
{
    m_worker.start(reinterpret_cast<HMODULE>(&__ImageBase));
}

void NetMapModule::stop_worker_if_running()
{
    m_worker.stop();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl kit_create()
{
    return new NetMapModule();
}

extern "C" __declspec(dllexport) KitModuleIface* __cdecl powertoy_create()
{
    return kit_create();
}
