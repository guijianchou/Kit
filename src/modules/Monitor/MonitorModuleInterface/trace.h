#pragma once

class Trace
{
public:
    static void RegisterProvider();
    static void UnregisterProvider();
    static void EnableMonitor(const bool enabled) noexcept;
};
