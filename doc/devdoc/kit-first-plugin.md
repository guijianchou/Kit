# Kit First Plugin Development

Kit currently follows the PowerToys module model. `Awake`, `LightSwitch`, and `Monitor` are the active modules, and module exposure is controlled by maintained lists instead of filesystem probing.

## Recommended First Step

Build the first new feature as a Kit module unless the goal is specifically to import a plugin host.

This keeps the work inside the already-tested runner, module interface, Settings, Home, and Quick Access path. It also avoids stabilizing two things at once: a new feature and a plugin-host ecosystem.

## If The Feature Is A Kit Module

Use this path for a first native or managed utility:

1. Start from the closest upstream PowerToys module shape when one exists.
2. Put reusable behavior in a testable library before wiring the runner. Monitor uses `MonitorLib` for scanning, hashing, CSV persistence, duplicate grouping, organization, and cleanup primitives.
3. Add the worker project. Keep command-line entry points explicit, for example `--scan-once` for one-shot operation and `--pid` plus a named exit event for runner-managed lifetime.
4. Add the module interface project. Follow the Awake/LightSwitch pattern: `powertoy_create`, stable module key, `get_config`, `set_config`, `enable`, `disable`, worker launch, and clean shutdown signaling.
5. Add the module projects to `Kit.slnx`.
6. Add the module interface DLL to `src/runner/main.cpp` in the `knownModules` list.
7. Preserve upstream `PowerToys.Interop` and `PowerToys.GPOWrapper` project references when the module uses them. Those WinMDs are part of Kit's PowerToys compatibility surface and should regenerate from a clean Release tree.
8. Add Settings route/navigation only for the new module.
9. Add Home dashboard metadata only if the module should be visible on Home.
10. Add a Quick Access action only when the module has a real action. Otherwise, let Home fall back to opening the module settings page.
11. Add focused tests for runner registration, Settings routing, Home listing, Quick Access behavior, worker project shape, core library behavior, and any added WinMD/GPO dependency.
12. Build the module interface and any service or worker project sequentially during local verification.

## Monitor As The Reference Kit Module

Monitor is the first Kit-authored module and should be used as the current reference for small module development:

- Core library: `src/modules/Monitor/MonitorLib`
- Worker: `src/modules/Monitor/Monitor`
- Native module interface: `src/modules/Monitor/MonitorModuleInterface`
- Settings model: `src/settings-ui/Settings.UI.Library/MonitorSettings.cs`
- Settings page: `src/settings-ui/Settings.UI/SettingsXAML/Views/MonitorPage.xaml`
- Home and Quick Access registration: `DashboardViewModel.cs` and `QuickAccessViewModel.cs`
- Registration/static tests: `MonitorSettingsRegistration.cs` and `MonitorWorkerProjectTests.cs`

The useful pattern is not the exact Monitor feature set, but the shape: isolate logic in a library, keep the worker simple, let the native interface own runner lifetime, and cover every manual registration point with tests.

See `kit-development-experience.md` for the first-phase implementation notes and stability checklist that came out of Monitor.

## If The Feature Is A PowerToys Run Plugin

PowerToys Run is not currently active in Kit. Before writing or importing a Run plugin, import and stabilize the Run host first.

Use these upstream references:

- `modules/launcher/architecture.md`
- `modules/launcher/project_structure.md`
- `modules/launcher/new-plugin-checklist.md`
- `modules/launcher/debugging.md`
- `modules/launcher/plugins/overview.md`

After the Run host is active, the plugin should follow the upstream Run plugin checklist with Kit-specific settings, branding, and telemetry disabled or removed.

## If The Feature Is A Command Palette Extension

Command Palette is also not currently active in Kit. Treat the copied CmdPal docs as design and compatibility reference until the host is imported.

Use these upstream references:

- `modules/cmdpal/powertoys-extension-local-development.md`
- `modules/cmdpal/CmdPal-Values.md`
- `modules/cmdpal/command-pal-anatomy/command-palette-anatomy.md`
- `modules/cmdpal/initial-sdk-spec/initial-sdk-spec.md`

## Stability Baseline

Before starting the first module/plugin branch, keep this baseline green:

Run these commands from the `src/kit` project root.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe' 'src\settings-ui\Settings.UI\PowerToys.Settings.csproj' /t:Restore,Build /p:Configuration=Debug /p:Platform=x64 /m /nr:false /nologo
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe' 'src\settings-ui\QuickAccess.UI\PowerToys.QuickAccess.csproj' /t:Restore,Build /p:Configuration=Debug /p:Platform=x64 /m /nr:false /nologo
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe' 'src\modules\Monitor\Tests\Monitor.UnitTests\Monitor.UnitTests.csproj' /t:Restore,Build /p:Configuration=Debug /p:Platform=x64 /m:1 /nr:false /nologo
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe' 'src\modules\Monitor\Monitor\PowerToys.Monitor.csproj' /t:Restore,Build /p:Configuration=Debug /p:Platform=x64 /m:1 /nr:false /nologo
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe' 'src\modules\Monitor\MonitorModuleInterface\MonitorModuleInterface.vcxproj' /t:Restore,Build /p:Configuration=Debug /p:Platform=x64 /m:1 /nr:false /nologo
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe' 'src\runner\Kit.vcxproj' /t:Restore,Build /p:Configuration=Debug /p:Platform=x64 /m /nr:false /nologo
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe' 'src\settings-ui\Settings.UI.UnitTests\Settings.UI.UnitTests.csproj' /t:Restore,Build /p:Configuration=Debug /p:Platform=x64 /m:1 /nr:false /nologo
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe' 'Debug\x64\tests\Monitor.UnitTests\net10.0-windows10.0.26100.0\Monitor.UnitTests.dll' /Platform:x64
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe' 'Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.dll' /Platform:x64
```

Run module interface projects with `/m:1` when building them independently. Some upstream native projects share generated outputs and tracking logs, so independent parallel MSBuild commands can fail even when the projects are valid.

For copied PowerToys modules that consume `PowerToys.Interop` or `PowerToys.GPOWrapper`, also validate at least one clean Release build of the module or UI surface. A good smoke test is to remove the shared Release WinMD files, then build the consuming project and confirm the native WinMD and CsWinRT projection are regenerated before C# compilation.

When starting from a source-size handoff, expect the first build to restore local packages again if `src\kit\packages` was removed. That cache is not source and can be regenerated, but the first Visual Studio build after cleanup will be slower. A clean baseline should recreate `packages`, `x64\Release`, `WinUI3Apps`, shared WinMDs, and CsWinRT generated sources without manual copying.

## Current Test Boundary

`Settings.UI.UnitTests` intentionally excludes ViewModel tests for PowerToys modules that Kit has removed from the active Settings UI. The remaining tests cover General, Kit branding and storage, Home module listing, Quick Access visibility, Monitor's scan UI shape, runner Awake/Monitor registration, settings serialization, and command-setting parsing. Monitor's own unit tests cover the core library, worker project shape, and runner-managed worker lifetime.
