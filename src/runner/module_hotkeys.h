#pragma once
#include <Windows.h>

// Call only on the runner window thread, which owns modules() and registrations.
void initialize_module_hotkeys(HWND window);
void refresh_module_hotkeys();
void clear_module_hotkeys();
bool dispatch_module_hotkey(WPARAM id, LPARAM keyData);
