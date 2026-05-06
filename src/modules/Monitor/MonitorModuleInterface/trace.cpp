#include "pch.h"
#include "trace.h"

#include <common/Telemetry/TraceBase.h>

TRACELOGGING_DEFINE_PROVIDER(
    g_hProvider,
    "Microsoft.PowerToys",
    // {8f18ba03-058b-489a-bf61-fb1254fa3c75}
    (0x8f18ba03, 0x058b, 0x489a, 0xbf, 0x61, 0xfb, 0x12, 0x54, 0xfa, 0x3c, 0x75),
    TraceLoggingOptionProjectTelemetry());

void Trace::EnableMonitor(const bool enabled) noexcept
{
    TraceLoggingWriteWrapper(
        g_hProvider,
        "Monitor_EnableMonitor",
        ProjectTelemetryPrivacyDataTag(ProjectTelemetryTag_ProductAndServicePerformance),
        TraceLoggingKeyword(PROJECT_KEYWORD_MEASURE),
        TraceLoggingBoolean(enabled, "Enabled"));
}
