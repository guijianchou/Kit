#pragma once

#include <common/Telemetry/TraceBase.h>

class Trace : public telemetry::TraceBase
{
public:
    // Compatibility hooks for the original PowerToys module interface.
    static void EnablePowerDisplay(const bool enabled) noexcept;
    static void ActivatePowerDisplay() noexcept;
};
