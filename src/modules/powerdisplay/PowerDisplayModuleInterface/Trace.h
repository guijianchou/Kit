#pragma once

class Trace
{
public:
    static void RegisterProvider();
    static void UnregisterProvider();

    // Compatibility hooks for the original PowerToys module interface.
    static void EnablePowerDisplay(const bool enabled) noexcept;
    static void ActivatePowerDisplay() noexcept;
};
