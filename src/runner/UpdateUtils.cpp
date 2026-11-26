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
    void record_helper_failure(const wchar_t* action, const wchar_t* code, const std::optional<std::time_t>& baseline)
    {
        UpdateState::store([&](UpdateState& state) {
            // Keep a more specific failure already reported by the helper.
            if (state.githubUpdateLastCheckedDate != baseline && !state.errorCode.empty())
            {
                return;
            }

            // A failed check must not discard a verified download.
            if (wcscmp(action, L"check") != 0 || state.state != UpdateState::readyToInstall)
            {
                state.state = wcscmp(action, L"check") == 0 ? UpdateState::networkError : UpdateState::errorDownloading;
            }
            state.errorCode = code;
            const auto now = timeutil::now();
            state.githubUpdateLastCheckedDate = state.githubUpdateLastCheckedDate.has_value() && *state.githubUpdateLastCheckedDate >= now
                ? *state.githubUpdateLastCheckedDate + 1 : now;
        });
    }

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
    const auto baseline = UpdateState::read().githubUpdateLastCheckedDate;
    auto command = L"\"" + helper.wstring() + L"\" " + action + L" \"" + root.wstring() + L"\" " + std::to_wstring(GetCurrentProcessId());
    STARTUPINFOW startup{ sizeof(startup) };
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(helper.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, root.c_str(), &startup, &process))
    {
        const auto error = GetLastError();
        Logger::error(L"Could not start Portable updater for {}: {}", action, error);
        record_helper_failure(action, error == ERROR_ACCESS_DENIED ? L"accessDenied" :
            error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND ? L"helperMissing" : L"helperFailed", baseline);
        update_badge();
        return;
    }

    wil::unique_handle thread{ process.hThread };
    wil::unique_handle child{ process.hProcess };
    const auto waitResult = WaitForSingleObject(child.get(), INFINITE);
    DWORD exitCode = 0;
    if (waitResult != WAIT_OBJECT_0 || !GetExitCodeProcess(child.get(), &exitCode))
    {
        Logger::error(L"Could not read Portable updater result for {}: {}", action, GetLastError());
        record_helper_failure(action, L"helperFailed", baseline);
    }
    else if (exitCode != 0)
    {
        // A status probe is intentionally non-blocking while another update operation runs.
        if (exitCode == 3 && wcscmp(action, L"status") == 0)
        {
            return;
        }
        Logger::error(L"Portable updater failed for {} with exit code {}", action, exitCode);
        record_helper_failure(action, exitCode == ERROR_ACCESS_DENIED ? L"accessDenied" :
            exitCode == 3 ? L"helperBusy" : L"helperFailed", baseline);
    }
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
