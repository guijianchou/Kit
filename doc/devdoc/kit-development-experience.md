# Kit Development Experience

This note captures the first-phase lessons from turning the PowerToys-derived Kit shell into a stable local workspace and adding Monitor as the first Kit-authored module.

## 2026-06-16 Version 2.0.3 Monitor And Framework Review

This pass moved Kit to `2.0.3` after rechecking the Kit runner, Settings, common build/package, sparse package identity, and policy surfaces against the local PowerToys-main framework baseline.

- Version.props, README, README_zh, changelog, this development log, the sparse package manifest, GPO support markers, and version metadata tests now use Kit version `2.0.3`.
- The PowerToys-main framework comparison did not require pulling back deleted inactive module surfaces; Kit's central package and build target differences remain intentional local-workspace boundaries.
- Monitor manual scan progress now completes from the worker completion event even when the progress JSON cannot be read.
- Monitor background scans no longer signal the manual scan completion event, so Settings cannot mistake background completion for the current manual scan.
- Monitor installer cleanup now selects the highest-confidence installed-software match for each installer after gathering candidates.
- Monitor VCP capability comparison now includes value metadata so color preset cache changes refresh Settings correctly.

## 2026-06-13 Version 2.0.2 PowerDisplay Removal And Upstream Module Sync

This pass moved Kit to `2.0.2` after syncing the copied Awake and LightSwitch modules with the local PowerToys-main reference while preserving Kit's local-only runtime boundaries.

- Version.props, README, README_zh, changelog, the sparse package manifest, GPO support markers, and version metadata tests now use Kit version `2.0.2`.
- The active Kit module set is now `Awake`, `Light Switch`, and `Monitor`; PowerDisplay was removed from runner loading, solution entries, Settings navigation, Quick Access routing, GPO projection, Settings serialization, resources, assets, docs, and module source.
- LightSwitch no longer keeps the deleted PowerDisplay profile bridge, Force Light/Force Dark custom-action plumbing, or PowerToys-named runtime events.
- LightSwitch now uses Kit-named toggle, manual-override, and service-stop events, stops its scheduler service when the schedule changes to `Off`, and keeps toggle-hotkey handling independent of the scheduler process.
- Awake and LightSwitch keep no-op trace compatibility hooks without active PowerToys telemetry providers, writes, telemetry include paths, or `EtwTrace` project references.

## 2026-05-28 Version 2.0.1 Stability Refactor

This pass finalized Kit's docs, tests, and Settings code against the local PowerToys-main reference and moved Kit to `2.0.1`.

- Version.props, README, README_zh, changelog, this development log, and the version metadata regression test now use Kit version `2.0.1`.
- Quick Access and Settings serialization now reference only the active Kit module set: `Awake`, `Light Switch`, `Monitor`, and `PowerDisplay`.
- Managed Settings and Quick Access no longer keep telemetry send paths, telemetry event source files, or `ManagedTelemetry` project references.
- The inactive `ManagedTelemetry` source tree and managed telemetry base file were deleted after confirming no active project still references them.
- Active outputs now remove stale `PowerToys.ManagedTelemetry` and TraceEvent support binaries left by old build graphs.
- Awake and PowerDisplay no longer keep managed telemetry write calls or module-local telemetry event source classes.
- Awake and PowerDisplay native module interfaces now keep only no-op trace compatibility hooks, matching Light Switch and avoiding active-module TraceLogging providers or writes.
- LightSwitchService, MonitorModuleInterface, and ModuleTemplate trace sources now keep no-op runtime hooks without telemetry include paths or `EtwTrace` project references.
- All active native module-interface trace headers and projects no longer inherit `TraceBase` or keep telemetry include paths.
- PowerDisplay no longer exposes or listens to a settings telemetry IPC event; this matches Kit's removed runner settings telemetry worker instead of carrying an event with no active consumer.
- `PowerToys.Interop` now exposes only Kit's active runtime constants through the WinRT `Constants` projection and shared native constants header. Inactive PowerToys Run, FancyZones, Advanced Paste, CmdPal, Keyboard Manager, Mouse utilities, preview, Hosts, Workspaces, and telemetry event names were deleted rather than kept as unused compatibility surface.
- The active Settings termination WinRT projection is now `KitRunnerTerminateSettingsEvent`, matching the underlying Kit-named event instead of keeping a PowerToys-named method on the active IPC path.
- The inactive AdvancedPaste-only `LanguageModelProvider` source tree was deleted instead of only being removed from build graphs. Its AI provider package pins, provider UI metadata/helpers, non-serialized AI enum helpers, stale Foundry Local UI string, and stale `OpenAI` third-party notice entry were removed with it, while historical settings serialization models remain for old JSON compatibility.
- The Shortcut Conflict window no longer keeps inactive AdvancedPaste, Mouse Without Borders, Peek, or PowerToys Run settings special cases. It now relies on the generic SettingsFactory path for active modules and no longer carries the inactive PowerToys Run `HotkeyChanged` workaround.
- `SettingsFactory` now resolves only Quick Access, LightSwitch, and PowerDisplay hotkey settings through explicit repository loaders. It no longer scans the Settings.UI.Library assembly for every historical `IHotkeyConfig` or exposes unused broad factory APIs.
- `PageViewModelBase` no longer carries the inactive MouseUtils conflict special branch. Active Settings pages now use only the generic module-name conflict matching path.
- Settings package-reference comments now describe current dependency alignment for the active Settings runtime closure instead of citing inactive CmdPal, Mouse Without Borders, or Advanced Paste module hacks.
- The unused Registry Preview-only `SkiaSharp.Views.WinUI` central package pin and stale third-party notice entry were removed after confirming no Kit project references it.
- The unused Command Palette extension central package pin was removed after confirming no Kit project references `Microsoft.CommandPalette.Extensions`.
- The unused Command Palette Adaptive Cards central package pins were removed, along with stale AdaptiveCards third-party notice entries, after confirming no Kit project references `AdaptiveCards.ObjectModel.WinUI3`, `AdaptiveCards.Rendering.WinUI3`, `AdaptiveCards.Templating`, or `Microsoft.Bot.AdaptiveExpressions.Core`; in the local PowerToys-main reference these pins feed the deleted CmdPal Adaptive Cards form view models.
- The unused Command Palette WinGet interop central package pin was removed after confirming no Kit project references `Microsoft.WindowsPackageManager.ComInterop`; in the local PowerToys-main reference that package feeds the deleted CmdPal WinGet extension.
- The unused AdvancedPaste Markdown conversion central package pins were removed, along with the stale ReverseMarkdown third-party notice entry, after confirming no Kit project references `HtmlAgilityPack` or `ReverseMarkdown`; in the local PowerToys-main reference these packages feed the deleted AdvancedPaste Markdown helper.
- The unused PowerToys Run central package pins were removed, along with the stale PowerToys Run Mages third-party notice section, after confirming no Kit project references `hyjiacan.pinyin4net`, `Mages`, or `UnitsNet`; in the local PowerToys-main reference these packages feed the deleted PowerToys Run pinyin search, Calculator, History, and Unit Converter paths.
- The stale PowerToys Run Wox/Window Walker and Registry Preview HexBox utility notice sections were removed after confirming the local PowerToys-main reference uses them only for deleted Launcher/CmdPal and Registry Preview paths.
- The unused PreviewPane STL and PowerAccent central package pins were removed after confirming no Kit project references `HelixToolkit`, `HelixToolkit.Core.Wpf`, or `UnicodeInformation`; in the local PowerToys-main reference these packages feed the deleted STL thumbnail provider and PowerAccent Core paths.
- The unused Command Palette toolkit and host central package pins were removed, along with the stale ToolGood.Words.Pinyin third-party notice section, after confirming no Kit project references `Shmuelie.WinRTServer` or `ToolGood.Words.Pinyin`; in the local PowerToys-main reference these packages feed the deleted CmdPal extension host/template and pinyin fuzzy matcher paths.
- The deleted-module-only DSC, Workspaces/FancyZones, Peek, and PowerToys Run OneNote central package pins were removed after confirming no Kit project references `ModernWpfUI`, `NJsonSchema`, `ScipBe.Common.Office.OneNote`, or `SharpCompress`; in the local PowerToys-main reference these packages feed deleted DSC schema, Workspaces/FancyZones editor, Peek archive preview, and PowerToys Run OneNote paths.
- The deleted-utility central package pins for Hosts, Registry Preview, PowerToys Run, PowerAccent, and RTF conversion paths were removed after confirming no Kit project references `CommunityToolkit.WinUI.Collections`, `CommunityToolkit.WinUI.UI.Controls.DataGrid`, `ControlzEx`, `Interop.Microsoft.Office.Interop.OneNote`, `LazyCache`, `Microsoft.Toolkit.Uwp.Notifications`, `RtfPipe`, or `WPF-UI`; in the local PowerToys-main reference these packages feed deleted Hosts UI collections, Registry Preview grids, PowerToys Run notifications/OneNote caching, PowerAccent UI, and RTF conversion paths.
- The deleted Launcher, AI, and CmdPal central package pins were removed, along with the stale CmdPal WyHash third-party notice section, after confirming no Kit project references `Microsoft.Data.Sqlite`, `Microsoft.Graphics.Win2D`, `Microsoft.WindowsAppSDK.AI`, `NLog`, `NLog.Extensions.Logging`, `NLog.Schema`, `System.ClientModel`, `System.Numerics.Tensors`, or `WyHash`; the local PowerToys-main snapshot still keeps these as upstream central pins for deleted Launcher, AdvancedPaste/OpenAI, ImageResizer AI, and CmdPal paths.
- The inactive CmdPal Calculator and File Explorer/Peek shared assets were deleted after confirming `PowerToys-main` only uses `CalculatorEngineCommon`, `FilePreviewCommon`, Monaco assets, `modulesRegistry.h`, and shell-extension registration helpers from deleted CmdPal, PreviewPane, Peek, installer, or Registry Preview paths. This also removed the FilePreviewCommon-only `UTF.Unknown` package pin, stale Command Palette/File Explorer/Peek NOTICE sections, File Explorer add-in logger constants, the unused shell-extension registry generator, and Awake's stale launcher logger name.
- Inactive Settings models, source files, unit tests, assets, icons, controls, converters, OOBE view models, and the legacy sibling Settings asset tree were deleted instead of being kept behind project exclusions.
- GPOWrapper and module GPO helpers now expose only active modules that currently have policy rules plus the retained startup, update, and diagnostics rules; Monitor is active but intentionally remains policy-unavailable until a real Monitor policy is added. Inactive module and installer/update-toast policy readers were deleted from runtime and tests. ADMX/ADML policy assets now match the same Kit 2.0.1 policy surface.
- The upstream BugReportTool source and launch paths were deleted from `tools`, runner tray/menu code, General, and Quick Access because the tool collects inactive PowerToys module state.
- The inactive standalone module_loader utility and orphaned CmdPal version props were deleted because Command Palette is not part of the active Kit module set.
- The Quick Access window now uses the current WinUI SystemBackdrop API instead of the deprecated WinUIEx backdrop attached property, so its Debug build is warning-free again.
- The Quick Access window title is now `Kit Quick Access` instead of the upstream `PowerToys Quick Access (Preview)` label.
- Shared module-interface and Settings dispatch comments no longer describe inactive AdvancedPaste or PowerToys Run special cases as current runtime behavior; the retained compatibility fields stay in place.
- The stale Color Picker, ImageResizer, and PowerRename NOTICE sections were removed after confirming those deleted utility sources are not used by Kit's active module set.
- PowerDisplay, Light Switch, and runner comments/logs now describe Kit runner, in-process hotkeys, and trace hooks directly instead of citing inactive CmdPal, PowerToys Runner, or telemetry behavior as current.
- `src/PackageIdentity` remains a live solution dependency, but its sparse package manifest now declares only the retained Settings identity entry instead of deleted PowerOCR, ImageResizer, or Command Palette app identities.
- The checked-in sparse package manifest version now matches Kit `2.0.1`, local signing examples use Kit paths, and the standalone signing helpers no longer default to deleted CmdPal package paths or report success when no package was signed.
- Standalone signing now resolves `signtool` from PATH or the Windows SDK, treats current-user certificate trust as the normal development path, requires explicit machine-root trust only when requested, and signs the sparse package by default unless explicit targets or all packages are requested.
- `tools/build/build-essentials.ps1` now builds Quick Access along with the runner and Settings so the fast local build path regenerates both UI executables that `Kit.exe` can launch.
- Local build helpers now keep MSBuild extra arguments as arrays, cache the resolved MSBuild executable after Visual Studio environment import, normalize duplicate `PATH`/`Path` values from `VsDevCmd.bat`, and default local builds to skip CopyOnWrite/RunVSTest SDK resolver imports when package source mapping would otherwise block restore.
- `UITestAutomation` keeps the shared Light Switch UI-test infrastructure but no longer carries inactive FancyZones, Hosts, Workspaces, PowerRename, Command Palette, or Screen Ruler launch targets and cleanup branches. The harness now resolves Kit install roots, launches `Kit.exe`, attaches to Kit Settings, and knows the four active module executables.
- PowerDisplay runner IPC launches now parse the runner PID and pipe name before AppInstance registration, bypass standalone single-instance redirection for runner-owned IPC processes, keep normal single-instance behavior for user launches, and route Settings deep links through `Kit.exe`.
- `ModuleHelper` now exposes enabled-state, icon, and label behavior only for the active Kit modules plus General settings, while preserving historical module-key mappings for old settings JSON and IPC compatibility. Historical settings DTOs stay in place without advertising deleted modules to active Settings and Quick Access callers.
- `doc/devdoc/kit-first-plugin.md` now names the full four-module active set, including `PowerDisplay`.
- The XAML search index builder now derives icons from active Settings XAML only and no longer carries inactive upstream icon overrides or Mouse Jump panel fallbacks.
- The runner keyboard hook no longer carries Shortcut Guide Win-key tracking, and the shared module interface no longer exposes the legacy Win-key tracking methods for inactive Shortcut Guide behavior.
- The keyboard hook window registration no-op was deleted after pressed-key timers were removed; tray startup now registers only the centralized hotkey window path that still consumes the runner HWND.
- The inactive settings telemetry worker source files were deleted along with their runner project filter entries; the privacy regression test now checks the source tree and filters, not only the build project.
- The inactive Settings UI test project was deleted because it was no longer included by `Kit.slnx` and still automated removed OOBE and PowerToys module surfaces.
- The inactive DSC source tree and manifest generation script were deleted after confirming DSC projects are no longer included by `Kit.slnx`.
- The DSC-only Settings `setAdditional` command-line entry point was deleted after DSC generation was removed; Settings now keeps only the retained `set` and `get` command paths.
- Inactive Settings UI resource strings for removed module pages and OOBE surfaces were deleted from the English resource file, with regression coverage for stale resource prefixes.
- Disabled OOBE/SCOOBE launch flag plumbing was removed from runner startup, the Settings launcher, and the Settings command-line entry point after the corresponding windows and resources were deleted.
- Unused OOBE/SCOOBE SettingsAPI state helpers, backup rules, residual resources, and XAML styles were removed so startup and backup no longer carry state for deleted windows.
- Backup/restore defaults were pruned to generic active Kit settings rules by deleting inactive Keyboard Manager, FancyZones, Workspaces, PowerToys Run restore entries, and the PowerToys Run plugin fix-up branch.
- Settings and Quick Access now clean stale inactive Settings payloads from the shared WinUI output; Quick Access copies only active Settings icons.
- Regression coverage now guards the four-module Quick Access boundary, deleted inactive Settings surfaces, GPO policy trimming, ADMX/ADML policy assets, BugReportTool removal, stale output cleanup, telemetry-free managed app projects, active managed modules without telemetry sends, deleted managed telemetry source, active native module no-op trace providers, telemetry-free build targets and headers, ModuleTemplate no-op trace defaults, Awake README telemetry-free documentation, PowerDisplay's removed settings telemetry IPC, the trimmed `PowerToys.Interop` IPC constant surface, the Kit-named Settings termination projection, deleted AdvancedPaste AI provider source/package/UI/enum helper remnants, removed Shortcut Conflict inactive-module special cases, the explicit SettingsFactory hotkey boundary, the removed inactive MouseUtils page conflict branch, Settings package-reference comment cleanup, Registry Preview-only SkiaSharp package pin removal, Command Palette extension package pin removal, Command Palette Adaptive Cards package pin removal, Command Palette WinGet interop package pin removal, AdvancedPaste Markdown conversion package pin removal, PowerToys Run package pin removal, deleted PowerToys Run and Registry Preview utility notice sections, PreviewPane STL and PowerAccent package pin removal, Command Palette toolkit and host package pin removal, deleted-module package pin removal, deleted-utility package pin removal, deleted Launcher/AI/CmdPal package pin removal, deleted Preview/Peek/CmdPal shared assets, deleted utility NOTICE sections, current Kit runtime wording, the sparse package active app identity boundary, active Kit UI-test launch targets, PowerDisplay runner IPC single-instancing, active-module `ModuleHelper` behavior, sparse package version/signing defaults, build/signing helper stability defaults, full active-module first-plugin docs, and the Quick Access fast-build dependency.
- Verification for this refactor used Visual Studio 18 MSBuild for `Kit.vcxproj`, `PowerToys.Settings.csproj`, `PowerToys.QuickAccess.csproj`, `PowerDisplay.csproj`, `PackageIdentity.vcxproj`, `GPOWrapper.vcxproj`, `UnitTests-CommonUtils.vcxproj`, `AwakeModuleInterface.vcxproj`, `PowerDisplayModuleInterface.vcxproj`, `LightSwitchModuleInterface.vcxproj`, `LightSwitchService.vcxproj`, `MonitorModuleInterface.vcxproj`, `ModuleTemplateCompileTest.vcxproj`, and `Settings.UI.UnitTests.csproj`; `vstest.console.exe` reported 426/426 passing CommonUtils tests in the broader 2.0.1 pass, 149/149 passing Settings UI tests after the active-module managed/native telemetry slimming pass, 150/150 passing Settings UI tests after the interop IPC constant slimming pass, 151/151 passing Settings UI tests after the Settings termination projection rename and AdvancedPaste AI provider helper cleanup, 152/152 passing Settings UI tests after removing Shortcut Conflict inactive-module special cases, 153/153 passing Settings UI tests after narrowing SettingsFactory to explicit hotkey settings, 154/154 passing Settings UI tests after deleting the inactive MouseUtils conflict branch, 155/155 passing Settings UI tests after cleaning Settings package-reference comments, 156/156 passing Settings UI tests after removing the Registry Preview-only SkiaSharp package pin, 157/157 passing Settings UI tests after removing the Command Palette extension package pin, 158/158 passing Settings UI tests after removing the Command Palette Adaptive Cards package pins, 159/159 passing Settings UI tests after removing the Command Palette WinGet interop package pin, 160/160 passing Settings UI tests after removing the AdvancedPaste Markdown conversion package pins, 161/161 passing Settings UI tests after removing the PowerToys Run package pins, 162/162 passing Settings UI tests after removing the PreviewPane STL and PowerAccent package pins, 163/163 passing Settings UI tests after removing the Command Palette toolkit and host package pins, 164/164 passing Settings UI tests after removing the deleted-module package pins, 165/165 passing Settings UI tests after removing the deleted-utility package pins, 166/166 passing Settings UI tests after removing the deleted Launcher, AI, and CmdPal package pins, 167/167 passing Settings UI tests after deleting the inactive Preview/Peek/CmdPal shared assets, 168/168 passing Settings UI tests after the Quick Access title, runtime comment, and 2.0.1 metadata finalization, 169/169 passing Settings UI tests after deleting stale utility NOTICE sections and rewording current Kit runtime logs/comments, 170/170 passing Settings UI tests after trimming the sparse package manifest to active app identities, and 173/173 passing Settings UI tests after the UITestAutomation, ModuleHelper, sparse package metadata, plugin-doc, and Quick Access fast-build cleanup. The same final pass also reported 409/409 passing CommonUtils tests after removing `ModulesRegistry.Tests.cpp`, and MSBuild reported 0 warnings and 0 errors for `Settings.UI.UnitTests.csproj`, `UnitTests-CommonUtils.vcxproj`, `logger.vcxproj`, `AwakeModuleInterface.vcxproj`, `LightSwitchModuleInterface.vcxproj`, `PowerDisplay.csproj`, `PackageIdentity.vcxproj`, and `Kit.vcxproj`.

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
- Native settings telemetry worker scaffolding has been removed from the runner source tree; the privacy boundary now keeps both the runner project and filters free of `settings_telemetry` files. Do not wire telemetry back in unless a future local-only diagnostics design replaces the upstream send path.
- ETW trace scaffolding is still present around runner lifetime. Treat it as local trace infrastructure, not as an opt-in telemetry feature; any future removal should be done separately from module compatibility work.

The same pass cleaned Git's stale worktree metadata with `git worktree prune`. Before pruning, Git reported `C:\Users\Zen\Repo\Codings\Kit\.worktrees\kit-phase1-host` as prunable because its gitdir pointed to a non-existent location. After pruning, `git worktree list --porcelain` reports only the current `C:\Users\Zen\Repo\Codes\Kit` worktree.

## Settings And Home Lessons

- Home should be fed from the same maintained active-module list as Settings and tests.
- Quick Access can safely show modules without direct quick actions if it falls back to opening the module settings page.
- Empty states must be based on visible item count, not raw item count, because disabled or GPO-hidden modules can still exist in collections.
- Settings cards that host inline progress or mixed controls should use `HorizontalContentAlignment="Stretch"` and a two-column `Grid` so the middle space is usable instead of opening a second row.
- Cross-module UI should tolerate intentionally removed or future modules. Light Switch can expose its active PowerDisplay profile bridge, but similar bridges must not assume unrelated inactive modules are present.
- UI text should stay English and Kit-branded. Keep `PowerToys` only where build-facing names, namespaces, assembly names, module interface DLL names, or origin attribution still require it.

## Build And Test Lessons

- Use targeted builds before whole-solution builds. Settings, Quick Access, runner, module interfaces, and module workers can fail for different reasons.
- Do not let Debug outputs prove Release packaging. Debug can keep stale `WinUI3Apps` files after earlier successful builds, while a clean Release tree exposes missing build dependencies.
- The runner can successfully start the tray while the Settings window is unavailable. `settings_window.cpp` launches `WinUI3Apps\PowerToys.Settings.exe` relative to `Kit.exe`, and Quick Access launches `WinUI3Apps\PowerToys.QuickAccess.exe` the same way. Keep `Kit.slnx` runner build dependencies on both UI executable projects so `Kit.slnx /t:Kit` regenerates the full runtime shape.
- Copied PowerToys modules often depend on CsWinRT projections generated from `PowerToys.Interop.winmd` and `PowerToys.GPOWrapper.winmd`. Clean Release builds can leave a bad intermediate state where `cswinrt.rsp` remains but the generated `.cs` projection files are gone; in that case CsWinRT may skip generation and later C# compilation reports missing `PowerToys.Interop` or `PowerToys.GPOWrapper` namespaces. Kit now invalidates that stale rsp state in `Common.Dotnet.CsWinRT.props`, and both native WinMD projects publish their WinMDs to the shared configuration output.
- Run native module-interface builds sequentially when building them independently. Shared native outputs such as `Version.pdb` and tracking logs can create false failures under unrelated parallel MSBuild invocations; `src/common/version/version.vcxproj` now uses `/FS` so the two version sources do not race the same PDB under normal MSBuild scheduling.
- Keep `Settings.UI.UnitTests` aligned with Kit's trimmed module set. Tests for removed PowerToys pages should not block Kit, but tests for Kit registration points should be strict.
- Clean generated `Debug`, `Release`, and `TestResults` outputs before handing the tree back for a fresh Visual Studio compile when the goal is to verify a clean build. Remove wider `bin`, `obj`, `x64`, or `AnyCPU` directories only when a full source-clean is intentionally needed.
- Use `git worktree prune` only for stale worktree metadata already reported as prunable. For live worktree directories, inspect branch and uncommitted status before removing anything.
- When the workspace grows by tens of GB, the usual cause is build output, not source. Safe cleanup targets are top-level `src\kit\x64`, `src\kit\Debug`, `src\kit\Release`, `src\kit\.vs`, root `TestResults`, and project-local `bin`, `obj`, `x64`, `Debug`, `Release`, and `TestResults` directories.
- Treat `src\kit\packages` as a restore cache. It is ignored by Git and can be deleted before uploading to GitHub, but keeping it locally speeds up rebuilds and avoids confusing cold-build errors from missing WIL, C++/WinRT, or native package imports.
- If `src\kit\packages` was deleted, run Visual Studio `Restore NuGet Packages` or a full solution build before investigating missing-header or missing-WinMD errors. Partial project builds after package cleanup can produce misleading first errors.
- Prefer Visual Studio MSBuild for projects that transitively build native `vcxproj` dependencies. `dotnet test` without a prior VS MSBuild build can fail early on missing `$(VCTargetsPath)` before any managed tests run.

## 2026-05-29 Kit 2.0.1 Isolation And IPC Stabilization

This pass continued the PowerToys-main comparison and tightened the places where Kit still behaved like a patch stack over upstream PowerToys:

- Common Settings deep links and the PowerDisplay settings link now resolve the Kit install/debug path and launch only `Kit.exe`. Missing paths are logged instead of silently falling back to an installed upstream `PowerToys.exe`.
- PowerDisplay runner toggles now stay on the runner-owned `kit_power_display_` named pipe. The module interface no longer uses no-argument `ShellExecuteExW` when the runner-managed process is already alive, because runner IPC launches intentionally bypass standalone AppInstance registration.
- PowerDisplay buffers named-pipe messages that arrive before `MainWindow` exists and flushes them after window creation, so the first toggle or profile-apply message after process startup is not dropped.
- PowerDisplay pipe writes now return an `HRESULT`; if the owned child is still present but the pipe is broken, the process manager restarts its owned process, reconnects the pipe, and retries the message once.
- PowerDisplay pipe startup now treats `ERROR_PIPE_CONNECTED` as an already-connected client, so an early `NamedPipeClientStream` connection does not wait for an overlapped event that will never be signaled. Module teardown now waits for the queued stop task to send the terminate message and clean the child process before the executor is destroyed. Standalone activation redirects use a bounded COM wait instead of an infinite wait.
- `ModuleHelper.GetModuleKey` now exposes IPC/settings keys only for `Awake`, `LightSwitch`, `Monitor`, `PowerDisplay`, and General settings. Inactive upstream module keys remain in compatibility settings model types, but are not addressable through the shared helper.
- UITestAutomation cleanup now resolves active Kit executable paths through `ModuleConfigData` and kills only matching processes at those paths. This keeps names such as `PowerToys.Settings.exe` and `PowerToys.PowerDisplay.exe` usable in Kit outputs without killing an installed official PowerToys build.
- UITestAutomation cleanup now returns separate matched/killed/failed state and logs inspect/kill failures. This preserves path-scoped cleanup while making stale elevated or inaccessible Kit processes visible instead of silently skipping cleanup.
- Local build helpers now treat `.slnf` as solution files for `-RestoreOnly`, respect `/property:` long-form overrides for default skip properties, forward `-RequireMachineRoot` through direct signing entry points, and document that current-user trust is the default signing path.
- Settings deep links now call a Kit-only install resolver. The upstream-compatible `PowerToys.exe` resolver remains separated for copied-module compatibility helpers, but Kit Settings links do not use it.
- Remaining unreferenced inactive Settings resources for Keyboard Manager, File Explorer add-ons, Mouse utilities, Screen Ruler, Peek, Workspaces, and Hosts were removed so the resource file matches the active four-module UI surface.
- Runner now honors the `enable_quick_access` general setting instead of forcing Quick Access off, and periodic update toasts now respect the Settings notification toggle.
- Quick Access rolls a module toggle back when the runner IPC update fails, keeping the UI state aligned with the actual module state.
- Awake module destruction now signals the child process, waits for shutdown, uses a bounded terminate fallback when the signal path cannot be established or does not complete, and closes process/thread handles before deleting the module interface.
- Settings compatibility models no longer probe the inactive Command Palette package path, so deleted CmdPal package state cannot leak into current Settings startup.
- Local sparse package re-registration now points at the publisher-adjusted `.user/PowerToysSparse.AppxManifest.xml` emitted by `BuildSparsePackage.ps1`, while CI still keeps the checked-in manifest publisher unchanged.
- Local signing helper defaults use the development certificate subject and current-user trust path; broader machine/root trust remains an explicit opt-in.
- The remaining inactive Settings resource strings and unused VariantAssignment package pins were removed to keep the active Kit surface aligned with the four retained modules.
- A follow-up resource pass removed inactive File Explorer Preview, Shortcut Guide activation, Screen Ruler, and ZoomIt picker strings that were still present in the active Settings resource file without active XAML references.
- The XAML search index builder now excludes `SearchResultsPage` and `ShortcutConflictWindow`, preventing generated Settings search data from returning the search page itself or a non-navigable conflict window.
- Settings launch failure now clears the runner launch-in-progress guard before leaving `run_settings_window`, so a transient `CreateProcessW` failure does not suppress future Settings opens.
- Settings launch now atomically claims the launch-in-progress guard in `open_settings_window` before creating the detached launcher thread, then keeps that guard held after `CreateProcessW` succeeds until runner/settings IPC is started and `g_settings_process_id` is registered. This closes both duplicate-launch windows: a fast second open before the worker thread starts and a fast second open between process creation and IPC/PID registration. If token or IPC setup fails after the child is created, the runner ends partial IPC state, signals the Settings terminate event, uses a bounded terminate fallback, and resets the terminate event so the next Settings process does not consume a stale signal.
- LightSwitch disable now signals a Kit-named service-stop event before using its bounded terminate fallback, and module destruction closes all event handles created by the interface.
- Disabled LightSwitch Force Light/Force Dark UI comments, settings custom-action commands, and unused force-mode named events were removed; the retained immediate action surface is the active toggle hotkey/event path.
- Settings command-line `set`/`get` helpers now reject inactive module names and inactive `Enabled.*` keys before reflective settings lookup. The retained command-line compatibility surface is General plus `Awake`, `LightSwitch`, `Monitor`, and `PowerDisplay`, matching the documented active module set instead of preserving callable FancyZones, PowerToys Run, or Mouse Without Borders settings.
- `EnabledModules` now asserts the active default boundary directly: Awake, LightSwitch, and PowerDisplay are default-enabled; Monitor remains default-off until the user enables the module.

Verification for this pass used Visual Studio 18 MSBuild and VSTest:

1. Built `src/common/UITestAutomation/UITestAutomation.csproj` Debug x64.
2. Built `src/modules/powerdisplay/PowerDisplay/PowerDisplay.csproj` Debug x64.
3. Built `src/modules/powerdisplay/PowerDisplayModuleInterface/PowerDisplayModuleInterface.vcxproj` Debug x64.
4. Built `src/settings-ui/Settings.UI.UnitTests/Settings.UI.UnitTests.csproj` Debug x64.
5. Ran `vstest.console.exe` with `FullyQualifiedName~BuildCompatibility`, which reported 74/74 passing tests.
6. Added failing regression coverage for Quick Access settings/IPC rollback, update-toast notification gating, Awake shutdown cleanup, CmdPal package-probe removal, sparse package helper output, signing helper defaults, inactive resource cleanup, and unused package pin removal.
7. Rebuilt `Settings.UI.UnitTests.csproj` Debug x64 and ran `FullyQualifiedName~BuildCompatibility`, which reported 77/77 passing tests for the expanded compatibility coverage.
8. Rebuilt `Kit.vcxproj`, `AwakeModuleInterface.vcxproj`, `PowerToys.QuickAccess.csproj`, `PowerToys.Settings.csproj`, and `PackageIdentity.vcxproj` Debug x64 after the runtime/package changes.
9. Ran the full `Settings.UI.UnitTests.dll`, which reported 176/176 passing tests.
10. Ran `BuildSparsePackage.ps1 -Platform x64 -Configuration Debug -NoSign`, which created `x64\Debug\PowerToysSparse.msix` and printed the generated `.user\PowerToysSparse.AppxManifest.xml` re-registration command.
11. Added regression coverage for the extra inactive File Explorer Preview, Shortcut Guide activation, Screen Ruler, and ZoomIt picker resource strings, verified the focused resource cleanup test failed first, then passed after deleting the unused resource entries.
12. Added failing regression coverage for search-index page exclusions, Settings launch guard cleanup, and LightSwitch service-stop/handle lifecycle cleanup before implementing those fixes.
13. Rebuilt `Settings.UI.UnitTests.csproj`, `Kit.vcxproj`, `LightSwitchModuleInterface.vcxproj`, `LightSwitchService.vcxproj`, and `PowerToys.Settings.csproj` Debug x64 after the cleanup.
14. Ran the focused search/settings/LightSwitch compatibility filter, which reported 3/3 passing tests; reran `FullyQualifiedName~BuildCompatibility`, which reported 78/78 passing tests; then ran the full `Settings.UI.UnitTests.dll`, which reported 177/177 passing tests.
15. Added failing regression coverage for disabled LightSwitch force-mode UI/action/event remnants, then removed them; `LightSwitchModuleInterface.vcxproj` rebuilt successfully, `FullyQualifiedName~BuildCompatibility` reported 79/79 passing tests, and the full `Settings.UI.UnitTests.dll` reported 178/178 passing tests.
16. Added failing regression coverage for the Settings duplicate-launch guard windows, stale terminate-event cleanup, partial IPC start cleanup, and Settings child cleanup after token or IPC setup failure, then moved launch-guard claiming into `open_settings_window`, kept the guard held through IPC/PID registration, made partial `TwoWayPipeMessageIPC::end()` safe, and added bounded child termination on setup failures. Rebuilt `Settings.UI.UnitTests.csproj` and `Kit.vcxproj` Debug x64; the focused Settings launch compatibility filter reported 4/4 passing tests.
17. Added failing regression coverage for inactive Settings command-line module access, General `get` alias handling, and stale inactive `EnabledModules` defaults, then allowlisted only General plus the four active settings modules and moved command tests to active module settings. Rebuilt `Settings.UI.UnitTests.csproj` Debug x64; the focused command/default filter reported 15/15 passing tests. The final `FullyQualifiedName~BuildCompatibility` run reported 88/88 passing tests, and the full Settings UI test assembly reported 190/190 passing tests.

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
