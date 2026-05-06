#pragma once

#include <windows.h>

class Trace
{
public:
    static void RegisterProvider();
    static void UnregisterProvider();
    static void Enable(bool enabled) noexcept;
    static void ShortcutInvoked() noexcept;
};
