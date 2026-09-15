#pragma once
#include "LightSwitchSettings.h"
#include <optional>

// Represents runtime-only information (not saved in settings.json)
struct LightSwitchState
{
    ScheduleMode lastAppliedMode = ScheduleMode::Off;
    bool isManualOverride = false;
    bool isSystemLightActive = false;
    bool isAppsLightActive = false;
    bool isNightLightActive = false;
    int lastEvaluatedDay = -1;
    int lastTickMinutes = -1;

    // Derived, runtime-resolved times
    int effectiveLightMinutes = 0; // the boundary we actually act on
    int effectiveDarkMinutes = 0; // includes offsets if needed
};

// The controller that reacts to settings changes, time ticks, and manual overrides.
class LightSwitchStateManager
{
public:
    LightSwitchStateManager();

    // Called when settings.json changes or stabilizes.
    void OnSettingsChanged();

    // Called every minute (from service worker tick).
    void OnTick();

    // Called when manual override is toggled (via shortcut or system change).
    void OnManualOverride();

    // Called when night light changes in windows settings
    void OnNightLightChange();

    // Initial sync at startup to align internal state with system theme
    void SyncInitialThemeState();

    // Sync current theme state when external theme changes
    void SyncCurrentThemeState();

    // Accessor for current state (optional, for diagnostics)
    LightSwitchState GetState() const
    {
        std::lock_guard<std::mutex> lock(_stateMutex);
        return _state;
    }

private:
    LightSwitchState _state;
    mutable std::mutex _stateMutex;

    void EvaluateAndApplyIfNeeded();
    bool CoordinatesAreValid(const std::wstring& lat, const std::wstring& lon);
};
