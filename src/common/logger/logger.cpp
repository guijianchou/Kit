// logger.cpp : Defines the functions for the static library.
//
#include "pch.h"
#include "framework.h"
#include "logger.h"
#include <unordered_map>
#include <vector>
#include <filesystem>
#include <system_error>
#include <spdlog/sinks/daily_file_sink.h>
#include <spdlog/sinks/msvc_sink.h>
#include <spdlog/sinks/null_sink.h>

using spdlog::sinks_init_list;
using spdlog::level::level_enum;
using spdlog::sinks::daily_file_sink_mt;
using spdlog::sinks::msvc_sink_mt;
using std::make_shared;

namespace
{
    const std::unordered_map<std::wstring, level_enum> logLevelMapping = {
        { L"trace", level_enum::trace },
        { L"debug", level_enum::debug },
        { L"info", level_enum::info },
        { L"warn", level_enum::warn },
        { L"err", level_enum::err },
        { L"critical", level_enum::critical },
        { L"off", level_enum::off },
    };

    std::wstring environment_variable(const wchar_t* name)
    {
        wchar_t* value = nullptr;
        size_t length = 0;
        if (_wdupenv_s(&value, &length, name) != 0 || value == nullptr)
        {
            return {};
        }

        std::wstring result{ value };
        free(value);
        return result;
    }

    std::filesystem::path executable_directory()
    {
        std::wstring buffer(MAX_PATH, L'\0');
        DWORD length = ::GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (length >= buffer.size())
        {
            buffer.resize(0x8000);
            length = ::GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        }

        if (length == 0 || length >= buffer.size())
        {
            return {};
        }

        buffer.resize(length);
        return std::filesystem::path(buffer).parent_path();
    }

    std::wstring widen_utf8(const std::string& text)
    {
        if (text.empty())
        {
            return {};
        }

        const int size = ::MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
        if (size <= 0)
        {
            return std::wstring(text.begin(), text.end());
        }

        std::wstring result(static_cast<size_t>(size), L'\0');
        ::MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), result.data(), size);
        return result;
    }

    // A null logger that is not registered in the spdlog registry, so a repeated failure with
    // the same name cannot throw "logger with name already exists".
    std::shared_ptr<spdlog::logger> make_unregistered_null_logger(const std::string& name)
    {
        return make_shared<spdlog::logger>(name, make_shared<spdlog::sinks::null_sink_mt>());
    }
}

level_enum getLogLevel(std::wstring_view logSettingsPath)
{
    auto logLevel = get_log_settings(logSettingsPath).logLevel;
    if (auto it = logLevelMapping.find(logLevel); it != logLevelMapping.end())
    {
        return it->second;
    }

    if (auto it = logLevelMapping.find(LogSettings::defaultLogLevel); it != logLevelMapping.end())
    {
        return it->second;
    }
    return level_enum::trace;
}

std::shared_ptr<spdlog::logger> Logger::logger = make_unregistered_null_logger("null");

bool Logger::wasLogFailedShown()
{
    return !environment_variable(logFailedShown.c_str()).empty();
}

void Logger::init(std::string loggerName, std::wstring logFilePath, std::wstring_view logSettingsPath)
{
    level_enum logLevel = level_enum::trace;
    try
    {
        logLevel = getLogLevel(logSettingsPath);
    }
    catch (...)
    {
    }

    if (auto existing = spdlog::get(loggerName))
    {
        logger = existing;
        logger->info("{} logger is initialized", loggerName);
        return;
    }

    std::wstring failure;
    std::shared_ptr<spdlog::sinks::sink> sink;
    // A failed write must not relocate a module's logs to LocalLow or the executable
    // directory. Callers select their log location explicitly, including low-integrity callers.
    try
    {
        if (const auto parent = std::filesystem::path(logFilePath).parent_path(); !parent.empty())
        {
            std::error_code ec;
            std::filesystem::create_directories(parent, ec);
        }

        sink = make_shared<daily_file_sink_mt>(logFilePath, 0, 0, false, LogSettings::retention);
    }
    catch (const std::exception& ex)
    {
        failure = widen_utf8(ex.what());
    }
    catch (...)
    {
        failure = L"unknown error";
    }

    if (!sink)
    {
        // Preserve debugger diagnostics without creating another file. Do not register this
        // logger: a later init can retry the configured path if it becomes writable again.
        logger = make_shared<spdlog::logger>(loggerName, make_shared<msvc_sink_mt>());
        logger->set_level(logLevel);
        logger->set_pattern("[%Y-%m-%d %H:%M:%S.%f] [%n] [t-%t] [%l] %v");
        std::wstring message = L"[Kit] Logger '" + widen_utf8(loggerName) + L"' cannot be initialized for '" + logFilePath + L"'; file logging is disabled: " + failure;

        ::OutputDebugStringW((message + L"\n").c_str());
        ::SetEnvironmentVariableW(logFailedShown.c_str(), L"yes");
        return;
    }

    std::shared_ptr<spdlog::logger> created;
    if (IsDebuggerPresent())
    {
        auto msvc_sink = make_shared<msvc_sink_mt>();
        msvc_sink->set_pattern("[%Y-%m-%d %H:%M:%S.%f] [%n] [t-%t] [%l] %v");
        created = make_shared<spdlog::logger>(loggerName, sinks_init_list{ sink, msvc_sink });
    }
    else
    {
        created = make_shared<spdlog::logger>(loggerName, sink);
    }

    created->set_level(logLevel);
    created->set_pattern("[%Y-%m-%d %H:%M:%S.%f] [p-%P] [t-%t] [%l] %v");
    created->flush_on(logLevel); // Auto flush on every log message.
    try
    {
        spdlog::register_logger(created);
        logger = created;
    }
    catch (...)
    {
        // Another thread registered the same name first; share that instance.
        auto existing = spdlog::get(loggerName);
        logger = existing ? existing : created;
    }

    logger->info("{} logger is initialized", loggerName);
    logger->info(L"Log file: {} (process folder: {})", logFilePath, executable_directory().wstring());
}

void Logger::init(std::vector<spdlog::sink_ptr> sinks)
{
    auto init_logger = std::make_shared<spdlog::logger>("", begin(sinks), end(sinks));
    if (!init_logger)
    {
        return;
    }

    Logger::logger = init_logger;
}
