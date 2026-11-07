# Winget smoke tests

Run these scripts from PowerShell 7 on Windows after building the complete x64 Kit runtime. `-Configuration Debug` is the default; use `-RuntimeRoot` to select another complete runtime directory. Run live tests one at a time with Kit, Settings, and WingetService closed. None of these tests downloads or installs application packages.

| Script | Coverage | Temporary changes and restoration |
| --- | --- | --- |
| `Invoke-WingetNativeSmoke.ps1` | Loads the real native module DLL; checks default-off state, bounded enable, repeated enable/disable, and service lifecycle. | Isolates Winget settings/state, disables daily checks and notifications, tracks its own processes, and restores the original state tree and settings. |
| `Invoke-WingetServiceSmoke.ps1` | Starts the real service with a controlled parent; validates the pipe server PID, lightweight `go` discovery before other catalog queries, selected-only details, read-only installation preparation, stale identity rejection, updates/inventory, and parent-bound exit. Its command allowlist excludes enqueue, downloads and installation. | Temporarily clears service state and disables daily checks/notifications, then restores settings/state byte-for-byte with hash verification. WinGet's disk caches are preserved; a fresh service process is not a cold disk-cache test. `-TraceService` also temporarily enables and then restores global log settings. |
| `Invoke-WingetToastSmoke.ps1` | Builds an isolated probe, queries actual updates, invokes the compiled production notifier, and requires the resulting tag/group/count/route in Windows notification history. | Backs up the three exact current-user registration subtrees, refuses to replace an existing Winget notification, removes its own test notification, then restores and verifies all saved values, types, subkeys, and original existence. No notification activation is clicked. |

The toast wrapper generates its project and entry point under `TestResults/WingetToast/<run-id>/Probe`, references `Kit.WingetLib.dll` from the selected runtime, and builds through `tools/build/build.ps1`. Its output is isolated from the Kit runtime. A clean checkout needs no pre-existing `TestResults` harness. Reports, accepted toast XML, and before/after registration snapshots remain in that run directory. If WinGet is unavailable or no real updates are found, delivery is reported as unvalidated rather than passed.

```powershell
# Compile only; safe to use while other UI validation is running.
./tools/tests/Winget/Invoke-WingetToastSmoke.ps1 -BuildOnly

# Live checks; close Kit first and run serially.
./tools/tests/Winget/Invoke-WingetNativeSmoke.ps1
./tools/tests/Winget/Invoke-WingetServiceSmoke.ps1 -TraceService
./tools/tests/Winget/Invoke-WingetToastSmoke.ps1
```

`-BuildOnly` is supported by the native and toast wrappers. It does not start the harness or access application settings, notification registration, or Windows notification history. The toast wrapper still requires the already-built runtime libraries for compilation. On machines where Visual Studio is not discoverable, supply `-VisualStudioPath`, for example `-VisualStudioPath 'C:\Program Files\Microsoft Visual Studio\18\Enterprise'`. Build may restore development dependencies; it never requests application package installation through WinGet.

Bilingual Settings page checks are separate: `tools/tests/SettingsUI/Invoke-WingetPageSmoke.ps1 -Language en-us` and `-Language zh-CN`. They must also run serially with the live service/native/toast checks. These smokes and the fake-client unit tests do not prove real installer execution or notification-click activation.

Discovery first-page waiting uses `-DiscoverTimeoutSeconds` (default 30). Details and other queries use `-CheckTimeoutSeconds`. Timeout failures remain failures even if cancellation was requested; native COM cancellation and retained-worker admission are verified with the core fake-worker tests. The UI checks include unresolved-to-resolved row identity/focus/selection, search timing and empty/error states, details loading/cancel/retry, and existing output/flicker regression assertions.
