// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Loads the actual built module DLL and observes its real child process handles.
// This harness never sends a package command or invokes a modeled implementation.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <Objbase.h>
#include <TlHelp32.h>
#include <interface/kit_module_interface.h>
#include <algorithm>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using Clock = std::chrono::steady_clock;

void Require(bool condition, const char* message)
{
    if (!condition)
    {
        throw std::runtime_error(message);
    }
}

class ChildTracker
{
    struct Child
    {
        DWORD pid;
        HANDLE process;
    };

    const std::wstring expectedPath;
    std::mutex gate;
    std::vector<Child> children;
    std::ofstream ownership;
    bool invalidChild = false;
    std::jthread monitor;

public:
    ChildTracker(std::wstring path, const std::filesystem::path& report)
        : expectedPath(std::move(path)), ownership(report, std::ios::out | std::ios::trunc)
    {
        Require(ownership.good(), "Cannot open the owned process report.");
        monitor = std::jthread([this](std::stop_token token) {
            while (!token.stop_requested())
            {
                Scan();
                Sleep(10);
            }
        });
    }

    ~ChildTracker()
    {
        monitor.request_stop();
        monitor.join();
        std::lock_guard lock(gate);
        for (const auto& child : children)
        {
            // Only handles captured for our own exact-image direct children can be killed.
            if (WaitForSingleObject(child.process, 0) == WAIT_TIMEOUT)
            {
                TerminateProcess(child.process, 9);
                WaitForSingleObject(child.process, 5000);
            }
            CloseHandle(child.process);
        }
    }

    void Scan()
    {
        std::lock_guard lock(gate);
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            invalidChild = true;
            return;
        }
        PROCESSENTRY32W entry{ sizeof(entry) };
        if (Process32FirstW(snapshot, &entry))
        {
            do
            {
                if (entry.th32ParentProcessID != GetCurrentProcessId()
                    || _wcsicmp(entry.szExeFile, L"Kit.WingetService.exe") != 0
                    || std::any_of(children.begin(), children.end(), [&](const Child& child) { return child.pid == entry.th32ProcessID; }))
                {
                    continue;
                }
                HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE | PROCESS_TERMINATE, FALSE, entry.th32ProcessID);
                if (!process)
                {
                    continue; // An immediately destroyed service may already have exited.
                }
                wchar_t buffer[32768];
                DWORD length = static_cast<DWORD>(std::size(buffer));
                if (!QueryFullProcessImageNameW(process, 0, buffer, &length)
                    || _wcsicmp(buffer, expectedPath.c_str()) != 0)
                {
                    CloseHandle(process);
                    invalidChild = true;
                    continue;
                }
                FILETIME created{}, exited{}, kernel{}, user{};
                if (!GetProcessTimes(process, &created, &exited, &kernel, &user))
                {
                    CloseHandle(process);
                    invalidChild = true;
                    continue;
                }
                const ULONGLONG creationTime = (static_cast<ULONGLONG>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
                children.push_back({ entry.th32ProcessID, process });
                ownership << entry.th32ProcessID << '\t' << creationTime << std::endl;
            } while (Process32NextW(snapshot, &entry));
        }
        CloseHandle(snapshot);
    }

    size_t Active()
    {
        Scan();
        std::lock_guard lock(gate);
        Require(!invalidChild, "A child process identity or enumeration check failed.");
        return static_cast<size_t>(std::count_if(children.begin(), children.end(), [](const Child& child) {
            return WaitForSingleObject(child.process, 0) == WAIT_TIMEOUT;
        }));
    }

    size_t Observed()
    {
        Scan();
        std::lock_guard lock(gate);
        return children.size();
    }

    void WaitForCount(size_t expected, DWORD timeoutMs)
    {
        const auto deadline = Clock::now() + std::chrono::milliseconds(timeoutMs);
        while (Clock::now() < deadline)
        {
            if (Active() == expected)
            {
                return;
            }
            Sleep(20);
        }
        Require(false, "The expected owned service process count was not reached.");
    }
};

int wmain(int argc, wchar_t** argv)
{
    if (argc != 4)
    {
        std::cerr << "Usage: WingetNativeSmoke.exe <actual-module.dll> <owned-processes.tsv> <max-enable-ms>\n";
        return 2;
    }

    HMODULE library = nullptr;
    KitModuleIface* module = nullptr;
    HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(comResult))
    {
        return 3;
    }

    try
    {
        const auto modulePath = std::filesystem::absolute(argv[1]).lexically_normal();
        Require(_wcsicmp(modulePath.filename().c_str(), L"Kit.WingetModuleInterface.dll") == 0, "Select the actual Winget module DLL.");
        const auto servicePath = modulePath.parent_path() / L"Kit.WingetService.exe";
        Require(std::filesystem::is_regular_file(modulePath) && std::filesystem::is_regular_file(servicePath), "The built module or service is missing.");
        const long maximumEnableMs = std::stol(argv[3]);
        Require(maximumEnableMs > 0 && maximumEnableMs <= 5000, "Invalid enable latency limit.");
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        library = LoadLibraryExW(modulePath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        Require(library != nullptr, "Loading the actual built module DLL failed.");
        const auto factory = reinterpret_cast<kit_create_func>(GetProcAddress(library, "kit_create"));
        Require(factory != nullptr, "The actual DLL does not export kit_create.");
        ChildTracker children(servicePath.wstring(), argv[2]);
        module = factory();
        Require(module != nullptr && _wcsicmp(module->get_key(), L"Winget") == 0, "The loaded module identity is incorrect.");
        Require(!module->is_enabled_by_default() && !module->is_enabled(), "Winget must start disabled by default.");
        Require(children.Active() == 0, "The disabled module spawned a service.");
        std::cout << "PASS default_disabled" << std::endl;

        auto started = Clock::now();
        module->enable();
        const auto enableMs = std::chrono::duration<double, std::milli>(Clock::now() - started).count();
        Require(enableMs < maximumEnableMs, "enable() blocked beyond its latency limit.");
        Require(module->is_enabled(), "enable() did not publish enabled state.");
        children.WaitForCount(1, 15000);
        // Observe an interval so a short-lived failed startup cannot count as healthy.
        Sleep(700);
        Require(children.Active() == 1, "The enabled service exited during startup.");
        const size_t firstObserved = children.Observed();
        for (int index = 0; index < 12; ++index)
        {
            module->enable();
        }
        Sleep(300);
        Require(children.Active() == 1 && children.Observed() == firstObserved, "Repeated enable spawned another service.");
        std::cout << "PASS enable_ms=" << enableMs << " repeated_enable_services=1" << std::endl;

        started = Clock::now();
        module->disable();
        const auto disableMs = std::chrono::duration<double, std::milli>(Clock::now() - started).count();
        Require(!module->is_enabled(), "disable() left enabled state set.");
        children.WaitForCount(0, 8000);
        std::cout << "PASS disable_ms=" << disableMs << " remaining_services=0" << std::endl;

        started = Clock::now();
        module->enable();
        module->destroy();
        module = nullptr;
        const auto destroyMs = std::chrono::duration<double, std::milli>(Clock::now() - started).count();
        children.WaitForCount(0, 8000);
        Sleep(400);
        Require(children.Active() == 0, "A service appeared after immediate destroy completed.");
        std::cout << "PASS immediate_destroy_ms=" << destroyMs << " remaining_services=0" << std::endl;
        Require(FreeLibrary(library) != FALSE, "The module DLL failed to unload.");
        library = nullptr;
        std::cout << "PASS actual_native_module_lifecycle" << std::endl;
    }
    catch (const std::exception& exception)
    {
        std::cerr << "FAIL " << exception.what() << std::endl;
        if (module)
        {
            module->destroy();
        }
        if (library)
        {
            FreeLibrary(library);
        }
        CoUninitialize();
        return 1;
    }

    CoUninitialize();
    return 0;
}
