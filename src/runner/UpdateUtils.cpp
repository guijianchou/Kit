#include "pch.h"

#include "UpdateUtils.h"
#include "tray_icon.h"

#include <common/SettingsAPI/settings_helpers.h>
#include <common/logger/logger.h>
#include <common/notifications/notifications.h>
#include <common/utils/HttpClient.h>
#include <common/utils/json.h>
#include <common/utils/timeutil.h>
#include <common/version/helper.h>
#include <common/version/version.h>

#include <filesystem>
#include <memory>
#include <optional>

#include <winrt/Windows.Data.Json.h>

namespace
{
    constexpr wchar_t KitLatestReleaseEndpoint[] = L"https://api.github.com/repos/guijianchou/Kit/releases/latest";
    constexpr wchar_t KitReleasesPage[] = L"https://github.com/guijianchou/Kit/releases";
    constexpr wchar_t GitHubHtmlUrlField[] = L"html_url";
    constexpr wchar_t LastCheckedField[] = L"githubUpdateLastCheckedDate";
    constexpr wchar_t ReleasePageField[] = L"releasePageUrl";
    constexpr wchar_t StateField[] = L"state";
    constexpr wchar_t UpdateStateFileName[] = L"UpdateState.json";
    constexpr std::wstring_view KitUpdateToastTag = L"KitUpdateAvailable";
    constexpr auto checkInterval = std::chrono::hours(24);

    enum class UpdatingState
    {
        UpToDate = 0,
        ErrorDownloading = 1,
        ReadyToDownload = 2,
        ReadyToInstall = 3,
        NetworkError = 4,
    };

    struct LatestReleaseInfo
    {
        VersionHelper version;
        std::wstring versionTag;
        std::wstring releasePage;
    };

    std::mutex updateCheckMutex;

    std::filesystem::path get_update_state_path()
    {
        std::filesystem::path path{ PTSettingsHelper::get_root_save_folder_location() };
        path.append(UpdateStateFileName);
        return path;
    }

    json::JsonObject load_update_state()
    {
        const auto state = json::from_file(get_update_state_path().wstring());
        return state.has_value() ? *state : json::JsonObject{};
    }

    void save_update_state(const json::JsonObject& state)
    {
        json::to_file(get_update_state_path().wstring(), state);
    }

    std::optional<std::time_t> get_last_checked(const json::JsonObject& state)
    {
        try
        {
            return timeutil::from_string(state.GetNamedString(LastCheckedField, L"").c_str());
        }
        catch (...)
        {
            return std::nullopt;
        }
    }

    void stamp_last_checked(json::JsonObject& state)
    {
        state.SetNamedValue(LastCheckedField, json::value(timeutil::to_string(timeutil::now())));
    }

    bool should_check_now(const json::JsonObject& state)
    {
        const auto lastChecked = get_last_checked(state);
        if (!lastChecked.has_value())
        {
            return true;
        }

        const auto lastCheckedTime = std::chrono::system_clock::from_time_t(*lastChecked);
        return std::chrono::system_clock::now() - lastCheckedTime >= checkInterval;
    }

    VersionHelper current_version()
    {
        return VersionHelper{ VERSION_MAJOR, VERSION_MINOR, VERSION_REVISION };
    }

    std::optional<VersionHelper> version_from_release_page(const std::wstring& releasePage)
    {
        const auto lastSlash = releasePage.find_last_of(L'/');
        if (lastSlash == std::wstring::npos || lastSlash + 1 >= releasePage.size())
        {
            return std::nullopt;
        }

        return VersionHelper::fromString(std::wstring_view{ releasePage }.substr(lastSlash + 1));
    }

    bool is_saved_update_available(const json::JsonObject& state)
    {
        try
        {
            const auto stateValue = static_cast<UpdatingState>(static_cast<int>(state.GetNamedNumber(StateField, 0)));
            if (stateValue != UpdatingState::ReadyToDownload && stateValue != UpdatingState::ReadyToInstall)
            {
                return false;
            }

            const std::wstring releasePage = state.GetNamedString(ReleasePageField, L"").c_str();
            const auto savedVersion = version_from_release_page(releasePage);
            return savedVersion.has_value() && *savedVersion > current_version();
        }
        catch (...)
        {
            return false;
        }
    }

    bool is_same_saved_update(const json::JsonObject& state, const VersionHelper& version)
    {
        try
        {
            const auto stateValue = static_cast<UpdatingState>(static_cast<int>(state.GetNamedNumber(StateField, 0)));
            if (stateValue != UpdatingState::ReadyToDownload && stateValue != UpdatingState::ReadyToInstall)
            {
                return false;
            }

            const std::wstring releasePage = state.GetNamedString(ReleasePageField, L"").c_str();
            const auto savedVersion = version_from_release_page(releasePage);
            return savedVersion.has_value() && *savedVersion == version;
        }
        catch (...)
        {
            return false;
        }
    }

    void set_update_badge(bool available)
    {
        const auto apply = [](PVOID data) {
            std::unique_ptr<bool> value{ static_cast<bool*>(data) };
            if (*value)
            {
                set_tray_icon_update_available(true);
            }
            else
            {
                set_tray_icon_update_available(false);
            }
        };

        auto value = std::make_unique<bool>(available);
        if (dispatch_run_on_main_ui_thread(apply, value.get()))
        {
            value.release();
            return;
        }

        if (available)
        {
            set_tray_icon_update_available(true);
        }
        else
        {
            set_tray_icon_update_available(false);
        }
    }

    std::optional<LatestReleaseInfo> fetch_latest_release()
    {
        http::HttpClient client;
        const auto body = client.request(winrt::Windows::Foundation::Uri{ KitLatestReleaseEndpoint }).get();
        const auto releaseObject = json::JsonValue::Parse(body).GetObjectW();

        const std::wstring tag = releaseObject.GetNamedString(L"tag_name", L"").c_str();
        const auto version = VersionHelper::fromString(tag);
        if (!version.has_value())
        {
            return std::nullopt;
        }

        std::wstring releasePage = releaseObject.GetNamedString(GitHubHtmlUrlField, L"").c_str();
        if (releasePage.empty())
        {
            releasePage = std::wstring{ KitReleasesPage } + L"/tag/" + tag;
        }

        return LatestReleaseInfo{ *version, tag, releasePage };
    }

    void save_update_available(const LatestReleaseInfo& release)
    {
        json::JsonObject state;
        state.SetNamedValue(StateField, json::value(static_cast<int>(UpdatingState::ReadyToDownload)));
        state.SetNamedValue(ReleasePageField, json::value(release.releasePage));
        state.SetNamedValue(L"downloadedInstallerFilename", json::value(L""));
        stamp_last_checked(state);
        save_update_state(state);
    }

    void save_up_to_date()
    {
        json::JsonObject state;
        state.SetNamedValue(StateField, json::value(static_cast<int>(UpdatingState::UpToDate)));
        state.SetNamedValue(ReleasePageField, json::value(L""));
        state.SetNamedValue(L"downloadedInstallerFilename", json::value(L""));
        stamp_last_checked(state);
        save_update_state(state);
    }

    void save_network_error()
    {
        json::JsonObject state;
        state.SetNamedValue(StateField, json::value(static_cast<int>(UpdatingState::NetworkError)));
        state.SetNamedValue(ReleasePageField, json::value(L""));
        state.SetNamedValue(L"downloadedInstallerFilename", json::value(L""));
        stamp_last_checked(state);
        save_update_state(state);
    }

    void show_update_available_toast(const LatestReleaseInfo& release)
    {
        std::wstring message = L"Kit ";
        message += release.versionTag;
        message += L" is available on GitHub.";

        notifications::show_toast_with_activations(
            message,
            L"Kit update available",
            {},
            { notifications::link_button{ L"Open releases", release.releasePage } },
            notifications::toast_params{ .tag = KitUpdateToastTag },
            release.releasePage);
    }

    void check_for_updates(bool force)
    {
        std::scoped_lock lock{ updateCheckMutex };

        auto previousState = load_update_state();
        if (!force && !should_check_now(previousState))
        {
            set_update_badge(is_saved_update_available(previousState));
            return;
        }

        try
        {
            const auto latestRelease = fetch_latest_release();
            if (!latestRelease.has_value())
            {
                Logger::warn(L"Kit update check did not return a parseable release.");
                save_network_error();
                set_update_badge(is_saved_update_available(previousState));
                return;
            }

            if (latestRelease->version <= current_version())
            {
                Logger::info(L"Kit update check found no newer version.");
                save_up_to_date();
                set_update_badge(false);
                return;
            }

            const bool alreadyNotified = is_same_saved_update(previousState, latestRelease->version);
            Logger::info(L"Kit update available: current={} latest={}", current_version().toWstring(), latestRelease->version.toWstring());
            save_update_available(*latestRelease);
            set_update_badge(true);

            if (!alreadyNotified)
            {
                show_update_available_toast(*latestRelease);
            }
        }
        catch (...)
        {
            Logger::warn(L"Kit update check failed.");
            save_network_error();
            set_update_badge(is_saved_update_available(previousState));
        }
    }
}

void PeriodicUpdateWorker()
{
    std::thread([] {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);

        set_update_badge(is_saved_update_available(load_update_state()));

        while (true)
        {
            check_for_updates(false);
            std::this_thread::sleep_for(std::chrono::hours(1));
        }
    }).detach();
}

void CheckForUpdatesCallback()
{
    std::thread([] {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        check_for_updates(true);
    }).detach();
}

SHELLEXECUTEINFOW LaunchPowerToysUpdate(const wchar_t*)
{
    return {};
}
