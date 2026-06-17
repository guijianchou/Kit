#pragma once

class Trace
{
public:
    static constexpr void RegisterProvider() noexcept {}
    static constexpr void UnregisterProvider() noexcept {}

    static constexpr void Enable(bool enabled) noexcept
    {
        (void)enabled;
    }

    static constexpr void ShortcutInvoked() noexcept {}
};
