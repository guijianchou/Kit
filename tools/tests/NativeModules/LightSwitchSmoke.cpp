#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <memory>
#include <cstdlib>
#include <string>
#include <string_view>

constexpr wchar_t StopEventName[] = L"Local\\KitLightSwitchServiceStopEvent-09b983c3-01df-4490-9f84-9f6e5c52c7d5";

std::wstring WorkerReadyEventName(DWORD pid)
{
    return L"Local\\KitReviewSmoke-LightSwitchReady-" + std::to_wstring(pid);
}

struct HandleCloser
{
    void operator()(void* handle) const noexcept
    {
        if (handle && handle != INVALID_HANDLE_VALUE)
            CloseHandle(handle);
    }
};
using ScopedHandle = std::unique_ptr<void, HandleCloser>;

#if defined(KIT_REVIEW_FAKE_WORKER)

// Built as a GUI-subsystem executable: no console window or theme API is used.
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv)
        return static_cast<int>(GetLastError());

    DWORD parentPid = 0;
    for (int i = 1; i + 1 < argc; ++i)
    {
        if (std::wstring_view(argv[i]) == L"--pid")
            parentPid = wcstoul(argv[++i], nullptr, 10);
    }
    LocalFree(argv);
    if (!parentPid)
        return ERROR_INVALID_PARAMETER;

    ScopedHandle parent(OpenProcess(SYNCHRONIZE, FALSE, parentPid));
    ScopedHandle stop(OpenEventW(SYNCHRONIZE, FALSE, StopEventName));
    if (!parent || !stop)
        return ERROR_INVALID_HANDLE;

    ScopedHandle ready(CreateEventW(nullptr, TRUE, TRUE, WorkerReadyEventName(GetCurrentProcessId()).c_str()));
    if (!ready)
        return static_cast<int>(GetLastError());

    HANDLE waits[] = { stop.get(), parent.get() };
    return WaitForMultipleObjects(2, waits, FALSE, INFINITE) == WAIT_FAILED
        ? static_cast<int>(GetLastError())
        : 0;
}

#else

#include <tlhelp32.h>
#include <roapi.h>
#include <optional>
#include <interface/kit_module_interface.h>
#include <chrono>
#include <filesystem>
#include <iostream>
#include <regex>
#include <stdexcept>
#include <vector>

namespace fs = std::filesystem;
using Clock = std::chrono::steady_clock;

void Check(bool condition, const char* message)
{
    if (!condition)
        throw std::runtime_error(message);
}

std::wstring ProcessPath(HANDLE process)
{
    std::wstring path(32768, L'\0');
    DWORD count = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(process, 0, path.data(), &count))
        return {};
    path.resize(count);
    return path;
}

bool SamePath(const std::wstring& left, const std::wstring& right)
{
    return _wcsicmp(left.c_str(), right.c_str()) == 0;
}

struct WorkerInfo
{
    DWORD pid;
    FILETIME created;
};

std::vector<WorkerInfo> FindOwnedWorkers(const std::wstring& expectedPath)
{
    ScopedHandle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    Check(snapshot.get() != INVALID_HANDLE_VALUE, "Cannot take the process snapshot.");
    PROCESSENTRY32W entry{ sizeof(entry) };
    std::vector<WorkerInfo> workers;
    if (!Process32FirstW(snapshot.get(), &entry))
        return workers;

    do
    {
        // Open/query only the exact fake-worker image and this harness's direct children.
        if (_wcsicmp(entry.szExeFile, L"Kit.LightSwitchService.exe") != 0 ||
            entry.th32ParentProcessID != GetCurrentProcessId())
            continue;

        ScopedHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, entry.th32ProcessID));
        if (!process || WaitForSingleObject(process.get(), 0) != WAIT_TIMEOUT)
            continue;
        if (!SamePath(ProcessPath(process.get()), expectedPath))
            continue;
        FILETIME created{}, exited{}, kernel{}, user{};
        if (!GetProcessTimes(process.get(), &created, &exited, &kernel, &user))
            continue;
        workers.push_back({ entry.th32ProcessID, created });
    } while (Process32NextW(snapshot.get(), &entry));
    return workers;
}

std::vector<WorkerInfo> WaitForCount(const std::wstring& path, size_t count, DWORD timeoutMs = 6000)
{
    const auto deadline = Clock::now() + std::chrono::milliseconds(timeoutMs);
    do
    {
        auto workers = FindOwnedWorkers(path);
        Check(workers.size() <= 1, "More than one owned fake worker is alive.");
        if (workers.size() == count)
            return workers;
        Sleep(20);
    } while (Clock::now() < deadline);
    const auto remaining = FindOwnedWorkers(path);
    std::cerr << "Expected worker count " << count << ", observed " << remaining.size();
    for (const auto& worker : remaining)
        std::cerr << " PID=" << worker.pid;
    std::cerr << '\n';
    throw std::runtime_error("Timed out waiting for the expected fake-worker count.");
}

void StableCount(const std::wstring& path, size_t expected, DWORD durationMs = 300)
{
    const auto deadline = Clock::now() + std::chrono::milliseconds(durationMs);
    do
    {
        Check(FindOwnedWorkers(path).size() == expected, "The fake-worker count was not stable.");
        Sleep(20);
    } while (Clock::now() < deadline);
}

WorkerInfo WaitForReadyWorker(const std::wstring& path)
{
    const auto deadline = Clock::now() + std::chrono::seconds(10);
    do
    {
        const auto workers = FindOwnedWorkers(path);
        Check(workers.size() <= 1, "More than one owned fake worker is alive.");
        if (workers.size() == 1)
        {
            ScopedHandle ready(OpenEventW(SYNCHRONIZE, FALSE, WorkerReadyEventName(workers.front().pid).c_str()));
            if (ready && WaitForSingleObject(ready.get(), 0) == WAIT_OBJECT_0)
                return workers.front();
        }
        Sleep(20);
    } while (Clock::now() < deadline);
    throw std::runtime_error("Timed out waiting for the fake worker to initialize its parent and stop handles.");
}

void StopExactFakeWorker(const std::wstring& path, const WorkerInfo& worker)
{
    ScopedHandle process(OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, worker.pid));
    Check(static_cast<bool>(process), "Cannot open the owned fake worker.");
    Check(SamePath(ProcessPath(process.get()), path), "Refusing to terminate a process outside the exact fake-worker path.");
    FILETIME created{}, exited{}, kernel{}, user{};
    Check(GetProcessTimes(process.get(), &created, &exited, &kernel, &user) != FALSE, "Cannot verify the fake-worker creation time.");
    Check(CompareFileTime(&created, &worker.created) == 0, "Refusing to terminate a reused PID.");
    Check(TerminateProcess(process.get(), 73) != FALSE, "Cannot simulate the fake-worker crash.");
    Check(WaitForSingleObject(process.get(), 5000) == WAIT_OBJECT_0, "The crashed fake worker did not exit.");
}

void SetMode(KitModuleIface* module, std::wstring_view mode)
{
    // Both theme targets stay false in every test configuration.
    std::wstring config = LR"({"name":"LightSwitch","version":"1.0","properties":{"changeSystem":{"value":false},"changeApps":{"value":false},"scheduleMode":{"value":")";
    config += mode;
    config += LR"("},"lightTime":{"value":480},"darkTime":{"value":1200},"sunrise_offset":{"value":0},"sunset_offset":{"value":0},"latitude":{"value":"0.0"},"longitude":{"value":"0.0"},"toggle-theme-hotkey":{"value":{"win":true,"ctrl":true,"shift":true,"alt":false,"code":68}}}})";
    module->set_config(config.c_str());
}

struct ThemeValues
{
    LSTATUS status[3]{};
    DWORD value[3]{};
};

ThemeValues ReadThemeValues()
{
    ThemeValues result;
    const wchar_t* names[] = { L"AppsUseLightTheme", L"SystemUsesLightTheme", L"ColorPrevalence" };
    for (size_t i = 0; i < 3; ++i)
    {
        DWORD size = sizeof(DWORD);
        result.status[i] = RegGetValueW(HKEY_CURRENT_USER,
            L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
            names[i], RRF_RT_REG_DWORD, nullptr, &result.value[i], &size);
    }
    return result;
}

void CheckThemeUnchanged(const ThemeValues& before)
{
    const auto after = ReadThemeValues();
    for (size_t i = 0; i < 3; ++i)
    {
        Check(before.status[i] == after.status[i] && before.value[i] == after.value[i],
            "Theme registry values changed during the smoke test; no theme values were restored automatically.");
    }
}

void ProtectTestChildren()
{
    // The non-inheritable job handle deliberately lives until process exit.
    // Closing it earlier would also terminate this harness. Windows closes it on
    // normal exit or on the PowerShell watchdog's exact-process termination.
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    Check(job != nullptr, "Cannot create the test-only child-cleanup job.");
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) ||
        !AssignProcessToJobObject(job, GetCurrentProcess()))
    {
        CloseHandle(job);
        throw std::runtime_error("Cannot confine this harness and its own children to the cleanup job.");
    }
}

void RunLifecycle(const std::wstring& dllPath)
{
    const auto fakePath = (fs::path(dllPath).parent_path() / L"LightSwitchService" / L"Kit.LightSwitchService.exe").wstring();
    HMODULE library = LoadLibraryExW(dllPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    Check(library != nullptr, "Cannot load the copied LightSwitchModuleInterface DLL.");
    KitModuleIface* module = nullptr;
    try
    {
        const auto create = reinterpret_cast<kit_create_func>(GetProcAddress(library, "kit_create"));
        Check(create != nullptr, "The module factory export is missing.");
        module = create();
        Check(module != nullptr && !module->is_enabled(), "The module was not created disabled.");

        // Refuse to dispatch any action unless the real DLL loaded the controlled file.
        int capacity = 0;
        module->get_config(nullptr, &capacity);
        Check(capacity > 0 && capacity < 1048576, "The module returned an invalid settings buffer size.");
        std::vector<wchar_t> buffer(static_cast<size_t>(capacity), L'\0');
        Check(module->get_config(buffer.data(), &capacity), "Cannot read the module's loaded configuration.");
        const std::wstring loadedConfig(buffer.data());
        Check(std::regex_search(loadedConfig, std::wregex(LR"("changeSystem"\s*:\s*\{[^}]*"value"\s*:\s*false)")) &&
            std::regex_search(loadedConfig, std::wregex(LR"("changeApps"\s*:\s*\{[^}]*"value"\s*:\s*false)")) &&
            std::regex_search(loadedConfig, std::wregex(LR"("value"\s*:\s*"Off")")),
            "The DLL did not load the controlled Off/false/false configuration; no actions were dispatched.");

        module->enable();
        Check(module->is_enabled(), "Off mode did not keep the module enabled.");
        for (int i = 0; i < 8; ++i)
            module->enable();
        StableCount(fakePath, 0);
        Check(module->get_hotkeys(nullptr, 0) == 0 && !module->on_hotkey(0), "Removed manual hotkeys were unexpectedly exposed.");
        StableCount(fakePath, 0);
        std::cout << "PASS Off cold start and repeated enable: no worker; removed hotkey stays unavailable.\n";

        SetMode(module, L"FixedHours");
        auto first = WaitForReadyWorker(fakePath);
        std::cout << "DIAG first worker PID=" << first.pid << " tick=" << GetTickCount64() << '\n';
        for (int i = 0; i < 8; ++i)
            module->enable();
        StableCount(fakePath, 1);
        const auto sameWorker = WaitForReadyWorker(fakePath);
        Check(first.pid == sameWorker.pid && CompareFileTime(&first.created, &sameWorker.created) == 0,
            "Repeated enable replaced the live worker.");
        std::cout << "PASS Active schedule and repeated enable: one unchanged worker.\n";

        std::cout << "DIAG before crash PID=" << first.pid << " tick=" << GetTickCount64() << '\n';
        StopExactFakeWorker(fakePath, first);
        std::cout << "DIAG terminated first worker; waiting for zero tick=" << GetTickCount64() << '\n';
        WaitForCount(fakePath, 0);
        std::cout << "DIAG crash cleanup observed; dispatching recovery tick=" << GetTickCount64() << '\n';
        const auto actionStart = Clock::now();
        module->enable();
        const auto actionUs = std::chrono::duration_cast<std::chrono::microseconds>(Clock::now() - actionStart).count();
        const auto recovered = WaitForReadyWorker(fakePath);
        Check(first.pid != recovered.pid || CompareFileTime(&first.created, &recovered.created) != 0,
            "Recovery did not create a new worker.");
        std::cout << "PASS Worker crash recovery on enable; callback returned in " << actionUs << " us.\n";

        for (int i = 0; i < 8; ++i)
        {
            SetMode(module, L"Off");
            Sleep(5);
            SetMode(module, L"FixedHours");
            module->enable();
            Check(FindOwnedWorkers(fakePath).size() <= 1, "Rapid mode changes created duplicate workers.");
        }
        WaitForReadyWorker(fakePath);
        StableCount(fakePath, 1);
        SetMode(module, L"Off");
        WaitForCount(fakePath, 0);
        StableCount(fakePath, 0);
        std::cout << "PASS Rapid Off/active changes converge to the final mode without duplicate workers.\n";

        module->disable();
        module->disable();
        Check(!module->is_enabled() && !module->on_hotkey(0), "Disabled module still accepts actions.");
        module->enable();
        module->enable();
        StableCount(fakePath, 0);
        SetMode(module, L"FixedHours");
        WaitForReadyWorker(fakePath);
        module->disable();
        module->disable();
        WaitForCount(fakePath, 0);
        Check(!module->is_enabled(), "Repeated disable did not leave the module disabled.");
        std::cout << "PASS Off re-enable and repeated active disable clean up the worker.\n";

        module->enable();
        WaitForReadyWorker(fakePath);
        module->destroy();
        module = nullptr;
        WaitForCount(fakePath, 0);
        std::cout << "PASS destroy() alone cleans up an active worker.\n";
    }
    catch (...)
    {
        if (module)
            module->destroy();
        FreeLibrary(library);
        throw;
    }
    FreeLibrary(library);
}

DWORD LaunchRealWorker(const std::wstring& path, DWORD parentPid)
{
    std::wstring command = L"\"" + path + L"\" --pid " + std::to_wstring(parentPid);
    STARTUPINFOW startup{ sizeof(startup) };
    PROCESS_INFORMATION info{};
    Check(CreateProcessW(path.c_str(), command.data(), nullptr, nullptr, FALSE,
        CREATE_NO_WINDOW, nullptr, nullptr, &startup, &info) != FALSE, "Cannot launch the exact real-worker path.");
    ScopedHandle process(info.hProcess);
    ScopedHandle thread(info.hThread);
    if (WaitForSingleObject(process.get(), 6000) != WAIT_OBJECT_0)
    {
        // This is the handle returned by this harness's own CreateProcess call.
        TerminateProcess(process.get(), 74);
        WaitForSingleObject(process.get(), 5000);
        throw std::runtime_error("The real-worker early-exit check timed out; its exact process was stopped.");
    }
    DWORD code = 0;
    Check(GetExitCodeProcess(process.get(), &code) != FALSE, "Cannot read the real-worker exit code.");
    return code;
}

void RunRealWorkerChecks(const std::wstring& path)
{
    ScopedHandle stop(CreateEventW(nullptr, TRUE, FALSE, StopEventName));
    Check(static_cast<bool>(stop), "Cannot create the isolated real-worker stop event.");
    Check(SetEvent(stop.get()) != FALSE, "Cannot signal the early-stop event.");
    Check(LaunchRealWorker(path, GetCurrentProcessId()) == 0, "The pre-stopped real worker did not exit successfully.");
    std::cout << "PASS Real worker exits with a pre-signaled stop event.\n";

    Check(ResetEvent(stop.get()) != FALSE, "Cannot reset the isolated stop event.");
    constexpr DWORD invalidParent = 2147483647;
    ScopedHandle unexpectedParent(OpenProcess(SYNCHRONIZE, FALSE, invalidParent));
    Check(!unexpectedParent, "The selected invalid parent PID is unexpectedly present.");
    const auto code = LaunchRealWorker(path, invalidParent);
    Check(code != 0, "The real worker reported success for a missing parent process.");
    std::cout << "PASS Real worker propagates parent-binding failure (exit " << code << ").\n";
}

int wmain(int argc, wchar_t* argv[])
{
    std::cout << std::unitbuf;
    wchar_t guarded[2]{};
    if (argc != 4 || GetEnvironmentVariableW(L"KIT_REVIEW_SMOKE_GUARDED", guarded, 2) != 1 || guarded[0] != L'1')
    {
        std::cerr << "Use the guarded Invoke-LightSwitchSmoke.ps1 wrapper.\n";
        return 2;
    }
    try
    {
        Check(SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS) != FALSE, "Cannot configure test DLL search directories.");
        const auto cookie = AddDllDirectory(argv[3]);
        Check(cookie != nullptr, "Cannot add the verified product-output dependency directory.");
        ProtectTestChildren();
        Check(SUCCEEDED(RoInitialize(RO_INIT_MULTITHREADED)), "Cannot initialize WinRT for the module settings API.");
        const auto before = ReadThemeValues();
        if (std::wstring_view(argv[1]) == L"--lifecycle")
            RunLifecycle(argv[2]);
        else if (std::wstring_view(argv[1]) == L"--real-worker")
            RunRealWorkerChecks(argv[2]);
        else
            throw std::runtime_error("Unknown smoke-test mode.");
        CheckThemeUnchanged(before);
        std::cout << "PASS Theme registry values unchanged.\n";
        RoUninitialize();
        RemoveDllDirectory(cookie);
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL " << error.what() << '\n';
        return 1;
    }
    catch (...)
    {
        std::cerr << "FAIL An unexpected native or WinRT exception occurred.\n";
        return 1;
    }
}

#endif
