# Kit

**Language / 语言:** English | [中文](README_zh.md)

---

Kit is a local, self-use Windows utility workspace derived from Microsoft PowerToys. It exists so selected PowerToys utilities can be modified, isolated, and compared against an installed official PowerToys build on the same machine.

## Project Goal

Kit is currently a stability-first PowerToys-derived workspace, not a full product rebrand. The main design choice is to keep the upstream runner, module interface, settings, and dashboard patterns recognizable so copied PowerToys modules can be validated with minimal adapter code.

Kit-specific changes should stay small and intentional: branding, settings storage, visible navigation, Home content, backup and restore defaults, and removal of product services that do not belong in a local workspace.

## Current Version

Current Kit version: `1.2.0`.

## Changelog

See [changelog.md](changelog.md) for the full version history.

## Phase One Closeout

The first phase is now effectively a working Kit shell plus one newly-authored module. The framework can load explicit PowerToys-style modules, show them in Settings and Home, keep Kit-branded storage separate from official PowerToys, and run Monitor's Downloads workflow through the existing runner/module-interface/settings path.

The current stable handoff point is:

- Keep `Awake`, `Light Switch`, `Monitor`, and `PowerDisplay` as the active module set.
- Keep module discovery explicit through maintained lists and tests. Do not add filesystem probing yet.
- Keep General and Home in English Kit wording, with automatic update and telemetry surfaces removed.
- Keep Monitor's worker headless. User actions and progress should be surfaced through Settings/Home, not worker windows.
- Keep Settings scan progress tied to worker progress/completion state. Avoid scan-completion UI that advances independently from the worker.
- Clean build artifacts before handoff so the next Visual Studio build starts from source state.
- The workspace can be reduced back to source size after a stable handoff. Local `Debug`, `Release`, `x64`, `bin`, `obj`, `TestResults`, `.vs`, and restored `packages` directories are disposable build state.

## Current Module Set

The active Kit module set is deliberately small:

- `Awake`
- `Light Switch`
- `Monitor`
- `PowerDisplay`

Kit does not automatically expose every upstream PowerToys utility copied in the source tree. Modules are enabled only after they are registered in the maintained Kit lists for the runner, Settings navigation, Home, and tests.

## PowerToys Compatibility Model

Kit follows the PowerToys module-loading model instead of inventing a new plugin protocol. The runner loads known module interface DLLs through the maintained `KitKnownModules` list in `src/runner/main.cpp`, currently:

- `PowerToys.AwakeModuleInterface.dll`
- `PowerToys.LightSwitchModuleInterface.dll`
- `PowerToys.MonitorModuleInterface.dll`
- `PowerToys.PowerDisplayModuleInterface.dll`

This fixed list is intentional. It avoids unstable directory probing and makes each imported module an explicit compatibility decision. When another PowerToys module is brought into Kit, it should be added to the runner, solution, settings routing, Home dashboard metadata, and tests together.

## Architecture

- `src/runner` starts Kit, loads module interface DLLs, owns module lifetime, and coordinates settings IPC with the Settings app. The executable is already separated enough to launch as `Kit.exe` while many build-facing project names still retain upstream PowerToys names. At runtime the runner opens the Settings and Quick Access apps from `WinUI3Apps` next to `Kit.exe`, so the runner build target must keep explicit dependencies on both UI executable projects.
- `src/modules` contains the active utilities. `Awake` is copied from upstream PowerToys with `Awake.ModuleServices`, `Awake`, and `AwakeModuleInterface`; `LightSwitch` is the current Kit utility module; `Monitor` is the first Kit-authored module created from the earlier Python Downloads monitor; `PowerDisplay` is imported from the PowerToys-style module shape with its Settings page, profile dialogs, model library, WinUI app, and module interface.
- `src/settings-ui/Settings.UI` contains the WinUI Settings app, including Home, General, module pages, navigation, and page-level view models.
- `src/settings-ui/Settings.UI.Controls` contains shared UI controls such as Quick Access.
- `src/settings-ui/Settings.UI.Library` contains settings models, settings serialization, module settings repositories, backup and restore helpers, GPO helpers, and shared settings infrastructure.
- `src/common` retains shared native and managed PowerToys infrastructure used by the runner, modules, and Settings.

Runtime settings are stored under Kit-specific application data, such as `%LOCALAPPDATA%\Kit\settings.json`, rather than the official PowerToys settings directory. Backup and restore defaults also use Kit branding, including `Documents\Kit\Backup`, `HKCU\Software\Microsoft\Kit`, and `Kit_settings_*` temporary backup folders.

## Recent Awake and Home Implementation

The latest Home work keeps PowerToys behavior but scopes it to Kit's active modules:

- `DashboardViewModel` uses `KitModuleCatalog.DashboardModules`, currently `Awake`, `LightSwitch`, `Monitor`, and `PowerDisplay`, so the Home utility list is fixed and predictable.
- `QuickAccessViewModel` still supports actionable Quick Access items, but Home passes the dashboard module list so enabled Kit modules appear consistently.
- Quick Access first tries the normal launcher. If a module has no direct quick action, Home falls back to opening that module's settings page. This lets `Awake` and `Monitor` behave usefully without creating fake shortcut actions while `LightSwitch` and `PowerDisplay` keep direct toggle actions.
- `Awake` contributes a `DashboardModuleActivationItem` that displays the current Awake mode in the Home shortcuts card, using the existing PowerToys dashboard item template.
- The Quick Access empty state now uses the count of visible items, not the raw item collection count, so disabled or GPO-hidden modules do not leave an empty card visible.

## Monitor Implementation

Monitor is the first Kit module developed directly against the PowerToys module shape. It keeps the earlier Python monitor's core behavior while moving the implementation into buildable, testable Kit projects:

- `src/modules/Monitor/MonitorLib` contains the managed core library for Downloads scanning, extension and smart-rule categorization, SHA1 hashing, Python-compatible CSV persistence, duplicate grouping, file organization, installer-cleanup primitives, and scan progress snapshots.
- `src/modules/Monitor/Monitor` builds the Monitor worker as `PowerToys.Monitor.exe` when an apphost is present and as `PowerToys.Monitor.dll` in apphost-less Debug outputs. It supports `--scan-once` for one-shot scans, writes progress to `%LOCALAPPDATA%\Kit\Monitor\scan-progress.json`, signals a named scan-completed event, and supports `--pid` for runner-managed lifetime.
- `src/modules/Monitor/MonitorModuleInterface` builds `PowerToys.MonitorModuleInterface.dll`. It follows the Awake/LightSwitch interface pattern: `powertoy_create`, key `Monitor`, explicit enable/disable, worker launch from the module output folder, `dotnet` fallback when the worker apphost is missing, exit-event signaling, basic custom actions for scan/organize/clean requests, and no filesystem module probing.
- `src/settings-ui/Settings.UI.Library` owns `MonitorSettings`, `MonitorProperties`, serialization, enabled-state, and module-helper mappings.
- `src/settings-ui/Settings.UI` owns `MonitorPage`, `MonitorViewModel`, Shell navigation, Home dashboard metadata, English resources, and settings routing.
- `src/settings-ui/Settings.UI.Controls` includes Monitor in the Kit Quick Access module list so Home can expose it consistently when enabled.

The current Monitor parity target is the Python implementation's baseline functionality: scan Downloads, maintain `results.csv`, categorize files, preserve duplicate rows for analytics, organize files by category, and provide dry-run/delete primitives for installer cleanup. Registry-backed real installed-software discovery and richer UI actions are future refinements.

The current Settings surface includes a manual scan card, `OrganizeDownloads` and `CleanInstallers` toggles, a `Run in background` toggle, a default Downloads folder picker, a hash algorithm drop-down with SHA1 as the default, and a same-row progress bar/percentage placed between the Manual Scan content and the Scan button. The Monitor module toggle controls whether the module and Settings actions are available. `Run in background` separately controls whether the runner starts the persistent worker on enable; when it is off, Scan Now still launches a one-shot scan. The one-shot scan always refreshes the category folders and CSV state, then applies the current action toggles: `OrganizeDownloads` defaults to on, `CleanInstallers` defaults to off, and `Run in background` defaults to off. The progress display now reads worker progress snapshots and completion state instead of advancing from a UI-only timer.

## Recent Monitor Worker Progress Stabilization

The latest Monitor pass fixed the manual Scan Now path that could leave Settings stuck on `Waiting for worker progress`:

- Runner IPC was already dispatching the `scanNow` action correctly; the failure was in the module interface worker launch path.
- The module interface used to search only for `PowerToys.Monitor.exe`. Debug builds can produce `PowerToys.Monitor.dll` without a ready apphost, so the interface now prefers the same-folder exe and falls back to `dotnet.exe "PowerToys.Monitor.dll"`.
- Settings clears stale `scan-progress.json` before starting a manual scan, resets the scan-completed event, and then polls worker-written snapshots.
- The worker reports real scan phases through `MonitorScanProgressFileReporter`; completed snapshots include the final record count.
- The manual worker smoke path was validated with a temporary Downloads directory so local user Downloads contents were not touched during verification.

## Recent Monitor And Light Switch Stabilization

The latest settings pass keeps the active module behavior closer to upstream PowerToys while preserving Kit's trimmed module surface:

- Monitor's Scan Now action sends the `scanNow` custom action and the worker runs one pass with `--use-configured-actions`. This keeps manual scan, category-folder creation, organization, installer cleanup, and CSV writing on one code path while letting `OrganizeDownloads` and `CleanInstallers` decide which side effects are allowed.
- Monitor's module enable path reads `runInBackground` before launching the worker. The module can stay enabled for Settings/Home/manual actions without starting a persistent worker.
- Monitor's Settings page now places `OrganizeDownloads`, `CleanInstallers`, and `Run in background` immediately below Manual scan, matching the setting's control flow.
- Light Switch keeps the upstream `Apply monitor settings to` shape and now routes PowerDisplay profile selection to the imported PowerDisplay Settings page. The controls are enabled from `GeneralSettings.Enabled.PowerDisplay`, and profile names are loaded from Kit storage at `%LOCALAPPDATA%\Kit\PowerDisplay\profiles.json` when that file exists. The loader remains tolerant of missing or malformed profile data.
- `Settings.UI.UnitTests` now has static regression coverage for Monitor settings order and Light Switch's PowerDisplay enable/profile-loading path.

## General and Home UI Scope

General keeps the useful PowerToys settings structure but removes automatic update and telemetry controls. The About section shows the Kit version, GitHub repository, and a check-only release prompt. Home uses the PowerToys-style intro, module list, Quick Access, and shortcuts layout, but only for Kit modules.

Visible UI should use English Kit text. Keep `PowerToys` only where it is still required for build-facing namespaces, assembly names, module interface names, upstream compatibility, or origin attribution.

## Stability Direction

Near-term work should optimize for predictable builds and low-risk PowerToys compatibility:

- Prefer upstream PowerToys patterns and small deltas over new local abstractions.
- Keep module registration explicit until the current runner/settings/module compatibility is boringly stable.
- Reduce places that need manual module-list updates only after the existing lists are covered by tests.
- Keep Settings, runner, module interface projects, Quick Access, and copied module projects buildable independently before widening to whole-solution builds.
- Keep runner build dependencies aligned with runtime-launched UI apps. `Kit.exe` can start and show a tray icon even when `WinUI3Apps\PowerToys.Settings.exe` is missing; Debug outputs can hide that problem with stale files, so clean Release validation must confirm both Settings and Quick Access executables are regenerated.
- Keep PowerToys CsWinRT metadata stable for copied modules. `PowerToys.Interop.winmd` and `PowerToys.GPOWrapper.winmd` are published into `$(RepoRoot)$(Platform)\$(Configuration)` by the native projects, and `Common.Dotnet.CsWinRT.props` invalidates stale `cswinrt.rsp` files when a previous failed or cleaned build left no generated projection sources. This prevents imported modules such as `Awake` and Quick Access from compiling before their `PowerToys.*` projections are regenerated.
- Repair or exclude stale upstream tests only when the missing production surface is intentionally removed. `Settings.UI.UnitTests` now excludes ViewModel tests for PowerToys modules that are not part of the active Kit Settings surface.
- Keep UI state derived from real settings and module state. Home should show enabled modules consistently, and each Quick Access command should either perform a real action or navigate to the module settings page.
- Keep Kit storage, backup, window title, and visible text separate from the installed official PowerToys app.
- Do not re-enable automatic download/install or telemetry behavior in Kit.
- Keep installer/updater entry points and settings telemetry inert. The runner may check GitHub releases and write `UpdateState.json`, but `update_now`, installer staging, updater executable launch paths, and the old settings telemetry source must remain inactive unless a future change deliberately replaces them with local-only behavior.
- Keep new modules split into a testable core library, worker process, native module interface, settings model, settings page, Home metadata, and static registration tests.
- Run C++ module-interface verification sequentially, or through the solution scheduler, when projects share native outputs such as `Version.pdb` and `PowerToys.Interop` tracking logs. Independent parallel MSBuild invocations can race those shared files and report false build failures.
- Keep documentation close to the implementation after each stabilization pass. The module-registration lists are intentionally manual, so stale docs are a real integration risk.

## Recent Release Build Regression

A clean Release x64 build exposed a PowerToys compatibility issue around CsWinRT and native WinMD outputs. The visible errors were missing `PowerToys.GPOWrapper`, missing `GpoRuleConfigured`, and missing `PowerToys.Interop.winmd` or `PowerToys.GPOWrapper.winmd` under `x64\Release`.

The investigation found two related failure modes:

- Native WinMD producer projects could finish without reliably publishing their merged WinMDs to the shared configuration output expected by copied PowerToys modules.
- Some managed projects could keep a stale `Generated Files\CsWinRT\cswinrt.rsp` file after a failed or cleaned build while the generated projection `.cs` files were gone. CsWinRT then skipped regeneration and later C# compilation failed because the `PowerToys.*` namespaces were absent.

The compatibility fix keeps the upstream PowerToys dependency shape intact:

- `PowerToys.Interop.vcxproj` and `GPOWrapper.vcxproj` now copy their WinMD outputs into `$(RepoRoot)$(Platform)\$(Configuration)`.
- `Common.Dotnet.CsWinRT.props` removes stale CsWinRT response files when no generated projection sources exist, forcing projection regeneration.
- `Settings.UI.UnitTests` has a `BuildCompatibility` regression check for the stale-projection guard and shared WinMD publication rules.

Two additional full-solution Release cleanup items were handled during the same pass: the DSC module list no longer advertises the removed `MouseJump` settings surface, and `UnitTests-CommonUtils` now builds with `/utf-8` so upstream `spdlog/fmt` Unicode support is accepted consistently.

## Artifact Cleanup

After the framework reached a usable state, the local workspace was cleaned from build-output size back to source size. The large directories were generated artifacts, not required source:

- `src\kit\x64`
- `src\kit\Release`
- `src\kit\.vs`
- root `TestResults`
- project-local `bin`, `obj`, `x64`, `Debug`, `Release`, and `TestResults` directories under `src\kit\src` and `src\kit\tools`
- `src\kit\packages`

The first cleanup pass removed about 28.71 GB of compiler and test outputs. A later full cleanup removed about 39 GB of regenerated Debug/Release outputs. `src\kit\packages` is a NuGet restore cache, not source; it is already covered by `src\kit\.gitignore` through `**/[Pp]ackages/*`, so it should not be uploaded to GitHub. Removing `packages` is safe for source state, but the next Visual Studio or MSBuild compile must restore NuGet packages again and may take longer on the first run.

Recommended cleanup policy:

- Before GitHub upload or archival, remove `src\kit\x64`, `src\kit\Debug`, `src\kit\Release`, `.vs`, `TestResults`, project `bin`/`obj` folders, and `src\kit\packages`.
- During local iterative development, keep `src\kit\packages` if disk space allows. It prevents cold-build failures and slow restores caused by missing packages such as WIL and C++/WinRT.
- If `packages` was removed, run Visual Studio `Restore NuGet Packages` or perform a full solution build before judging compile errors from missing headers or WinMD projections.
- Release builds keep only `en-US` satellite resources, remove generated debug symbols and native link artifacts from runtime outputs, exclude inactive OOBE and AI model assets from the Settings publish, and no longer build the AdvancedPaste-only `LanguageModelProvider` dependency for the active Kit module set.
- WindowsAppSDK 1.8 still contributes its own Windows AI/Onnx runtime files through the `Microsoft.WindowsAppSDK` meta-package. Removing those would require replacing the meta-package with granular WindowsAppSDK package references, so it is deferred until Settings compatibility can be validated more broadly.

After source-size cleanup, `src\kit` should look close to source-only size: source and docs remain, while `x64`, `Release`, `.vs`, `packages`, and project `bin`/`obj` directories should be absent until the next restore/build.

## Git Worktree Cleanup

Git worktrees are used only when an isolated branch workspace is needed. On 2026-04-29, `git worktree prune` removed the stale `C:\Users\Zen\Repo\Codings\Kit\.worktrees\kit-phase1-host` record. The current `git worktree list --porcelain` baseline should show only `C:\Users\Zen\Repo\Codes\Kit` unless a new isolated worktree has been created deliberately.

Use `git worktree prune` for stale records that Git already marks prunable. Do not delete a live worktree directory until its branch state and uncommitted files have been checked.

## First Plugin Direction

Kit does not yet have an active third-party plugin host. The practical first step is a PowerToys-style module import or a small Kit module that uses the existing runner/settings/module interface contract. Monitor is the first module following this route.

Use the copied plugin docs under `doc/devdoc` as reference material, not as an active contract, until PowerToys Run or Command Palette is intentionally imported. If the first plugin must be a PowerToys Run plugin, import and stabilize the Run host first; otherwise, build the first feature as an explicit Kit module and wire it through the same maintained lists used by `Awake` and `LightSwitch`.

See `doc/devdoc/kit-first-plugin.md` for the first-module checklist and validation baseline. See `doc/devdoc/kit-development-experience.md` for the first-phase lessons learned and next stabilization checklist.

## Adding Another PowerToys Module

Use this checklist when importing another upstream module:

1. Copy the module source and keep its upstream project shape intact where possible.
2. Add the module projects and required build dependencies to `Kit.slnx`.
3. Add the module interface DLL to the runner `KitKnownModules` list.
4. Add Settings navigation, route mapping, page/view model inclusion, and GPO page mapping only for the imported module.
5. Keep upstream CsWinRT references intact when the module uses `PowerToys.Interop` or `PowerToys.GPOWrapper`; build the module once from a clean Release tree to confirm the WinMD projections regenerate.
6. Add Home dashboard metadata only when the module should appear on Home.
7. Add Quick Access behavior only when there is a real quick action; otherwise use settings-page navigation as the fallback.
8. Add focused static or unit coverage for the runner list, navigation route, dashboard list, Quick Access behavior, and any added WinMD/GPO dependency.
9. Validate targeted builds before broader solution builds.

## Verification Snapshot

Local verification on 2026-04-25 used Visual Studio 18 MSBuild and VSTest. The following targeted Debug x64 builds passed with 0 warnings and 0 errors:

- `Monitor.UnitTests.csproj` Debug x64
- `PowerToys.Monitor.csproj` Debug x64
- `MonitorModuleInterface.vcxproj` Debug x64
- `PowerToys.Settings.csproj` Debug x64
- `PowerToys.QuickAccess.csproj` Debug x64
- `Kit.vcxproj` Debug x64
- `Awake.csproj` Debug x64
- `AwakeModuleInterface.vcxproj` Debug x64
- `LightSwitchModuleInterface.vcxproj` Debug x64
- `LightSwitchService.vcxproj` Debug x64

`Settings.UI.UnitTests.csproj` now builds cleanly after aligning the test project with Kit's trimmed module set and Kit settings path. `vstest.console.exe` passed `Settings.UI.UnitTests.dll` with 59/59 tests passing. `vstest.console.exe` also passed `Monitor.UnitTests.dll` with 13/13 tests passing, including static coverage for Monitor runner/solution registration and worker lifetime handling.

After the Release runner build-dependency fix, the targeted `Kit.slnx /t:Kit` Release x64 build passed and produced the runtime trio expected from a clean tree:

- `x64\Release\Kit.exe`
- `x64\Release\WinUI3Apps\PowerToys.Settings.exe`
- `x64\Release\WinUI3Apps\PowerToys.QuickAccess.exe`

After the PowerToys CsWinRT/WinMD compatibility fix, a full `Kit.slnx` Release x64 build also passed locally and produced the copied-module metadata expected by Awake, Quick Access, Settings, DSC, and other PowerToys-derived surfaces:

- `x64\Release\PowerToys.Interop.winmd`
- `x64\Release\PowerToys.GPOWrapper.winmd`
- regenerated CsWinRT projections such as `PowerToys.GPOWrapper.cs` in consuming project `obj` directories

Local verification on 2026-04-29 covered the latest Monitor/Light Switch Settings pass:

- `Settings.UI.UnitTests.csproj` Debug x64 built with Visual Studio 18 MSBuild.
- `dotnet test Settings.UI.UnitTests.csproj -p:Platform=x64 -p:Configuration=Debug --no-build --filter "LightSwitchPowerDisplayIntegrationShouldFollowOriginalModuleContract|MonitorRunInBackgroundShouldBeImmediatelyAfterManualScan"` ran the Settings UI test assembly with 77/77 tests passing.
- `PowerToys.Settings.csproj` Release x64 built successfully and regenerated `x64\Release\WinUI3Apps\PowerToys.Settings.dll`.
- `git worktree prune` removed the stale external worktree metadata, and `git worktree list --porcelain` now reports only the current `C:\Users\Zen\Repo\Codes\Kit` worktree.

Before handing a clean tree to Visual Studio, local build outputs and restore caches can be removed. The next compile should recreate the runtime output directory, the `WinUI3Apps` children, shared WinMD files, CsWinRT projections, and package restore cache together.
