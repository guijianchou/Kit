#pragma once

class Trace
{
public:
    static void RegisterProvider();
    static void UnregisterProvider();

    // Compatibility hook for the original PowerToys module interface.
    static void EnableAwake(const bool enabled) noexcept;
};
