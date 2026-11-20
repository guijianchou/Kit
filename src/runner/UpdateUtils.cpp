#include "pch.h"
#include "UpdateUtils.h"
#include "tray_icon.h"

#include <common/logger/logger.h>
#include <common/updating/updateState.h>
#include <common/utils/timeutil.h>
#include <filesystem>
#include <memory>

namespace
{
    void update_badge()
    {
        const auto state = UpdateState::read();
        auto value = std::make_unique<bool>(state.state == UpdateState::readyToDownload || state.state == UpdateState::readyToInstall);
        if (dispatch_run_on_main_ui_thread([](PVOID data) {
                std::unique_ptr<bool> available{ static_cast<bool*>(data) };
                set_tray_icon_update_available(*available);
            }, value.get()))
        {
            value.release();
        }
    }
}

void RunPortableUpdate(const wchar_t* action)
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    auto uninitialize = wil::scope_exit([] { winrt::uninit_apartment(); });
    wchar_t runner[MAX_PATH]{};
    GetModuleFileNameW(nullptr, runner, ARRAYSIZE(runner));
    const auto root = std::filesystem::path(runner).parent_path();
    const auto helper = root / L"Updater" / L"Kit.Updater.exe";
    auto command = L"\"" + helper.wstring() + L"\" " + action + L" \"" + root.wstring() + L"\" " + std::to_wstring(GetCurrentProcessId());
    STARTUPINFOW startup{ sizeof(startup) };
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(helper.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, root.c_str(), &startup, &process))
    {
        Logger::error(L"Could not start Portable updater: {}", GetLastError());
        UpdateState::store([&](UpdateState& state) {
            state.state = wcscmp(action, L"check") == 0 ? UpdateState::networkError : UpdateState::errorDownloading;
            state.errorCode = L"helperMissing";
            state.githubUpdateLastCheckedDate.emplace(timeutil::now());
        });
        return;
    }

    wil::unique_handle thread{ process.hThread };
    wil::unique_handle child{ process.hProcess };
    WaitForSingleObject(child.get(), INFINITE);
    update_badge();
}

void CheckForUpdatesCallback()
{
    RunPortableUpdate(L"check");
}

void CancelPortableUpdate()
{
    wil::unique_handle signal{ OpenEventW(EVENT_MODIFY_STATE, FALSE, L"Local\\KitPortableUpdateCancel") };
    if (signal) { SetEvent(signal.get()); }
}

SHELLEXECUTEINFOW LaunchPowerToysUpdate(const wchar_t*)
{
    return {};
}
