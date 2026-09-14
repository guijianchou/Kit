#pragma once

#include <Windows.h>
#include <optional>

namespace QuickAccessHost
{
    void start();
    // Queues a launch/show request without blocking the caller on process startup.
    void show();
    void stop();
    bool is_running();
}
