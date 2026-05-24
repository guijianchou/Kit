#pragma once

#include <common/Telemetry/TraceBase.h>

class Trace : public telemetry::TraceBase
{
public:
    // Compatibility hook for the original PowerToys module interface.
    static void EnableAwake(const bool enabled) noexcept;
};
