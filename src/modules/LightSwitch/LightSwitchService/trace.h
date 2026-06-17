#pragma once

#include <string>

class Trace
{
public:
    class LightSwitch
    {
    public:
        static constexpr void RegisterProvider() noexcept {}
        static constexpr void UnregisterProvider() noexcept {}

        static constexpr void ScheduleModeToggled(const std::wstring& newMode) noexcept
        {
            (void)newMode;
        }

        static constexpr void ThemeTargetChanged(bool changeApps, bool changeSystem) noexcept
        {
            (void)changeApps;
            (void)changeSystem;
        }
    };
};
