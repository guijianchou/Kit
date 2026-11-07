#include <Windows.h>
#include <common/logger/logger.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <set>
#include <stdexcept>

// Isolate the settings store; production logger.cpp is compiled unchanged.
LogSettings::LogSettings() : logLevel(defaultLogLevel) {}
LogSettings get_log_settings(std::wstring_view) { return {}; }

namespace
{
    void Require(bool condition, const char* message)
    {
        if (!condition)
            throw std::runtime_error(message);
    }

    std::set<std::filesystem::path> Files(const std::filesystem::path& root)
    {
        std::set<std::filesystem::path> files;
        for (const auto& entry : std::filesystem::recursive_directory_iterator(root))
            if (entry.is_regular_file())
                files.insert(entry.path());
        return files;
    }

    bool ContainsMessage(const std::filesystem::path& directory, const std::string& message)
    {
        for (const auto& path : Files(directory))
        {
            std::ifstream stream(path);
            std::string text((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
            if (text.find(message) != std::string::npos)
                return true;
        }
        return false;
    }
}

int wmain(int argc, wchar_t** argv)
{
    try
    {
        Require(argc == 2, "Expected an isolated test root.");
        const std::filesystem::path root = std::filesystem::absolute(argv[1]);
        const auto local = root / L"Local" / L"Kit";
        std::filesystem::create_directories(local);
        // The script sets these before launch too, covering both Win32 and CRT environment reads.
        _wputenv_s(L"LOCALAPPDATA", (root / L"Local").c_str());
        _wputenv_s(L"USERPROFILE", (root / L"Profile").c_str());

        const auto normal = local / L"Writable" / L"Logs";
        Logger::init("native-path-normal", (normal / L"test.log").wstring(), L"");
        Logger::info("native normal write");
        Logger::flush();
        Require(ContainsMessage(normal, "native normal write"), "The configured path did not receive normal log messages.");
        std::cout << "PASS native: configured path accepts log messages.\n";

        const auto blocked = local / L"Blocked";
        { std::ofstream file(blocked); file << "This file deliberately prevents directory creation."; }
        const auto before = Files(root);
        Logger::init("native-path-retry", (blocked / L"Logs" / L"test.log").wstring(), L"");
        Logger::info("Failure preserves debugger logging without another file.");
        Logger::flush();
        Logger::init("native-path-retry", (blocked / L"Logs" / L"test.log").wstring(), L"");
        Require(Files(root) == before, "Failed initialization scattered log files.");
        Require(!std::filesystem::exists(root / L"Profile" / L"AppData" / L"LocalLow"), "A LocalLow fallback directory was created.");
        std::cout << "PASS native: repeated failure is nonfatal and no files scatter.\n";

        std::filesystem::remove(blocked);
        Logger::init("native-path-retry", (blocked / L"Logs" / L"test.log").wstring(), L"");
        Logger::info("native recovered write");
        Logger::flush();
        Require(ContainsMessage(blocked / L"Logs", "native recovered write"), "Logging did not recover at its configured path.");
        std::cout << "PASS native: the configured path recovers when it becomes writable.\n";
        return 0;
    }
    catch (const std::exception& ex)
    {
        std::cerr << ex.what() << '\n';
        return 1;
    }
}
