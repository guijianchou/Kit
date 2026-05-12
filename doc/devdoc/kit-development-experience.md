# Kit Development Experience

This note captures the first-phase lessons from turning the PowerToys-derived Kit shell into a stable local workspace and adding Monitor as the first Kit-authored module.

## 2026-05-12 Version 1.2.0 Release Metadata

This pass moved Kit from 1.1.6 to 1.2.0 after the update-check scheduler hardening and documentation cleanup.

- Version.props, README, README_zh, changelog, and the version metadata regression test now use Kit version `1.2.0`.
- The changelog remains the source for release notes after the README cleanup.
- The check-only update boundary remains unchanged: Kit checks `https://github.com/guijianchou/Kit/releases` and does not auto-download or launch an updater.

## 2026-05-11 General Update Layout Cleanup And 1.1.6 Release Notes

This pass moved Kit from 1.1.5 to 1.1.6 and cleaned up the General page update surface after aligning release checking with the local PowerToys-main pattern.

- General again uses a top `General_VersionAndUpdate` section for version and update state. The version is no longer repeated in a bottom About card.
- The manual "Checking for updates" row now lives inside the version/update expander, while the update result InfoBar sits below the expander like PowerToys-main.
- Kit keeps the update flow check-only: no automatic download, `Download & install`, `Install now`, or updater launch UI is restored.
- Release links continue to point at `https://github.com/guijianchou/Kit/releases`.
- README, README_zh, Version.props, and the version metadata regression test now use Kit version `1.1.6`.

## 2026-05-11 Update Check Architecture And 1.1.5 Release Notes

This pass moved Kit from 1.1.4 to 1.1.5 and removed the patch-on-patch update-check flow that had drifted away from the local PowerToys-main shape.

- Runner owns the active release check and writes the same upstream-style `UpdateState.json` contract that Settings already knows how to watch. Settings no longer writes update results or owns a polling loop.
- Manual checks and daily checks share the same runner code path, guarded so repeated clicks cannot queue parallel GitHub requests.
- Settings captures the pre-click update-state timestamp and accepts only a newer result for the current manual check. File watcher refreshes normally complete the visible "Checking for updates" state; the timeout path only prevents a lost IPC/file event from leaving the UI stuck forever.
- The release-check boundary remains GitHub prompt only: no automatic download, installer staging, updater executable, or `update_now` flow is restored.
- README, README_zh, Version.props, and the version metadata regression test now use Kit version `1.1.5`.

## 2026-05-09 Update Check Reliability And 1.1.4 Release Notes

This pass moved Kit from 1.1.3 to 1.1.4 and fixed the manual update check path that could report "up to date" from cached data while the machine was offline.

- Runner release checks now use a WinRT HTTP client with no-cache read and write behavior, plus `Cache-Control` and `Pragma` no-cache headers for GitHub's latest release API.
- Settings prevents page-load cached state from overwriting the visible in-flight "Checking for updates" status.
- README, README_zh, Version.props, and the version metadata regression test now use Kit version `1.1.4`.

## Phase One Result

Kit now has a small, explicit module surface:

- `Awake`, copied from upstream PowerToys to validate compatibility.
- `Light Switch`, the existing Kit module.
- `Monitor`, the first module built from a previous Python implementation and wired through the PowerToys module shape.
- `PowerDisplay`, imported from the PowerToys-style module shape to validate a larger module with a WinUI app, model library, Settings page, profile dialogs, named-pipe control, and Light Switch profile integration.

The important result is not that every PowerToys module is available. The important result is that the runner, Settings app, Home dashboard, Quick Access, Kit-branded storage, backup paths, and tests agree on the same intentionally maintained module set.

## Decisions That Worked

- Keep the PowerToys module contract. The runner still loads module interface DLLs, each module exposes the expected exports, and Settings talks to modules through the existing IPC/custom-action path.
- Keep module loading explicit. `src/runner/main.cpp` owns the known module DLL list. This avoided fragile source-tree probing and made each imported module a deliberate compatibility decision.
- Add tests around every manual registration point. Monitor has static coverage for runner registration, solution inclusion, Settings route/page wiring, Home listing, Quick Access visibility, and worker project shape.
- Split new module work into a testable core library, a worker process, a native module interface, and Settings/Home integration. This made Monitor easier to validate than a single app-style port of the Python code.
- Keep worker UI out of the worker. Enabling a module should not show a standalone window; visible actions belong in Settings or Home.
- Preserve upstream layout where possible. Most friction came from local deltas drifting from PowerToys conventions, not from the conventions themselves.

## Monitor Lessons

Monitor is the reference shape for the next Kit-authored module:

- `MonitorLib` owns testable behavior: scan rules, hashing, CSV persistence, duplicate grouping, organization, and installer-cleanup primitives.
- `PowerToys.Monitor.exe` is a headless worker. It supports one-shot scans and runner-managed lifetime.
- `PowerToys.MonitorModuleInterface.dll` owns enable/disable, worker launch, exit-event signaling, and custom actions.
- `MonitorSettings`, `MonitorProperties`, and `SndMonitorSettings` keep the settings model aligned with the Settings app and serialization context.
- `MonitorPage` exposes manual scan, `OrganizeDownloads`, `CleanInstallers`, the separate `Run in background` toggle, Downloads folder selection, hash algorithm selection, and worker-reported scan progress in the same PowerToys-style Settings surface.

The Monitor module toggle and background worker toggle are deliberately separate. The module toggle controls whether Settings, Home, and custom actions are usable. `Run in background` controls whether the runner starts the persistent worker on enable. Manual Scan remains available when background mode is off and sends a one-shot `scanNow` action. That action now uses the same configured-action path as the worker: each run creates any missing category folders, applies `OrganizeDownloads` when enabled, applies `CleanInstallers` when enabled, scans the Downloads tree, and writes `results.csv`.

Scan progress is reported by the worker through `%LOCALAPPDATA%\Kit\Monitor\scan-progress.json` plus a named scan-completed event. Settings still owns the visual timer that polls this state, but it no longer invents completion by incrementing a UI-only counter.

## 2026-04-29 Settings Stabilization

This pass tightened two Settings behaviors without widening the active module set:

- Monitor's `Run in background` card was moved directly under Manual scan so the UI matches the control flow: manual one-shot work first, then optional persistent background mode, then scan configuration.
- A static Settings UI regression test now verifies that `Monitor_RunInBackgroundSettingsCard` stays immediately below `Monitor_ScanNowSettingsCard` and before folder/path settings.
- Monitor later added explicit `OrganizeDownloads` and `CleanInstallers` toggles above `Run in background`. Defaults are `OrganizeDownloads=true`, `CleanInstallers=false`, and `RunInBackground=false`, so a plain Scan Now organizes by default but does not delete installers unless the cleanup toggle is enabled.
- The worker now keeps the Monitor pass order stable: ensure category folders, optionally organize root Downloads files, optionally clean matched installers, scan, then write CSV.
- Light Switch's `Apply monitor settings to` controls were traced against `PowerToys-main`. The upstream page gates those controls on `IsPowerDisplayEnabled`; Kit had drifted by hardcoding that value to `false`, which made the option impossible to enable.
- Kit now restores the upstream enable check by reading `GeneralSettings.Enabled.PowerDisplay`, while keeping the implementation safe for a trimmed build where the full PowerDisplay module is not active.
- PowerDisplay profile names are loaded from Kit storage at `%LOCALAPPDATA%\Kit\PowerDisplay\profiles.json` with a lightweight JSON parser. Missing, malformed, or incomplete profile data clears the list instead of breaking the Light Switch page.
- The regression test for Light Switch checks the GeneralSettings PowerDisplay gate, profile-file path, JSON parsing, and profile list population so this optional bridge does not silently regress again.

## 2026-05-05 Monitor Progress And Worker Launch Stabilization

This pass fixed the manual Monitor scan path that could leave Settings stuck on `Waiting for worker progress` while the Downloads folder remained unchanged.

- Runner logs showed `scanNow` was dispatched to Monitor, so the Settings-to-runner IPC path was intact.
- Monitor module-interface logs showed `Failed to locate Monitor executable named 'PowerToys.Monitor.exe'`, which meant the worker never started.
- The module interface now resolves the worker from its own output directory, prefers `PowerToys.Monitor.exe`, and falls back to `dotnet.exe "PowerToys.Monitor.dll"` for Debug outputs where the apphost exe is missing.
- Settings clears stale `scan-progress.json` before starting a manual scan, resets the scan-completed event, and displays only worker-written progress snapshots.
- `MonitorScanProgressFileReporter` writes progress through temp-file replacement, so Settings can poll progress without reading a partially written JSON file.
- The worker emits scan phases and a final completed snapshot with record count, then signals the named scan-completed event.
- The fix stays inside the PowerToys module-interface pattern: hidden worker process, module-owned lifetime, explicit custom actions, and no filesystem module probing.

## 2026-05-06 PowerDisplay Integration And 1.1.0 Release Notes

This pass moved the PowerDisplay import into the 1.1.0 release baseline and documented the larger active-module set.

- PowerDisplay is now part of the maintained active module list alongside Awake, Light Switch, and Monitor.
- Settings UI wiring follows the upstream Settings framework: Shell navigation, route mapping, Settings page/view model, profile dialogs, Dashboard metadata, Quick Access actions, module helper mapping, and serialization registration are all explicit.
- The module is isolated from an installed official PowerToys build by using Kit app-data paths, Kit-prefixed PowerDisplay runtime events, Kit-prefixed Light Switch bridge events, and a `kit_power_display_` named-pipe prefix.
- Light Switch now points its PowerDisplay profile routing at the imported PowerDisplay Settings page and reads profile names from Kit storage.
- The .NET Settings/Common UI build layer follows the local PowerToys-main net10 baseline for target frameworks and central package pins, while updater and telemetry entry points remain intentionally inert.
- The README changelog, development notes, source version props, generated version header, and version regression test now use Kit version `1.1.0`.
- Verification for this baseline used Visual Studio MSBuild/VSTest rather than plain `dotnet test`, because the Settings and module projects transitively build native C++/WinUI targets.

## 2026-05-06 .NET 10 Build Baseline And 1.1.1 Release Notes

This pass moved Kit from the 1.1.0 PowerDisplay baseline to the 1.1.1 build-alignment baseline.

- `Common.Dotnet.CsWinRT.props` now uses `net10.0`, matching the local PowerToys-main shared .NET target framework.
- Quick Access, Settings UI Controls, Common UI Controls, and UITestAutomation now target `net10.0-windows10.0.26100.0`.
- `Directory.Packages.props` now follows the local PowerToys-main .NET 10 central package pins, including .NET 10 `Microsoft.Extensions.*`, `System.*`, WindowsAppSDK, and analyzer package versions.
- Settings build entry points were adjusted so targeted Settings builds restore and build `Settings.UI.XamlIndexBuilder` correctly after the net10 migration.
- Build scripts and developer docs now reference the .NET 10 Settings target framework and PowerToys Run plugin target framework.
- The 1.1.1 changelog originally kept Kit's updater boundary fully inert. Starting in 1.1.3, only GitHub release checking is active; downloads, updater launches, and telemetry remain disabled.
- `Settings.UI.UnitTests` now covers the .NET 10 build-layer expectations, README version metadata, and the no-updater/no-telemetry boundary.
- Verification used Visual Studio 18 MSBuild for Settings unit tests, Quick Access, UITestAutomation, and the runner, plus VSTest for the full Settings test assembly.

## 2026-05-08 Startup And Settings Load Optimization And 1.1.2 Release Notes

This pass moved Kit from the 1.1.1 build-alignment baseline to the 1.1.2 startup/load optimization baseline.

- Runner startup now loads general settings once in `WinMain`, applies them, and passes the same JSON object into initial module enablement.
- `start_enabled_powertoys` no longer calls `load_general_settings` internally, avoiding a duplicate settings-file read on the startup path.
- Kit startup no longer reads disabled OOBE/SCOOBE state or writes last-version state when those experiences remain inactive.
- The tray keeps the existing update-badge API but no longer reads `UpdateState.json` during initialization. Current release checking compiles the shared update-state storage in the runner so Settings can watch the same file boundary.
- Settings startup no longer eagerly constructs the OOBE shell view model.
- General Settings defers diagnostic ETW cleanup and backup dry-run refresh until after page load, and Shell page search indexing is delayed off the first frame.
- Home now filters Monitor's status-only activation rows out of the Shortcuts card. Monitor remains in the Home module list and keeps the normal Settings/Quick Access fallback, but it no longer appears beside modules that expose real shortcut actions.
- `Settings.UI.UnitTests` now covers the 1.1.2 version metadata, README changelog, development log entry, startup disk-I/O boundary, first-frame deferral, settings reuse contract, and Monitor Home Shortcuts filtering.
- Verification used Visual Studio 18 MSBuild for `Settings.UI.UnitTests.csproj` and `Kit.vcxproj`, plus VSTest for targeted startup/load tests, `BuildCompatibility`, `FrameworkPrivacyDefaults`, the Monitor Home Shortcuts regression, and the full Settings test assembly.

## 2026-04-29 Privacy, Updater, And Worktree Review

This review re-checked the trimmed Kit shell for product-service behavior that should not run in a local self-use fork:

- General Settings keeps the About group small and local-purpose: Kit version, GitHub repository, and check-only release status. Product-service surfaces are not part of the visible Kit page.
- Automatic download/install UI remains removed from General. The backing ViewModel pins update notifications, automatic downloads, and What's New after updates to disabled values; install-update and updater launch handlers are inert.
- Runner update behavior is limited to GitHub release checking and `UpdateState.json` writes. `UpdateUtils.cpp` keeps compatibility symbols, but the launch helper does not start an updater flow.
- The update toast URI handler returns an error for `update_now/`, so a stale notification payload cannot launch the updater.
- Settings telemetry source files still exist from upstream, but `settings_telemetry::init()` is not called by the runner. Do not wire it back in unless a future local-only diagnostics design replaces the upstream send path.
- ETW trace scaffolding is still present around runner lifetime. Treat it as local trace infrastructure, not as an opt-in telemetry feature; any future removal should be done separately from module compatibility work.

The same pass cleaned Git's stale worktree metadata with `git worktree prune`. Before pruning, Git reported `C:\Users\Zen\Repo\Codings\Kit\.worktrees\kit-phase1-host` as prunable because its gitdir pointed to a non-existent location. After pruning, `git worktree list --porcelain` reports only the current `C:\Users\Zen\Repo\Codes\Kit` worktree.

## Settings And Home Lessons

- Home should be fed from the same maintained active-module list as Settings and tests.
- Quick Access can safely show modules without direct quick actions if it falls back to opening the module settings page.
- Empty states must be based on visible item count, not raw item count, because disabled or GPO-hidden modules can still exist in collections.
- Settings cards that host inline progress or mixed controls should use `HorizontalContentAlignment="Stretch"` and a two-column `Grid` so the middle space is usable instead of opening a second row.
- Cross-module UI should tolerate intentionally removed modules. Light Switch can expose its PowerDisplay profile bridge, but the Settings app must remain stable when the PowerDisplay module itself is not part of the active Kit module set.
- UI text should stay English and Kit-branded. Keep `PowerToys` only where build-facing names, namespaces, assembly names, module interface DLL names, or origin attribution still require it.

## Build And Test Lessons

- Use targeted builds before whole-solution builds. Settings, Quick Access, runner, module interfaces, and module workers can fail for different reasons.
- Do not let Debug outputs prove Release packaging. Debug can keep stale `WinUI3Apps` files after earlier successful builds, while a clean Release tree exposes missing build dependencies.
- The runner can successfully start the tray while the Settings window is unavailable. `settings_window.cpp` launches `WinUI3Apps\PowerToys.Settings.exe` relative to `Kit.exe`, and Quick Access launches `WinUI3Apps\PowerToys.QuickAccess.exe` the same way. Keep `Kit.slnx` runner build dependencies on both UI executable projects so `Kit.slnx /t:Kit` regenerates the full runtime shape.
- Copied PowerToys modules often depend on CsWinRT projections generated from `PowerToys.Interop.winmd` and `PowerToys.GPOWrapper.winmd`. Clean Release builds can leave a bad intermediate state where `cswinrt.rsp` remains but the generated `.cs` projection files are gone; in that case CsWinRT may skip generation and later C# compilation reports missing `PowerToys.Interop` or `PowerToys.GPOWrapper` namespaces. Kit now invalidates that stale rsp state in `Common.Dotnet.CsWinRT.props`, and both native WinMD projects publish their WinMDs to the shared configuration output.
- Run native module-interface builds sequentially when building them independently. Shared native outputs such as `Version.pdb` and tracking logs can create false failures under unrelated parallel MSBuild invocations.
- Keep `Settings.UI.UnitTests` aligned with Kit's trimmed module set. Tests for removed PowerToys pages should not block Kit, but tests for Kit registration points should be strict.
- Clean generated `Debug`, `Release`, and `TestResults` outputs before handing the tree back for a fresh Visual Studio compile when the goal is to verify a clean build. Remove wider `bin`, `obj`, `x64`, or `AnyCPU` directories only when a full source-clean is intentionally needed.
- Use `git worktree prune` only for stale worktree metadata already reported as prunable. For live worktree directories, inspect branch and uncommitted status before removing anything.
- When the workspace grows by tens of GB, the usual cause is build output, not source. Safe cleanup targets are top-level `src\kit\x64`, `src\kit\Debug`, `src\kit\Release`, `src\kit\.vs`, root `TestResults`, and project-local `bin`, `obj`, `x64`, `Debug`, `Release`, and `TestResults` directories.
- Treat `src\kit\packages` as a restore cache. It is ignored by Git and can be deleted before uploading to GitHub, but keeping it locally speeds up rebuilds and avoids confusing cold-build errors from missing WIL, C++/WinRT, or native package imports.
- If `src\kit\packages` was deleted, run Visual Studio `Restore NuGet Packages` or a full solution build before investigating missing-header or missing-WinMD errors. Partial project builds after package cleanup can produce misleading first errors.
- Prefer Visual Studio MSBuild for projects that transitively build native `vcxproj` dependencies. `dotnet test` without a prior VS MSBuild build can fail early on missing `$(VCTargetsPath)` before any managed tests run.

## Latest Verification Notes

On 2026-04-29, the Settings stabilization pass used this verification flow:

1. Added failing regression tests for Monitor card order and Light Switch PowerDisplay integration.
2. Built `Settings.UI.UnitTests.csproj` Debug x64 with Visual Studio 18 MSBuild.
3. Ran `dotnet test Settings.UI.UnitTests.csproj -p:Platform=x64 -p:Configuration=Debug --no-build --filter "LightSwitchPowerDisplayIntegrationShouldFollowOriginalModuleContract|MonitorRunInBackgroundShouldBeImmediatelyAfterManualScan"`.
4. Confirmed the Settings UI test assembly reported 77/77 passing tests.
5. Built `PowerToys.Settings.csproj` Release x64 successfully to validate XAML/WinUI generation in the Release configuration.
6. Pruned stale Git worktree metadata and confirmed `git worktree list --porcelain` contains only `C:\Users\Zen\Repo\Codes\Kit`.

On 2026-05-05, the Monitor progress and launch stabilization pass used this verification flow:

1. Built `MonitorModuleInterface.vcxproj` Debug x64 with Visual Studio 18 MSBuild.
2. Built `PowerToys.Monitor.csproj` Debug x64 with Visual Studio 18 MSBuild.
3. Ran `dotnet test Monitor.UnitTests.csproj -c Debug -p:Platform=x64 --no-restore`, which reported 39/39 passing tests.
4. Built `Settings.UI.UnitTests.csproj` Debug x64 with Visual Studio 18 MSBuild.
5. Ran `vstest.console.exe` against the Monitor-focused Settings tests, which reported 2/2 passing tests.
6. Built full `Kit.slnx` Debug x64 successfully. The only reported warnings were existing `UITestAutomation` architecture mismatch warnings.
7. Ran the worker through `dotnet PowerToys.Monitor.dll --scan-once --organize` against a temporary Downloads directory and confirmed exit code 0, a moved test file, and a completed progress snapshot.

## Release Main Window Regression

A clean Release build exposed a runner dependency issue that Debug had masked. The symptom was: `Kit.exe` started, the tray icon appeared, but the main Settings window did not open. The reason was that the runner target could build without also building `PowerToys.Settings.exe` and `PowerToys.QuickAccess.exe`; old Debug outputs already contained those files, while the cleaned Release output did not.

The fix is to keep these projects as explicit `BuildDependency` entries under `src/runner/Kit.vcxproj` in `Kit.slnx`:

- `src/settings-ui/Settings.UI/PowerToys.Settings.csproj`
- `src/settings-ui/QuickAccess.UI/PowerToys.QuickAccess.csproj`

The regression check is:

1. Clean `Debug` and `Release` output directories.
2. Build `Kit.slnx /t:Kit /p:Configuration=Release /p:Platform=x64`.
3. Confirm `x64\Release\Kit.exe`, `x64\Release\WinUI3Apps\PowerToys.Settings.exe`, and `x64\Release\WinUI3Apps\PowerToys.QuickAccess.exe` are present.

## Source-Size Handoff Cleanup

After a stable phase, Kit can be returned to a source-size workspace for archival, GitHub upload, or manual test handoff. The cleanup passes removed tens of GB of compiler/test outputs and restore artifacts, leaving `src\kit` close to source size. `packages` is disposable because it is restored from NuGet, but it is also useful local cache during day-to-day work.

Use this cleanup shape only after confirming the project is in a usable state:

1. Remove top-level generated output under `src\kit`: `.vs`, `x64`, `Debug`, and `Release`.
2. Remove root `TestResults`.
3. Remove generated project folders under `src\kit\src` and `src\kit\tools`: `bin`, `obj`, `x64`, `Debug`, `Release`, and `TestResults`.
4. Remove `src\kit\packages` before GitHub upload or archival. Keep it for local iteration if disk space is not a concern.
5. Re-scan for the same directory names to confirm no obvious build remnants remain.

Use long-path deletion if `Settings.UI\obj` leaves behind WinRT source-generator files with very long names. Those are generated files and can be removed with a Windows long-path prefix.

When deleting build outputs after running Kit, stop any process launched from the output tree first. In practice this can include `Kit.exe`, `PowerToys.LightSwitchService.exe`, and `PowerToys.Monitor.exe` under `src\kit\x64\<Configuration>`. Otherwise Windows may deny deletion of the output directory.

When an untouched upstream source copy is available, use it as a deletion baseline before removing nested generated folders. Compare `src\kit\src` against `PowerToys-main\src` by relative path; any `src\kit\src\...\x64` directory whose matching `PowerToys-main\src\...\x64` path does not exist is a build artifact, not source. This caught the real post-build size problem: 39 nested `x64` directories under `src\kit\src`, about 32 GB total, while leaving the separate top-level `src\kit\x64` runtime output untouched when that directory is intentionally being kept.

For GitHub upload, the expected source-clean state is:

- `src\kit\packages` absent.
- top-level `.vs`, `x64`, `Debug`, `Release`, and `TestResults` absent.
- recursive `bin`, `obj`, `x64`, `Debug`, `Release`, and `TestResults` folders absent outside ignored package caches.
- `src\kit\.gitignore` still contains `**/[Pp]ackages/*`.

For the first build after this cleanup, let Visual Studio restore packages before reading the remaining errors. Missing `wil/resource.h`, missing `Microsoft.Windows.CppWinRT.props`, or missing `PowerToys.Interop.winmd` immediately after deleting `packages` usually means restore/build order, not a source regression.

## Next Stabilization Checklist

1. Surface Monitor worker failure details, cancellation, and recent scan summaries in Settings.
2. Audit Monitor settings parity between `Settings.UI.Library` and `MonitorLib` before long-running background mode is widened.
3. Decide whether the active module list should remain manually maintained or move to a single generated source of truth. Do this only after the current manual lists stay covered by tests.
4. Keep automatic update and telemetry controls removed from Kit's visible settings.
5. Keep `%LOCALAPPDATA%\Kit`, `Documents\Kit\Backup`, `HKCU\Software\Microsoft\Kit`, and Kit temporary backup names separate from official PowerToys.
6. Import a plugin host only when the host itself is the feature being stabilized. Until then, prefer PowerToys-style Kit modules.
7. For every new module, add tests before relying on Visual Studio manual validation.

## Handoff Checklist

Before handing a phase to manual Visual Studio testing:

1. Update `README.md` with the active module set, architecture notes, and known limitations.
2. Update this devdoc with any new integration lessons.
3. Run targeted Settings and module tests when build outputs are present.
4. Remove local build outputs so the next compile starts clean.
5. Decide whether to keep or remove `src\kit\packages`; keeping it speeds up the next build, removing it gives the smallest handoff.
6. Avoid staging unrelated upstream PowerToys or generated-output churn.
