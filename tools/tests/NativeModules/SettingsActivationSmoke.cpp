// Isolated regression host. The two activation functions are extracted unchanged
// from settings_window.cpp by Invoke-SettingsActivationSmoke.ps1 at compile time.
#define NOMINMAX
#include <windows.h>
#include <crtdbg.h>
#include <atomic>
#include <exception>
#include <iostream>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>
#include "runner/settings_window_startup.h"

struct Logger
{
    template<typename... T> static void info(T&&...) {}
    template<typename... T> static void warn(T&&...) {}
    template<typename... T> static void error(T&&...) {}
};

struct TestHandle
{
    HANDLE value = nullptr;
    ~TestHandle() { if (value) CloseHandle(value); }
    explicit operator bool() const { return value != nullptr; }
    void reset(HANDLE handle) { if (value) CloseHandle(value); value = handle; }
};

struct TwoWayPipeMessageIPC
{
    std::vector<std::wstring> messages;
    void send(const std::wstring& message) { messages.push_back(message); }
};

SettingsWindowStartup settings_window_startup;
TwoWayPipeMessageIPC* current_settings_ipc = nullptr;
std::mutex ipc_mutex;
std::mutex settings_launch_mutex;
std::atomic_bool g_isLaunchInProgress = false;
bool g_settings_shutdown_requested = false;
bool g_settings_process_closed = true;
DWORD g_settings_process_id = 0;
TestHandle settings_shutdown_event;
std::thread settings_thread;
std::atomic_int unexpectedLaunches = 0;
void run_settings_window(std::optional<std::wstring>) { ++unexpectedLaunches; }
std::wstring get_last_error_or_default(DWORD) { return L"isolated test error"; }

#include "SettingsActivation.production.inc"

namespace
{
    TestHandle probeRequest;
    TestHandle probeComplete;
    std::atomic_bool stopProbe = false;
    std::atomic_bool ipcWasFree = false;
    std::atomic_bool launchWasFree = false;
    bool observeActivation = false;
    bool requestReentry = false;
    bool reentered = false;
    bool lockViolation = false;
    bool probeTimedOut = false;
    int activationMessages = 0;
    std::string reentryError;

    void Require(bool condition, const char* message)
    {
        if (!condition) throw std::runtime_error(message);
    }

    // Only this auxiliary thread calls try_lock: the UI thread may currently own
    // either mutex in the production code, so probing there would be undefined.
    void ProbeThread()
    {
        while (WaitForSingleObject(probeRequest.value, INFINITE) == WAIT_OBJECT_0)
        {
            if (stopProbe) return;
            const bool ipcFree = ipc_mutex.try_lock();
            if (ipcFree) ipc_mutex.unlock();
            const bool launchFree = settings_launch_mutex.try_lock();
            if (launchFree) settings_launch_mutex.unlock();
            ipcWasFree = ipcFree;
            launchWasFree = launchFree;
            SetEvent(probeComplete.value);
        }
    }

    LRESULT CALLBACK TestWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (observeActivation && (message == WM_SHOWWINDOW || message == WM_WINDOWPOSCHANGING || message == WM_ACTIVATE))
        {
            ++activationMessages;
            ResetEvent(probeComplete.value);
            SetEvent(probeRequest.value);
            if (WaitForSingleObject(probeComplete.value, 2000) != WAIT_OBJECT_0)
            {
                probeTimedOut = true;
            }
            else if (!ipcWasFree || !launchWasFree)
            {
                lockViolation = true;
            }
            else if (requestReentry && !reentered)
            {
                reentered = true;
                // A synchronous activation callback can reenter Runner dispatch.
                // Enter once only and only after the independent lock probe passes.
                try { open_settings_window(std::nullopt); }
                catch (const std::exception& error) { reentryError = error.what(); }
            }
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    void Activate(HWND window, TwoWayPipeMessageIPC& ipc, const char* name,
                  std::optional<std::wstring> target, bool hasIpc, int initialShow, bool nested = false, bool requireCallback = true)
    {
        observeActivation = false;
        ShowWindow(window, initialShow);
        ipc.messages.clear();
        current_settings_ipc = hasIpc ? &ipc : nullptr;
        activationMessages = 0;
        lockViolation = probeTimedOut = reentered = false;
        reentryError.clear();
        requestReentry = nested;
        observeActivation = true;
        std::cout << "CASE " << name << std::endl;
        try { open_settings_window(target); }
        catch (...) { observeActivation = false; throw; }
        observeActivation = false;
        Require(!probeTimedOut, "Lock probe timed out during activation callback.");
        Require(!lockViolation, "Activation callback ran while ipc_mutex or settings_launch_mutex was held.");
        Require(IsWindowVisible(window) && !IsIconic(window), "Existing test window was not restored and shown.");
        if (requireCallback) Require(activationMessages > 0, "No real Win32 activation callback was observed.");
        Require(unexpectedLaunches == 0 && !settings_thread.joinable(), "Existing-window activation entered the process launch path.");
        if (hasIpc)
        {
            const std::wstring expected = L"{\"ShowYourself\":\"" + target.value_or(L"Dashboard") + L"\"}";
            Require(ipc.messages.size() == (nested ? 2U : 1U), "Unexpected ShowYourself message count.");
            Require(ipc.messages.front() == expected, "The existing-window IPC route was not preserved.");
            if (nested) Require(ipc.messages.back() == L"{\"ShowYourself\":\"Dashboard\"}", "Reentry did not retain the Dashboard route.");
        }
        else Require(ipc.messages.empty(), "No-IPC activation must not synthesize an IPC send.");
        if (nested) Require(reentered && reentryError.empty(), "Synchronous activation reentry failed.");
        std::cout << "PASS " << name << "; activation_callbacks=" << activationMessages
                  << "; lock_probe=" << (activationMessages > 0 ? "both_mutexes_free" : "not_observed_already_visible_no_state_change")
                  << "; route_messages=" << ipc.messages.size() << std::endl;
    }
}

int main()
{
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE);
    _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
    probeRequest.reset(CreateEventW(nullptr, FALSE, FALSE, nullptr));
    probeComplete.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!probeRequest || !probeComplete) return 2;
    std::thread observer(ProbeThread);
    HWND window = nullptr;
    int result = 0;
    try
    {
        WNDCLASSW windowClass{};
        windowClass.hInstance = GetModuleHandleW(nullptr);
        windowClass.lpfnWndProc = TestWindowProc;
        windowClass.lpszClassName = L"KitSettingsActivationIsolatedSmoke";
        Require(RegisterClassW(&windowClass) != 0, "Could not register isolated test window.");
        window = CreateWindowExW(0, windowClass.lpszClassName, L"Isolated Settings activation regression",
                                 WS_OVERLAPPEDWINDOW, 80, 80, 380, 220, nullptr, nullptr, windowClass.hInstance, nullptr);
        Require(window != nullptr, "Could not create isolated test window.");
        g_settings_process_id = GetCurrentProcessId();
        settings_window_startup.CompleteStartup();
        TwoWayPipeMessageIPC ipc;
        Activate(window, ipc, "existing-winget-hidden", L"Winget", true, SW_HIDE);
        Activate(window, ipc, "existing-winget-visible", L"Winget", true, SW_SHOW, false, false);
        Activate(window, ipc, "existing-dashboard-default", std::nullopt, true, SW_HIDE);
        Activate(window, ipc, "existing-without-ipc", L"Winget", false, SW_HIDE);
        Activate(window, ipc, "existing-minimized", L"Winget", true, SW_MINIMIZE);
        Activate(window, ipc, "activation-callback-reentry", L"Winget", true, SW_HIDE, true);
        for (int iteration = 0; iteration < 12; ++iteration)
            Activate(window, ipc, "repeated-activation", L"Winget", true, iteration % 2 ? SW_MINIMIZE : SW_HIDE);
        std::cout << "PASS: 18 real existing-window activation cases; no Runner, Settings process, configuration or notifications used." << std::endl;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL: " << error.what() << std::endl;
        result = 1;
    }
    observeActivation = false;
    if (window) DestroyWindow(window);
    stopProbe = true;
    SetEvent(probeRequest.value);
    observer.join();
    if (settings_thread.joinable()) settings_thread.join();
    return result;
}
