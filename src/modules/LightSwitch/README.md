# LightSwitch

LightSwitch changes the Windows system and application themes. It supports fixed times, sunrise/sunset with offsets, and following Windows Night Light. The schedule can also be turned off while the module remains available for manual theme changes.

Manual switching remains available through Kit Quick access and the LightSwitch CLI (`light`, `dark`, and `toggle`). Quick access signals the module's toggle event; the module passes the request to the service through its existing semaphore. Automatic schedules and CLI commands continue to use the service's theme state manager.

LightSwitch does not register global keyboard shortcuts. The settings page has no shortcut editor. Default settings and settings saved from the Settings UI do not contain `toggle-theme-hotkey`. Existing settings files containing that retired property remain readable; the property is ignored and cannot register a shortcut.

The native components are `LightSwitchModuleInterface` and `LightSwitchService`. The settings model and page are in `Settings.UI.Library` and `Kit.Settings`, respectively.
