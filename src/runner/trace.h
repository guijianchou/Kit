#pragma once

#include <common/Telemetry/TraceBase.h>

struct GeneralSettings;

class Trace : public telemetry::TraceBase
{
public:
    static void EventLaunch(const std::wstring& versionNumber, bool isProcessElevated);
    static void SettingsChanged(const GeneralSettings& settings);

    // Update trace events
    static void UpdateCheckCompleted(bool success, bool updateAvailable, const std::wstring& fromVersion, const std::wstring& toVersion);
    static void UpdateDownloadCompleted(bool success, const std::wstring& version);

    // Tray icon interaction trace events
    static void TrayIconLeftClick(bool quickAccessEnabled);
    static void TrayIconDoubleClick(bool quickAccessEnabled);
    static void TrayIconRightClick(bool quickAccessEnabled);
};
