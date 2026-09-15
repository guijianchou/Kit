#pragma once

class Trace
{
public:
    static constexpr void RegisterProvider() noexcept {}
    static constexpr void UnregisterProvider() noexcept {}

    static constexpr void EnableLocalserver(const bool enabled) noexcept
    {
        (void)enabled;
    }
};
