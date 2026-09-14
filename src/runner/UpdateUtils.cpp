#include "pch.h"

#include "UpdateUtils.h"
#include "tray_icon.h"

#include <common/logger/logger.h>
#include <common/updating/updateState.h>
#include <common/utils/json.h>
#include <common/utils/timeutil.h>
#include <common/version/helper.h>
#include <common/version/version.h>

#include <memory>
#include <mutex>
#include <optional>

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Web.Http.h>
#include <winrt/Windows.Web.Http.Filters.h>
#include <winrt/Windows.Web.Http.Headers.h>

namespace
{
    constexpr wchar_t KitLatestReleaseEndpoint[] = L"https://api.github.com/repos/guijianchou/Kit/releases/latest";
    constexpr wchar_t KitReleasesPage[] = L"https://github.com/guijianchou/Kit/releases";
    constexpr wchar_t GitHubHtmlUrlField[] = L"html_url";
    constexpr wchar_t KitReleaseCheckUserAgent[] = L"Kit release checker";
    struct LatestReleaseInfo
    {
        VersionHelper version;
        std::wstring releasePage;
    };

    std::mutex updateCheckMutex;

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

    bool is_update_available(const UpdateState& state)
    {
        if (state.state != UpdateState::readyToDownload && state.state != UpdateState::readyToInstall)
        {
            return false;
        }

        const auto savedVersion = version_from_release_page(state.releasePageUrl);
        return savedVersion.has_value() && *savedVersion > current_version();
    }

    void set_update_badge(bool available)
    {
        const auto apply = [](PVOID data) {
            std::unique_ptr<bool> value{ static_cast<bool*>(data) };
            set_tray_icon_update_available(*value);
        };

        auto value = std::make_unique<bool>(available);
        if (dispatch_run_on_main_ui_thread(apply, value.get()))
        {
            value.release();
            return;
        }

        set_tray_icon_update_available(available);
    }

    std::optional<winrt::hstring> fetch_latest_release_body()
    {
        namespace filters = winrt::Windows::Web::Http::Filters;
        namespace http = winrt::Windows::Web::Http;

        filters::HttpBaseProtocolFilter filter;
        filter.CacheControl().ReadBehavior(filters::HttpCacheReadBehavior::NoCache);
        filter.CacheControl().WriteBehavior(filters::HttpCacheWriteBehavior::NoCache);

        http::HttpClient client{ filter };
        auto headers = client.DefaultRequestHeaders();
        headers.UserAgent().TryParseAdd(KitReleaseCheckUserAgent);
        headers.TryAppendWithoutValidation(L"Cache-Control", L"no-cache, no-store");
        headers.TryAppendWithoutValidation(L"Pragma", L"no-cache");

        const auto response = client.GetAsync(winrt::Windows::Foundation::Uri{ KitLatestReleaseEndpoint }).get();
        if (!response.IsSuccessStatusCode())
        {
            Logger::warn(L"Kit update check GitHub request failed with HTTP status {}.", static_cast<uint32_t>(response.StatusCode()));
            return std::nullopt;
        }

        return response.Content().ReadAsStringAsync().get();
    }

    std::optional<LatestReleaseInfo> fetch_latest_release()
    {
        const auto body = fetch_latest_release_body();
        if (!body.has_value())
        {
            return std::nullopt;
        }

        const auto releaseObject = json::JsonValue::Parse(*body).GetObjectW();

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

        return LatestReleaseInfo{ *version, releasePage };
    }

    void save_update_available(const LatestReleaseInfo& release)
    {
        UpdateState::store([&](UpdateState& state) {
            state.state = UpdateState::readyToDownload;
            state.releasePageUrl = release.releasePage;
            state.downloadedInstallerFilename = {};
            state.githubUpdateLastCheckedDate.emplace(timeutil::now());
        });
    }

    void save_up_to_date()
    {
        UpdateState::store([](UpdateState& state) {
            state.state = UpdateState::upToDate;
            state.releasePageUrl = {};
            state.downloadedInstallerFilename = {};
            state.githubUpdateLastCheckedDate.emplace(timeutil::now());
        });
    }

    void save_check_failure()
    {
        UpdateState::store([](UpdateState& state) {
            state.state = UpdateState::networkError;
            state.releasePageUrl = {};
            state.downloadedInstallerFilename = {};
            state.githubUpdateLastCheckedDate.emplace(timeutil::now());
        });
    }

    bool check_for_updates()
    {
        std::scoped_lock lock{ updateCheckMutex };

        const auto previousState = UpdateState::read();
        try
        {
            const auto latestRelease = fetch_latest_release();
            if (!latestRelease.has_value())
            {
                Logger::warn(L"Kit update check did not return a parseable release.");
                save_check_failure();
                set_update_badge(is_update_available(previousState));
                return false;
            }

            if (latestRelease->version <= current_version())
            {
                Logger::info(L"Kit update check found no newer version.");
                save_up_to_date();
                set_update_badge(false);
                return true;
            }

            Logger::info(L"Kit update available: current={} latest={}", current_version().toWstring(), latestRelease->version.toWstring());
            save_update_available(*latestRelease);
            set_update_badge(true);

            return true;
        }
        catch (...)
        {
            Logger::warn(L"Kit update check failed.");
            save_check_failure();
            set_update_badge(is_update_available(previousState));
            return false;
        }
    }
}

void CheckForUpdatesCallback()
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    auto uninitialize = wil::scope_exit([] { winrt::uninit_apartment(); });
    check_for_updates();
}

SHELLEXECUTEINFOW LaunchPowerToysUpdate(const wchar_t*)
{
    return {};
}
