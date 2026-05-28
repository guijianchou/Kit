#pragma once
#include <string>

struct LogSettings
{
    // The following strings are not localizable
    inline const static std::wstring defaultLogLevel = L"trace";
    inline const static std::wstring logLevelOption = L"logLevel";
    inline const static std::string runnerLoggerName = "runner";
    inline const static std::wstring logPath = L"Logs\\";
    inline const static std::wstring runnerLogPath = L"RunnerLogs\\runner-log.log";
    inline const static std::string actionRunnerLoggerName = "action-runner";
    inline const static std::wstring actionRunnerLogPath = L"RunnerLogs\\action-runner-log.log";
    inline const static std::string updateLoggerName = "update";
    inline const static std::wstring updateLogPath = L"UpdateLogs\\update-log.log";
    inline const static std::string awakeLoggerName = "awake";
    inline const static std::wstring awakeLogPath = L"Logs\\awake-log.log";
    inline const static std::string lightSwitchLoggerName = "light-switch";
    inline const static std::string powerDisplayLoggerName = "powerdisplay";
    inline const static int retention = 30;
    std::wstring logLevel;
    LogSettings();
};

// Get log settings from file. File with default options is created if it does not exist
LogSettings get_log_settings(std::wstring_view file_name);
