# Native module verification

Run these commands from the Kit repository root in PowerShell 7 after an x64 Debug solution build:

```powershell
.\tools\tests\NativeModules\Build-Harness.ps1
.\x64\Debug\tests\NativeModules\ModuleAbiSmoke.exe (Join-Path $PWD 'x64\Debug\tests\NativeModules\UpstreamFixture.dll')
.\tools\tests\NativeModules\Invoke-LightSwitchSmoke.ps1 -IncludeRealWorkerChecks
.\tools\tests\NativeModules\Measure-RunnerStartup.ps1
```

The ABI fixture is compiled against the local `source/PowerToys` header, while the host uses the current Kit header. It verifies virtual dispatch and the legacy export without installing an upstream plugin.

The LightSwitch wrapper refuses to run with existing Kit processes. It saves and restores the exact LightSwitch settings bytes, confines its children to a cleanup job, checks that theme registry values stay unchanged, and uses a fake worker for lifecycle cases. Real-worker cases exercise early stop and invalid parent binding only. A failed restore keeps its backup and manifest in `x64/Debug/tests/NativeModules`; use `-RestoreSettingsOnly` after resolving the failure.

Startup measurement refuses existing Kit processes, launches `--silent`, observes the per-process startup log, and terminates only its own process handles to check worker exit. It preserves the current enabled-module configuration and does not measure Settings rendering or plugin readiness. `-ExecutablePath` can select a staged `Kit.exe`, and `-ReportPath` selects the JSON report.

The current default is seven module DLLs (`-ExpectedModuleCount` can select a different build). Reports include each DLL's load duration. Existing Localserver, AI Hub and Winget workers also block measurement. Run only in an idle test session: enabled workers use the current Kit configuration and may adopt existing managed services.

Generated binaries and fixture backups live under the ignored `x64/Debug/tests/NativeModules` directory. These checks do not exercise real system theme scheduling or WinUI interactions.

`Invoke-SettingsActivationSmoke.ps1` is a standalone Debug x64 regression for existing Settings-window activation. It extracts the actual `open_settings_window` and `bring_settings_to_front` definitions from `src/runner/settings_window.cpp`, preserving their code and source-line mappings, and compiles them with a minimal IPC recorder and an ordinary test-owned Win32 window. No Runner, Settings process, user settings, notifications, or scheduled tasks are used. An independent thread probes the real lifecycle mutexes during synchronous activation callbacks, including a one-time reentrant open request. The checks cover visible, hidden, minimized, no-IPC, Winget/default Dashboard routes, and repeated activation. Output, source hashes and compiler logs go under `TestResults/WinGetNotificationCrash`; `-SourcePath` accepts an archived source file for before/after regression evidence.
