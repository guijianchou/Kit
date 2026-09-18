#pragma once
#include <common/Telemetry/TraceBase.h>

class Trace : public telemetry::TraceBase
{
public:
    static void RegisterProvider() noexcept {}
    static void UnregisterProvider() noexcept {}
    static void EnableAIHub(const bool /*enabled*/) noexcept {}
};
