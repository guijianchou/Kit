#pragma once

#include "ProjectTelemetry.h"

#define TraceLoggingWriteWrapper(provider, eventName, ...)   \
    if (IsDataDiagnosticsEnabled())                          \
    {                                                        \
        TraceLoggingWrite(provider, eventName, __VA_ARGS__); \
    }

namespace telemetry
{

class TraceBase
{
public:
    static void RegisterProvider()
    {
    }

    static void UnregisterProvider()
    {
    }

    static bool IsDataDiagnosticsEnabled()
    {
        return false;
    }
};

} // namespace telemetry
