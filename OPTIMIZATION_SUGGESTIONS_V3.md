# Kit Optimization Suggestions — V3

After the second codex pass. The "shrink and stabilize" arc is essentially done; this round shifts to **what to build next** rather than what to remove. Earlier shrinking advice has been retired or moved to Appendix A.

## 0. State of the Tree (1.0.3)

| Snapshot | x64 active dir | Notes |
|---|---|---|
| V1 baseline (`1.0.1`) | 1003 MB | starting point |
| V2 (`1.0.2 beta1`) | 794 MB | locale + PDB + LanguageModelProvider trim |
| **V3 (`1.0.3`)** | **573 MB** | + static-lib pruning, Common.UI WPF/WinForms off, source-tree purge |
| Total reduction | **−43 %** | from baseline |

Verified by build output: 0 `.lib` files, 0 `.pdb` files in `x64/1.0.3/`. `Common.UI.csproj` now `<UseWPF>false</UseWPF>` / `<UseWindowsForms>false</UseWindowsForms>`. All 109 `<Compile/Page/None Remove>` rules in `PowerToys.Settings.csproj` were resolved by deleting the underlying source files (only 15 generic asset/content removes remain, all upstream-merge protection).

`BuildCompatibility.cs` now has 23 regression tests, including the strongest one — `KitSettingsShouldDeleteInactiveModuleSourceFilesInsteadOfExcludingThem` — which physically asserts that 24 inactive module file prefixes are gone from disk, not just hidden from the build.

The trajectory is healthy. Codebase is now positioned for capability work, not janitorial work.

## 1. What V2 Items Codex Applied

| V2 item | Status | Where | New test |
|---|---|---|---|
| N1 Static-lib pruning (`.lib`/`.exp`/`.lib.lastcodeanalysissucceeded` post-build delete) | ✅ | [Directory.Build.targets](src/kit/Directory.Build.targets) — `KitRemoveStaticLibArtifactsFromRuntimeOutput` | `ReleaseBuildShouldKeepSlimPublishDefaults` updated |
| N1+ AI model-provider artifact pruning | ✅ (added beyond V2) | same — `KitRemoveInactiveModelProviderArtifactsFromRuntimeOutput` deletes `Models/*.svg` and `*Foundry*` | same |
| V1 A.7 Drop WPF/WinForms in Common.UI | ✅ | [Common.UI.csproj:7-8](src/kit/src/common/Common.UI/Common.UI.csproj#L7-L8) | `ReleaseBuildShouldKeepSlimPublishDefaults` |
| N2 Stale version dir cleanup | ✅ (script form) | `tools/build/clean-stale-versions.ps1` + `tools/build/verify-runtime-artifacts.ps1` (both with `-WhatIf`) | `KitBuildToolsShouldSupportExplicitOutputCleanupAndArtifactChecks` |
| N3 Delete physical `<Compile Remove>` files | ✅ | `PowerToys.Settings.csproj` no longer contains `<Compile Remove>` rules | `KitSettingsShouldDeleteInactiveModuleSourceFilesInsteadOfExcludingThem` (24 module prefixes) |
| B.12/B.13 Trim slnx | ✅ | `CalculatorEngineCommon`, `FilePreviewCommon`, `PowerToys.ModuleContracts`, `UITestAutomation`, `Awake.ModuleServices`, `dsc/` no longer in `Kit.slnx` | `KitSolutionShouldNotDirectlyBuildInactiveCommonAndDscProjects` |
| Quick Access fallback to Settings | ✅ (new beyond V2) | `LauncherViewModel.OpenModuleSettings` + `IQuickAccessCoordinator.OpenModuleSettings` + `SettingsDeepLink.SettingsWindow.Monitor` | `KitQuickAccessFlyoutShouldOpenSettingsForModulesWithoutDirectActions` |

## 2. What V2 Items Were Skipped (and why they're now low priority)

- **B.10 Monitor framework-dependent** — Monitor still imports `Common.SelfContained.props`. The `WinUI3Apps/` dir is 282 MB and contains a full self-contained Settings runtime, so Monitor's marginal contribution is small (~5–10 MB max). Skip until Settings itself moves to framework-dependent, which is a much bigger lift.
- **B.14 Rename ETW provider GUID** — Cosmetic. The `Microsoft.PowerToys` provider still registers but the runner's `Trace::EventLaunch` is empty. Leave for a coordinated "Kit telemetry" pass if/when local-only diagnostics are introduced.
- **WindowsAppSDK Onnx/AI subset** — README's "Recent Release Build Regression" already explains this is deferred: the AI runtime files come from the `Microsoft.WindowsAppSDK` meta-package. Splitting into granular package references is risky and has unbounded validation cost. Don't touch until/unless WinAppSDK upgrades give a cleaner split.

## 3. The Strategic Question

Three credible directions for Kit's next phase. Each is internally consistent; pick one as the primary focus, do the other two opportunistically.

### Direction A — **Daily-Use Polish** (most pragmatic)

Treat Kit as the user's actual every-day driver. Make the three active modules feel finished. Modest scope, high signal.

### Direction B — **Module Framework** (most leveraged)

Promote Monitor's pattern (lib + worker + interface + settings + tests) into a `Kit.ModuleSdk` so adding modules four, five, and six is a 30-minute exercise instead of a 2-day archaeology dig.

### Direction C — **Selective PowerToys Imports** (most visible)

Pull 1–2 more upstream modules into Kit. Validates the compatibility model and hits user-visible wins quickly.

Recommendation: **A first, then B, then C.** Polish keeps the active surface honest; the SDK work crystallizes patterns the polish revealed; imports come last because they're easiest once the SDK exists.

## 4. Direction A — Daily-Use Polish

Prioritized concrete work. Each item is 1–3 days of work, has a clear acceptance test, and unblocks the next.

### A1. **Monitor: real worker progress** (highest user signal) ⭐

The README explicitly flags this: *"replacing it with worker-reported progress is a next-phase stabilization item"*. Today the Settings UI advances a timer; the worker scan returns no progress signal. For a folder with 20k+ files this is invisible.

**Design** (uses what's already in place):
- Worker writes progress JSON to `%LOCALAPPDATA%\Kit\Monitor\scan-progress.json` every N seconds during scan: `{ "phase": "hashing|categorizing|writing", "filesProcessed": X, "filesTotal": Y, "currentDirectory": "...", "startedAt": "..." }`. Atomic write via temp+rename.
- Settings page polls this file with a 500 ms timer while a scan is active (active = `scanNow` was just sent and no completion event yet).
- Add a `monitor_scan_completed` named event (sibling to `MONITOR_EXIT_EVENT` in `shared_constants.h`). Settings stops polling when signaled.
- Regression test: `MonitorScanProgressFileShouldUpdateAtomicallyDuringScan` reads the temp+rename pattern, asserts that `scan-progress.json` is never partially written.

**Why a file, not a pipe**: The Monitor worker exits between one-shot scans. A pipe would have to be re-established every scan. A file is observable from any process, survives the worker dying, and is naturally idempotent.

### A2. **Monitor: real installed-software discovery**

README: *"Registry-backed real installed-software discovery and richer UI actions are future refinements."*. `CleanInstallers` today defaults to `false` because it would be too aggressive without knowing which installers correspond to currently-installed software.

**Design**:
- New `InstalledSoftwareIndex` class in `MonitorLib`: enumerates `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall`, `HKLM\Software\WOW6432Node\...`, `HKCU\...\Uninstall`. Returns a set of `(displayName, displayVersion, installDate, installLocation)`.
- Match against scanned installers (`*.msi`, `*.exe` in Downloads) by name + size + date heuristics. A match means "this Downloads file is the installer for software actually present".
- Settings adds an `Auto-clean installers older than N days only when matched to installed software` toggle. Default N = 60.
- This is the missing safety rail that lets `CleanInstallers` default to `true`.
- Tests: `InstalledSoftwareIndexShouldReadHKLMUninstall` (mock registry), `InstallerMatcherShouldRequireBothNameAndVersionMatch`.

### A3. **LightSwitch: native PowerDisplay degradation**

Today PowerDisplay profile names are read from `%LOCALAPPDATA%\Kit\PowerDisplay\profiles.json` if it happens to exist. Nobody writes that file in Kit because PowerDisplay isn't an active module. So the dropdown is always empty unless the user manually places a file.

**Design choice** (pick one):
- A3a. Hide the "Apply monitor settings to" group entirely when `Enabled.PowerDisplay = false` (cleaner). The current "lightweight tolerant" parser stays but isn't exercised.
- A3b. Replace it with a **monitor-name-only** picker that uses native `EnumDisplayDevices` + `DisplayConfigGetDeviceInfo`, no PowerDisplay dependency. Useful for "apply this theme switch only to monitor X" without importing PowerDisplay.

A3a is 1 day. A3b is 4–5 days but ships a feature. Recommend A3a now, A3b later as part of A4 if needed.

### A4. **LightSwitch: schedule preview & next-fire indicator**

Currently `LightSwitch` runs `LightSwitchService.exe` and switches at sunrise/sunset or fixed times, but the Settings page doesn't show *when the next switch will happen*. For a daily-use module that's the single most useful piece of feedback.

**Design**:
- Service computes next fire time on each settings change; writes to `%LOCALAPPDATA%\Kit\LightSwitch\next-fire.json` (atomic). Same shape as A1's progress file.
- Settings page surfaces "Next switch: light → dark at 17:42 today" as a read-only `InfoBar`/`SettingsCard` subtitle.
- Test: `LightSwitchServiceShouldPersistNextFireTime`.

This is also a stepping stone for A5 (Quick Access activation badge).

### A5. **Quick Access: activation items for LightSwitch + Monitor**

Today only Awake contributes a `DashboardModuleActivationItem` to the Home shortcuts card showing current Awake mode. Light Switch and Monitor land in the launcher tile list but don't surface their state.

**Design**:
- LightSwitch activation item: shows current mode (`Light` / `Dark`) + next-fire countdown from A4. Tap toggles via the existing `LIGHTSWITCH_TOGGLE_EVENT`.
- Monitor activation item: shows last scan summary — `42 new files, 3 categorized` plus a "Scan now" button that fires `scanNow` custom action. Read-only otherwise.
- Both follow Awake's existing `IDashboardActivationItem` pattern. No new contract.

### A6. **`Kit.exe --status` / `--list-modules` CLI** (for the README's "side-by-side with installed PowerToys" workflow)

Trivial but useful. Adds two flags to the runner:
- `--list-modules` — prints `KitKnownModules` and which DLLs successfully loaded with PIDs of started workers.
- `--status` — prints version, settings dir (`%LOCALAPPDATA%\Kit\settings.json`), scheduler folder, mutex name, pipe names. One-shot, exits.

Lets the user verify "Kit and PowerToys are both running and not colliding" without opening Settings or a debugger. ~50 lines in `main.cpp`.

### A7. **Documentation: a `doc/devdoc/kit-module-checklist.md`** (one-pager)

The "Adding Another PowerToys Module" section in README is buried under nine other sections. Promote the checklist (currently 9 numbered steps) into a standalone one-pager and link to it from README. Add explicit file-touch counts so a contributor knows the surface area before starting.

## 5. Direction B — Module Framework

Once two of A1–A5 ship, the duplication becomes obvious enough to refactor.

### B1. **Extract `Kit.ModuleSdk` (managed)**

A new `src/common/Kit.ModuleSdk/` library with:

- `IKitModule` — `string Key`, `bool IsEnabled`, `void Enable/Disable`, `void SendCustomAction(string)`, `void RegisterScanProgressCallback(...)`.
- `IKitModuleProgressReporter` (the file-based shape used in A1/A4 generalized).
- `KitNamedExitEvent` — typed wrapper around the `KIT_*_EXIT_EVENT` pattern in `shared_constants.h`.
- `KitModuleSettingsRoot<T>` — typed `%LOCALAPPDATA%\Kit\<Module>\` accessor for settings, progress, and log files. Replaces the ad-hoc string concatenation in MonitorSettings, LightSwitch profile loader, etc.

Migrate Monitor first (already the cleanest). LightSwitch second. Awake last (it's the upstream copy; touch lightly).

### B2. **Extract `Kit.ModuleInterface` (native)**

The three native interface DLLs (`AwakeModuleInterface.vcxproj`, `LightSwitchModuleInterface.vcxproj`, `MonitorModuleInterface.vcxproj`) duplicate enable/disable/worker-launch/exit-event logic. Build a static lib `src/common/Kit.ModuleInterface/` exposing:

```cpp
class KitModuleBase : public PowertoyModuleIface {
    // implements common ABI
    virtual std::wstring_view worker_exe_name() const = 0;
    virtual std::wstring_view exit_event_name() const = 0;
    virtual void on_enable_kit_module() {}     // overridable
    virtual void on_custom_action(const wchar_t*) {}
};
```

Each module's `dllmain.cpp` becomes ~50 lines (key, name, worker, custom-action handler). This is the prerequisite for adding modules four+ without C++ archaeology.

Acceptance: `MonitorModuleInterface.dll`, `LightSwitchModuleInterface.dll`, `AwakeModuleInterface.dll` all link against `Kit.ModuleInterface.lib`. Each `dllmain.cpp` < 80 lines.

### B3. **`Kit.Modules.json` single-source manifest**

Today the active module list lives in five places:
1. `runner/main.cpp` `KitKnownModules`
2. `Kit.slnx` `BuildDependency`
3. `Settings.UI` Shell navigation dictionary
4. `Settings.UI.Controls` `KitDashboardModules`
5. `BuildCompatibility.cs` static assertions

Each new module = five edits. Codex's regression tests catch drift, but the friction is real.

**Proposal**: a top-level `src/kit/KitModules.json` or `KitModules.props` file:
```json
{
  "modules": [
    { "key": "Awake", "interfaceDll": "PowerToys.AwakeModuleInterface.dll", "settingsRoute": "Awake", "homeIcon": "Awake.png" },
    { "key": "LightSwitch", "interfaceDll": "PowerToys.LightSwitchModuleInterface.dll", ... },
    { "key": "Monitor", "interfaceDll": "PowerToys.MonitorModuleInterface.dll", ... }
  ]
}
```

Drives the C++ list via a generated header (CMake/MSBuild item-list expansion), the C# list via a source generator, and the slnx `BuildDependency` via a generated `.props`. `BuildCompatibility.cs` reads the same JSON.

This is the single highest-leverage change in B. Do it after B1/B2 stabilize so the JSON shape isn't a guess.

### B4. **`tools/build/new-module.ps1`**

Once B1+B2+B3 land, scaffolding a new module is mechanical: create `src/modules/<Name>/{Lib,Worker,Interface,Tests}/`, append to `KitModules.json`, run `dotnet new` for the lib + worker, copy interface boilerplate, regenerate the C++ header and C# manifest. Should be < 5 min from `.ps1 invoke` to "appears disabled in Settings UI".

## 6. Direction C — Selective PowerToys Imports

Scoped suggestions, ordered by import effort vs daily-use ROI for a self-use fork.

### C1. **`PowerRename`** (good first PowerToys-import target)

- Pulls in `PowerRenameLib` + `PowerRenameContextMenu` + a small UI (`PowerRenameUILib`).
- No WinAppSDK dependency for the core; UI is WPF/XAML island.
- Lives entirely in user space (right-click context menu integration via shell ext).
- Already has good unit tests upstream.
- Risk: shell extension registration interaction with installed PowerToys (if both register, OS picks one — ensure Kit's CLSID is distinct).

After Monitor, this is the cleanest "import + register + test" exercise. Validates B2 if the SDK extraction is done first.

### C2. **`Hosts` (Hosts file editor)**

- Self-contained WinUI3 app (`Hosts.exe`) + module interface DLL.
- No upstream service / no shell ext.
- Edits a system file (requires elevation), so it's a good test of Kit's UAC / elevation handling — currently underexercised.
- Small surface area, ~15 source files in upstream.

### C3. **`AlwaysOnTop`**

- Native C++ module, no managed UI page (all in Settings).
- Single hotkey + window pinning; very short import.
- Tests Kit's `centralized_kb_hook` integration.

Skip these for now (more invasive):
- **Workspaces** — depends on `Workspaces.Editor.exe`, `Workspaces.Snapshot.exe`, and a UI app. Adds ~40 MB of WinAppSDK surface.
- **PowerLauncher / Run** — large plugin host, brings PluginLoader + ~30 plugins. README explicitly says "import the Run host first" — not yet justified.
- **AdvancedPaste** — needs LanguageModelProvider (which Kit already explicitly removed).
- **CmdPal** — its own monorepo subtree; would be its own fork.
- **MouseWithoutBorders** — requires 2 physical machines per AGENTS.md, can't be tested.

### C4. **Validation policy for imports**

Define and enforce in `BuildCompatibility.cs`:
1. New module DLL must be in `KitKnownModules` and `Kit.slnx` `BuildDependency`.
2. New module's CLSIDs (shell ext, COM) must be distinct from upstream PowerToys (compile-time GUID check).
3. New module's `%LOCALAPPDATA%` directory must be `%LOCALAPPDATA%\Kit\<Module>\`, never `%LOCALAPPDATA%\Microsoft\PowerToys\<Module>\`.
4. New module's settings JSON must round-trip through `MonitorSettings`/`AwakeSettings`-style serialization context (no naked `JsonSerializer.Serialize`).

## 7. Cross-Cutting Improvements (do alongside any direction)

### X1. **Backup/restore for module-specific data**

Kit already has `Documents\Kit\Backup` for general settings. Extend to module data: Monitor's `results.csv`, LightSwitch's profiles, Awake's session state. Each module declares its data files via the SDK (B1) and the existing backup pipeline picks them up.

### X2. **Crash dumps without telemetry**

`%LOCALAPPDATA%\Kit\Logs\` already exists. Add a minidump writer for unhandled exceptions in workers and runner. Local-only — no upload. README's "Privacy, Updater, And Worktree Review" section is explicit that telemetry stays inert; this is the local-diagnostics complement.

### X3. **Settings JSON schema validation on load**

Today malformed `settings.json` silently falls back to defaults (with warnings in logs). Add a validation pass that, on settings load, validates against an embedded JSON schema and surfaces errors in Settings → General → "Settings status" with a "Reset to defaults" button. Avoids the silent-data-loss class of bug.

### X4. **CI: a tiny GitHub Actions workflow** (only if Kit goes public)

Just `Kit.slnx /t:Build,Test /p:Configuration=Release /p:Platform=x64` against a Windows-2022 runner. Catches the `BuildCompatibility.cs` regressions on every push. ~30 lines of YAML. README already notes "Verification Snapshot" cadence — automating it is a 1-day exercise.

### X5. **Verify-runtime-artifacts as a build gate**

`tools/build/verify-runtime-artifacts.ps1` already exists (V2 success). Wire it into `Directory.Build.targets` as a post-build step on Release for the runner project. If `.lib` / `.pdb` / non-en locale dirs reappear, the build fails. Belt+suspenders next to the existing post-build delete targets.

## 8. Recommended Roadmap

Quarter-style (each item 1–3 weeks in calendar terms for a part-time self-use fork):

**Now → Next 4 weeks** (Direction A polish)
1. A1 Monitor real progress
2. A4 LightSwitch next-fire indicator
3. A6 `Kit.exe --status` CLI
4. A7 Module checklist doc
5. X5 Verify-runtime-artifacts as build gate

**Next 4–8 weeks** (Direction A feature finish)
6. A2 InstalledSoftwareIndex + safer CleanInstallers default
7. A3 PowerDisplay group hide-when-disabled (A3a)
8. A5 LightSwitch + Monitor activation items in Quick Access
9. X1 Module-data backup/restore extension

**Next 8–16 weeks** (Direction B SDK)
10. B1 Kit.ModuleSdk (managed)
11. B2 Kit.ModuleInterface (native)
12. B3 KitModules.json single-source manifest
13. B4 `new-module.ps1` scaffold

**Optional, after B lands** (Direction C imports)
14. C1 PowerRename
15. C2 Hosts (or skip if A2 makes it redundant for daily use)

## 9. What Not To Do

- Don't import PowerToys Run / PowerLauncher before B is done. Plugin host complexity will swamp the SDK extraction.
- Don't add telemetry, even "local-only with opt-in upload". README's privacy direction is explicit and codex's regression tests pin it. If diagnostics need persistence, write them as files under `%LOCALAPPDATA%\Kit\Logs\` (X2).
- Don't undo the Quick Access fallback (`OpenModuleSettings`). It's the cleanest way to give modules-without-direct-actions a working tile and was just stabilized.
- Don't expand `%LOCALAPPDATA%\Kit\` to multiple roots. Keep the prefix consistent — the V2 work that pinned `KIT_MSI_MUTEX_NAME`, scheduler `\Kit`, pipe `kit_*`, event `Kit*` is what makes side-by-side with installed PowerToys safe. Extending it requires the same name-collision audit.
- Don't re-add module pages by `<Page Include>` to inactive modules. The codex deletion test specifically forbids ViewModels with prefixes like `AdvancedPaste*`, `FancyZones*`, etc. — re-introducing requires updating that test's exclusion list, which is the friction that prevents drift.
- Don't keep `x64/1.0.1` and `x64/1.0.2 beta1` after `1.0.3` is the active version. Run `tools/build/clean-stale-versions.ps1` before tagging.

## 10. Tracking

Each numbered item in §4–§7 is sized for a single PR with a `BuildCompatibility.cs` regression test. The current 23 tests should grow to ~35 after Direction A. Watch for the test class hitting ~50 — at that point split it into per-area files (`BuildCompatibility.Monitor.cs`, `BuildCompatibility.Branding.cs`, etc.) before it becomes a god-class.

---

## Appendix A — Retired Recommendations

These appeared in V1/V2 and have been superseded.

| Item | Status | Note |
|---|---|---|
| V1 A.1–A.7 | Applied or evolved | All slimming work done |
| V1 A.6 Delete `ai_detection.h` | **Reversed** | Codex's gated-test approach is correct; do not delete |
| V2 N1 static-lib pruning | Applied | post-build delete in Directory.Build.targets |
| V2 N2 stale version dirs | Applied | manual script with `-WhatIf` |
| V2 N3 source-tree purge | Applied | physical files deleted, regression test enforced |
| V2 B.10 Monitor framework-dependent | **Skip** | marginal saving while Settings stays self-contained |
| V2 B.14 ETW provider rename | **Defer** | cosmetic; no telemetry runs |
| V1 A.8 Trimming `<TrimmerRootAssembly>` | **Defer** | acceptable risk only after a CI gate exists |
