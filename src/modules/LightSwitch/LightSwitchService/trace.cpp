#include "pch.h"
#include "trace.h"

// These methods are retained as no-op runtime compatibility hooks.
#pragma warning(disable : 26497)

void Trace::LightSwitch::RegisterProvider()
{
}

void Trace::LightSwitch::UnregisterProvider()
{
}

void Trace::LightSwitch::ScheduleModeToggled(const std::wstring& newMode) noexcept
{
    static_cast<void>(newMode);
}

void Trace::LightSwitch::ThemeTargetChanged(bool changeApps, bool changeSystem) noexcept
{
    static_cast<void>(changeApps);
    static_cast<void>(changeSystem);
}
