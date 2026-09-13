# Kit

**Language / 语言:** English | [中文](README_zh.md)

---

Kit is a local, self-use Windows utility workspace derived from Microsoft PowerToys. It exists so selected PowerToys utilities can be modified, isolated, and compared against an installed official PowerToys build on the same machine.

## Project Goal

Kit is currently a stability-first PowerToys-derived workspace, not a full product rebrand. The main design choice is to keep the upstream runner, module interface, settings, and dashboard patterns recognizable so copied PowerToys modules can be validated with minimal adapter code.

Kit-specific changes should stay small and intentional: branding, settings storage, visible navigation, Home content, backup and restore defaults, and removal of product services that do not belong in a local workspace.

## Current Version

Current Kit version: `2.0.10`.

## Documentation

- [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) — Authoritative guide for developing Kit plugins and modules (WinUI 3 + Mica Alt, PowertoyModuleIface C++ contract, Zero Telemetry, and process watchdog).
- `doc/devdoc/kit-architecture.md` — Kit architecture reference (lightweight plugin-host direction).
- `doc/devdoc/powertoys-architecture.md` — verified upstream PowerToys framework architecture reference (Chinese).
- `doc/devdoc/architecture-comparison.md` — PowerToys vs Kit comparison and startup optimization analysis (Chinese).
- `doc/devdoc/kit-first-plugin.md` — first-module checklist and validation baseline.
- `doc/devdoc/kit-development-experience.md` — first-phase lessons learned and next stabilization checklist.
- `doc/devdoc/startup-optimization-analysis.md` — startup optimization analysis (Chinese).
- `fix.plan` — phased plugin-host plan (repo root).
- `fix.md` — upstream delta and fix list (repo root).
- `next.md` — sync progress and upstream-delta checklist (repo root).
- [changelog.md](changelog.md) — version history.


## Build Output Structure

Kit uses a version-based build output organization:

```
Kit/
├── bin/
│   ├── debug/
│   │   ├── 2.0.10/             # Current version debug build
│   │   │   ├── Kit.exe
│   │   │   ├── *.dll           (runtime dependencies)
│   │   │   └── Kit/            (application data subdirectory)
│   │   └── 2.0.11/             # Future versions
│   ├── release/
│   │   ├── 2.0.10/             # Current version release build
│   │   └── 2.0.11/             # Future versions
│   └── publish/
│       ├── 2.0.10.zip          # Packaged release distributions
│       └── 2.0.11.zip
```

**Version Numbering:**
- Version is extracted from `src/common/version/Generated Files/version_gen.h`
- Current: 2.0.10 (VERSION_MAJOR=2, VERSION_MINOR=0, VERSION_REVISION=10)
- After each build, outputs are organized into the corresponding version directory
- Release distributions are packaged as `.zip` files in `bin/publish/`. All build outputs are consolidated into the `bin/` directory at the repository root, and the scattered root-level build directories (`Debug/`, `Release/`, `x64/`, `AnyCPU/`) that MSBuild generates are removed after builds complete.

**Differences from PowerToys:**

| Aspect | PowerToys | Kit |
|--------|-----------|-----|
| **Organization** | Configuration-first (x64/Debug/) | Version-first (bin/debug/2.0.10/) |
| **Location** | Repository root | Centralized bin/ folder |
| **Versioning** | Not reflected in paths | Explicit version subdirectories |
| **Publishing** | Manual packaging | Dedicated bin/publish/ with .zip files |
| **Cleanup** | Configuration directories persist | Scattered outputs removed, only bin/ kept |

## Changelog

See [changelog.md](changelog.md) for the full version history.

## Phase One Closeout

The first phase is now effectively a working Kit shell hosting the active PowerToys-style modules. The framework can load explicit PowerToys-style modules, show them in Settings and Home, keep Kit-branded storage separate from official PowerToys, and run each module through the existing runner/module-interface/settings path.

The current stable handoff point is:

- Keep `Awake` and `Light Switch` as the active module set.
- Keep first-party module discovery explicit through maintained lists and tests. A third-party plugin host (`plugins/` + manifest) is planned per `fix.plan` but is not implemented yet.
- Keep General and Home in English Kit wording, with automatic update and telemetry surfaces removed.
- Keep Kit UI automation pointed at Kit's runner, Settings window, install roots, and the active module executables. It must not attach to an installed upstream PowerToys build by accident.
- Keep Settings deep links and module settings links launching `Kit.exe` only. Do not fall back to an installed upstream `PowerToys.exe` from Kit UI.
- Clean build artifacts before handoff so the next Visual Studio build starts from source state.
- The workspace can be reduced back to source size after a stable handoff. Local `Debug`, `Release`, `x64`, `bin`, `obj`, `TestResults`, `.vs`, and restored `packages` directories are disposable build state.

## Architecture

- `src/runner` starts Kit, loads module interface DLLs, owns module lifetime, and coordinates settings IPC with the Settings app. The executable is already separated enough to launch as `Kit.exe` while many build-facing project names still retain upstream PowerToys names. At runtime the runner opens the Settings and Quick Access apps from `WinUI3Apps` next to `Kit.exe`, so the runner build target must keep explicit dependencies on both UI executable projects.
- `src/modules` contains the active utilities. `Awake` is copied from upstream PowerToys with `Awake.ModuleServices`, `Awake`, and `AwakeModuleInterface`; `LightSwitch` is the current Kit utility module
- `src/settings-ui/Settings.UI` contains the WinUI Settings app, including Home, General, module pages, navigation, and page-level view models.
- `src/settings-ui/Settings.UI.Controls` contains shared UI controls such as Quick Access.
- `src/settings-ui/Settings.UI.Library` contains settings models, settings serialization, module settings repositories, backup and restore helpers, GPO helpers, and shared settings infrastructure.
- `src/common` retains shared native and managed PowerToys infrastructure used by the runner, modules, and Settings.

Runtime settings are stored under Kit-specific application data, such as `%LOCALAPPDATA%\Kit\settings.json`, rather than the official PowerToys settings directory. Backup and restore defaults also use Kit branding, including `Documents\Kit\Backup`, `HKCU\Software\Microsoft\Kit`, and `Kit_settings_*` temporary backup folders.

## Current Module Set

The active Kit module set is deliberately small:

- `Awake`
- `Light Switch`

`Monitor` was removed in `2.0.8`; see the removal record in `doc/devdoc/kit-development-experience.md` and the version history in [changelog.md](changelog.md).

Kit does not automatically expose every upstream PowerToys utility copied in the source tree. Modules are enabled only after they are registered in the maintained Kit lists for the runner, Settings navigation, Home, and tests.

## PowerToys Compatibility Model

Kit follows the PowerToys module-loading model instead of inventing a new plugin protocol. The runner loads known module interface DLLs through the maintained `KitKnownModules` list in `src/runner/main.cpp`, currently:

- `PowerToys.AwakeModuleInterface.dll`
- `PowerToys.LightSwitchModuleInterface.dll`

This fixed list is intentional for first-party modules: it avoids unstable directory probing and makes each imported module an explicit compatibility decision. The planned third-party plugin host (`fix.plan` P1) adds a `plugins/` folder plus `manifest.json` for externally developed plugins while leaving this first-party list untouched. When another first-party PowerToys module is brought into Kit, it should be added to the runner, solution, settings routing, Home dashboard metadata, and tests together.

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

## Plugin Direction

Kit's core direction is a lightweight plugin host: keep the main framework (runner + Settings UI + common libraries) free of module business logic, start fast, and host both official PowerToys modules and third-party custom plugins through the same `PowertoyModuleIface` + `powertoy_create()` contract.

- First-party modules (`Awake`, `Light Switch`) stay in the compiled `KitKnownModules` list for deep integration (Home, Quick Access, settings routes, tests).
- Third-party plugins are planned to load from a `plugins/` folder (interface DLL + `manifest.json`), with only enabled plugins loaded and a generic settings page rendering each plugin's `get_config` JSON.
- Official PowerToys module copies need framework prerequisites first (ManagedTelemetry, logger/settings/EtwTrace sync) — see `fix.plan` P0.

Until the plugin host lands, `Light Switch` remains the Kit-authored module following the existing contract route, and `Awake` is the upstream-copied baseline. For comprehensive development instructions, see [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md); use `doc/devdoc/kit-first-plugin.md` for the first-module checklist and `doc/devdoc/kit-development-experience.md` for lessons learned. The actionable phased plan is `fix.plan` (repo root); the target architecture is `doc/devdoc/kit-architecture.md`.

## Stability Direction

Near-term work should optimize for predictable builds and low-risk PowerToys compatibility:

- Prefer upstream PowerToys patterns and small deltas over new local abstractions.
- Keep module registration explicit until the current runner/settings/module compatibility is boringly stable.
- Reduce places that need manual module-list updates only after the existing lists are covered by tests.
- Keep Settings, runner, module interface projects, Quick Access, and copied module projects buildable independently before widening to whole-solution builds.
- Keep runner build dependencies aligned with runtime-launched UI apps. `Kit.exe` can start and show a tray icon even when `WinUI3Apps\PowerToys.Settings.exe` is missing; Debug outputs can hide that problem with stale files, so clean Release validation must confirm both Settings and Quick Access executables are regenerated.
- Keep PowerToys CsWinRT metadata stable for copied modules. `PowerToys.Interop.winmd` and `PowerToys.GPOWrapper.winmd` are published into `$(RepoRoot)$(Platform)\$(Configuration)` by the native projects, and `Common.Dotnet.CsWinRT.props` invalidates stale `cswinrt.rsp` files when a previous failed or cleaned build left no generated projection sources. This prevents imported modules such as `Awake` and Quick Access from compiling before their `PowerToys.*` projections are regenerated.
- Delete intentionally removed upstream tests and sources rather than hiding them behind project exclusions. `Settings.UI.UnitTests` now has `BuildCompatibility` coverage for inactive Settings sources, unit tests, assets, icons, controls, converters, the legacy sibling Settings asset tree, and stale WinUI output cleanup.
- Keep UI state derived from real settings and module state. Home should show enabled modules consistently, and each Quick Access command should either perform a real action or navigate to the module settings page.
- Keep Kit storage, backup, window title, and visible text separate from the installed official PowerToys app. Backup defaults should stay generic to Kit's active module settings and not carry inactive PowerToys module-specific files or restore fix-ups.
- Do not re-enable automatic download/install or telemetry behavior in Kit.
- Keep installer/updater entry points and settings telemetry inert. The runner may check GitHub releases and write `UpdateState.json`, but `update_now`, installer staging, updater executable launch paths, and the old settings telemetry source must remain inactive unless a future change deliberately replaces them with local-only behavior.
- Keep GPO policy wrappers and ADMX/ADML policy assets scoped to active modules that currently have policy rules, plus retained product-wide startup/update/diagnostics rules. Do not carry inactive PowerToys module policies, installer-only policy readers, or stale update-toast readers in Kit.
- Keep DSC-only Settings command-line entry points out of Kit. The retained Settings command-line surface is only the active `set`/`get` compatibility paths; do not restore `setAdditional` unless DSC generation returns as an active feature.
- Keep OOBE/SCOOBE launch and state paths out of Kit while those windows are not shipped. Do not restore their SettingsAPI helpers, backup rules, resources, or styles unless the full onboarding surface returns as an active feature.
- Keep the upstream BugReportTool out of the active Kit runtime. Its collection model is broad PowerToys diagnostic state, including inactive modules that Kit does not ship.
- Keep inactive Command Palette and standalone module-loader development surfaces out of Kit until they are intentionally imported. The orphaned CmdPal version props and `tools/module_loader` should be deleted instead of copied forward.
- Keep new modules split into a testable core library, worker process, native module interface, settings model, settings page, Home metadata, and static registration tests.
- Run C++ module-interface verification sequentially, or through the solution scheduler, when projects share native outputs such as `Version.pdb` and `PowerToys.Interop` tracking logs. Independent parallel MSBuild invocations can race those shared files and report false build failures.
- Keep local build scripts friendly to non-VS shells. Forward MSBuild arguments as arrays, cache the resolved MSBuild path after importing the Visual Studio environment, normalize duplicate `PATH`/`Path` values from `VsDevCmd.bat`, and skip local CopyOnWrite/RunVSTest SDK resolver imports by default when package source mapping blocks SDK restore.
- Keep removed module surfaces deleted from runner, Settings, GPO, Quick Access, and solution files instead of hiding them behind project exclusions.
- Keep development signing scoped and explicit. Current-user certificate trust is the default local path; machine-wide root trust, recursive package signing, and non-sparse package signing should be opt-in.
- Keep documentation close to the implementation after each stabilization pass. The module-registration lists are intentionally manual, so stale docs are a real integration risk.

## Recent Awake and Home Implementation

The latest Home work keeps PowerToys behavior but scopes it to Kit's active modules:

- `DashboardViewModel` uses `KitModuleCatalog.DashboardModules`, currently `Awake` and `LightSwitch`, so the Home utility list is fixed and predictable.
- `QuickAccessViewModel` still supports actionable Quick Access items, but Home passes the dashboard module list so enabled Kit modules appear consistently.
- Quick Access first tries the normal launcher. If a module has no direct quick action, Home falls back to opening that module's settings page. This lets `Awake` behave usefully without creating a fake shortcut action while `LightSwitch` keeps its direct toggle action.
- `Awake` contributes a `DashboardModuleActivationItem` that displays the current Awake mode in the Home shortcuts card, using the existing PowerToys dashboard item template.
- The Quick Access empty state now uses the count of visible items, not the raw item collection count, so disabled or GPO-hidden modules do not leave an empty card visible.

## General and Home UI Scope

General keeps the useful PowerToys settings structure but removes automatic update and telemetry controls. The About section shows the Kit version, GitHub repository, and a check-only release prompt. Home uses the PowerToys-style intro, module list, Quick Access, and shortcuts layout, but only for Kit modules.

Visible UI should use English Kit text. Keep `PowerToys` only where it is still required for build-facing namespaces, assembly names, module interface names, upstream compatibility, or origin attribution.


## Artifact Cleanup

After the framework reached a usable state, the local workspace was cleaned from build-output size back to source size. The large directories were generated artifacts, not required source:

- root `x64`, `Debug`, `Release`, `.vs`
- root `TestResults`
- project-local `bin`, `obj`, `x64`, `Debug`, `Release`, and `TestResults` directories under `src` and `tools`
- root `packages`

The first cleanup pass removed about 28.71 GB of compiler and test outputs. A later full cleanup removed about 39 GB of regenerated Debug/Release outputs. `packages` is a NuGet restore cache, not source; it is already covered by `.gitignore` through `**/[Pp]ackages/*`, so it should not be uploaded to GitHub. Removing `packages` is safe for source state, but the next Visual Studio or MSBuild compile must restore NuGet packages again and may take longer on the first run.

Recommended cleanup policy:

- Before GitHub upload or archival, remove root `x64`, `Debug`, `Release`, `.vs`, `TestResults`, project `bin`/`obj` folders, and root `packages`.
- During local iterative development, keep `packages` if disk space allows. It prevents cold-build failures and slow restores caused by missing packages such as WIL and C++/WinRT.
- If `packages` was removed, run Visual Studio `Restore NuGet Packages` or perform a full solution build before judging compile errors from missing headers or WinMD projections.
- Release builds keep only `en-US` satellite resources, remove generated debug symbols and native link artifacts from runtime outputs, prune inactive Settings module assets, icons, resource strings, OOBE/model assets, stale inactive control XBF outputs, and no longer carry the AdvancedPaste-only `LanguageModelProvider` source tree, AI provider package pins, provider UI metadata/helpers, or non-serialized AI enum helpers for the active Kit module set.
- Shortcut Conflict hotkey lookup is explicit for Quick Access and LightSwitch instead of scanning every historical `IHotkeyConfig` settings model from the PowerToys-derived library.
- WindowsAppSDK 1.8 still contributes its own Windows AI/Onnx runtime files through the `Microsoft.WindowsAppSDK` meta-package. Removing those would require replacing the meta-package with granular WindowsAppSDK package references, so it is deferred until Settings compatibility can be validated more broadly.

After source-size cleanup, the repository should look close to source-only size: source and docs remain, while `x64`, `Release`, `.vs`, `packages`, and project `bin`/`obj` directories should be absent until the next restore/build.

## Git Worktree Cleanup

Git worktrees are used only when an isolated branch workspace is needed. On 2026-04-29, `git worktree prune` removed a stale external worktree record. The current `git worktree list --porcelain` baseline should show only the active Kit worktree unless a new isolated worktree has been created deliberately.

Use `git worktree prune` for stale records that Git already marks prunable. Do not delete a live worktree directory until its branch state and uncommitted files have been checked.

## Recent Light Switch Stabilization

The latest settings pass keeps the active module behavior closer to upstream PowerToys while preserving Kit's trimmed module surface:

- Light Switch keeps the upstream schedule, Night Light, and toggle-hotkey shape, but no longer carries the deleted PowerDisplay profile bridge.
- `Settings.UI.UnitTests` now has static regression coverage for Light Switch's no-PowerDisplay boundary and the removed Monitor module surface.

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

## Verification Snapshot

Local verification on 2026-04-25 used Visual Studio 18 MSBuild and VSTest. The following targeted Debug x64 builds passed with 0 warnings and 0 errors:

- `PowerToys.Settings.csproj` Debug x64
- `PowerToys.QuickAccess.csproj` Debug x64
- `Kit.vcxproj` Debug x64
- `Awake.csproj` Debug x64
- `AwakeModuleInterface.vcxproj` Debug x64
- `LightSwitchModuleInterface.vcxproj` Debug x64
- `LightSwitchService.vcxproj` Debug x64

`Settings.UI.UnitTests.csproj` now builds cleanly after aligning the test project with Kit's trimmed module set and Kit settings path. `vstest.console.exe` passed `Settings.UI.UnitTests.dll` with 59/59 tests passing, including static coverage for runner/solution registration and the removed Monitor surface.

After the Release runner build-dependency fix, the targeted `Kit.slnx /t:Kit` Release x64 build passed and produced the runtime trio expected from a clean tree:

- `x64\Release\Kit.exe`
- `x64\Release\WinUI3Apps\PowerToys.Settings.exe`
- `x64\Release\WinUI3Apps\PowerToys.QuickAccess.exe`

After the PowerToys CsWinRT/WinMD compatibility fix, a full `Kit.slnx` Release x64 build also passed locally and produced the copied-module metadata expected by Awake, Quick Access, Settings, DSC, and other PowerToys-derived surfaces:

- `x64\Release\PowerToys.Interop.winmd`
- `x64\Release\PowerToys.GPOWrapper.winmd`
- regenerated CsWinRT projections such as `PowerToys.GPOWrapper.cs` in consuming project `obj` directories

Local verification on 2026-04-29 covered the latest Light Switch Settings pass:

- `Settings.UI.UnitTests.csproj` Debug x64 built with Visual Studio 18 MSBuild.
- `vstest.console.exe` ran `Settings.UI.UnitTests.dll` with a filter for `LightSwitchPowerDisplayIntegrationShouldFollowOriginalModuleContract`; 77/77 tests passed.
- `PowerToys.Settings.csproj` Release x64 built successfully and regenerated `x64\Release\WinUI3Apps\PowerToys.Settings.dll`.
- `git worktree prune` removed the stale external worktree metadata, and `git worktree list --porcelain` now reports only the active Kit worktree.

Before handing a clean tree to Visual Studio, local build outputs and restore caches can be removed. The next compile should recreate the runtime output directory, the `WinUI3Apps` children, shared WinMD files, CsWinRT projections, and package restore cache together.