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
#include <spdlog/sinks/stdout_color_sinks-inl.h>
#include <iostream>

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

    // Log locations to try, in order.
    //
    // The configured location lives under %LOCALAPPDATA%, which a low integrity process
    // (for example one started from a sandboxed tree) cannot write to. spdlog then fails with a
    // misleading "no such file or directory". LocalLow is the location such a process can
    // write, and the executable folder is the last resort for portable/staged builds.
    std::vector<std::filesystem::path> log_path_candidates(const std::filesystem::path& requested)
    {
        std::vector<std::filesystem::path> candidates{ requested };
        auto add = [&candidates](std::filesystem::path candidate) {
            for (const auto& existing : candidates)
            {
                if (_wcsicmp(existing.c_str(), candidate.c_str()) == 0)
                {
                    return;
                }
            }

            candidates.push_back(std::move(candidate));
        };

        const std::wstring localAppData = environment_variable(L"LOCALAPPDATA");
        const std::wstring userProfile = environment_variable(L"USERPROFILE");
        const std::wstring requestedText = requested.wstring();
        if (!userProfile.empty())
        {
            std::filesystem::path relative;
            if (!localAppData.empty() && requestedText.size() > localAppData.size() &&
                _wcsnicmp(requestedText.c_str(), localAppData.c_str(), localAppData.size()) == 0)
            {
                relative = std::filesystem::path(requestedText.substr(localAppData.size())).relative_path();
            }
            else
            {
                relative = std::filesystem::path(L"Kit") / L"Logs" / requested.filename();
            }

            add(std::filesystem::path(userProfile) / L"AppData" / L"LocalLow" / relative);
        }

        if (const auto exeDirectory = executable_directory(); !exeDirectory.empty())
        {
            std::filesystem::path subfolder = requested.parent_path().filename();
            if (subfolder.empty())
            {
                subfolder = L"Logs";
            }

            add(exeDirectory / subfolder / requested.filename());
        }

        return candidates;
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

    std::wstring usedLogFilePath;
    std::vector<std::wstring> failures;
    std::shared_ptr<spdlog::sinks::sink> sink;
    for (const auto& candidate : log_path_candidates(logFilePath))
    {
        try
        {
            if (const auto parent = candidate.parent_path(); !parent.empty())
            {
                std::error_code ec;
                std::filesystem::create_directories(parent, ec);
            }

            sink = make_shared<daily_file_sink_mt>(candidate.wstring(), 0, 0, false, LogSettings::retention);
            usedLogFilePath = candidate.wstring();
            break;
        }
        catch (const std::exception& ex)
        {
            failures.push_back(candidate.wstring() + L": " + widen_utf8(ex.what()));
        }
        catch (...)
        {
            failures.push_back(candidate.wstring() + L": unknown error");
        }
    }

    if (!sink)
    {
        // Logging stays off for this process. There is no dialog: this runs inside the runner,
        // module DLLs and headless workers, where a modal box blocks startup, and the reason is
        // what actually needs to reach a developer.
        logger = make_unregistered_null_logger(loggerName);
        std::wstring message = L"[Kit] Logger '" + widen_utf8(loggerName) + L"' cannot be initialized; logging is disabled for this process.";
        for (const auto& failure : failures)
        {
            message += L"\n  " + failure;
        }

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
    logger->info(L"Log file: {} (process folder: {})", usedLogFilePath, executable_directory().wstring());
    for (const auto& failure : failures)
    {
        logger->warn(L"Log location skipped: {}", failure);
    }

    if (_wcsicmp(usedLogFilePath.c_str(), logFilePath.c_str()) != 0)
    {
        logger->warn(L"LOGGER_FALLBACK: configured log path '{}' is not writable by this process; logging to '{}' instead.", logFilePath, usedLogFilePath);
    }
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
