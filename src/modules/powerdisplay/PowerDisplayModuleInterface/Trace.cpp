#include "pch.h"
#include "trace.h"

// These methods are retained as no-op runtime compatibility hooks.
#pragma warning(disable : 26497)

void Trace::RegisterProvider()
{
}

void Trace::UnregisterProvider()
{
}

void Trace::EnablePowerDisplay(_In_ bool enabled) noexcept
{
    static_cast<void>(enabled);
}

void Trace::ActivatePowerDisplay() noexcept
{
}
