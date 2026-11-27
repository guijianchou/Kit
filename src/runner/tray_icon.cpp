#include "pch.h"
#include "module_hotkeys.h"
#include "Generated files/resource.h"
#include "settings_window.h"
#include "tray_icon.h"
#include "general_settings.h"
#include "quick_access_host.h"
#include "trace.h"
#include <Windows.h>
#include <memory>

#include <common/utils/resources.h>
#include <common/version/version.h>
#include <common/logger/logger.h>
#include <common/utils/elevation.h>
#include <common/Themes/theme_listener.h>
#include <common/Themes/theme_helpers.h>
#include <common/utils/window.h>

namespace
{
    HWND tray_icon_hwnd = NULL;

    // Set when Windows confirms that the full session is ending so WM_DESTROY
    // can skip cross-process cleanup the OS is already performing in parallel.
    bool g_system_session_ending = false;

    void log_session_end_message(UINT message, WPARAM wparam, LPARAM lparam)
    {
        if (message != WM_QUERYENDSESSION && message != WM_ENDSESSION)
        {
            return;
        }

        const auto flags = static_cast<DWORD>(lparam);
        const bool close_app = (flags & ENDSESSION_CLOSEAPP) != 0;
        const bool critical = (flags & ENDSESSION_CRITICAL) != 0;
        const bool logoff = (flags & ENDSESSION_LOGOFF) != 0;
        const wchar_t* reason = L"shutdown_or_restart";
        if (close_app)
        {
            reason = L"restart_manager_close_app";
        }
        else if (logoff)
        {
            reason = L"logoff";
        }

        if (message == WM_QUERYENDSESSION)
        {
            Logger::info(L"Runner received WM_QUERYENDSESSION: reason={}, flags={:#010x}, close_app={}, critical={}, logoff={}",
                         reason,
                         flags,
                         close_app,
                         critical,
                         logoff);
        }
        else
        {
            Logger::info(L"Runner received WM_ENDSESSION: session_ending={}, reason={}, flags={:#010x}, close_app={}, critical={}, logoff={}",
                         wparam != FALSE,
                         reason,
                         flags,
                         close_app,
                         critical,
                         logoff);
        }
    }

    enum
    {
        wm_icon_notify = WM_APP,
        wm_run_on_main_ui_thread,
    };

    // Contains the Windows Message for taskbar creation.
    UINT wm_taskbar_restart = 0;

    NOTIFYICONDATAW tray_icon_data;
    bool tray_icon_created = false;
    bool s_tray_icon_visible = true;

    bool add_tray_icon_with_retry()
    {
        if (tray_icon_data.hWnd == nullptr)
        {
            return false;
        }

        tray_icon_data.uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE;

        // Clean up any stale or zombie icon left in Explorer by a previously terminated instance
        Shell_NotifyIcon(NIM_DELETE, &tray_icon_data);

        BOOL res = Shell_NotifyIcon(NIM_ADD, &tray_icon_data);
        if (res)
        {
            Logger::info(L"Shell_NotifyIcon(NIM_ADD) succeeded.");
            return true;
        }

        DWORD err = GetLastError();
        Logger::warn(L"Shell_NotifyIcon(NIM_ADD) failed with error {:#010x}, retrying...", err);

        for (int retry = 1; retry <= 3; ++retry)
        {
            Sleep(retry * 150);
            Shell_NotifyIcon(NIM_DELETE, &tray_icon_data);
            res = Shell_NotifyIcon(NIM_ADD, &tray_icon_data);
            if (res)
            {
                Logger::info(L"Shell_NotifyIcon(NIM_ADD) succeeded on retry {}.", retry);
                return true;
            }
        }

        Logger::error(L"Shell_NotifyIcon(NIM_ADD) failed after retries, last error={:#010x}", GetLastError());
        return false;
    }

    HMENU h_menu = nullptr;
    HMENU h_sub_menu = nullptr;
    bool double_click_timer_running = false;
    bool double_clicked = false;
    constexpr UINT_PTR tray_click_timer_id = 1;
    POINT tray_icon_click_point;
    std::optional<bool> last_quick_access_state; // Track the last known Quick Access state

    static ThemeListener theme_listener;
    static bool theme_adaptive_enabled = false;
    static bool update_available = false;
    static HICON owned_tray_icon = nullptr;
    static std::optional<Theme> applied_system_theme;
    static bool applied_theme_adaptive = false;
    static bool applied_update_available = false;
    static UINT applied_icon_dpi = 0;
    static DWORD tray_ui_thread_id = 0;

    static void handle_theme_change();
    static bool apply_tray_icon_on_ui_thread(bool force_reload);
}

// Struct to fill with callback and the data. The window_proc is responsible for cleaning it.
struct run_on_main_ui_thread_msg
{
    main_loop_callback_function _callback;
    PVOID data;
};

bool dispatch_run_on_main_ui_thread(main_loop_callback_function _callback, PVOID data)
{
    if (tray_icon_hwnd == NULL)
    {
        return false;
    }
    auto wnd_msg = std::make_unique<run_on_main_ui_thread_msg>();
    wnd_msg->_callback = _callback;
    wnd_msg->data = data;

    if (!PostMessage(tray_icon_hwnd, wm_run_on_main_ui_thread, 0, reinterpret_cast<LPARAM>(wnd_msg.get())))
    {
        return false;
    }

    wnd_msg.release();
    return true;
}

void change_menu_item_text(const UINT item_id, wchar_t* new_text)
{
    MENUITEMINFOW menuitem = { .cbSize = sizeof(MENUITEMINFOW), .fMask = MIIM_TYPE | MIIM_DATA };
    GetMenuItemInfoW(h_menu, item_id, false, &menuitem);
    menuitem.dwTypeData = new_text;
    SetMenuItemInfoW(h_menu, item_id, false, &menuitem);
}

void open_quick_access_flyout_window()
{
    QuickAccessHost::show();
}

void handle_tray_command(HWND window, const WPARAM command_id, LPARAM lparam)
{
    Logger::info("handle_tray_command: command_id={}", command_id);
    switch (command_id)
    {
    case ID_SETTINGS_MENU_COMMAND:
    {
        std::wstring settings_window{ winrt::to_hstring(ESettingsWindowNames_to_string(static_cast<ESettingsWindowNames>(lparam))) };
        Logger::info(L"handle_tray_command: ID_SETTINGS_MENU_COMMAND target={}", settings_window);
        open_settings_window(settings_window);
    }
    break;
    case ID_CLOSE_MENU_COMMAND:
        if (h_menu)
        {
            DestroyMenu(h_menu);
        }
        DestroyWindow(window);
        break;
    case ID_DOCUMENTATION_MENU_COMMAND:
    {
        RunNonElevatedEx(L"https://github.com/guijianchou/Kit", L"", L"");
        break;
    }
    case ID_QUICK_ACCESS_MENU_COMMAND:
    {
        open_quick_access_flyout_window();
        break;
    }
    case ID_UPDATE_MENU_COMMAND:
    {
        open_settings_window(std::wstring{ L"Overview" });
        break;
    }
    }
}

void click_timer_elapsed()
{
    double_click_timer_running = false;
    if (!double_clicked)
    {
        Trace::TrayIconLeftClick(get_general_settings().enableQuickAccess);

        if (get_general_settings().enableQuickAccess)
        {
            open_quick_access_flyout_window();
        }
        else
        {
            open_settings_window(std::nullopt);
        }
    }
}

LRESULT __stdcall tray_icon_window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
{
    if (message == settings_activation_message())
    {
        if (lparam >= 0 && lparam <= static_cast<LPARAM>(ESettingsWindowNames::Winget))
            handle_tray_command(window, ID_SETTINGS_MENU_COMMAND, lparam);
        return 0;
    }
    log_session_end_message(message, wparam, lparam);

    LRESULT session_end_result = 0;
    if (handle_stateless_session_end_message(window, message, wparam, lparam, session_end_result, &g_system_session_ending))
    {
        return session_end_result;
    }

    switch (message)
    {
    case WM_HOTKEY:
        return dispatch_module_hotkey(wparam, lparam) ? 0 : DefWindowProc(window, message, wparam, lparam);
    case WM_CREATE:
        if (wm_taskbar_restart == 0)
        {
            tray_icon_hwnd = window;
            wm_taskbar_restart = RegisterWindowMessageW(L"TaskbarCreated");
        }
        break;
    case WM_DESTROY:
        theme_listener.DelSystemThemeChangedHandler(&handle_theme_change);
        KillTimer(window, tray_click_timer_id);
        double_click_timer_running = false;
        double_clicked = false;
        cancel_settings_window_startup();
        Logger::info(L"Runner WM_DESTROY, system_session_ending={}", g_system_session_ending);
        if (!g_system_session_ending)
        {
            if (tray_icon_created)
            {
                Shell_NotifyIcon(NIM_DELETE, &tray_icon_data);
                tray_icon_created = false;
            }
            close_settings_window();
        }
        if (owned_tray_icon != nullptr)
        {
            DestroyIcon(owned_tray_icon);
            owned_tray_icon = nullptr;
            tray_icon_data.hIcon = nullptr;
        }
        tray_icon_hwnd = NULL;
        PostQuitMessage(0);
        break;
    case WM_SETTINGCHANGE:
        if (theme_adaptive_enabled &&
            (lparam == 0 || _wcsicmp(reinterpret_cast<LPCWSTR>(lparam), L"ImmersiveColorSet") == 0))
        {
            apply_tray_icon_on_ui_thread(false);
        }
        break;
    case WM_THEMECHANGED:
        if (theme_adaptive_enabled)
        {
            apply_tray_icon_on_ui_thread(false);
        }
        break;
    case WM_DPICHANGED:
        apply_tray_icon_on_ui_thread(true);
        break;
    case WM_CLOSE:
        DestroyWindow(window);
        break;
    case WM_COMMAND:
        handle_tray_command(window, wparam, lparam);
        break;
    case WM_TIMER:
        if (wparam == tray_click_timer_id)
        {
            KillTimer(window, tray_click_timer_id);
            if (double_click_timer_running)
            {
                click_timer_elapsed();
            }
            return 0;
        }
        break;
    // Shell_NotifyIcon can fail when we invoke it during the time explorer.exe isn't present/ready to handle it.
    // We'll also never receive wm_taskbar_restart message if the first call to Shell_NotifyIcon failed, so we use
    // WM_WINDOWPOSCHANGING which is always received on explorer startup sequence.
    case WM_WINDOWPOSCHANGING:
    {
        if (!tray_icon_created && s_tray_icon_visible && tray_icon_data.hWnd != nullptr)
        {
            apply_tray_icon_on_ui_thread(false);
            tray_icon_created = add_tray_icon_with_retry();
        }
        break;
    }
    default:
        if (message == wm_icon_notify)
        {
            switch (lparam)
            {
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
            {
                bool quick_access_enabled = get_general_settings().enableQuickAccess;

                Trace::TrayIconRightClick(quick_access_enabled);

                // Reload menu if Quick Access state has changed or is first time
                if (h_menu && (!last_quick_access_state.has_value() || quick_access_enabled != last_quick_access_state.value()))
                {
                    DestroyMenu(h_menu);
                    h_menu = nullptr;
                    h_sub_menu = nullptr;
                }

                last_quick_access_state = quick_access_enabled;

                if (!h_menu)
                {
                    h_menu = LoadMenu(reinterpret_cast<HINSTANCE>(&__ImageBase), MAKEINTRESOURCE(ID_TRAY_MENU));
                }
                if (h_menu)
                {
                    static std::wstring settings_menuitem_label = GET_RESOURCE_STRING(IDS_SETTINGS_MENU_TEXT);
                    static std::wstring settings_menuitem_label_leftclick = GET_RESOURCE_STRING(IDS_SETTINGS_MENU_TEXT_LEFTCLICK);
                    static std::wstring close_menuitem_label = GET_RESOURCE_STRING(IDS_CLOSE_MENU_TEXT);
                    static std::wstring documentation_menuitem_label = GET_RESOURCE_STRING(IDS_DOCUMENTATION_MENU_TEXT);
                    static std::wstring quick_access_menuitem_label = GET_RESOURCE_STRING(IDS_QUICK_ACCESS_MENU_TEXT);

                    // Update Settings menu text based on Quick Access state
                    if (quick_access_enabled)
                    {
                        change_menu_item_text(ID_SETTINGS_MENU_COMMAND, settings_menuitem_label.data());
                    }
                    else
                    {
                        change_menu_item_text(ID_SETTINGS_MENU_COMMAND, settings_menuitem_label_leftclick.data());
                    }

                    change_menu_item_text(ID_CLOSE_MENU_COMMAND, close_menuitem_label.data());
                    change_menu_item_text(ID_DOCUMENTATION_MENU_COMMAND, documentation_menuitem_label.data());
                    change_menu_item_text(ID_QUICK_ACCESS_MENU_COMMAND, quick_access_menuitem_label.data());

                    // Hide or show Quick Access menu item based on setting
                    if (!h_sub_menu)
                    {
                        h_sub_menu = GetSubMenu(h_menu, 0);
                    }
                    if (!quick_access_enabled)
                    {
                        // Remove Quick Access menu item when disabled
                        DeleteMenu(h_sub_menu, ID_QUICK_ACCESS_MENU_COMMAND, MF_BYCOMMAND);
                    }
                }
                if (!h_sub_menu)
                {
                    h_sub_menu = GetSubMenu(h_menu, 0);
                }

                // Dynamically add/remove "Update available" menu item and its separator
                DeleteMenu(h_sub_menu, ID_UPDATE_MENU_COMMAND, MF_BYCOMMAND);
                // Remove the separator right after the update item (position 0 after deletion)
                if (GetMenuItemCount(h_sub_menu) > 0)
                {
                    MENUITEMINFOW mii = { .cbSize = sizeof(mii), .fMask = MIIM_FTYPE };
                    if (GetMenuItemInfoW(h_sub_menu, 0, TRUE, &mii) && (mii.fType & MFT_SEPARATOR))
                    {
                        DeleteMenu(h_sub_menu, 0, MF_BYPOSITION);
                    }
                }
                if (update_available)
                {
                    InsertMenuW(h_sub_menu, 0, MF_BYPOSITION | MF_STRING, ID_UPDATE_MENU_COMMAND, GET_RESOURCE_STRING(IDS_UPDATE_AVAILABLE_MENU_TEXT).c_str());
                    InsertMenuW(h_sub_menu, 1, MF_BYPOSITION | MF_SEPARATOR, 0, nullptr);
                }

                POINT mouse_pointer;
                GetCursorPos(&mouse_pointer);
                SetForegroundWindow(window); // Needed for the context menu to disappear.
                TrackPopupMenu(h_sub_menu, TPM_CENTERALIGN | TPM_BOTTOMALIGN, mouse_pointer.x, mouse_pointer.y, 0, window, nullptr);
                break;
            }
            case WM_LBUTTONUP:
            {
                // ignore event if this is the second click of a double click
                if (!double_click_timer_running)
                {
                    // start timer for detecting single or double click
                    double_click_timer_running = true;
                    double_clicked = false;

                    // Keep click state and window actions on the tray UI thread.
                    // A window timer is cancelled on destruction; no detached
                    // callback can race Runner shutdown or the second click.
                    if (!SetTimer(window, tray_click_timer_id, GetDoubleClickTime(), nullptr))
                    {
                        Logger::warn("Could not create the tray click timer; handling the click immediately");
                        click_timer_elapsed();
                    }
                }
                break;
            }
            case WM_LBUTTONDBLCLK:
            {
                Trace::TrayIconDoubleClick(get_general_settings().enableQuickAccess);

                double_clicked = true;
                open_settings_window(std::nullopt);
                break;
            }
            break;
            }
        }
        else if (message == wm_run_on_main_ui_thread)
        {
            if (lparam != NULL)
            {
                const std::unique_ptr<run_on_main_ui_thread_msg> msg{ reinterpret_cast<run_on_main_ui_thread_msg*>(lparam) };
                msg->_callback(msg->data);
                lparam = NULL;
            }
            break;
        }
        else if (message == wm_taskbar_restart)
        {
            if (s_tray_icon_visible && tray_icon_data.hWnd != nullptr)
            {
                apply_tray_icon_on_ui_thread(false);
                tray_icon_created = add_tray_icon_with_retry();
            }
            break;
        }
    }
    return DefWindowProc(window, message, wparam, lparam);
}

namespace
{
    static HICON load_theme_adaptive_icon(Theme theme, bool has_update, int cx, int cy)
    {
        std::wstring icon_path = get_module_folderpath();
        if (theme == Theme::Dark)
        {
            icon_path += has_update ? L"\\svgs\\PowerToysWhiteUpdate.ico" : L"\\svgs\\PowerToysWhite.ico";
        }
        else
        {
            icon_path += has_update ? L"\\svgs\\PowerToysDarkUpdate.ico" : L"\\svgs\\PowerToysDark.ico";
        }
        Logger::trace(L"get_icon: Loading icon from path: {} ({}x{})", icon_path, cx, cy);

        HICON icon = static_cast<HICON>(LoadImageW(nullptr,
                                                   icon_path.c_str(),
                                                   IMAGE_ICON,
                                                   cx,
                                                   cy,
                                                   LR_LOADFROMFILE));
        if (!icon)
        {
            Logger::warn(L"get_icon: Failed to load icon from {}, error: {}", icon_path, GetLastError());
        }
        return icon;
    }

    static HICON load_default_tray_icon(bool has_update, int cx, int cy)
    {
        auto h_instance = reinterpret_cast<HINSTANCE>(&__ImageBase);
        const UINT flags = LR_SHARED | ((cx == 0 && cy == 0) ? LR_DEFAULTSIZE : 0);
        HICON icon = static_cast<HICON>(LoadImageW(h_instance,
                                                   MAKEINTRESOURCEW(has_update ? APPICON_UPDATE : APPICON),
                                                   IMAGE_ICON,
                                                   cx,
                                                   cy,
                                                   flags));
        if (!icon)
        {
            icon = LoadIconW(h_instance, MAKEINTRESOURCEW(has_update ? APPICON_UPDATE : APPICON));
        }
        if (!icon)
        {
            icon = LoadIconW(nullptr, IDI_APPLICATION);
        }
        return icon;
    }

    static UINT get_tray_icon_dpi()
    {
        if (tray_icon_hwnd != nullptr)
        {
            const UINT dpi = GetDpiForWindow(tray_icon_hwnd);
            if (dpi != 0)
            {
                return dpi;
            }
        }
        return USER_DEFAULT_SCREEN_DPI;
    }

    static bool apply_tray_icon_on_ui_thread(bool force_reload)
    {
        const Theme system_theme = ThemeHelpers::GetSystemTheme();
        const UINT dpi = get_tray_icon_dpi();

        if (!force_reload &&
            tray_icon_data.hIcon != nullptr &&
            applied_theme_adaptive == theme_adaptive_enabled &&
            applied_update_available == update_available &&
            (!theme_adaptive_enabled || applied_system_theme == system_theme) &&
            applied_icon_dpi == dpi)
        {
            return true;
        }

        int cx = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        int cy = GetSystemMetricsForDpi(SM_CYSMICON, dpi);
        if (cx <= 0 || cy <= 0)
        {
            cx = GetSystemMetrics(SM_CXSMICON);
            cy = GetSystemMetrics(SM_CYSMICON);
        }

        bool new_owned = false;
        HICON new_icon = nullptr;
        bool effective_adaptive = theme_adaptive_enabled;

        if (theme_adaptive_enabled)
        {
            new_icon = load_theme_adaptive_icon(system_theme, update_available, cx, cy);
            if (new_icon)
            {
                new_owned = true;
            }
            else
            {
                Logger::warn(L"apply_tray_icon_on_ui_thread: Failed to load theme adaptive icon, falling back to default");
                effective_adaptive = false;
            }
        }

        if (!new_icon)
        {
            new_icon = load_default_tray_icon(update_available, cx, cy);
        }

        if (!new_icon)
        {
            Logger::error(L"apply_tray_icon_on_ui_thread: Failed to load any tray icon");
            return false;
        }

        HICON previous_owned = owned_tray_icon;
        tray_icon_data.hIcon = new_icon;
        tray_icon_data.uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE;
        owned_tray_icon = new_owned ? new_icon : nullptr;
        applied_system_theme = system_theme;
        applied_theme_adaptive = effective_adaptive;
        applied_update_available = update_available;
        applied_icon_dpi = dpi;

        BOOL modify_result = TRUE;
        if (tray_icon_created)
        {
            modify_result = Shell_NotifyIcon(NIM_MODIFY, &tray_icon_data);
            if (!modify_result)
            {
                Logger::warn(L"apply_tray_icon_on_ui_thread: Shell_NotifyIcon(NIM_MODIFY) failed, error={:#010x}", GetLastError());
            }
        }

        if (previous_owned != nullptr && previous_owned != new_icon)
        {
            DestroyIcon(previous_owned);
        }

        Logger::info(L"tray_icon: Applied icon (theme_adaptive={}, system_theme={}, update_available={}, dpi={}, size={}x{}, tray_created={}, modify_ok={})",
                     effective_adaptive,
                     system_theme == Theme::Dark ? L"dark" : L"light",
                     update_available,
                     dpi,
                     cx,
                     cy,
                     tray_icon_created,
                     modify_result != FALSE);
        return modify_result != FALSE;
    }

    static void handle_theme_change_on_ui_thread(PVOID)
    {
        if (theme_adaptive_enabled)
        {
            apply_tray_icon_on_ui_thread(false);
        }
    }

    static void handle_theme_change()
    {
        dispatch_run_on_main_ui_thread(&handle_theme_change_on_ui_thread, nullptr);
    }
}

void start_tray_icon(bool isProcessElevated, bool theme_adaptive)
{
    tray_ui_thread_id = GetCurrentThreadId();
    theme_adaptive_enabled = theme_adaptive;
    auto h_instance = reinterpret_cast<HINSTANCE>(&__ImageBase);

    update_available = false;

    HICON class_icon = load_default_tray_icon(false, 0, 0);
    {
        UINT id_tray_icon = 1;

        WNDCLASS wc = {};
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.hInstance = h_instance;
        wc.lpszClassName = pt_tray_icon_window_class;
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = tray_icon_window_proc;
        wc.hIcon = class_icon;
        RegisterClass(&wc);
        auto hwnd = CreateWindowW(wc.lpszClassName,
                                  pt_tray_icon_window_class,
                                  WS_OVERLAPPEDWINDOW | WS_POPUP,
                                  CW_USEDEFAULT,
                                  CW_USEDEFAULT,
                                  CW_USEDEFAULT,
                                  CW_USEDEFAULT,
                                  nullptr,
                                  nullptr,
                                  wc.hInstance,
                                  nullptr);
        Logger::info(L"start_tray_icon: CreateWindowW(class={}, name={}) returned hwnd={}", wc.lpszClassName, pt_tray_icon_window_class, (void*)hwnd);
        if (!hwnd) winrt::throw_last_error();
        tray_icon_hwnd = hwnd;
        memset(&tray_icon_data, 0, sizeof(tray_icon_data));
        tray_icon_data.cbSize = sizeof(tray_icon_data);
        tray_icon_data.hWnd = hwnd;
        tray_icon_data.uID = id_tray_icon;
        tray_icon_data.uCallbackMessage = wm_icon_notify;

        std::wstringstream pt_version_tooltip_stream;
        if (isProcessElevated)
        {
            pt_version_tooltip_stream << GET_RESOURCE_STRING(IDS_TRAY_ICON_ADMIN_TOOLTIP) << L": ";
        }

        pt_version_tooltip_stream << L"Kit " << get_product_version() << '\0';
        std::wstring pt_version_tooltip = pt_version_tooltip_stream.str();
        wcscpy_s(tray_icon_data.szTip, sizeof(tray_icon_data.szTip) / sizeof(WCHAR), pt_version_tooltip.c_str());
        tray_icon_data.uFlags = NIF_ICON | NIF_TIP | NIF_MESSAGE;
        ChangeWindowMessageFilterEx(hwnd, settings_activation_message(), MSGFLT_ALLOW, nullptr);

        // Subscribe before sampling the initial theme so a concurrent LightSwitch
        // or Windows theme change during startup cannot be missed.
        theme_listener.AddSystemThemeChangedHandler(&handle_theme_change);

        apply_tray_icon_on_ui_thread(true);

        if (s_tray_icon_visible)
        {
            tray_icon_created = add_tray_icon_with_retry();
            // Re-check after NIM_ADD in case the system theme flipped while Explorer
            // was registering the tray icon.
            apply_tray_icon_on_ui_thread(false);
        }
    }
}

void set_tray_icon_visible(bool shouldIconBeVisible)
{
    if (tray_icon_hwnd != nullptr && tray_ui_thread_id != 0 && GetCurrentThreadId() != tray_ui_thread_id)
    {
        auto value = std::make_unique<bool>(shouldIconBeVisible);
        if (dispatch_run_on_main_ui_thread([](PVOID data) {
                std::unique_ptr<bool> visible{ static_cast<bool*>(data) };
                set_tray_icon_visible(*visible);
            }, value.get()))
        {
            value.release();
        }
        return;
    }

    s_tray_icon_visible = shouldIconBeVisible;
    if (shouldIconBeVisible)
    {
        if (!tray_icon_created && tray_icon_data.hWnd != nullptr)
        {
            apply_tray_icon_on_ui_thread(false);
            tray_icon_created = add_tray_icon_with_retry();
        }
    }
    else
    {
        if (tray_icon_created)
        {
            Shell_NotifyIcon(NIM_DELETE, &tray_icon_data);
            tray_icon_created = false;
        }
    }
}

void set_tray_icon_update_available(bool available)
{
    if (tray_icon_hwnd != nullptr && tray_ui_thread_id != 0 && GetCurrentThreadId() != tray_ui_thread_id)
    {
        auto value = std::make_unique<bool>(available);
        if (dispatch_run_on_main_ui_thread([](PVOID data) {
                std::unique_ptr<bool> flag{ static_cast<bool*>(data) };
                set_tray_icon_update_available(*flag);
            }, value.get()))
        {
            value.release();
        }
        return;
    }

    if (update_available == available)
    {
        return;
    }

    update_available = available;
    Logger::info(L"set_tray_icon_update_available: update_available={}", update_available);
    apply_tray_icon_on_ui_thread(true);
}

void set_tray_icon_theme_adaptive(bool theme_adaptive)
{
    if (tray_icon_hwnd != nullptr && tray_ui_thread_id != 0 && GetCurrentThreadId() != tray_ui_thread_id)
    {
        auto value = std::make_unique<bool>(theme_adaptive);
        if (dispatch_run_on_main_ui_thread([](PVOID data) {
                std::unique_ptr<bool> flag{ static_cast<bool*>(data) };
                set_tray_icon_theme_adaptive(*flag);
            }, value.get()))
        {
            value.release();
        }
        return;
    }

    Logger::info(L"set_tray_icon_theme_adaptive: Called with theme_adaptive={}, current theme_adaptive_enabled={}",
                 theme_adaptive, theme_adaptive_enabled);

    theme_adaptive_enabled = theme_adaptive;
    if (tray_icon_hwnd != nullptr)
    {
        apply_tray_icon_on_ui_thread(true);
    }
}

void stop_tray_icon()
{
    if (tray_icon_created)
    {
        SendMessage(tray_icon_hwnd, WM_CLOSE, 0, 0);
    }
}

bool is_system_session_ending()
{
    return g_system_session_ending;
}
