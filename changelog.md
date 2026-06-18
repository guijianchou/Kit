# Changelog

**Language / 语言:** English | [中文](#更新日志)

---

## English

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
- Slimming: Pruned `PowerToys.Interop` WinRT and shared IPC constants to Kit's active runtime surface, removing inactive PowerToys Run, FancyZones, Advanced Paste, CmdPal, Keyboard Manager, Mouse utilities, preview, Hosts, Workspaces, and telemetry event names.
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
- Slimming: Removed the deleted Launcher, AI, and CmdPal central package pins after confirming no Kit project references `Microsoft.Data.Sqlite`, `Microsoft.Graphics.Win2D`, `Microsoft.WindowsAppSDK.AI`, `NLog`, `NLog.Extensions.Logging`, `NLog.Schema`, `System.ClientModel`, `System.Numerics.Tensors`, or `WyHash`, and removed the stale CmdPal WyHash third-party notice section.
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
- Tests: Added regression coverage for active-module Quick Access boundaries, deleted inactive settings surfaces, GPO policy trimming, BugReportTool removal, stale output cleanup, telemetry-free managed app projects, active managed modules without telemetry sends, deleted managed telemetry source, active native module no-op trace providers, telemetry-free build targets and headers, ModuleTemplate no-op trace defaults, Awake README telemetry-free documentation, PowerDisplay's removed settings telemetry IPC, the trimmed `PowerToys.Interop` IPC constant surface, the Kit-named Settings termination projection, deleted AdvancedPaste AI provider source/package/UI/enum helper remnants, removed Shortcut Conflict inactive-module special cases, the explicit SettingsFactory hotkey boundary, the removed inactive MouseUtils page conflict branch, Settings package-reference comment cleanup, Registry Preview-only SkiaSharp package pin removal, Command Palette extension package pin removal, Command Palette Adaptive Cards package pin removal, Command Palette WinGet interop package pin removal, AdvancedPaste Markdown conversion package pin removal, PowerToys Run package pin removal, deleted PowerToys Run and Registry Preview utility notice sections, PreviewPane STL and PowerAccent package pin removal, Command Palette toolkit and host package pin removal, deleted-module package pin removal, deleted-utility package pin removal, deleted Launcher/AI/CmdPal package pin removal, deleted Preview/Peek/CmdPal shared assets, deleted utility NOTICE sections, current Kit runtime wording, and the sparse package active app identity boundary.
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
- 瘦身：确认没有 Kit 项目引用 `Microsoft.Data.Sqlite`、`Microsoft.Graphics.Win2D`、`Microsoft.WindowsAppSDK.AI`、`NLog`、`NLog.Extensions.Logging`、`NLog.Schema`、`System.ClientModel`、`System.Numerics.Tensors` 或 `WyHash` 后，移除仅服务于已删除 Launcher、AI 和 CmdPal 路径的 central package pins，并删除陈旧 CmdPal WyHash 第三方 notice 段。
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
