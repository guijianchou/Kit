# Changelog

**Language / 语言:** English | [中文](#更新日志)

## English

### 2.2.1

- **WinUI 3 Page Crash Fixes & Navigation Stability**:
  - **General Settings Page (`GeneralPage.xaml`)**: Fixed fatal `XamlParseException` / `0xC000027B` crash on page load caused by `AiHub_MainEndpoint_ApiKeyCard.PlaceholderText` in `Resources.resw` targeting the parent `SettingsCard` rather than the child `PasswordBox`. Assigned `x:Uid="AiHub_MainEndpoint_ApiKeyBox"` to the `PasswordBox` and relocated ViewModel initialization before `InitializeComponent()`.
  - **UDP Test Page (`UDPtestPage.xaml` & `UDPtestPage.xaml.cs`)**:
    - Fixed crash from `UDPtest_ClearHistoryButton.Text` in resource files attempting to set a non-existent property on `Button`. Replaced with tooltip attached property syntax `[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`.
    - Fixed runtime crash caused by `Application.Current.Resources["AccentButtonStyle"]` throwing `COMException: Element not found` (WinUI 3 system theme styles are not stored in `Application.Current.Resources`). Added explicit `AccentHealthButtonStyle` based on `AccentButtonStyle` in page resources and guarded runtime lookups.
    - Introduced `ThemeBrushHelper` with safe `TryGetValue` fallback brushes (`SuccessBrush`, `CautionBrush`, `CriticalBrush`, `AttentionBrush`, `SecondaryTextBrush`, `StrokeDefaultBrush`) across `UDPtestViewModel.cs`, `UDPtestLineRowViewModel.cs`, and `RecentProbeBarViewModel.cs` to eliminate brush indexing exceptions.
  - **AI Hub Page (`AIHubPage.xaml` & `AIHubPage.xaml.cs`)**:
    - Fixed `InvalidCastException` (`SolidColorBrush` to `Windows.UI.Color`) in XAML compiled bindings (`AIHubPage_obj1_Bindings.Update_ViewModel_HealthScoreGrade`) by binding the `SeverityToBrushConverter` directly to `Ellipse.Fill` rather than `<SolidColorBrush Color="..." />`.
    - Moved ViewModel instantiation prior to `InitializeComponent()` to guarantee bindings attach to a valid ViewModel instance during the initial XAML layout pass.
- **Diagnostic Logging Infrastructure**:
  - Added synchronous first-chance and unhandled exception logging in `App.xaml.cs` writing to `%LOCALAPPDATA%\Kit\crash.log` with full stack traces, timestamps, and thread IDs for immediate post-mortem analysis.
- **Documentation & Plugin Guidelines**:
  - Updated `PLUGIN_DEVELOPMENT.md` with best practices for WinUI 3 page lifecycle, strict `x:Uid` target property rules, attached property localization syntax, and safe theme resource lookups using `ThemeBrushHelper`.

### 2.2.0

- **AI Hub Security Audit UI & Architecture Overhaul**:
  - Restructured tiered security policy pipeline: global policy (`%LOCALAPPDATA%\Kit\AiHub\security.md`) and task-specific essential policy (`chains/{task}/AGENTS.md`), complete with default scaffolding in `SecurityPolicyService.cs` and dedicated Settings expander panels.
  - Formally integrated execution pipeline visualization: `Codex / Pi → Security policy + AGENTS.md → Summary → Action`.
  - Beautified and pixel-aligned the Security Audit tab in `AIHubPage.xaml` strictly matching the reference project: Health overview donut ring, health grade badges (A-F), finding severity distribution bar, audit activity metrics, and priority action banners.
  - Fixed WinUI 3 XAML binding crash when viewing security finding details by enforcing strict `SolidColorBrush` return type in `SeverityToBrushConverter.cs`.
  - Added dynamic bilingual culture formatting: detection facts, root cause analysis, and actionable recommendations (`PriorityActionText`) dynamically follow the active system language (`zh-CN` vs `en-US`).
  - Restored light theme purple icon (`#A756FF`) for AI Hub across Settings sidebar navigation and module assets.
- **100% Pure Bilingual Localization Across Kit & Plugins**:
  - Removed all mixed-language parenthetical annotations and bilingual slashes across the main shell and plugins (e.g. `(保持唤醒)` -> `保持唤醒`, `启用 Awake` -> `启用保持唤醒`, `启用 UDP Test` -> `启用网络探测`, `启用 AI Hub` -> `启用 AI 智能中心`, `Essential Policy (全局任务策略)` -> `基础任务策略`).
  - Localized Localserver interface (40+ all-caps metric labels replaced with pure Chinese), UDPtest table headers & metadata strip, Shell navigation, Dashboard, Shortcut conflict & assignment dialogs, and Quick Access flyout.
  - Fully localized `FindingDetailsDialog.xaml.cs` (event ID, affected scope, severity, timestamps, formatted report output).
- **Test Suite Verification & Quality Assurance**:
  - `Kit.Settings.csproj`: 0 Warnings, 0 Errors.
  - `Kit.AiHub.UnitTests.csproj`: 108 Passed, 0 Failed, 1 Skipped.
  - `Settings.UI.UnitTests.dll`: 196 Passed, 0 Failed.

### 2.1.1

- Refined status bar sparkline dimensions and density in Localserver and UDPtest plugins to match original projects:
  - Adjusted vertical bar width from 3px to 5px and corner radius to 2px, eliminating overly thin and needle-like dense bars.
  - Set comfortable 2px inter-bar spacing with 7px pitch, achieving a solid, high-legibility pill-shaped telemetry strip.
  - Adjusted tick heights to 16px (16px/11px/6px in UDPtest), fully matching `LocalServerHub.App` and `NetworkMonitor.App`.
  - Expanded historical sample buffer capacity to 240 items and automatically pre-fill telemetry on loading active services.

### 2.1.0

- Added new UDPtest native plugin module and UI page:
  - High-performance probe engine supporting TCP HTTPS latency, UDP echo, STUN binding, and NAT type discovery.
  - Interactive status dashboard, node verification, and historical probe strip with doubled 60-bar capacity.
- Unified plugin master switch lifecycle across modules with official Awake/LightSwitch standards:
  - Fixed GPO configuration check to prevent false "managed by your organization" warning lock.
  - Synchronized cascade stop: toggling off the master switch immediately terminates all child services and probe workers (Localserver `StopAllLinesAsync()`, UDPtest `StopAsync()`) and pauses background identity polling.
  - Applied WinUI 3 standard 0.38 disabled opacity and control gray-out, and neutralized indicator lights to inactive gray.
- Status bar display optimizations:
  - Localserver: aligned telemetry tick cadence to 1s/sample and increased vertical bar height by 20% (to 20px).
  - UDPtest: doubled recent probe bar capacity to 60 items and increased bar height to 20px.
- Updated `PLUGIN_DEVELOPMENT.md` with master switch cascade stopping and status bar guidelines.

### 2.0.23

- Redesigned Settings AI Service UI/UX using standard PowerToys SettingsExpander and SettingsCard controls, aligning hierarchy and layout with native settings.
- Streamlined endpoint headers to display only `Model · Effort` (e.g. `gpt-5.6-luna · medium`), removing unnecessary subtitle badges and verbose notes.
- Decoupled Localserver module from AI Hub: completely removed AI analysis card, prompt chains, service dependencies, and view models.
- Added centralized Diagnostics & Logging management to Kit General Settings: master logging toggle, log level selector (trace, debug, info, warn, error, critical, off), and one-click log folder explorer, synchronized with ManagedCommon.Logger and `%LOCALAPPDATA%\Kit\log_settings.json`.
- Updated test suites and verified zero-defect execution.

### 2.0.22

- Refactored AI Hub into an in-place compact layout directly within General Settings, eliminating nested subpage navigation and breadcrumb buttons.
- Added master toggle collapse/expand behavior: configuration is hidden when AI Hub is disabled and revealed in-place when enabled.
- Added explicit [Apply] confirmation button for switching AI kernels (Codex CLI / Pi CLI).
- Replicated Locals compact endpoints and security policy (security.md) expander panels with full bilingual localization (en-US / zh-CN).

### 2.0.21

- Moved AI Hub under Settings, removed its standalone sidebar entry and Home module card, and kept the global enable switch inside AI Hub settings.
- Added a localized Settings entry and return link. AI Hub keeps Settings selected in the sidebar when opened directly or from Localserver.

### 2.0.20

- Fixed incomplete Kit namespace migration in SettingsAPI, Awake WinRT projection, Settings/Quick Access executable paths, and PRI resource loading.
- Restored the upstream native interface slot order, retained the legacy factory fallback, and hardened DLL loading and configuration buffer handling.
- Deferred the search index until the first query, skipped empty keyboard hooks, and serialized module settings once for both IPC compatibility keys.
- Fixed Localserver graceful stop deadlines and cancellation cleanup during readiness. Pending edits are flushed before closing; active services keep their Settings host and output pipes alive while the window is hidden.
- Routed Localserver log previews through bounded periodic refresh, corrected the Kit log directory action, refreshed shared enable state, and localized hardware status labels.
- Made LightSwitch configuration and runtime state reads return locked snapshots. Updated the native module template, plugin guide, and reproducible Debug staging checks.
- Validation: managed/native regression tests and AI Hub Native AOT smoke. Full WinUI visual interaction remains pending because Windows automation cannot bind the Kit window.

### 2.0.19

- Version: Bumped Kit to `2.0.19`.
- Localserver Card Typography & UX Optimization:
  - Fixed HOST label line wrapping into "HOS\nT" by switching to `Auto,*` column sizing and stacking `SystemEditionText` and `SystemBuildText`.
  - Upgraded hardware info display to match original `LocalServerHub` standards: refined CPU from raw identifier to friendly name (`Ryzen 5 5600H · 12 logical cores`), GPU to compact name (`RTX 3050 Ti`), and OS edition/build (`Windows 11 LTSC`, `Build 26100.9168`).
  - Improved environment verdict contrast: `EnvSummaryText` now renders in crisp theme-aware `SystemFillColorSuccessBrush` (green) with semi-bold typography.
  - De-cluttered Health Ring: centered clean health state text without crowded icons and static labels.
  - Added `NoWrap` single-line truncation with tooltips to command and working directory fields.
  - Standardized metrics typography: introduced `MetricPrimaryStyle` and `MetricCompactStyle` for headline values (PORT, PID, REGISTERED, RUNNING, FAULTED).
- LightSwitch Theme Switching & Night Auto-Switch Fixes:
  - Fixed bug in `DetectAndHandleExternalThemeChange` where scheduled night transition was falsely diagnosed as external Windows change, locking the service into manual override and preventing dark mode from applying.
  - Fixed manual override toggle logic to properly respect user intent without premature schedule snapback.
  - Fixed `SunsetToSunrise` schedule evaluation when coordinates are unset/invalid to reliably fall back to scheduled times, eliminating the 0,0 boundary degradation.
  - Synchronized target theme in `LightSwitchInterface::ToggleTheme()` to ensure system and app themes toggle synchronously.

### 2.0.18

- Version: Bumped Kit to `2.0.18`.
- Shortcuts Feature Removal: Completely removed the Shortcuts card and hotkey system from Dashboard (`DashboardPage.xaml`, `DashboardViewModel.cs`), resolving the flickering icon issue caused by continuous item template recreation.
- Module Hotkey Cleanup: Removed hotkey configuration cards and controls from `LightSwitchPage.xaml` and `LightSwitchViewModel.cs`. Removed `ToggleThemeHotkey` from `LightSwitchProperties.cs` and `LightSwitchSettings.cs`.
- Native C++ Interface: Removed hotkey definitions, settings deserialization, registration (`get_hotkeys`), and handling (`on_hotkey`) from `LightSwitchModuleInterface/dllmain.cpp`, releasing global keyboard hook registrations.
- Quick Access Tooltip: Cleaned up tooltip lookup in `QuickAccessViewModel.cs` to eliminate removed hotkey dependencies.

### 2.0.17

- Version: Bumped Kit to `2.0.17`.
- Toolbar Cleanup & Auto-Save: Removed redundant `Save` and `Reload` buttons from the Services toolbar. Line configuration now automatically persists to `services.json` upon collapsing/folding the drawer, auto-saves with debouncing during parameter edits, and saves when navigating away from the page.
- Add Line Experience: Clicking `Add Line` now automatically unfolds the configuration drawer so parameters can be edited immediately.
- Table Column Layout & Alignment: Restructured the service table to share a unified 8-column proportional grid (`NAME` 1.8*, `PORT` 0.7*, `STATE` 1.1*, `UPTIME` 0.9*, `CPU` 0.6*, `MEM` 0.8*, `PID` 0.7*, Actions 110px). Eliminated the excessive empty space between `NAME` and `PORT`.
- Status Bar Pixel-Alignment: Status text (`Status: ● [State]`) is positioned in Column 0 under `NAME`, while the real-time recent status bars (`RecentBars`) start directly under Column 1 (`PORT`) left-aligned and span across to `PID`, perfectly matching the original reference design.
- Stop Responsiveness: Fixed `LineRowViewModel.IsOn` to evaluate to `false` during `Stopping` so the toggle switch turns off immediately without fighting the user. Fast-pathed process termination in `ServiceRunner.StopCoreAsync()` when no GUI window exists, reducing shutdown latency from 5-10 seconds down to < 1 second.

### 2.0.16

- Version: Bumped Kit to `2.0.16`.
- Shell UI: Adjusted unhidden navigation pane width (`OpenPaneLength="176"`, widened by 1/3 from 132) to prevent text clipping while maintaining a compact layout.
- Language Unification: Strict localization isolation between English (`en-us`) and Simplified Chinese (`zh-CN`). Eliminated all mixed-language bilingual slashes, ensuring 100% key parity and native phrasing.
- Service Line UI/UX Overhaul: Aligned service line rows strictly with high-density design, featuring real-time recent status bars (`RecentBars`), running lock indicator (`🔒`) with configuration freeze (`IsEnabled="{x:Bind IsEditable}"`), and Targets-style expandable configuration drawer.
- Configuration Management: Relocated `Delete Line` button to the bottom of the expanded configuration drawer with safety confirmation flyout; eliminated service line reordering and auto-start on boot toggles.
- Diagnostics & Output: Retained top overview cards (Environment with Health Ring & CPU trend canvas, System hardware metrics, and Session counters) alongside a collapsible bottom console output drawer with live tail follow, log folder shortcut, and buffer clear.
- Stability: Fixed `XamlParseException` on navigating to Localserver caused by `.Content` Uid resolution on `TextBlock` elements.

### 2.0.15

- Localserver UI: Restored Health and System as persistent cards above the service list. They share a row when the content area is at least 720 DIP wide and stack on narrower windows. Removed the introductory image to reduce unused space.
- Logging: Removed the Localserver log panel and its UI refresh loop. Already-redacted service output now flows through a bounded background queue into Kit's existing Settings logger, preserving service IDs, streams, timestamps, and sequence numbers. Overload and write failures are counted; disposal does not block the UI, and process-exit draining is bounded.
- Language: Connected page labels, service states, environment checks, and operation feedback to Kit's existing English and Chinese resources. Runtime state and action availability no longer depend on translated text.
- Reliability: Preserved configuration origins and concurrent-save checks, fixed consecutive saves after cross-file reordering, reused encrypted secret migration, and kept intentionally empty service catalogs empty. Added confirmation and process-identity checks for destructive service actions; service URLs use the live port and existing variable expansion.
- Lifecycle and documentation: Paused hardware sampling while the page is inactive, prevented overlapping refreshes, and retained cached management state until Settings actually closes. Updated the plugin guide and both READMEs with the three active modules, logging path, layout rules, and the current Settings-hosted management boundary.

### 2.0.14

- Version: Bumped Kit to `2.0.14`.
- Plugin Integration: Fully integrated `Localserver` as a first-party native built-in plugin into Kit per the plugin development specification (`PLUGIN_DEVELOPMENT.md`).
- WinUI 3 + Mica Architecture: Embedded natively in Kit's main window (`LocalserverPage.xaml`), adopting Mica Alt backdrop tokens and complete UI/UX modernization without standalone application overhead.
- Diagnostics & Telemetry: Added Zone 1 dual-panel diagnostics featuring Environment dependency checks (with interactive command copying and port releasing flyouts) and System hardware monitoring (GPU core/memory/temperature metrics, host CPU/RAM gauges, and 60-second historical CPU curve canvas).
- Service Line Operations: Modernized target service lines into `Line` with unified header controls (status indicator, runtime duration, resource consumption, PID, quick browser launch, and toggle switch). Implemented strict runtime configuration freezing (`IsEnabled="{x:Bind IsEditable}"`) when running to prevent runtime mutation. Relocated `Delete Line` with safety confirmation flyout to the bottom of the expanded details panel.
- Strict Cleanup: Enforced removal of legacy "Last Error" labels, "链路隐藏 (Show on main window)" toggle, and per-line auto-start on boot. User data strictly isolated under `%LOCALAPPDATA%\Kit\Localserver\`.

### 2.0.13

- Version: Bumped Kit to `2.0.13`.
- Lifecycle & PowerToys Parity: Restored PowerToys' native lifecycle model. Closing the main window (`Window_Closed`) now closes the Settings UI and leaves the runner in the system tray, allowing background modules (Awake, LightSwitch, and global hotkeys) to stay active without interruption. Complete exit is cleanly handled via the tray icon context menu.
- Cold-Start Launch Fix: Fixed the issue where clicking `Kit.exe` on a cold start would only launch `kit.runner` in the background without opening the UI. Interactive execution of `Kit.exe` without `--autorun` now automatically opens the main Settings window on the very first click, whether the runner was already in the tray or started from scratch.
- Auto-Start Parameterization: Updated Task Scheduler registration in `auto_start_helper.cpp` to pass `--autorun`, ensuring Windows login auto-start continues to boot silently to the system tray.

### 2.0.12

- Version: Bumped Kit to `2.0.12`.
- Startup & Task Scheduler: Fixed `is_auto_start_task_active_for_this_user()` to treat missing `\Kit` Task Scheduler folder as `S_FALSE` (inactive) instead of logging spurious `[error] ITaskFolder doesn't exist: -7ff8fffe` errors on every Settings launch.
- Settings & Runner IPC: Switched `killrunner` IPC handler from synchronous `SendMessageW` to non-blocking `PostMessageW(..., WM_CLOSE, ...)`. Added robust dual-channel runner teardown in Settings `Window_Closed` (both IPC `killrunner` and tray window message fallback), ensuring clean exit and preventing orphaned runner processes regardless of HWND resolution.
- Staging & Tooling: Fixed `Stage-Debug.ps1` byte calculation and object formatting for manifest generation. Updated staging targets and manifests for `2.0.12`.
- Validation: 100% test pass rate across all suites (680/680 unit and integration tests passing). Runner initialization verified at 18ms with 0 lingering background processes on clean exit.

### 2.0.11

- Version: Bumped Kit to `2.0.11`.
- Settings: Serialized launch, exit, restart, and IPC cleanup. Normal close uses asynchronous runner notification; restart hands over after the old Settings process exits, and launch failures no longer close the runner.
- LightSwitch: Made worker lifecycle event-driven. Off keeps manual actions without a worker; repeated enable avoids duplicate workers, and the first action after a worker crash restores scheduling. Early stop signals, parent validation, and cancellable settings debounce improve shutdown handling.
- Awake: Isolated the single-instance mutex as `Local\Kit.Awake` and scoped worker lookup to the Kit path and session for coexistence with official PowerToys.
- Quick Access: Deferred process startup until first use, moved launch work off the keyboard hook, merged duplicate requests, and cancelled queued work when disabled.
- Startup and privacy: Removed background release checks, retries, update toasts, and obsolete startup cleanup; manual release checks remain. GPO compatibility consistently returns `not_configured`. Startup-task operations target only Kit-owned entries and avoid re-registering unchanged tasks.
- Dependencies: Removed the unused Monitor-era `Microsoft.Data.Sqlite` reference, central version pin, and corresponding notices.
- Plugin development: Expanded the root specification with compatibility limits, registration, logo sizes and paths, data isolation, lifecycle, and WinUI 3 + Mica Alt requirements. Synchronized template sources, ZIP metadata, dependencies, and compile coverage; corrected README build-output documentation.
- Validation baseline before the version bump: `Settings.UI.UnitTests` passed 200/200; the full x64 Debug build completed with 0 warnings and 0 errors; isolated LightSwitch smoke checks passed, including real-worker early stop and invalid-parent handling, with theme values unchanged and original settings restored. Final `2.0.11` rerun results are pending; WinUI interaction checks and startup timing measurements remain open.

### 2.0.10

- Version: Bumped Kit to `2.0.10`.
- UI/UX: Removed redundant "Close Kit" footer navigation item and dialog from the sidebar; closing is now handled cleanly and standardly via the top-right window close button ('X').
- Runtime & Lifecycle: Fixed background `kit.runner` (`Kit.exe`) continuing to run after closing the main window. Closing the main window now coordinates a full, graceful termination of both the UI and background runner (via `killrunner` IPC, tray window message fallback, and runner process-watchdog auto-close).
- UI/UX & Localization: Fixed application crash on language switch in Settings. Pruned language options to strictly supported locales (English default, Chinese Simplified). Added bounds checking, standalone `%LOCALAPPDATA%\Kit\language.json` persistence, and automatic application relaunch.
- Localization: Added native WinUI 3 MRT Core Chinese Simplified resources (`Strings/zh-CN/Resources.resw`) aligned 1:1 with English keys, eliminating all PRI263 build warnings, and removed obsolete language strings.
- UI/UX: Aligned selection controls and time pickers in LightSwitch and Settings to the standard upstream PowerToys width (`SettingActionControlMinWidth` = 240px) for consistent, balanced layouts.
- UI/UX: Further refined card brush styling on Mica Alt backdrop inspired by `Locals` (`#80FFFFFF` 50% fill in Light mode, `#0AFFFFFF` in Dark mode with subtle borders), ensuring seamless, natural contrast.
- Build & Tests: 100% test pass rate (186/186 in `Settings.UI.UnitTests`). Complete debug outputs staged in `bin/debug/2.0.10/`.

### 2.0.9

- Version: Bumped Kit to `2.0.9`.
- UI/UX: Overhauled sidebar navigation with standard WinUI 3 `LeftCompact` mode; sidebar smoothly collapses to a 48px compact icon bar and expands on demand.
- UI/UX: Restored default expanded sidebar state aligned with upstream PowerToys behavior, and removed the duplicate collapse icon from the footer.
- UI/UX: Fixed the General Settings button in the footer menu by migrating `GeneralNavigationItem` to `<NavigationView.FooterMenuItems>` and wiring `ShellViewModel` item resolution.
- UI/UX: Modernized visual styling with WinUI 3 + Mica Alt backdrop (referenced from `Locals` design system).
- UI/UX: Refined card and surface brushes on Mica Alt to use soft translucent fills (`#90FFFFFF` in Light mode, `#0DFFFFFF` in Dark mode) and subtle borders (`#1F000000`), eliminating harsh, stark white cards.
- UI/UX: Fixed double-rendering of background and border in `Card.xaml` primitive with `OverlayCornerRadius`.
- Telemetry: Conducted full zero-telemetry audit across Awake and LightSwitch; replaced upstream ETW telemetry providers (`TraceLoggingWrite`) with no-op stubs (`trace.h/cpp`), ensuring zero background telemetry leaks.
- Runtime: Restored worker graceful shutdown with named exit events and PID watchdog to prevent orphan background processes.
- Runtime: Optimized LightSwitch scheduler to cleanly terminate service process when schedule mode is set to Off, and decoupled from upstream PowerDisplay.
- Docs: Published comprehensive, production-grade `PLUGIN_DEVELOPMENT.md` guide at repository root referencing upstream PowerToys and Kit architectures.
- Tests: Maintained 100% test pass rate (186/186 tests passing in `Settings.UI.UnitTests`).
- Build: Staged complete verified debug outputs in `bin/debug/2.0.9/`.

### 2.0.8

- Version: Bumped Kit to `2.0.8`.
- Slimming: Fully removed `Monitor` module from active modules, keeping `Awake` and `LightSwitch` as the focused active module set.
- Runtime: Stabilized LightSwitch service and Awake module interfaces with Kit-isolated storage under `%LOCALAPPDATA%\Kit\`.
- Build: Established version-based build output organization under `bin/debug/2.0.8/`.

### 2.0.7

- Version: Bumped Kit to `2.0.7`.
- Monitor: Kept one-shot manual scans alive during long directory enumeration by emitting hashing progress heartbeats before the final file count is known.
- Monitor: Streamed file and directory enumeration through the scanner, organizer, and installer cleaner so large Downloads folders no longer require full per-directory materialization before work continues.
- Monitor: Range-bounded scan status summaries at the SQLite query layer and shared progress freshness detection between the worker and Settings.
- Monitor: No-progress timeout tracking now resets only after the progress reporter returns, avoiding false worker heartbeats when progress output fails.
- Monitor: Wakes the no-progress timeout watcher during disposal instead of waiting for the next polling sleep.
- Monitor: Hardened manual scan launch arguments by validating Settings-provided scan ids and using Windows command-line quoting rules.
- Monitor: Invalid manual scan ids now write a failed progress snapshot for the requested scan id and return without launching an untracked one-shot worker.
- Monitor: Enabled SQLite WAL journaling with a busy timeout on the scan-status database so the background worker writer and the Settings reader no longer contend, removing the rare status-unavailable flicker during a background scan.
- Monitor: Added a configurable installer-cleanup confidence threshold (50-95%, default 70) in Settings; the worker clamps it into a safe 0.5-0.95 range before matching installers.
- Monitor: Added a non-destructive Preview cleanup action that reports how many installers would be removed and the space they would free without deleting anything, and real cleanup now reports a removed/freed summary.
- Monitor: Extracted the shared file-enumeration helper used by the scanner, organizer, and installer cleaner and centralized the no-progress timeout, removing duplicated logic with no behavior change.
- Monitor: Fixed the Settings status legend clipping the `Warning` and `No data` labels by laying the legend out with a horizontal stack panel so each item keeps its natural width instead of a uniform-cell grid sized to the first item.
- Cleanup: Removed an orphaned PowerDisplay-era Monitor selection view model that no active Settings surface referenced.
- Docs: Updated README, README_zh, changelog, development log, sparse package version, and GPO support marker `SUPPORTED_KIT_2_0_7`.
- Tests: Added regression coverage for Monitor directory-enumeration heartbeats, streaming enumeration, range-bounded status queries, shared progress freshness, progress-timeout ordering, scan-id validation, and version metadata for `2.0.7`.
- Tests: Added coverage for installer-cleanup confidence clamping, dry-run installer preview reporting, and the new Settings confidence/preview card registration.

### 2.0.6

- Version: Bumped Kit to `2.0.6`.
- Monitor: `OrganizeDownloads` now defaults to off, so background and one-shot scans only index the Downloads folder until the user opts in; the explicit Organize action is unchanged.
- Monitor: The background worker now restarts only when a setting it actually consumes changes, so unrelated or duplicate config saves no longer interrupt an in-progress background scan.
- Monitor: Hardened the worker lifetime watcher so a teardown race can no longer raise an unobserved `ObjectDisposedException`.
- Branding: Replaced leftover `aka.ms/PowerToys*` links on Kit's own surfaces (tray documentation menu, Quick Access docs, General elevated-help link, Awake learn-more and CLI help, Light Switch overview/learn-more, and elevated notification help) with the Kit project page.
- Cleanup: Removed stale build output and a leftover native build scratch folder; no tracked files affected.
- Docs: Updated README, README_zh, changelog, development log, sparse package version, and GPO support marker `SUPPORTED_KIT_2_0_6`.
- Tests: Updated version metadata, Monitor default, and GPO support-marker coverage for `2.0.6`.

### 2.0.5

- Version: Bumped Kit to `2.0.5`.
- UI: Aligned General settings with the newer PowerToys-main layout by grouping Run at startup and administrator state under `Startup & permissions`.
- UI: Kept Appearance & behavior focused on language, theme, system tray, and Quick Access settings, including the upstream system-tray expander icon.
- Docs: Updated README, README_zh, changelog, development log, sparse package version, and GPO support marker `SUPPORTED_KIT_2_0_5`.
- Tests: Updated static General page and version metadata coverage for the new layout and version.

### 2.0.4

- Version: Bumped Kit to `2.0.4`.
- UI: Aligned Settings startup, refresh, empty-search fallback, and Dashboard deep-link routing with the PowerToys-main Dashboard-first framework behavior.
- UI: Kept the `Overview` deep link mapped to General settings so existing Quick Access update routing continues to open the update section.
- Slimming: Removed disabled updater install/download resource strings that no longer have visible Kit UI or active installer behavior.
- Docs: Updated README, README_zh, changelog, development log, sparse package version, and GPO support marker `SUPPORTED_KIT_2_0_4`.
- Tests: Added static regression coverage for Dashboard-as-default Settings home and disabled updater install/download resource cleanup.

### 2.0.3

- Version: Bumped Kit to `2.0.3`.
- Review: Rechecked Kit's runner, Settings, common build/package, sparse package identity, and policy surfaces against the local PowerToys-main framework baseline.
- Runtime: Monitor manual scan progress now completes when the worker completion event is signaled even if the progress JSON cannot be read.
- Runtime: Monitor background scans no longer signal the manual scan completion event, preventing Settings from treating background completion as a manual scan result.
- Runtime: Monitor installer cleanup now keeps the highest-confidence installed-software match for each installer instead of reserving files by enumeration order.
- UI: Monitor VCP capability cache comparison now includes value metadata so color preset changes refresh Settings correctly.
- Docs: Updated README, README_zh, changelog, sparse package version, and GPO support marker `SUPPORTED_KIT_2_0_3`.

### 2.0.2

- Version: Bumped Kit to `2.0.2`.
- Runtime: Removed PowerDisplay from the active plugin/module surface, including runner loading, solution entries, Settings navigation, Quick Access routing, GPO projection, Settings serialization, resources, assets, and module source.
- Runtime: The active module set is now `Awake`, `Light Switch`, and `Monitor`.
- Runtime: Synced Awake and LightSwitch with the local PowerToys-main module shape while preserving Kit-only storage, IPC names, telemetry-disabled trace hooks, and updater boundaries.
- Runtime: LightSwitch now uses Kit-named toggle, manual-override, and service-stop events, stops the scheduler service when schedule mode switches to `Off`, and keeps the toggle hotkey from relaunching the scheduler process.
- Slimming: Removed the LightSwitch-to-PowerDisplay profile bridge and disabled Force Light/Force Dark custom-action plumbing after removing PowerDisplay.
- Policy: Updated Kit ADMX/ADML support markers to `SUPPORTED_KIT_2_0_2`.
- Tests: Updated static registration and compatibility coverage for the three active modules and the removed PowerDisplay boundary.

### 2.0.1

- Version: Bumped Kit to `2.0.1`.
- Runtime: Light Switch now reports the module as enabled only after its service process is created, so a failed `SearchPathW`/`CreateProcessW` launch no longer leaves the module marked enabled with tracing on.
- Runtime: Light Switch now stops its scheduler service when the schedule mode changes to `Off` instead of restarting it, signalling the service-stop event for a graceful exit before the bounded terminate fallback, and restored the `stop_worker_only`/`stop_service_if_running` lifecycle from disabled comments.
- Runtime: The Light Switch toggle hotkey now toggles the theme directly instead of relaunching a stopped scheduler service, and the now-unused `is_process_running` helper and a dead schedule-mode local were removed.
- Runtime: Quick Access now builds its launcher, coordinator, and Settings dashboard wiring without the unused runner-elevation state; the direct Light Switch and PowerDisplay toggle actions never used it, so the dead field, coordinator property, interface member, and `App.IsElevated` argument were removed.
- Refactor: Reworded the Light Switch hotkey parse error to name the active toggle-theme shortcut instead of the removed force-dark action.
- Tests: Added regression coverage for the Quick Access elevation-state removal, the Light Switch enable-after-launch ordering, the schedule-`Off` service stop, and the toggle-hotkey direct-toggle behavior.
- Refactor: Trimmed Quick Access and Settings serialization to the active four-module surface: `Awake`, `Light Switch`, `Monitor`, and `PowerDisplay`.
- Privacy: Removed managed telemetry sends, telemetry event sources, and `ManagedTelemetry` project references from the managed Settings and Quick Access layer.
- Privacy: Deleted the inactive `ManagedTelemetry` source tree and managed telemetry base file after removing all active project references.
- Privacy: Converted Awake and PowerDisplay native module trace providers into no-op compatibility hooks, matching Light Switch.
- Privacy: Converted LightSwitchService, MonitorModuleInterface, and ModuleTemplate trace sources to no-op runtime hooks and removed their telemetry include paths or `EtwTrace` references.
- Privacy: Removed the remaining `TraceBase` inheritance and telemetry include paths from active native module-interface trace headers and projects.
- Privacy: Removed the remaining no-op managed telemetry calls and event source classes from Awake and PowerDisplay.
- Privacy: Removed PowerDisplay's settings telemetry IPC event and module-interface signaling path after deleting the runner settings telemetry worker.
- Build: Active outputs now remove stale `PowerToys.ManagedTelemetry` and TraceEvent support binaries left by old build graphs.
- Slimming: Pruned `Kit.Interop` WinRT and shared IPC constants to Kit's active runtime surface, removing inactive PowerToys Run, FancyZones, Advanced Paste, CmdPal, Keyboard Manager, Mouse utilities, preview, Hosts, Workspaces, and telemetry event names.
- Runtime: Renamed the active Settings termination WinRT projection from `PowerToysRunnerTerminateSettingsEvent` to `KitRunnerTerminateSettingsEvent` while keeping the underlying Kit named event unchanged.
- Slimming: Deleted the inactive AdvancedPaste-only `LanguageModelProvider` source tree and removed its AI provider package pins, provider UI metadata/helpers, non-serialized AI enum helpers, stale Foundry Local UI string, and stale `OpenAI` third-party notice entry while preserving historical settings serialization models.
- UI: Removed Shortcut Conflict window special cases for inactive AdvancedPaste, Mouse Without Borders, Peek, and PowerToys Run settings while keeping the generic active-module conflict workflow.
- UI: Narrowed `SettingsFactory` to explicit Quick Access, LightSwitch, and PowerDisplay hotkey settings, deleting broad `IHotkeyConfig` reflection discovery and unused factory APIs from the shortcut-conflict path.
- UI: Removed the inactive MouseUtils conflict special branch from `PageViewModelBase`, so active Settings pages use the same module-name conflict path.
- Build: Replaced stale Settings package-reference comments that cited inactive CmdPal, Mouse Without Borders, and Advanced Paste with current dependency-alignment notes for the active Settings runtime closure.
- Slimming: Removed the unused Registry Preview-only `SkiaSharp.Views.WinUI` central package pin and stale third-party notice entry after confirming no Kit project references it.
- Slimming: Removed the unused Command Palette extension central package pin after confirming no Kit project references `Microsoft.CommandPalette.Extensions`.
- Slimming: Removed the unused Command Palette Adaptive Cards central package pins and stale AdaptiveCards third-party notice entries after confirming no Kit project references `AdaptiveCards.ObjectModel.WinUI3`, `AdaptiveCards.Rendering.WinUI3`, `AdaptiveCards.Templating`, or `Microsoft.Bot.AdaptiveExpressions.Core`.
- Slimming: Removed the unused Command Palette WinGet interop central package pin after confirming no Kit project references `Microsoft.WindowsPackageManager.ComInterop`.
- Slimming: Removed the unused AdvancedPaste Markdown conversion central package pins and stale ReverseMarkdown third-party notice entry after confirming no Kit project references `HtmlAgilityPack` or `ReverseMarkdown`.
- Slimming: Removed the unused PowerToys Run central package pins and stale PowerToys Run Mages third-party notice section after confirming no Kit project references `hyjiacan.pinyin4net`, `Mages`, or `UnitsNet`.
- Slimming: Removed stale PowerToys Run Wox/Window Walker and Registry Preview HexBox utility notice sections after confirming the local PowerToys-main reference uses them only for deleted Launcher/CmdPal and Registry Preview paths.
- Slimming: Removed the unused PreviewPane STL and PowerAccent central package pins after confirming no Kit project references `HelixToolkit`, `HelixToolkit.Core.Wpf`, or `UnicodeInformation`.
- Slimming: Removed the unused Command Palette toolkit and host central package pins and stale ToolGood.Words.Pinyin third-party notice section after confirming no Kit project references `Shmuelie.WinRTServer` or `ToolGood.Words.Pinyin`.
- Slimming: Removed the deleted-module-only central package pins for DSC, Workspaces/FancyZones, Peek, and PowerToys Run OneNote after confirming no Kit project references `ModernWpfUI`, `NJsonSchema`, `ScipBe.Common.Office.OneNote`, or `SharpCompress`.
- Slimming: Removed the deleted-utility central package pins for Hosts, Registry Preview, PowerToys Run, PowerAccent, and RTF conversion paths after confirming no Kit project references `CommunityToolkit.WinUI.Collections`, `CommunityToolkit.WinUI.UI.Controls.DataGrid`, `ControlzEx`, `Interop.Microsoft.Office.Interop.OneNote`, `LazyCache`, `Microsoft.Toolkit.Uwp.Notifications`, `RtfPipe`, or `WPF-UI`.
- Slimming: Removed the deleted Launcher, AI, and CmdPal central package pins after confirming no Kit project references `Microsoft.Graphics.Win2D`, `Microsoft.WindowsAppSDK.AI`, `NLog`, `NLog.Extensions.Logging`, `NLog.Schema`, `System.ClientModel`, `System.Numerics.Tensors`, or `WyHash`; `Microsoft.Data.Sqlite` remains for Monitor scan status storage, and the stale CmdPal WyHash third-party notice section was removed.
- Slimming: Deleted inactive CmdPal Calculator and File Explorer/Peek shared assets after confirming `PowerToys-main` only uses `CalculatorEngineCommon`, `FilePreviewCommon`, Monaco assets, `modulesRegistry.h`, and shell-extension registration helpers from deleted CmdPal, PreviewPane, Peek, installer, or Registry Preview paths; the unused `UTF.Unknown` package pin, stale NOTICE sections, File Explorer logger constants, and stale Awake launcher logger name were removed with them.
- Slimming: Deleted the legacy sibling Settings asset tree and inactive Settings models, source files, unit tests, assets, icons, controls, converters, and OOBE view models instead of hiding them behind project exclusions.
- Policy: Trimmed GPOWrapper and Settings GPO helper policy surface to active modules plus retained startup, update, and diagnostics policy readers.
- Policy: Trimmed ADMX/ADML policy assets to the same Kit 2.0.1 active policy surface.
- Runtime: Removed the upstream BugReportTool source and runner, tray, General, and Quick Access launch paths so Kit no longer collects inactive PowerToys module state.
- Build: Deleted the inactive standalone module_loader utility and orphaned CmdPal version props until Command Palette becomes an active Kit module.
- UI: Quick Access now uses the current WinUI SystemBackdrop API, clearing deprecated WinUIEx backdrop build warnings.
- UI: Renamed the Quick Access window title from the upstream `PowerToys Quick Access (Preview)` label to `Kit Quick Access`.
- Refactor: Reworded shared module-interface and settings-dispatch comments so runtime code no longer documents inactive AdvancedPaste or PowerToys Run special cases as current behavior.
- Refactor: Removed stale Color Picker, ImageResizer, and PowerRename third-party notice sections after confirming those deleted utility sources are not shipped by Kit.
- Refactor: Reworded PowerDisplay, Light Switch, and runner comments/logs so active runtime code no longer describes inactive CmdPal, PowerToys Runner, or telemetry behavior as current Kit behavior.
- Build: Trimmed the active sparse package manifest to the retained Settings identity entry and removed deleted PowerOCR, ImageResizer, and Command Palette app identities.
- Build: Aligned the checked-in sparse package manifest version with Kit `2.0.1`, removed stale CmdPal signing-helper defaults, kept local signing examples on Kit paths, and made signing helpers fail when no package is signed.
- Build: Local sparse package re-registration now uses the publisher-adjusted `.user/PowerToysSparse.AppxManifest.xml` generated by the package helper instead of asking developers to register the checked-in manifest directly.
- Build: Hardened signing helpers with Windows SDK `signtool` discovery, current-user certificate trust by default, opt-in machine root trust, and sparse-package-only signing unless explicit targets or all packages are requested.
- Build: Updated the fast build essentials helper so it builds Quick Access along with the runner and Settings, matching the UI executables that `Kit.exe` launches at runtime.
- Build: Hardened local build helpers so MSBuild arguments stay array-based, Visual Studio environment imports cache the resolved MSBuild path and normalize `PATH`, and local builds skip CopyOnWrite/RunVSTest SDK resolver imports by default.
- Refactor: Pruned inactive FancyZones, Hosts, Workspaces, PowerRename, Command Palette, and Screen Ruler launch targets from `UITestAutomation`; the harness now targets Kit install roots, `Kit.exe`, Kit Settings, and the four active module executables.
- Runtime: PowerDisplay runner IPC launches now bypass standalone AppInstance redirection while normal user launches keep single-instance behavior, and Settings deep links launch `Kit.exe`.
- Runtime: PowerDisplay toggles now always use the runner-owned `kit_power_display_` named pipe instead of spawning a no-argument standalone instance, buffer early pipe messages until the WinUI window exists, and retry once after restarting the owned IPC pipe when writes fail.
- Runtime: Common and PowerDisplay Settings deep links now launch only `Kit.exe`; Kit no longer falls back to an installed upstream `PowerToys.exe` from Settings or module settings links.
- Refactor: Narrowed `ModuleHelper` enabled-state, icon, label, and IPC/settings module-key behavior to the active Kit modules plus General settings while preserving historical module-key mappings only in compatibility DTOs.
- Tests: Kit UI automation cleanup is path-scoped to the current Kit output or install root, so active-module executable names such as `PowerToys.Settings.exe` no longer cause global process kills against an installed official PowerToys build.
- Build: `.slnf` local builds now honor `-RestoreOnly`, build-script default-property detection respects `/property:` overrides, direct package-signing entry points can opt into `-RequireMachineRoot`, and the shared native `version.vcxproj` uses `/FS` to avoid `Version.pdb` write races.
- Runtime: PowerDisplay pipe startup now treats `ERROR_PIPE_CONNECTED` as an already-connected client, module teardown waits for the runner-owned child process stop path to run, and standalone activation redirects use a bounded COM wait instead of an infinite wait.
- Slimming: Removed remaining inactive Keyboard Manager, File Explorer add-ons, Mouse utilities, Screen Ruler, Peek, Workspaces, and Hosts Settings resource strings that were no longer referenced by active Kit pages.
- Runtime: Settings deep links now use a Kit-only install resolver; the upstream-compatible `PowerToys.exe` resolver remains separated for copied-module compatibility helpers but is not used by Kit Settings links.
- Runtime: Runner now honors the `enable_quick_access` general setting instead of forcing Quick Access off, and periodic update toasts now respect the Settings notification toggle.
- Runtime: Quick Access rolls a module toggle back when the runner IPC update fails, keeping the UI state aligned with the actual module state.
- Runtime: Awake module destruction now signals the child process, waits for shutdown, uses a bounded terminate fallback when the signal path fails, and closes process/thread handles before the module interface is deleted.
- Slimming: Removed the inactive CmdPal package-state probe from Settings compatibility models and deleted the remaining inactive Settings resource strings plus unused VariantAssignment package pins.
- Slimming: Removed remaining inactive File Explorer Preview, Shortcut Guide activation, Screen Ruler, and ZoomIt picker resource strings from the active Settings resource file.
- Build: XAML search index generation now excludes `SearchResultsPage` and `ShortcutConflictWindow`, so generated Settings search data only points at navigable Settings pages.
- Runtime: Settings launch failures now clear the launch-in-progress guard before returning, so a missing or failed Settings process does not block later open attempts.
- Runtime: Settings launch now atomically claims the launch-in-progress guard before creating the launcher thread, keeps it held until runner/settings IPC has started and the Settings process ID is registered, and terminates a created Settings child if token or IPC setup cannot continue.
- Runtime: LightSwitch now signals a named service-stop event before its bounded terminate fallback and closes all module-owned event handles during module destruction.
- Slimming: Removed disabled LightSwitch Force Light/Force Dark UI comments, custom-action plumbing, and unused force-mode event handles so the module exposes only the active toggle path.
- Slimming: Settings command-line `set`/`get` resolution now allowlists only General plus the active `Awake`, `LightSwitch`, `Monitor`, and `PowerDisplay` settings modules, and inactive enabled-state keys such as Mouse Without Borders are rejected.
- Runtime: Light Switch and PowerDisplay are now explicit default-enabled active modules in `EnabledModules`, while Monitor remains default-off until the user enables it.
- Tests: Added regression coverage for the PowerDisplay pipe early-connect path, synchronous process-manager stop, bounded redirect wait, Kit-only deep-link resolver, richer UI automation cleanup result reporting, and the expanded inactive resource cleanup.
- Tests: Added regression coverage for Quick Access settings/IPC rollback, update-toast notification gating, Awake shutdown cleanup, CmdPal package-probe removal, sparse package helper output, signing helper defaults, inactive resource cleanup, search-index page exclusions, Settings launch guard cleanup and IPC setup failure cleanup, Settings command-line active-module allowlisting, LightSwitch service-stop lifecycle, disabled force-mode removal, and unused package pin removal.
- Docs: Updated the first-plugin development note to name all four active modules, including `PowerDisplay`.
- Build: XAML search index builder no longer carries inactive upstream module icon and panel fallbacks; active page icons are derived from Settings XAML.
- Runtime: Removed inactive Shortcut Guide Win-key tracking from the runner keyboard hook and module interface.
- Runtime: Removed the no-op keyboard hook window registration after deleting pressed-key timers.
- Privacy: Deleted the inactive settings telemetry worker source and runner project filters.
- Tests: Deleted the inactive Settings UI test project that still targeted removed OOBE and PowerToys surfaces.
- Build: Deleted the inactive DSC source tree and manifest generation script after removing DSC projects from `Kit.slnx`.
- Build: Deleted the DSC-only Settings `setAdditional` command-line entry point after removing DSC generation.
- Slimming: Deleted inactive Settings UI resource strings for removed module pages and OOBE surfaces.
- Runtime: Removed disabled OOBE/SCOOBE launch flag plumbing from the runner and Settings entry point.
- Slimming: Removed unused OOBE/SCOOBE SettingsAPI state helpers, backup rules, residual resources, and XAML styles.
- Slimming: Pruned backup/restore defaults to the active Kit settings surface by deleting inactive Keyboard Manager, FancyZones, Workspaces, PowerToys Run restore rules, and the PowerToys Run plugin fix-up code path.
- Build: Settings and Quick Access now remove stale inactive Settings assets from the shared WinUI output, and Quick Access copies only active Settings icons.
- Tests: Added regression coverage for the deleted legacy Settings asset copy and ADMX/ADML policy assets.
- Tests: Added regression coverage for active-module Quick Access boundaries, deleted inactive settings surfaces, GPO policy trimming, BugReportTool removal, stale output cleanup, telemetry-free managed app projects, active managed modules without telemetry sends, deleted managed telemetry source, active native module no-op trace providers, telemetry-free build targets and headers, ModuleTemplate no-op trace defaults, Awake README telemetry-free documentation, PowerDisplay's removed settings telemetry IPC, the trimmed `Kit.Interop` IPC constant surface, the Kit-named Settings termination projection, deleted AdvancedPaste AI provider source/package/UI/enum helper remnants, removed Shortcut Conflict inactive-module special cases, the explicit SettingsFactory hotkey boundary, the removed inactive MouseUtils page conflict branch, Settings package-reference comment cleanup, Registry Preview-only SkiaSharp package pin removal, Command Palette extension package pin removal, Command Palette Adaptive Cards package pin removal, Command Palette WinGet interop package pin removal, AdvancedPaste Markdown conversion package pin removal, PowerToys Run package pin removal, deleted PowerToys Run and Registry Preview utility notice sections, PreviewPane STL and PowerAccent package pin removal, Command Palette toolkit and host package pin removal, deleted-module package pin removal, deleted-utility package pin removal, deleted Launcher/AI/CmdPal package pin removal, deleted Preview/Peek/CmdPal shared assets, deleted utility NOTICE sections, current Kit runtime wording, and the sparse package active app identity boundary.
- Tests: Added regression coverage for Kit UI-test launch targets, path-scoped cleanup, active-module module keys, common and PowerDisplay `Kit.exe` settings links, PowerDisplay runner IPC single-instancing, early pipe-message buffering, pipe-write retry, and build/signing helper stability defaults.

### 1.2.0

- Version: Bumped Kit to `1.2.0`.
- Updates: Hardened the check-only Kit release scheduler so future-dated last-check values trigger a fresh check instead of causing a tight background loop.
- Docs: Kept README version metadata and changelog release notes aligned with the source version.
- Tests: Updated version metadata coverage for the README-to-changelog documentation split.

### 1.1.6

- General: Restored the PowerToys-main-style version/update section at the top of General while keeping Kit's updater boundary check-only.
- General: Moved update result messaging below the version/update expander so the in-progress "Checking for updates" row follows the upstream layout.
- General: Removed the bottom About card because the version is already shown in the update section.
- Updates: Kept Kit release links on `https://github.com/guijianchou/Kit/releases` and kept automatic download/install actions hidden.
- Tests: Updated version metadata coverage for `1.1.6` and added regression checks for the cleaned General update/About layout.

### 1.1.5

- Updates: Reworked release checking back onto the upstream `UpdateState.json` boundary: the runner checks GitHub and writes state, while Settings watches/reloads that state.
- Settings: Kept manual checks in "Checking for updates" until the watched update-state file reports a newer result or the request times out, preventing cached update state from replacing an in-flight check.
- Settings: Disabled repeated Check for updates clicks while a check is running and kept the release link visible only when a newer release is available.
- Build: Made the shared update-state storage compile cleanly in the runner without pulling in the full updater project.
- Tests: Added regression coverage for the upstream-style update-state boundary, cached-state race protection, and `1.1.5` README/version/development-log metadata.

### 1.1.4

- Updates: Forced GitHub release checks to bypass HTTP cache so offline manual checks cannot reuse stale cached responses and report "up to date".
- Settings: Prevented stale cached "up to date" state from replacing an in-flight manual check result.
- Tests: Added regression coverage for no-cache release checks and `1.1.4` README/version/development-log metadata.

### 1.1.3

- General: Added an About GitHub repository link and a manual check-for-updates entry point aligned with the version text.
- Updates: Added a check-only GitHub release check against `https://github.com/guijianchou/Kit/releases`, with a daily background check and toast only when a newer release is available.
- Updates: Kept Kit's updater boundary check-only; it does not automatically download, install, or launch an updater.
- Settings: Increased the About version and repository text size from caption text to body text.
- Tests: Added regression coverage for the Kit release-check IPC path, About feedback state, and `1.1.3` README/version metadata.

### 1.1.2

- Startup: Reduced startup and first-frame work by reusing the already-loaded general settings object for initial module enablement instead of reading settings twice.
- Startup: Removed inactive OOBE/SCOOBE version-state reads and writes from Kit runner startup.
- Tray: Stopped reading `UpdateState.json` during tray initialization while keeping the update-badge API available for any future explicit updater-state integration.
- Settings: Deferred General page diagnostic cleanup, backup dry-run refresh, and search-index construction until after the first frame.
- Home: Hid Monitor's status-only activation rows from the Home Shortcuts card so Monitor no longer appears as a shortcut-only module, while it remains available in the module list, Settings page, and Quick Access settings fallback.
- Tests: Added regression coverage for the startup/load optimization boundary, Monitor Home Shortcuts filtering, and updated version metadata checks for `1.1.2`.

### 1.1.1

- Build: Aligned the Kit Settings/Common UI build layer with the local PowerToys-main .NET 10 baseline, including shared CsWinRT target framework, Quick Access, Settings UI Controls, Common UI Controls, UITestAutomation, and central package pins.
- Build scripts and developer docs now reference the .NET 10 target framework for Settings publishing and PowerToys Run plugin checklist guidance.
- Settings: Added regression coverage so the .NET 10 build layer, README version metadata, and Kit's disabled updater/telemetry boundaries do not silently drift.
- Updater boundary: Kit keeps system tray update-badge rendering for an existing Kit update state, but automatic update checks, downloads, update launches, and telemetry remain disabled.

### 1.1.0

- Imported PowerDisplay into the active Kit module set with runner loading, solution build entries, Settings navigation, Dashboard metadata, Quick Access actions, serialization, and LightSwitch profile routing.
- Settings: Multiple UI and usability improvements across different utilities.
- General: Streamlined default module states so new installations start with a lighter initial experience.
- System tray icon: Updated the monochrome PowerToys system tray icon and retained update-badge rendering for an existing Kit update state; automatic update checks and downloads remain disabled.
- PowerDisplay now uses Kit app-data paths and Kit-prefixed runtime events so it does not share state or named events with an installed official PowerToys build.

### 1.0.4

- Monitor Scan Now now follows worker-reported progress from `%LOCALAPPDATA%\Kit\Monitor\scan-progress.json` and the named scan-completed event instead of relying on a Settings-local progress timer.
- Monitor clears stale manual-scan progress before each Scan Now request so the Settings page cannot reuse an old completed or temporary progress state.
- Monitor worker writes progress snapshots from the scan pipeline, including phase, processed/total file counts, completion time, and final record count.
- Monitor module interface now resolves the worker from the module output directory and falls back to `dotnet.exe "PowerToys.Monitor.dll"` when the Debug output has no apphost `PowerToys.Monitor.exe`.
- Added regression coverage for Monitor progress file reporting, Settings progress consumption, and the module-interface worker launch fallback.

### 1.0.3

- Release builds prune native link artifacts (`*.lib`, `*.exp`, and static-library analysis markers) from the runtime output after `Kit.exe` builds.
- Release builds remove non-English runtime satellite folders and inactive AI model-provider artifacts from the active Kit output, matching the managed satellite trim.
- Added `tools\build\clean-stale-versions.ps1` for explicit cleanup of old versioned output folders while preserving the active version, `Debug`, and `Release`.
- Added `tools\build\verify-runtime-artifacts.ps1` to check versioned or `Release` outputs for link artifacts, PDBs, Foundry assets, and non-English locale folders.
- Removed unused WPF/WinForms dependencies from `Common.UI` so Settings and Quick Access do not pull WPF runtime assemblies through that shared library.
- Deleted inactive Settings module source/XAML files instead of keeping them hidden behind `Compile Remove` and `Page Remove` rules.
- Trimmed inactive common, DSC, and unused Awake service projects from `Kit.slnx` while keeping `Common.Search` because Settings search still uses it.
- Quick Access now opens a module's Settings page when a visible tile has no direct launcher action, including Monitor.

---

## 中文

## 更新日志

### 2.2.1

- **WinUI 3 页面崩溃修复与导航稳定性提升**：
  - **常规设置页（`GeneralPage.xaml`）**：修复页面加载时 `Resources.resw` 中的 `AiHub_MainEndpoint_ApiKeyCard.PlaceholderText` 误将属性应用在父级 `SettingsCard`（控件无此属性）上引发的致命 `XamlParseException` / `0xC000027B` 崩溃。为子级 `PasswordBox` 赋予专用 `x:Uid="AiHub_MainEndpoint_ApiKeyBox"` 并将 ViewModel 初始化提升至 `InitializeComponent()` 之前。
  - **UDP 探测测试页（`UDPtestPage.xaml` & `UDPtestPage.xaml.cs`）**：
    - 修复因 `Resources.resw` 中 `UDPtest_ClearHistoryButton.Text` 尝试向 `Button` 反射设置不存在的 `Text` 属性导致的崩溃，纠正为 WinUI 附加属性语法 `[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`。
    - 修复直接索引 `Application.Current.Resources["AccentButtonStyle"]` 抛出 `COMException: Element not found` 崩溃问题（WinUI 3 内置系统样式不在 `Application.Current.Resources` 根字典中），通过在页面局部资源显式派生 `AccentHealthButtonStyle` 并通过 `TryGetValue` 强化空安全防御。
    - 引入 `ThemeBrushHelper` 安全笔刷助手，在 `UDPtestViewModel.cs`、`UDPtestLineRowViewModel.cs` 与 `RecentProbeBarViewModel.cs` 中提供安全的 `TryGetValue` 与 Fluent 回退笔刷，彻底消除深浅主题切换与初始化时的笔刷索引崩溃。
  - **AI 智能中心页（`AIHubPage.xaml` & `AIHubPage.xaml.cs`）**：
    - 修复编译型绑定 `AIHubPage_obj1_Bindings.Update_ViewModel_HealthScoreGrade` 中 `SolidColorBrush` 无法转换为 `Windows.UI.Color` 的 `InvalidCastException` 崩溃，将 `SeverityToBrushConverter` 直接绑定至 `Ellipse.Fill` 笔刷属性。
    - 将 ViewModel 初始化提升至 `InitializeComponent()` 之前，确保在首次 XAML 布局遍历与绑定求值时已存在有效实例。
- **故障诊断与崩溃日志基础设施**：
  - 在 `App.xaml.cs` 中集成同步 First-Chance 与 Unhandled Exception 异常捕获机制，自动输出完整堆栈、时间戳和线程 ID 至 `%LOCALAPPDATA%\Kit\crash.log`，实现事后秒级精准定界。
- **开发文档与插件规范优化**：
  - 在 `PLUGIN_DEVELOPMENT.md` 中补充 WinUI 3 页面生命周期规范、`x:Uid` 本地化强类型属性对齐规则、附加属性声明语法以及利用 `ThemeBrushHelper` 安全获取主题笔刷的最佳实践。

### 2.2.0

- **AI Hub 安全审计界面重构与策略管线架构升级**：
  - 重构分层安全策略体系：全局安全策略（`%LOCALAPPDATA%\Kit\AiHub\security.md`）与特定任务约束策略（`chains/{task}/AGENTS.md`），在 `SecurityPolicyService.cs` 中增加自动缺省模板脚手架，防范 I/O 崩溃并在常规设置中提供专属 Expander 配置卡片。
  - 正式形成任务策略执行管线规范：`Codex / Pi → 安全策略 + AGENTS.md → 审计摘要 → 建议动作`。
  - 深度还原并美化 `AIHubPage.xaml` 安全审计界面：健康状况概览环、A-F 级健康评级徽章、风险严重等级分布条、审计活动指标、首要建议操作横幅，以及详情弹窗 `FindingDetailsDialog.xaml`。
  - 修复点击审计详情弹窗时的 WinUI 3 崩溃：在 `SeverityToBrushConverter.cs` 中修正类型映射，强制返回 `SolidColorBrush` 笔刷，杜绝底层 XAML 绑定抛出 `E_NOINTERFACE`。
  - 审计结果动态多语种格式化：检测事实、根因分析和处置建议（`PriorityActionText`）根据运行时系统语言（`zh-CN` 与 `en-US`）动态呈现对应语言。
  - 恢复 AI Hub 原项目浅色紫色图标（`#A756FF`），统一侧边栏导航与模块资产视觉质感。
- **Kit 主框架与插件 100% 纯正中英双语对齐**：
  - 全面清除主界面与插件中的中英混杂小括号与斜杠（如 `(保持唤醒)` -> `保持唤醒`、`启用 Awake` -> `启用保持唤醒`、`启用 UDP Test` -> `启用网络探测`、`启用 AI Hub` -> `启用 AI 智能中心`、`Essential Policy (全局任务策略)` -> `基础任务策略`）。
  - 全面汉化 Localserver 监控卡片（40 余处全大写英文字段标签均替换为纯中文）、UDPtest 表头及元数据条纯中文、侧边栏导航、仪表盘、快捷键冲突与分配弹窗、托盘快速访问浮窗。
  - 完整汉化 `FindingDetailsDialog.xaml.cs` 内部拼接文案（事件 ID、影响范围、严重级别、时间戳及格式化报告输出）。
- **自动化测试套件与质量验证**：
  - `Kit.Settings.csproj`：0 警告、0 错误。
  - `Kit.AiHub.UnitTests.csproj`：108 通过，0 失败，1 跳过。
  - `Settings.UI.UnitTests.dll`：196 通过，0 失败。

### 2.1.1

- 深度对齐 Localserver 与 UDPtest 状态遥测条 UI/UX，完美还原原项目质感：
  - 调整状态微条宽度为 5px、圆角为 2px，彻底解决 3px 竖条过于细长针状与过密视觉问题。
  - 保持 2px 间距与 7px 节拍，呈现饱满、清晰、比例协调的药丸形竖柱时序条。
  - 对齐柱高层级（16px 全高，UDPtest 告警 11px、失败 6px），高度与原项目（`LocalServerHub.App`、`NetworkMonitor.App`）完全一致。
  - 扩充历史样本缓冲区至 240 条，检测到运行中服务时自动预填已有时序点，首尾边界精准对齐且满格无缝。

### 2.1.0

- 新增 UDPtest 模块及设置页：
  - 高性能网络探测引擎，支持 TCP HTTPS 延迟探测、UDP Echo 回显、STUN 绑定检测及 NAT 类型发现。
  - 交互式健康状态看板、节点验证（Node Verified）与历史探针条（容量倍增至 60 条）。
- 全面对齐插件主开关生命周期与原生（Awake / LightSwitch）规范：
  - 修复 GPO 规则判定，杜绝未配置策略模块出现 “This setting is managed by your organization” 的误报与锁死。
  - 主开关关闭时级联停用所有子链路与服务：Localserver 同步调用 `StopAllLinesAsync()` 停止全部子服务；UDPtest 同步调用 `StopAsync()` 停止所有探针 Worker 并挂起后台网络身份探测。
  - 主开关关闭时全局应用 WinUI 3 标准 `0.38` 暗化与置灰，状态指示灯与健康环切为中性禁用灰态（零亮绿灯、零后台活动）。
- 状态进度条与节拍优化：
  - Localserver：遥测节拍严格对齐为 1s/次，状态竖条高度增加 1/5 至 20px（容器增至 24px）。
  - UDPtest：状态条历史容量翻倍扩充至 60 条，竖条高度增至 20px（失败为 8px）。
- 同步更新主目录 `PLUGIN_DEVELOPMENT.md` 规范文档。

### 2.0.23

- 重新设计常规设置中“AI 服务”的 UI/UX：统一采用原生 PowerToys SettingsExpander 与 SettingsCard 排版，使内核与端点子选项层级与原生设置规范一致。
- 精简主/备用端点摘要显示，仅保留模型名称与思考强度（Model · Effort，如 gpt-5.6-luna · medium），去除冗余的小字备注与标签。
- 彻底移除 Localserver 插件中的 AI 分析卡片、ViewModel、服务类与 Prompt 链路依赖，保持 Localserver 纯净。
- 参考 PowerToys 日志系统在 Kit 常规设置中增加集中式“诊断与日志”管理：提供主日志开关、日志级别选择（trace/debug/info/warn/error/critical/off）及一键打开日志文件夹按钮，并同步联动 ManagedCommon.Logger 与 %LOCALAPPDATA%\Kit\log_settings.json。
- 完善单元测试覆盖并校验通过。

### 2.0.22

- 重构 AI Hub 设置为常规设置中的就地紧凑布局，彻底移除多余的子页面跳转与返回面包屑按钮。
- 增加总开关折叠/展开联动：AI Hub 未启用时隐藏全部详细配置，开启时就地展开。
- 增加 AI 执行内核（Codex CLI / Pi CLI）切换确认按钮 [应用]，避免误触。
- 对齐原项目紧凑接口端点（主/备用）与全局安全策略（security.md）配置面板，提供完整中英双语支持。

### 2.0.21

- 将 AI Hub 收入 Kit 设置，移除独立侧栏入口和首页插件卡片，全局启用开关只保留在 AI Hub 设置页。
- 增加双语设置入口和返回链接；直接打开或从 Localserver 跳转 AI Hub 时，侧栏保持“设置”选中。

### 2.0.20

- 修复 Kit 更名后遗漏的 SettingsAPI 命名空间、Awake WinRT 投影、Settings/Quick Access 启动路径和 PRI 资源加载。
- 恢复与本地上游一致的原生接口虚表槽位，保留旧工厂入口回退，加固 DLL 加载和配置缓冲区读取。
- 搜索索引改为首次查询时加载；没有快捷键时跳过键盘钩子；两种兼容 IPC 字段共用一次模块配置序列化。
- 修复 Localserver 优雅停止超时和健康检查期间取消的进程回收；关闭前异步保存待提交配置，有活跃服务时隐藏设置窗口并保留宿主和输出管道。
- 日志预览改为有界定时刷新，修正 Kit 日志目录入口、共享开关刷新及硬件状态双语资源。
- LightSwitch 配置与运行状态改为加锁快照；同步原生模块模板、插件开发指南和可复现的 Debug 交付校验脚本。
- 验证覆盖托管/原生回归和 AI Hub Native AOT 冒烟；Windows 自动化无法绑定 Kit 窗口，完整 WinUI 视觉交互仍待实际反馈。

### 2.0.19

- 版本：Kit 升级到 `2.0.19`。
- Localserver 状态卡片排版与视觉体验优化：
  - 修复 HOST 标签因列宽过小折行变为“HOS\nT”的排版缺陷，调整列宽为 `Auto,*` 并将操作系统版号与 Build 编号纵向优雅堆叠。
  - 硬件信息展示对齐原版 `LocalServerHub`：CPU 从原始环境变量识别符精简为友好型号（如 `Ryzen 5 5600H · 12 logical cores`），GPU 显示精简型号（如 `RTX 3050 Ti`）与规范驱动/设备信息，系统版号精简为 `Windows 11 LTSC` 与 `Build 26100.9168`。
  - 环境自检文本对比度与渲染优化：`All environment checks passed` 绑定独立 `EnvSummaryBrush`，通过时呈现高对比度绿色（`SystemFillColorSuccessBrush`）粗体，彻底杜绝浅灰不可见问题。
  - 健康环呼吸感提升：健康环中心居中展示清晰的健康状态文本（如 `IDLE` / `STOPPED`），移除拥挤的重叠图标与静态“HEALTH”标签。
  - 路径单行显示：为命令行与工作目录添加 `NoWrap` 与单行文本省略，附带鼠标浮动提示。
  - 关键指标层级强化：引入 `MetricPrimaryStyle` 与 `MetricCompactStyle` 等宽粗体字形，大幅突出 PORT、PID、REGISTERED、RUNNING、FAULTED 数值。
- LightSwitch 主题手动与夜间自动切换缺陷修复：
  - 修复 `DetectAndHandleExternalThemeChange` 中的重大缺陷：外部变更检测由比对当前系统与认知状态改为直接比对 `shouldBeLight`，导致夜间到达时误判为外部变更而死锁在 manual override，彻底修复夜间无法自动切换到深色主题的问题。
  - 修复手动切换状态机逻辑：手动切换智能判定是否脱离或回归计划，杜绝状态拉扯与立即自动复原。
  - 修复日落日出模式下未配置有效经纬度时边界退化为 `0,0` 导致全天恒为浅色（Light）的缺陷，无缝回退至默认配置时间。
  - 统一 `ToggleTheme` 目标主题，确保系统主题与应用主题同步切换。

### 2.0.18

- 版本：Kit 升级到 `2.0.18`。
- 彻底移除快捷键功能：从主页 Dashboard（`DashboardPage.xaml`、`DashboardViewModel.cs`）中彻底移除 Shortcuts（快捷键）卡片及刷新逻辑，从根源上解决因集合刷新重绘导致的主页图标持续闪烁问题。
- 设置界面与模块清理：移除 `LightSwitchPage.xaml` 与 `LightSwitchViewModel.cs` 中的快捷键设置分组、卡片与绑定；从 `LightSwitchProperties.cs` 与 `LightSwitchSettings.cs` 中清理 `ToggleThemeHotkey` 属性。
- 底层 C++ 接口精简：从 `LightSwitchModuleInterface/dllmain.cpp` 中完全移除热键配置读取、注册接口（`get_hotkeys`）与消息响应接口（`on_hotkey`），不再向系统注册全局底层键盘钩子。
- 快捷入口清理：优化 `QuickAccessViewModel.cs` 提示文本逻辑，移除已废弃的热键属性读取。

### 2.0.17

- 版本：Kit 升级到 `2.0.17`。
- 工具栏精简与自动保存：移除服务工具栏中冗余的“Save”与“Reload”按钮；新增配置自动保存逻辑：点击展开抽屉配置完毕折叠收起时自动持久化到 `services.json`，修改参数时防抖自动保存，离开页面时自动保存。
- 添加链路体验优化：点击“Add Line”后自动展开该链路的配置抽屉，方便用户直接录入或调整参数。
- 表格列比例与对齐统一：重构服务表格为统一的 8 列比例网格（`NAME` 1.8*、`PORT` 0.7*、`STATE` 1.1*、`UPTIME` 0.9*、`CPU` 0.6*、`MEM` 0.8*、`PID` 0.7*、操作栏 110px），彻底消除 `NAME` 栏过宽导致的空白断层。
- 状态栏竖条对齐原项目：Row 1 中 `Status: ● [State]` 固定在 Col 0（`NAME` 正下方），竖条状态栏（`RecentBars`）起始位置精确对齐 Col 1（`PORT` 端口正下方）并向右延伸至 `PID`，与原项目设计保持完全一致。
- 停止链路响应优化：修复 `IsOn` 属性在 `Stopping` 状态下仍为 `true` 导致开关反弹的问题，开关点击后立即显示关闭并防重入；优化 `ServiceRunner.StopCoreAsync()` 终止逻辑，无 GUI 窗口时快速升级进程销毁，将停止延迟从 5-10 秒降低至 1 秒以内。

### 2.0.16

- 版本：Kit 升级到 `2.0.16`。
- 主导航栏紧凑化：主程序侧边栏未隐藏时宽度优化为 `OpenPaneLength="176"`（比 132 拓宽三分之一），既保证主界面空间充裕，又彻底避免图标文字被遮挡。
- 语言统一规范：严格遵循 Kit 原生仅支持中英双语规范，彻底清除所有双语混合斜杠（中/En），实现 `en-us` 与 `zh-CN` 资源字典 100% 键值对齐与地道表述。
- 服务链路 UI/UX 全面重构：严格重构服务列表为高密度表格样式，集成最近状态色块条（`RecentBars`）、运行中锁定标志（`🔒`，停止后方可修改参数）以及 Targets 风格的展开配置抽屉。
- 链路配置精简与下沉：移除链路手动上下排序按钮与“开机自启”开关；将“删除链路”入口下沉至当前链路展开抽屉底部并配备防误触二次确认。
- 诊断看板与日志抽屉：顶部看板融合健康圆环（HealthRing）、60 秒 CPU 趋势图、系统硬件与会话统计；底部常驻可折叠输出日志抽屉（支持跟随滚动、日志目录直达与清空）。
- 稳定性修复：修复 Localserver 页面中由于部分 `TextBlock` 绑定了包含 `.Content` 的 `x:Uid` 导致的 `XamlParseException` 崩溃问题。

### 2.0.15

- Localserver 界面：Health（健康状态）与 System（系统资源）恢复到服务清单上方常驻；内容区达到 720 DIP 时并列，较窄时纵向排列。移除顶部介绍图，减少留白。
- 日志：删除 Localserver 日志面板及 UI 刷新循环。已脱敏的服务输出经有界后台队列批量接入 Kit 现有 Settings Logger，保留服务 Id、流类型、时间与序号；过载和写入失败均计数，释放不阻塞 UI，进程退出等待有明确上限。
- 语言：页面文案、服务状态、环境检查与操作反馈复用 Kit 中英文资源；运行状态与可用操作不再依赖翻译后的文本判断。
- 稳定性：保存保留配置来源与并发校验，修复跨文件重排后连续保存的误冲突；复用秘密值加密迁移，显式空服务目录保持为空。强制终止和释放端口增加确认与进程身份核验；服务链接复用变量展开与实际端口。
- 生命周期与规范：离页暂停硬件采样，刷新防重入，缓存管理状态在 Settings 真正关闭时释放。开发规范与中英文 README 补齐三个活动模块、日志目录、布局要求，以及当前由 Settings 承载管理的生命周期边界。

### 2.0.14

- 版本：Kit 升级到 `2.0.14`。
- 原生插件化迁移：将原本地服务链路管理器完整迁移并重构为 Kit 原生内置插件 `Localserver`，严格遵循插件开发规范（`PLUGIN_DEVELOPMENT.md`），源码归置于 `src/modules/Localserver/`。
- WinUI 3 + Mica 统一架构：彻底放弃独立应用窗口模式，以单页形式原生嵌入 Kit 主设置界面（`LocalserverPage.xaml`），完美复用 WinUI 3 + Mica Alt 设计语言。
- 诊断与硬件监控看板：打造区域 1 双卡片诊断区，包含 Environment 环境依赖项检测（支持交互式芯片命令复制与一键释放被占端口）与 System 硬件监控（GPU 核心利用率 / 显存占用 / 核心温度指标、宿主 CPU & 内存占用仪表盘及 60 秒历史 CPU 曲线 Canvas）。
- 服务链路管理 (Target -> Line)：将目标服务链路统一命名为 `Line`。主标题行融合状态指示点、运行时长、端口、PID、资源占用率、快捷打开链接与单链路启用开关；实现严格的运行期配置只读锁定（`IsEnabled="{x:Bind IsEditable}"`）；将删除链路入口下沉至“展开详细配置”底部并加入二次确认浮窗。
- 历史冗余精简：彻底移除“Last Error”文本标签、彻底移除“链路隐藏（主界面显示）”开关、彻底移除单链路“开机/启动自启”开关。运行时数据严格隔离于 `%LOCALAPPDATA%\Kit\Localserver\`。

### 2.0.13

- 版本：Kit 升级到 `2.0.13`。
- 生命周期与 PowerToys 规范对齐：严格沿用 PowerToys 原生进程模型。主窗口关闭（`Window_Closed`）仅关闭设置前端界面，Runner 保持在后台托盘常驻守护，确保 Awake（防息屏）、LightSwitch（快捷键监听）等后台服务持续有效；彻底退出完全交由系统托盘右键“退出”处理并干净退出。
- 冷启动直接拉起修复：彻底修复了冷启动点击 `Kit.exe` 不弹出界面、只拉起 `kit.runner` 且需要点击第二次才启动的问题。交互式双击 `Kit.exe` 时默认启用开窗逻辑，无论此前后台是否有 Runner，均保证在第 1 次点击时立即拉起并展示主程序设置界面。
- 开机自启参数化：在计划任务注册中添加 `--autorun` 参数，确保系统开机启动时保持静默常驻系统托盘，不弹窗打扰用户。

### 2.0.12

- 版本：Kit 升级到 `2.0.12`。
- 启动与计划任务：修复 `is_auto_start_task_active_for_this_user()` 在 `\Kit` 计划任务文件夹尚未创建时误触发 `ExitOnFailure` 的问题；将其作为正常未启用状态（`S_FALSE`）处理，彻底消除每次打开设置时的虚假 `[error] ITaskFolder doesn't exist: -7ff8fffe` 错误日志。
- Settings 与 Runner 进程生命周期：将 `killrunner` IPC 处理程序从阻塞式的 `SendMessageW` 改为非阻塞的 `PostMessageW(..., WM_CLOSE, ...)`。在 Settings `Window_Closed` 事件中补齐 IPC `killrunner` 与托盘窗口消息的双通道退出兜底，确保无论窗口句柄查找状态如何，Runner 进程均可平稳退出且无孤儿残留。
- 打包与验证脚本：修复 `Stage-Debug.ps1` 在计算哈希列表总字节数时的属性管道问题与对象类型转换；同步适配 `2.0.12` 交付目录与清单校验。
- 测试与运行时质量：全套单元测试与集成测试 100% 通过（680/680）。实测 Runner 启动耗时收敛至 18ms，退出无后台孤儿残留。

### 2.0.11

- 版本：Kit 升级到 `2.0.11`。
- Settings：串行处理启动、退出、重启与 IPC 清理。正常关闭异步通知 Runner，重启等待旧 Settings 进程退出后再交接，启动失败不再导致 Runner 退出。
- LightSwitch：Worker 生命周期改为事件驱动。Off 仅保留手动操作，不启动 Worker；重复启用不重复创建进程，Worker 崩溃后的首次操作恢复调度。保留提前停止信号、校验父进程，并让设置 debounce 可取消。
- Awake：单实例 mutex 隔离为 `Local\Kit.Awake`，进程查询限定 Kit 路径与会话，支持与官方 PowerToys 并存。
- Quick Access：首次使用时才启动进程，启动工作移出键盘钩子；合并重复请求，停用时取消排队任务。
- 启动与隐私：移除后台版本检查、重试、更新 toast 及无关旧启动清理，保留手动版本检查。GPO 兼容入口统一返回 `not_configured`；自启操作只针对确认属于 Kit 的任务，配置未变时不重复注册。
- 依赖：移除 Monitor 遗留且已无用途的 `Microsoft.Data.Sqlite` 引用、中央版本 pin 和对应 notice。
- 插件开发：补全根目录规范中的兼容边界、注册、Logo 规格与路径、数据隔离、生命周期和 WinUI 3 + Mica Alt 要求；同步模板源码、ZIP 元数据、依赖与编译覆盖，纠正 README 构建输出说明。
- 升版前验证基线：`Settings.UI.UnitTests` 通过 200/200，完整 x64 Debug 构建 0 警告、0 错误；LightSwitch 隔离 smoke 通过，包括真实 Worker 提前停止与无效父进程检查，主题值未改变且原设置已恢复。最终 `2.0.11` 复测结果待补；WinUI 交互验收与启动耗时测量仍待完成。

### 2.0.10

- 版本：Kit 升级到 `2.0.10`。
- UI/UX 移除冗余退出按钮：彻底移除侧边栏底部的“退出 Kit”导航项与确认弹窗；完全统一采用窗口右上角标准关闭按钮（'X'）管理窗口与退出生命周期。
- 进程生命周期与退出修复：彻底解决关闭主程序窗口后后台 `kit.runner` (`Kit.exe`) 依旧残留运行的 bug。主窗口关闭时协同联动退出 Settings 与后台 Runner（通过 IPC `killrunner` 指令、托盘窗口消息兜底以及 Runner 端的子进程退出看门狗自动联动），确保无后台孤儿进程。
- 语言设置与崩溃修复：彻底修复切换语言时程序崩溃问题。将语言项精简收敛为仅支持中文（简体）与英语（默认），加入健全的越界防御、独立的 `%LOCALAPPDATA%\Kit\language.json` 本地持久化，并在独立模式下实现自动平滑自重启。
- 本地化对齐：补全原生 WinUI 3 MRT Core 简体中文资源文件（`Strings/zh-CN/Resources.resw`），与英文键值 1:1 完整对齐，彻底消除 PRI263 构建告警，并彻底剔除多余的遗留语言字符串。
- UI/UX 控件尺寸对齐：将 LightSwitch 等插件的模式下拉选择框与时间选择器左右宽度统一对齐为上游 PowerToys 标准宽度（`SettingActionControlMinWidth` = 240px），确保排版整齐规范。
- UI/UX 卡片与 Mica 质感调优：参考 `Locals` 现代 WinUI 3 设计语言，进一步优化卡片半透明度（浅色 `#80FFFFFF` 50% 填充、深色 `#0AFFFFFF` 4% 填充及细致边缘描边），彻底消除小窗口和子项白斑突兀问题，使整体视觉和谐统一。
- 质量与产物归档：全量单元测试（186/186）持续 100% 通过；全量产物完整归档至 `bin/debug/2.0.10/`。

### 2.0.9

- 版本：Kit 升级到 `2.0.9`。
- UI/UX 侧栏导航重构：采用 WinUI 3 标准 `LeftCompact` 紧凑模式；点击折叠后平滑收缩为 48px 图标条，每个模块保持独立图标显示，点击顶部汉堡按钮平滑展开。
- UI/UX 默认展开对齐：恢复侧边栏默认处于展开状态，沿用 PowerToys 原生展示风格；移除左下角与顶部重复的多余缩放图标，仅保留顶部控制按钮。
- UI/UX 设置按钮交互修复：将左下角“通用设置”项迁移至标准的 `<NavigationView.FooterMenuItems>`，并在 `ShellViewModel` 路由中补充项解析，解决点击底部设置无反应的问题。
- UI/UX Mica Alt 现代视觉质感：全面升级为 WinUI 3 + Mica Alt 材质背景（对齐 `Locals` 视觉规范），优化整体窗口层级与通透感。
- UI/UX 卡片色彩与重影优化：深度定制 Mica Alt 下的卡片背景色，使用半透明柔和笔刷（浅色主题 `#90FFFFFF`、深色主题 `#0DFFFFFF`、描边 `#1F000000`），彻底消除小窗口与卡片过白刺眼的问题；修复 `Card.xaml` 原语中内外容器双层绘制边框与背景导致的边缘发白与重影。
- 零遥测基线合规审计：全量审计 Awake 与 LightSwitch 模块；彻底清除意外引入的上游 ETW 遥测 Provider (`TraceLoggingWrite`)，恢复纯粹的内联 no-op 桩 (`trace.h/cpp`)，严格杜绝任何遥测事件泄漏。
- 优雅生命周期与孤儿进程消除：恢复 Worker 进程的 PID 心跳看门狗与命名退出事件（Named Exit Event），Runner 退出或禁用时限时等待优雅退出，超时兜底终止，彻底避免后台僵尸服务残留。
- 模块性能与解耦：LightSwitch 调度模式置为 Off 时彻底终止服务进程，快捷键切主题不误拉起调度服务，且彻底解耦上游 PowerDisplay。
- 插件开发权威文档：在仓库根目录正式发布 `PLUGIN_DEVELOPMENT.md`，基于 upstream PowerToys 与 Kit 生产级源码详尽解析双层进程模型、C++ `PowertoyModuleIface` 契约、零遥测规范、进程看门狗、WinUI 3 卡片前端与 Runner 注册规范。
- 质量与回归测试：全量单元测试 `Settings.UI.UnitTests` 保持 186/186（100%）全部通过。
- 产物规范整理：构建并严格按规范同步全套产物至 `bin/debug/2.0.9/`。

### 2.0.8

- 版本：Kit 升级到 `2.0.8`。
- 轻量瘦身：彻底移除 `Monitor` 模块，将活动模块收敛为专注高频实用的 `Awake` 与 `LightSwitch`。
- 运行隔离：加固 LightSwitch 与 Awake 的 `%LOCALAPPDATA%\Kit\` 独立存储目录与 IPC 通道。
- 产物规范：建立 `bin/debug/2.0.8/` 版本化构建输出目录体系。

### 2.0.7


- 版本：Kit 升级到 `2.0.7`。
- Monitor：在长时间目录枚举期间持续写入 hashing 进度心跳，避免最终文件数尚未计算出来时被误判为手动扫描无进度。
- Monitor：scanner、organizer 和 installer cleaner 改为流式文件/目录枚举，大型 Downloads 文件夹不再需要先完整物化单个目录才能继续处理。
- Monitor：扫描状态摘要在 SQLite 查询层按范围过滤，worker 和 Settings 共用同一套进度 freshness 判断。
- Monitor：无进度超时现在只在进度 reporter 返回后刷新，避免进度输出失败时仍被当作 worker 心跳。
- Monitor：无进度超时 watcher 在 dispose 时会被唤醒，不再等待下一次轮询 sleep。
- Monitor：手动扫描启动参数会校验 Settings 传入的 scan id，并使用 Windows 命令行引用规则。
- Monitor：非法手动 scan id 会为请求的 scan id 写入失败进度快照并直接返回，不再启动无法追踪的一次性 worker。
- Monitor：为扫描状态数据库开启 SQLite WAL 日志与忙等待超时，使后台 worker（写）与 Settings（读）不再相互争用，消除后台扫描期间偶发的"状态不可用"闪烁。
- Monitor：在 Settings 中新增可配置的安装包清理置信阈值（50-95%，默认 70）；worker 在匹配安装包前会将其收敛到安全的 0.5-0.95 区间。
- Monitor：新增非破坏性的"预览清理"操作，在不删除任何文件的前提下报告将会清理多少个安装包及可释放空间；真实清理现在也会报告已删除/已释放摘要。
- Monitor：抽取 scanner、organizer 与 installer cleaner 共用的文件枚举 helper，并集中无进度超时常量，去除重复逻辑且行为不变。
- Monitor：修复 Settings 状态图例把 `Warning` 和 `No data` 标签尾部截断的问题——改用水平 stack panel 布局，使每个图例项保持自身自然宽度，而不再按首项尺寸统一单元格。
- 清理：移除一个无任何活动 Settings 界面引用、源自 PowerDisplay 时期的 Monitor 选择 view model。
- 文档：同步 README、README_zh、changelog、development log、sparse package version 和 GPO support marker `SUPPORTED_KIT_2_0_7`。
- 测试：新增 Monitor 目录枚举心跳、流式枚举、按范围查询状态摘要、共享进度 freshness、进度超时顺序、scan-id 校验和 `2.0.7` 版本元数据回归覆盖。
- 测试：新增安装包清理置信阈值收敛、dry-run 安装包预览报告，以及新增 Settings 置信度/预览卡注册的覆盖。

### 2.0.6

- 版本：Kit 升级到 `2.0.6`。
- Monitor：`OrganizeDownloads` 默认改为关闭，后台与单次扫描默认只索引 Downloads 文件夹，需用户主动开启才整理；显式的"整理"操作行为不变。
- Monitor：后台 worker 仅在其真正读取的设置发生变化时才重启，无关或重复的配置保存不再打断进行中的后台扫描。
- Monitor：加固 worker 生命周期监视线程，消除拆卸竞态可能抛出的未观察 `ObjectDisposedException`。
- 品牌：将 Kit 自有界面上残留的 `aka.ms/PowerToys*` 链接（托盘文档菜单、Quick Access 文档、General 提权帮助链接、Awake 了解更多与 CLI 帮助、Light Switch 概览与了解更多、提权通知帮助）替换为 Kit 项目页面。
- 清理：删除陈旧的构建输出和一个残留的原生构建临时目录；不影响任何受版本控制的文件。
- 文档：同步 README、README_zh、changelog、development log、sparse package version 和 GPO support marker `SUPPORTED_KIT_2_0_6`。
- 测试：更新版本元数据、Monitor 默认值和 GPO support marker 的覆盖到 `2.0.6`。

### 2.0.5

- 版本：Kit 升级到 `2.0.5`。
- UI：将 General settings 跟随新版 PowerToys-main 布局，把 Run at startup 和管理员状态归入 `Startup & permissions` 分组。
- UI：保持 Appearance & behavior 专注于语言、主题、系统托盘和 Quick Access 设置，并同步上游系统托盘 expander 图标。
- 文档：同步 README、README_zh、changelog、development log、sparse package version 和 GPO support marker `SUPPORTED_KIT_2_0_5`。
- 测试：更新 General 页面静态布局和版本元数据覆盖。

### 2.0.4

- 版本：Kit 升级到 `2.0.4`。
- UI：将 Settings 启动页、刷新、空搜索回退和 Dashboard 深链路由对齐到 PowerToys-main 的 Dashboard 优先主框架行为。
- UI：保留 `Overview` 深链到 General settings，确保现有 Quick Access 更新入口仍然打开更新区域。
- 精简：删除已禁用 updater install/download 功能残留的资源字符串，避免资源层继续暴露 Kit 没有启用的安装更新行为。
- 文档：同步 README、README_zh、changelog、development log、sparse package version 和 GPO support marker `SUPPORTED_KIT_2_0_4`。
- 测试：新增 Dashboard 作为 Settings 默认首页，以及禁用 updater install/download 资源清理的静态回归覆盖。

### 2.0.3

- 版本：Kit 升级到 `2.0.3`。
- 审查：再次将 Kit 的 runner、Settings、common build/package、sparse package identity 和 policy surface 与本地 PowerToys-main 主框架基线对比。
- 运行时：Monitor 手动扫描在 worker 完成事件发出后，即使无法读取 progress JSON，也会结束进度显示。
- 运行时：Monitor 后台扫描不再触发手动扫描完成事件，避免 Settings 将后台完成误判为手动扫描结果。
- 运行时：Monitor installer cleanup 现在为每个 installer 保留置信度最高的已安装软件匹配，不再被枚举顺序抢占。
- UI：Monitor VCP capability 缓存比较现在包含 value metadata，色温预设变化会正确刷新 Settings。
- 文档：同步 README、README_zh、changelog、sparse package version 和 GPO support marker `SUPPORTED_KIT_2_0_3`。

### 2.0.2

- 版本：Kit 升级到 `2.0.2`。
- 运行时：从活动插件/模块表面移除 PowerDisplay，包括 runner 加载、解决方案条目、Settings 导航、Quick Access 路由、GPO 投影、Settings 序列化、资源、资产和模块源码。
- 运行时：当前活动模块集为 `Awake`、`Light Switch` 和 `Monitor`。
- 运行时：同步本地 PowerToys-main 中的 Awake 和 LightSwitch 模块形状，同时保留 Kit 专用存储、IPC 名称、禁用遥测的 trace hook 和更新边界。
- 运行时：LightSwitch 现在使用 Kit 命名的 toggle、manual-override 和 service-stop 事件；schedule mode 切换到 `Off` 时会停止 scheduler service，toggle hotkey 不再重新拉起 scheduler 进程。
- 精简：移除 LightSwitch 到 PowerDisplay 的 profile bridge，以及删除 PowerDisplay 后无效的 Force Light/Force Dark custom-action 管线。
- 策略：Kit ADMX/ADML support marker 更新为 `SUPPORTED_KIT_2_0_2`。
- 测试：更新三活动模块和 PowerDisplay 删除边界的静态注册/兼容性覆盖。

### 2.0.1

- 版本：将 Kit 提升到 `2.0.1`。
- 运行时：Light Switch 现在只在服务进程成功创建后才将模块标记为已启用，`SearchPathW`/`CreateProcessW` 启动失败时不再让模块保持已启用且 tracing 开启的状态。
- 运行时：Light Switch 在计划模式切换为 `Off` 时改为停止调度服务而非重启，先发出 service-stop 事件让服务优雅退出，再走有界的 terminate 兜底，并从禁用注释中恢复了 `stop_worker_only`/`stop_service_if_running` 生命周期。
- 运行时：Light Switch 切换热键现在直接切换主题，而不是重新启动已停止的调度服务，同时移除了现已未使用的 `is_process_running` helper 和一个无用的计划模式局部变量。
- 运行时：Quick Access 现在在构建 launcher、coordinator 和 Settings dashboard 接线时不再携带未使用的 runner 提权状态；Light Switch 和 PowerDisplay 的直接切换操作从未使用它，因此移除了无用字段、coordinator 属性、接口成员和 `App.IsElevated` 参数。
- 重构：将 Light Switch 热键解析错误信息改为指向当前的 toggle-theme 快捷键，而非已移除的 force-dark 操作。
- 测试：为 Quick Access 提权状态移除、Light Switch 启动后再标记启用的顺序、计划 `Off` 时停止服务，以及切换热键直接切换的行为添加回归覆盖。
- 重构：将 Quick Access 和 Settings 序列化收敛到四个活动模块：`Awake`、`Light Switch`、`Monitor` 和 `PowerDisplay`。
- 隐私：从托管 Settings 和 Quick Access 层移除 telemetry 发送、telemetry 事件源和 `ManagedTelemetry` 项目引用。
- 隐私：删除非活动的 `ManagedTelemetry` 源码树和托管 telemetry base 文件，此前所有活动项目引用已移除。
- 隐私：将 Awake 和 PowerDisplay native module trace provider 改为 no-op 兼容钩子，与 Light Switch 保持一致。
- 隐私：将 LightSwitchService、MonitorModuleInterface 和 ModuleTemplate 的 trace 源码改为 no-op 运行时钩子，并移除其 telemetry include 路径或 `EtwTrace` 引用。
- 隐私：从活动 native module-interface trace 头文件和项目中移除剩余的 `TraceBase` 继承和 telemetry include 路径。
- 隐私：从 Awake 和 PowerDisplay 移除剩余的 no-op 托管 telemetry 调用和事件源类。
- 隐私：删除 runner settings telemetry worker 后，移除 PowerDisplay 的 settings telemetry IPC 事件和 module-interface 信号路径。
- 构建：活动输出现在会移除旧构建图遗留的 `PowerToys.ManagedTelemetry` 和 TraceEvent 支持二进制。
- 瘦身：将 `PowerToys.Interop` WinRT 和共享 IPC 常量裁剪到 Kit 的活动运行时表面，移除非活动 PowerToys Run、FancyZones、Advanced Paste、CmdPal、Keyboard Manager、鼠标工具、预览、Hosts、Workspaces 和 telemetry 事件名。
- 运行时：将活动的 Settings 终止 WinRT 投影从 `PowerToysRunnerTerminateSettingsEvent` 重命名为 `KitRunnerTerminateSettingsEvent`，底层 Kit 命名事件保持不变。
- 瘦身：删除非活动且仅供 AdvancedPaste 使用的 `LanguageModelProvider` 源码树，并移除其 AI provider 包 pin、provider UI metadata/helper、非序列化 AI enum helper、陈旧的 Foundry Local UI 字符串和陈旧的 `OpenAI` 第三方 notice 条目，同时保留历史 settings 序列化模型。
- UI：移除 Shortcut Conflict 窗口中针对非活动 AdvancedPaste、Mouse Without Borders、Peek 和 PowerToys Run settings 的特殊分支，同时保留通用的活动模块冲突处理流程。
- UI：将 `SettingsFactory` 收窄为显式的 Quick Access、LightSwitch 和 PowerDisplay 热键 settings，删除 shortcut-conflict 路径中的宽泛 `IHotkeyConfig` 反射发现和未使用 factory API。
- UI：从 `PageViewModelBase` 删除非活动 MouseUtils 冲突特殊分支，让活动 Settings 页面统一使用模块名匹配的冲突路径。
- 构建：将仍引用非活动 CmdPal、Mouse Without Borders 和 Advanced Paste 的 Settings 包引用注释改为当前 Settings 运行时依赖对齐说明。
- 瘦身：确认没有 Kit 项目引用后，移除仅服务于非活动 Registry Preview 的 `SkiaSharp.Views.WinUI` central package pin 和陈旧第三方 notice 条目。
- 瘦身：确认没有 Kit 项目引用 `Microsoft.CommandPalette.Extensions` 后，移除未使用的 Command Palette extension central package pin。
- 瘦身：确认没有 Kit 项目引用 `AdaptiveCards.ObjectModel.WinUI3`、`AdaptiveCards.Rendering.WinUI3`、`AdaptiveCards.Templating` 或 `Microsoft.Bot.AdaptiveExpressions.Core` 后，移除未使用的 Command Palette Adaptive Cards central package pins 和陈旧 AdaptiveCards 第三方 notice 条目。
- 瘦身：确认没有 Kit 项目引用 `Microsoft.WindowsPackageManager.ComInterop` 后，移除未使用的 Command Palette WinGet interop central package pin。
- 瘦身：确认没有 Kit 项目引用 `HtmlAgilityPack` 或 `ReverseMarkdown` 后，移除未使用的 AdvancedPaste Markdown conversion central package pins 和陈旧 ReverseMarkdown 第三方 notice 条目。
- 瘦身：确认没有 Kit 项目引用 `hyjiacan.pinyin4net`、`Mages` 或 `UnitsNet` 后，移除未使用的 PowerToys Run central package pins 和陈旧 PowerToys Run Mages 第三方 notice 段。
- 瘦身：确认本地 PowerToys-main 参考中 Wox/Window Walker 仅服务已删除 Launcher/CmdPal 路径、HexBox 仅服务已删除 Registry Preview 路径后，移除陈旧的 PowerToys Run Wox/Window Walker 和 Registry Preview HexBox utility notice 段。
- 瘦身：确认没有 Kit 项目引用 `HelixToolkit`、`HelixToolkit.Core.Wpf` 或 `UnicodeInformation` 后，移除未使用的 PreviewPane STL 和 PowerAccent central package pins。
- 瘦身：确认没有 Kit 项目引用 `Shmuelie.WinRTServer` 或 `ToolGood.Words.Pinyin` 后，移除未使用的 Command Palette toolkit/host central package pins 和陈旧 ToolGood.Words.Pinyin 第三方 notice 段。
- 瘦身：确认没有 Kit 项目引用 `ModernWpfUI`、`NJsonSchema`、`ScipBe.Common.Office.OneNote` 或 `SharpCompress` 后，移除仅服务于已删除 DSC、Workspaces/FancyZones、Peek 和 PowerToys Run OneNote 路径的 central package pins。
- 瘦身：确认没有 Kit 项目引用 `CommunityToolkit.WinUI.Collections`、`CommunityToolkit.WinUI.UI.Controls.DataGrid`、`ControlzEx`、`Interop.Microsoft.Office.Interop.OneNote`、`LazyCache`、`Microsoft.Toolkit.Uwp.Notifications`、`RtfPipe` 或 `WPF-UI` 后，移除仅服务于已删除 Hosts、Registry Preview、PowerToys Run、PowerAccent 和 RTF conversion 路径的 central package pins。
- 瘦身：确认没有 Kit 项目引用 `Microsoft.Graphics.Win2D`、`Microsoft.WindowsAppSDK.AI`、`NLog`、`NLog.Extensions.Logging`、`NLog.Schema`、`System.ClientModel`、`System.Numerics.Tensors` 或 `WyHash` 后，移除仅服务于已删除 Launcher、AI 和 CmdPal 路径的 central package pins；`Microsoft.Data.Sqlite` 保留用于 Monitor scan status storage，并删除陈旧 CmdPal WyHash 第三方 notice 段。
- 瘦身：确认 `PowerToys-main` 只在已删除 CmdPal、PreviewPane、Peek、installer 或 Registry Preview 路径中使用 `CalculatorEngineCommon`、`FilePreviewCommon`、Monaco 资产、`modulesRegistry.h` 和 shell-extension 注册 helper 后，删除这些 CmdPal Calculator 与 File Explorer/Peek 共享资产；同时移除未使用的 `UTF.Unknown` package pin、陈旧 NOTICE 段、File Explorer logger 常量，以及 Awake 中陈旧的 launcher logger 命名。
- 瘦身：删除旧 sibling Settings 资产树以及非活动 Settings 模型、源码、单元测试、资产、图标、控件、转换器和 OOBE ViewModel，不再把它们隐藏在项目排除规则后面。
- 策略：将 GPOWrapper 和 Settings GPO helper 策略表面裁剪到活动模块，以及仍保留的启动、更新和诊断策略读取器。
- 策略：将 ADMX/ADML 策略资产裁剪到同一套 Kit 2.0.1 活动策略表面。
- 运行时：删除上游 BugReportTool 源码，以及 runner、托盘、General 和 Quick Access 的启动路径，避免 Kit 收集非活动 PowerToys 模块状态。
- 构建：删除非活动的独立 module_loader 工具和孤立的 CmdPal 版本 props，直到 Command Palette 成为活动 Kit 模块。
- UI：Quick Access 现在使用当前 WinUI SystemBackdrop API，清除了已弃用 WinUIEx backdrop 造成的构建警告。
- UI：将 Quick Access 窗口标题从上游 `PowerToys Quick Access (Preview)` 改为 `Kit Quick Access`。
- 重构：改写共享模块接口和 Settings 分发注释，避免运行时代码继续把非活动 AdvancedPaste 或 PowerToys Run 特殊分支描述为当前行为。
- 构建：本地 sparse package 重新注册现在使用 package helper 生成并写入 `.user/PowerToysSparse.AppxManifest.xml` 的 publisher-adjusted manifest，不再要求开发者直接注册检入的 manifest。
- 构建：加固本地构建 helper，使 MSBuild 参数保持数组边界，Visual Studio 环境导入会缓存解析出的 MSBuild 路径并归一化 `PATH`，本地构建默认跳过 CopyOnWrite/RunVSTest SDK resolver 导入。
- 构建：加固签名 helper，加入 Windows SDK `signtool` 查找，默认使用当前用户证书信任，机器级根信任改为显式选择，并且默认只签 sparse package，除非显式指定目标或全部包。
- 重构：`UITestAutomation` 删除非活动 FancyZones、Hosts、Workspaces、PowerRename、Command Palette 和 Screen Ruler 启动目标；harness 现在指向 Kit 安装根目录、`Kit.exe`、Kit Settings 和四个活动模块可执行文件。
- 运行时：PowerDisplay runner IPC 启动现在会绕过独立 AppInstance 重定向，普通用户启动仍保留单实例行为；设置深链接现在启动 `Kit.exe`。
- 运行时：PowerDisplay 切换现在始终使用 runner 拥有的 `kit_power_display_` 命名管道，而不是启动无参数独立实例；WinUI 窗口创建前的早期管道消息会被缓存，并且管道写入失败后会重启自有 IPC 管道再重试一次。
- 运行时：Common 和 PowerDisplay Settings 深度链接现在只启动 `Kit.exe`；Kit 不再从 Settings 或模块设置链接回退到已安装的上游 `PowerToys.exe`。
- 重构：将 `ModuleHelper` 的启用状态、图标、标签以及 IPC/settings 模块键行为收窄到活动 Kit 模块和 General settings，同时只在兼容 DTO 中保留历史 module-key 映射。
- 测试：Kit UI 自动化清理现在按当前 Kit 输出或安装根目录限定路径，因此 `PowerToys.Settings.exe` 等活动模块可执行文件名不会对已安装的官方 PowerToys 构建执行全局进程终止。
- 构建：`.slnf` 本地构建现在遵守 `-RestoreOnly`，构建脚本默认属性检测识别 `/property:` 覆盖，直接 package 签名入口可以选择 `-RequireMachineRoot`，共享 native `version.vcxproj` 使用 `/FS` 避免 `Version.pdb` 写入竞争。
- 运行时：PowerDisplay 管道启动现在将 `ERROR_PIPE_CONNECTED` 视为客户端已连接，模块销毁会等待 runner 拥有的子进程停止路径执行完毕，独立启动重定向也改为有限 COM 等待而不是无限等待。
- 瘦身：删除剩余未被活动 Kit 页面引用的非活动 Keyboard Manager、File Explorer add-ons、Mouse utilities、Screen Ruler、Peek、Workspaces 和 Hosts Settings 资源字符串。
- 运行时：Settings 深度链接现在使用 Kit-only 安装路径解析器；保留的上游兼容 `PowerToys.exe` 解析器只用于复制模块兼容 helper，不再被 Kit Settings 链接使用。
- 运行时：runner 现在遵守 `enable_quick_access` 通用设置，不再强制关闭 Quick Access；后台更新 toast 也会遵守 Settings 中的通知开关。
- 运行时：Quick Access 在 runner IPC 更新失败时会回滚模块开关，避免 UI 状态和真实模块状态分离。
- 运行时：Awake 模块销毁现在会通知子进程退出、等待关闭；信号路径失败时使用有界 terminate fallback，并在 module interface 删除前关闭 process/thread handles。
- 瘦身：从 Settings 兼容模型中移除非活动 CmdPal package-state 探测，并删除剩余非活动 Settings 资源字符串和未使用的 VariantAssignment package pins。
- 瘦身：从活动 Settings 资源文件中删除剩余非活动 File Explorer Preview、Shortcut Guide activation、Screen Ruler 和 ZoomIt picker 资源字符串。
- 构建：XAML search index generation 现在排除 `SearchResultsPage` 和 `ShortcutConflictWindow`，生成的 Settings 搜索数据只指向可导航的 Settings 页面。
- 运行时：Settings 启动失败时现在会在返回前清理 launch-in-progress guard，避免缺失或启动失败的 Settings 进程阻断后续打开尝试。
- 运行时：Settings 启动现在会在创建 launcher 线程前原子抢占 launch-in-progress guard，并保持该 guard 直到 runner/settings IPC 已启动且 Settings 进程 ID 已注册；如果 token 或 IPC 设置无法继续，会终止已创建的 Settings 子进程。
- 运行时：LightSwitch 现在会在有界 terminate fallback 前通知具名 service-stop event，并在模块销毁时关闭所有模块拥有的 event handles。
- 瘦身：移除已禁用的 LightSwitch Force Light/Force Dark UI 注释、自定义 action 管线，以及未使用的 force-mode event handles，使模块只暴露活动 toggle 路径。
- 瘦身：Settings 命令行 `set`/`get` 解析现在只允许 General 以及活动的 `Awake`、`LightSwitch`、`Monitor` 和 `PowerDisplay` 设置模块，并拒绝 Mouse Without Borders 等非活动 enabled-state key。
- 运行时：Light Switch 和 PowerDisplay 现在是 `EnabledModules` 中显式默认启用的活动模块，Monitor 仍保持默认关闭直到用户启用。
- 测试：新增 PowerDisplay 管道早连接、同步 process-manager stop、有界重定向等待、Kit-only 深度链接解析器、更丰富 UI 自动化清理结果报告，以及扩大非活动资源清理范围的回归覆盖。
- 测试：新增 Quick Access 设置/IPC 回滚、update-toast 通知开关、Awake 关闭清理、CmdPal package 探测移除、sparse package helper 输出、签名 helper 默认值、非活动资源清理、search-index 页面排除、Settings 启动 guard 清理和 IPC 设置失败清理、Settings 命令行活动模块 allowlist、LightSwitch service-stop 生命周期、已禁用 force-mode 移除，以及未使用 package pin 移除的回归覆盖。
- 构建：XAML search index builder 不再携带非活动上游模块图标和 panel 兜底，活动页面图标改为从 Settings XAML 派生。
- 运行时：从 runner 键盘钩子和模块接口中移除非活动的 Shortcut Guide Win-key 跟踪路径。
- 运行时：删除 pressed-key 定时器后，移除键盘钩子的 no-op 窗口注册路径。
- 隐私：删除非活动的 settings telemetry worker 源文件和 runner 项目 filters 条目。
- 测试：删除仍指向已移除 OOBE 和 PowerToys 表面的非活动 Settings UI test 项目。
- 构建：从 `Kit.slnx` 移除 DSC 项目后，删除非活动的 DSC 源码树和 manifest 生成脚本。
- 构建：删除只供 DSC 使用的 Settings `setAdditional` 命令行入口，因为 DSC 生成已移除。
- 瘦身：删除已移除模块页面和 OOBE 表面的非活动 Settings UI 资源字符串。
- 运行时：从 runner 和 Settings 入口点移除已禁用的 OOBE/SCOOBE 启动标志管线。
- 瘦身：移除未使用的 OOBE/SCOOBE SettingsAPI 状态 helper、备份规则、残留资源和 XAML 样式。
- 瘦身：将备份/恢复默认值裁剪到活动 Kit 设置表面，删除非活动的 Keyboard Manager、FancyZones、Workspaces、PowerToys Run 恢复规则以及 PowerToys Run 插件修正代码路径。
- 构建：Settings 和 Quick Access 会从共享 WinUI 输出中移除陈旧的非活动 Settings 资产，Quick Access 只复制活动 Settings 图标。
- 测试：新增旧 Settings 资产副本删除和 ADMX/ADML 策略资产的回归覆盖。
- 测试：新增活动模块 Quick Access 边界、非活动 Settings 表面删除、GPO 策略裁剪、BugReportTool 删除、陈旧输出清理、托管应用无 telemetry 引用、活动托管模块无 telemetry 发送、已删除托管 telemetry 源码、活动 native module no-op trace provider、无 telemetry 构建目标和头文件、ModuleTemplate no-op trace 默认值、PowerDisplay settings telemetry IPC 删除、`PowerToys.Interop` IPC 常量表面裁剪、Kit 命名 Settings 终止投影、AdvancedPaste AI provider 源码/包/UI/enum helper 残留删除、Shortcut Conflict 非活动模块特殊分支移除、显式 SettingsFactory 热键边界、非活动 MouseUtils page conflict branch 删除、Settings 包引用注释清理、仅供 Registry Preview 使用的 SkiaSharp 包 pin 移除、Command Palette extension 包 pin 移除、Command Palette Adaptive Cards 包 pin 移除、Command Palette WinGet interop 包 pin 移除、AdvancedPaste Markdown conversion 包 pin 移除、PowerToys Run 包 pin 移除、已删除 PowerToys Run 和 Registry Preview utility notice 段、PreviewPane STL 和 PowerAccent 包 pin 移除、Command Palette toolkit/host 包 pin 移除、deleted-module package pin removal、deleted-utility package pin removal、deleted Launcher/AI/CmdPal package pin removal，以及已删除 Preview/Peek/CmdPal 共享资产的回归覆盖。
- 测试：新增 Kit UI-test 启动目标、按路径限定的清理、活动模块 module key、Common 和 PowerDisplay `Kit.exe` 设置链接、PowerDisplay runner IPC 单实例行为、早期管道消息缓存、管道写入重试，以及构建/签名 helper 稳定性默认值的回归覆盖。

### 1.2.0

- 版本：将 Kit 提升到 `1.2.0`。
- 更新：加固仅检查更新的 Kit release 调度逻辑，遇到未来时间的 last-check 值会重新检查，而不是触发后台紧循环。
- 文档：同步 README 版本元数据和 changelog 发布记录。
- 测试：更新 README 到 changelog 文档拆分后的版本元数据覆盖。

### 1.1.6

- 通用：将 General 顶部的版本/更新区域恢复为 PowerToys-main 风格，同时保持 Kit 的更新边界为仅检查。
- 通用：将更新结果提示移动到版本/更新 expander 下方，让检查中的 "Checking for updates" 行沿用上游布局。
- 通用：移除底部 About 卡片，因为版本号已经显示在更新区域中。
- 更新：继续使用 `https://github.com/guijianchou/Kit/releases` 作为 Kit release 链接，并保持自动下载/安装入口隐藏。
- 测试：更新 `1.1.6` 版本元数据覆盖，并新增 General 更新/About 布局清理的回归检查。

### 1.1.5

- 更新：将 release 检查重新收敛到上游 `UpdateState.json` 边界；runner 负责检查 GitHub 并写入状态，Settings 只监听并重载该状态。
- 设置：手动检查会保持 "Checking for updates" 状态，直到监听到更新状态文件里的新结果或超时，避免缓存状态覆盖正在进行的检查。
- 设置：检查期间禁用重复点击 Check for updates；仅在发现新版本时显示 release 链接。
- 构建：让共享 update-state 存储可以在 runner 中直接编译，不需要拉回完整 updater 项目。
- 测试：新增上游风格 update-state 边界、缓存状态竞态保护，以及 `1.1.5` README/版本/开发日志元数据回归覆盖。

### 1.1.4

- 更新：强制 GitHub release 检查绕过 HTTP 缓存，避免断网后的手动检查复用旧缓存并误报"已是最新"。
- 设置：避免陈旧缓存的"已是最新"状态覆盖正在进行的手动检查结果。
- 测试：新增 no-cache release 检查，以及 `1.1.4` README/版本/开发日志元数据回归覆盖。

### 1.1.3

- 通用：在 About 中添加 GitHub 仓库链接和手动检查更新入口，并与版本文本左对齐。
- 更新：新增仅检查的 GitHub release 检查，目标为 `https://github.com/guijianchou/Kit/releases`，后台每日检查一次，仅在有新版本时弹出 toast。
- 更新：保持 Kit 的更新边界为仅检查，不会自动下载、安装或启动更新程序。
- 设置：将 About 中的版本号和仓库文本从 caption 字号提升到 body 字号。
- 测试：为 Kit release 检查 IPC 路径、About 反馈状态和 `1.1.3` README/版本元数据添加回归覆盖。

### 1.1.2

- 启动：通过重用已加载的通用设置对象进行初始模块启用，而不是读取设置两次，减少了启动和首帧工作。
- 启动：从 Kit 运行器启动中删除了非活动的 OOBE/SCOOBE 版本状态读取和写入。
- 托盘：在托盘初始化期间停止读取 `UpdateState.json`，同时保持更新徽章 API 可用于任何未来的显式更新程序状态集成。
- 设置：将通用页面诊断清理、备份试运行刷新和搜索索引构建推迟到首帧之后。
- 主页：从主页快捷方式卡中隐藏了 Monitor 的仅状态激活行，因此 Monitor 不再显示为仅快捷方式模块，同时它仍然在模块列表、设置页面和快速访问设置回退中可用。
- 测试：为启动/加载优化边界、Monitor 主页快捷方式过滤以及 `1.1.2` 的更新版本元数据检查添加了回归覆盖。

### 1.1.1

- 构建：将 Kit 设置/通用 UI 构建层与本地 PowerToys-main .NET 10 基线对齐，包括共享的 CsWinRT 目标框架、快速访问、设置 UI 控件、通用 UI 控件、UITestAutomation 和中央包固定。
- 构建脚本和开发者文档现在引用 .NET 10 目标框架用于设置发布和 PowerToys Run 插件检查清单指导。
- 设置：添加了回归覆盖，以便 .NET 10 构建层、README 版本元数据和 Kit 的禁用更新程序/遥测边界不会悄悄漂移。
- 更新程序边界：Kit 保留系统托盘更新徽章渲染用于现有的 Kit 更新状态，但自动更新检查、下载、更新启动和遥测保持禁用。

### 1.1.0

- 将 PowerDisplay 导入到活动 Kit 模块集中，包括运行器加载、解决方案构建条目、设置导航、仪表板元数据、快速访问操作、序列化和 LightSwitch 配置文件路由。
- 设置：跨不同实用工具的多个 UI 和可用性改进。
- 通用：简化了默认模块状态，以便新安装以更轻的初始体验开始。
- 系统托盘图标：更新了单色 PowerToys 系统托盘图标，并保留了现有 Kit 更新状态的更新徽章渲染；自动更新检查和下载保持禁用。
- PowerDisplay 现在使用 Kit 应用数据路径和 Kit 前缀的运行时事件，因此它不与已安装的官方 PowerToys 构建共享状态或命名事件。

### 1.0.4

- Monitor 立即扫描现在遵循来自 `%LOCALAPPDATA%\Kit\Monitor\scan-progress.json` 的工作器报告进度和命名的扫描完成事件，而不是依赖于设置本地进度计时器。
- Monitor 在每次立即扫描请求之前清除陈旧的手动扫描进度，以便设置页面无法重用旧的已完成或临时进度状态。
- Monitor 工作器从扫描管道写入进度快照，包括阶段、已处理/总文件计数、完成时间和最终记录计数。
- Monitor 模块接口现在从模块输出目录解析工作器，并在调试输出没有 apphost `PowerToys.Monitor.exe` 时回退到 `dotnet.exe "PowerToys.Monitor.dll"`。
- 为 Monitor 进度文件报告、设置进度消费和模块接口工作器启动回退添加了回归覆盖。

### 1.0.3

- 发布构建在 `Kit.exe` 构建后从运行时输出中修剪本机链接工件（`*.lib`、`*.exp` 和静态库分析标记）。
- 发布构建从活动 Kit 输出中删除非英语运行时卫星文件夹和非活动 AI 模型提供程序工件，与托管卫星修剪匹配。
- 添加了 `tools\build\clean-stale-versions.ps1` 用于显式清理旧版本输出文件夹，同时保留活动版本、`Debug` 和 `Release`。
- 添加了 `tools\build\verify-runtime-artifacts.ps1` 以检查版本化或 `Release` 输出中的链接工件、PDB、Foundry 资产和非英语区域设置文件夹。
- 从 `Common.UI` 中删除了未使用的 WPF/WinForms 依赖项，以便设置和快速访问不会通过该共享库拉取 WPF 运行时程序集。
- 删除了非活动的设置模块源/XAML 文件，而不是将它们隐藏在 `Compile Remove` 和 `Page Remove` 规则后面。
- 从 `Kit.slnx` 中修剪了非活动的通用、DSC 和未使用的 Awake 服务项目，同时保留 `Common.Search`，因为设置搜索仍在使用它。
- 快速访问现在在可见磁贴没有直接启动器操作时打开模块的设置页面，包括 Monitor。
