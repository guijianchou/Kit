#include "pch.h"
#include "trace.h"

#include "general_settings.h"

#include <common/Telemetry/ProjectTelemetry.h>

TRACELOGGING_DEFINE_PROVIDER(
    g_hProvider,
    "Microsoft.PowerToys",
    (0x38e8889b, 0x9731, 0x53f5, 0xe9, 0x01, 0xe8, 0xa7, 0xc1, 0x75, 0x30, 0x74));

void Trace::EventLaunch(const std::wstring&, bool)
{
}

void Trace::SettingsChanged(const GeneralSettings&)
{
}

void Trace::UpdateCheckCompleted(bool, bool, const std::wstring&, const std::wstring&)
{
}

void Trace::UpdateDownloadCompleted(bool, const std::wstring&)
{
}

void Trace::TrayIconLeftClick(bool)
{
}

void Trace::TrayIconDoubleClick(bool)
{
}

void Trace::TrayIconRightClick(bool)
{
}
