# Kit Development Experience

This note captures the lessons from turning the PowerToys-derived Kit shell into a stable local workspace.

## 2026-09-17 Version 2.2.1 WinUI 3 Page Crash Fixes & Navigation Resilience

This pass moves Kit to `2.2.1` following root cause debugging and permanent remediation of fail-fast crashes (`0xC000027B` / stowed exceptions) across Settings (General), UDP test, and AI Hub pages:

- **Version & Manifest Updates**:
  - `Version.props`, `AppxManifest.xml`, `README.md`, `README_zh.md`, `changelog.md`, and this development log updated to `2.2.1` (or `2.2.1.0` in package identity).
- **Diagnostics Infrastructure**:
  - Added synchronous first-chance and unhandled exception logging via `System.IO.File.AppendAllText` into `%LOCALAPPDATA%\Kit\crash.log` in `App.xaml.cs`.
  - Stack traces, error codes, and thread information are captured immediately before any fail-fast termination, dramatically speeding up bug resolution.
- **Root Cause Analysis & Fixes**:
  - **GeneralPage (`GeneralPage.xaml`)**:
    - *Root Cause*: `Resources.resw` mapped `AiHub_MainEndpoint_ApiKeyCard.PlaceholderText` to `SettingsCard`, but `SettingsCard` does not possess a `PlaceholderText` property. WinUI 3 XAML reflectively attempts to set this property during `InitializeComponent()`, throwing a fatal `XamlParseException`.
    - *Resolution*: Directed the localization UID to the child `PasswordBox` (`x:Uid="AiHub_MainEndpoint_ApiKeyBox"`) and synchronized `en-us` and `zh-CN` resource keys. Moved ViewModel instantiation before `InitializeComponent()`.
  - **UDPtestPage (`UDPtestPage.xaml` & `UDPtestPage.xaml.cs`)**:
    - *Root Cause 1*: `UDPtest_ClearHistoryButton.Text` in `.resw` attempted to set `Text` on a `Button` (which only supports content/icons), throwing `XamlParseException`.
    - *Root Cause 2*: Tooltip resource keys targeted properties improperly. Corrected to attached property syntax `[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`.
    - *Root Cause 3*: Calling `Application.Current.Resources["AccentButtonStyle"]` threw `COMException: Element not found` because WinUI 3 framework system styles are not stored in `Application.Current.Resources`.
    - *Root Cause 4*: Unsafe direct indexing of theme brushes (`(Brush)Application.Current.Resources[...]`) threw exceptions when theme dictionaries were unmerged.
    - *Resolution*: Created `ThemeBrushHelper.cs` with safe `TryGetValue` lookup and fluent fallback brushes (`SuccessBrush`, `CautionBrush`, `CriticalBrush`, `AttentionBrush`, `SecondaryTextBrush`, `StrokeDefaultBrush`). Declared explicit `AccentHealthButtonStyle` in page resources and moved ViewModel instantiation prior to `InitializeComponent()`.
  - **AIHubPage (`AIHubPage.xaml` & `AIHubPage.xaml.cs`)**:
    - *Root Cause*: `AIHubPage_obj1_Bindings.Update_ViewModel_HealthScoreGrade` threw `InvalidCastException` because `SeverityToBrushConverter` returns a `SolidColorBrush`, but XAML wrapped it in `<SolidColorBrush Color="{...}" />` which expects a `Windows.UI.Color`.
    - *Resolution*: Bound `SeverityToBrushConverter` directly to `<Ellipse Fill="{...}" />`. Moved ViewModel creation before `InitializeComponent()`.
- **Validation**:
  - Verified with direct page launch commands (`General`, `UDPtest`, `AIHub`) with exit code 0 and 0 crashes in `%LOCALAPPDATA%\Kit\crash.log`.
  - Unit test suites passing cleanly.

## 2026-09-17 Version 2.2.0 AI Hub & UDPtest Integration and Bilingual Localization

This pass moves Kit to `2.2.0` with full integration of AI Hub (native module interface, security policy hierarchy, and security audit dashboard), UDPtest (network probe engine and high-density sparkline), and 100% pure bilingual localization.

- Version.props, README, README_zh, changelog, this development log, the sparse package manifest, and version metadata tests now use Kit version `2.2.0`.
- The active Kit module set is now `Awake`, `Light Switch`, `Localserver`, `UDPtest`, and `AI Hub`.
- AI Hub Security Audit:
  - Reconstructed tiered security policy pipeline: global policy (`%LOCALAPPDATA%\Kit\AiHub\security.md`) and essential task policies (`chains/{task}/AGENTS.md`), with execution workflow `Codex / Pi → Security policy + AGENTS.md → Analysis summary → Suggested action`.
  - Added auto-scaffolding in `SecurityPolicyService.cs` for default policies, preventing file I/O exceptions on clean setups.
  - Aligned Security Audit tab in `AIHubPage.xaml` strictly with original reference design: Health overview ring, grade badges (A-F), finding severity distribution bar, audit activity metrics, priority action banners, and `FindingDetailsDialog.xaml`.
  - Fixed WinUI 3 XAML binding crash in `SeverityToBrushConverter.cs` by ensuring returned object is strictly `SolidColorBrush`.
  - Dynamically format finding facts, root cause, and recommendations (`PriorityActionText`) based on current runtime UI culture (`zh-CN` vs `en-US`).
  - Restored light theme purple icon (`#A756FF`) for AI Hub.
- 100% Pure Bilingual Localization:
  - Removed all mixed parenthetical English annotations (e.g. `(保持唤醒)` -> `保持唤醒`, `启用 Awake` -> `启用保持唤醒`, `启用 UDP Test` -> `启用网络探测`, `启用 AI Hub` -> `启用 AI 智能中心`, `Essential Policy (全局任务策略)` -> `基础任务策略`).
  - Completely localized Localserver hardware/process metrics to pure Chinese, UDPtest table headers & metadata, Shell navigation, and Quick Access flyouts.
  - Fully localized `FindingDetailsDialog.xaml.cs`.
- Automated Test Validation:
  - `Kit.Settings.csproj`: 0 Warnings, 0 Errors.
  - `Kit.AiHub.UnitTests.csproj`: 108 Passed, 0 Failed, 1 Skipped.
  - `Settings.UI.UnitTests.dll`: 196 Passed, 0 Failed.

## 2026-09-12 Version 2.1.0 Monitor Module Removal

This pass moves Kit to `2.1.0` after removing the Monitor module to focus on the two core utilities (Awake and LightSwitch).

- Version.props, README, README_zh, changelog, this development log, the sparse package manifest, GPO support markers, and version metadata tests now use Kit version `2.1.0`.
- The active Kit module set is now `Awake` and `Light Switch`; Monitor was removed from runner loading, Settings UI navigation, Quick Access routing, GPO projection, Settings serialization, enabled modules JSON converter, unit tests, and documentation.
- ModuleType enum no longer includes Monitor; all Monitor-related switch cases, string conversions, and type mappings were removed from runner and Settings surfaces.
- Unit tests now assert that Monitor does not appear in runner settings output, UITestAutomation metadata, or enabled modules JSON.
- Documentation updated to reflect the two-module architecture focused on system utilities rather than file management.

## 2026-06-17 Version 2.0.5 General Layout Sync

This pass moved Kit to `2.0.5` after syncing the General settings layout with the newer local PowerToys-main General page.

- Version.props, README, README_zh, changelog, this development log, the sparse package manifest, GPO support markers, and version metadata tests now use Kit version `2.0.5`.
- General settings now uses the `Startup & permissions` group for Run at startup and administrator state, matching the newer PowerToys-main first-screen organization.
- Appearance & behavior now keeps Run at startup out of that section and retains language, theme, system tray, and Quick Access controls.
- The system tray expander now carries the upstream icon treatment, and the old standalone `Admin_Mode` group is no longer present in the General XAML.
- Settings UI tests now cover the `Startup & permissions` layout and the `2.0.5` version metadata.

## 2026-06-17 Version 2.0.4 Dashboard And Updater Surface Cleanup

This pass moved Kit to `2.0.4` after a final Kit framework comparison against the local PowerToys-main Dashboard-first Settings shell behavior.

- Version.props, README, README_zh, changelog, this development log, the sparse package manifest, GPO support markers, and version metadata tests now use Kit version `2.0.4`.
- Settings startup, refresh, empty-search fallback, and Dashboard deep-link routing now use `DashboardPage`, matching the PowerToys-main Dashboard-first shell behavior.
- The `Overview` deep link remains mapped to `GeneralPage` so Quick Access update routing can still open General settings without becoming the default Settings home.
- Disabled updater install/download resource strings were removed from the English resources because Kit exposes manual release checks and release links, not active installer download/install actions.
- Settings UI tests now cover Dashboard-as-default Settings home and the disabled updater install/download resource cleanup.

## 2026-06-16 Version 2.0.3 Framework Review

This pass moved Kit to `2.0.3` after rechecking the Kit runner, Settings, common build/package, sparse package identity, and policy surfaces against the local PowerToys-main framework baseline.

- Version.props, README, README_zh, changelog, this development log, the sparse package manifest, GPO support markers, and version metadata tests now use Kit version `2.0.3`.
- The PowerToys-main framework comparison did not require pulling back deleted inactive module surfaces; Kit's central package and build target differences remain intentional local-workspace boundaries.

## 2026-06-13 Version 2.0.2 PowerDisplay Removal And Upstream Module Sync

This pass moved Kit to `2.0.2` after syncing the copied Awake and LightSwitch modules with the local PowerToys-main reference while preserving Kit's local-only runtime boundaries.

- Version.props, README, README_zh, changelog, the sparse package manifest, GPO support markers, and version metadata tests now use Kit version `2.0.2`.
- The active Kit module set is now `Awake` and `Light Switch`; PowerDisplay was removed from runner loading, solution entries, Settings navigation, Quick Access routing, GPO projection, Settings serialization, resources, assets, docs, and module source.
- LightSwitch no longer keeps the deleted PowerDisplay profile bridge, Force Light/Force Dark custom-action plumbing, or PowerToys-named runtime events.
- LightSwitch now uses Kit-named toggle, manual-override, and service-stop events, stops its scheduler service when the schedule changes to `Off`, and keeps toggle-hotkey handling independent of the scheduler process.
- Awake and LightSwitch keep no-op trace compatibility hooks without active PowerToys telemetry providers, writes, telemetry include paths, or `EtwTrace` project references.

## 2026-05-28 Version 2.0.1 Stability Refactor

This pass finalized Kit's docs, tests, and Settings code against the local PowerToys-main reference and moved Kit to `2.0.1`.

- Version.props, README, README_zh, changelog, this development log, and the version metadata regression test now use Kit version `2.0.1`.
- Quick Access and Settings serialization now reference only the active Kit module set.
- Managed Settings and Quick Access no longer keep telemetry send paths, telemetry event source files, or `ManagedTelemetry` project references.
- The inactive `ManagedTelemetry` source tree and managed telemetry base file were deleted after confirming no active project still references them.
- Active outputs now remove stale `PowerToys.ManagedTelemetry` and TraceEvent support binaries left by old build graphs.
- Awake and PowerDisplay no longer keep managed telemetry write calls or module-local telemetry event source classes.
- Awake and PowerDisplay native module interfaces now keep only no-op trace compatibility hooks, matching Light Switch and avoiding active-module TraceLogging providers or writes.
- LightSwitchService and ModuleTemplate trace sources now keep no-op runtime hooks without telemetry include paths or `EtwTrace` project references.
- All active native module-interface trace headers and projects no longer inherit `TraceBase` or keep telemetry include paths.
- PowerDisplay no longer exposes or listens to a settings telemetry IPC event; this matches Kit's removed runner settings telemetry worker instead of carrying an event with no active consumer.
- `Kit.Interop` now exposes only Kit's active runtime constants through the WinRT `Constants` projection and shared native constants header. Inactive PowerToys Run, FancyZones, Advanced Paste, CmdPal, Keyboard Manager, Mouse utilities, preview, Hosts, Workspaces, and telemetry event names were deleted rather than kept as unused compatibility surface.
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
- The deleted Launcher, AI, and CmdPal central package pins were removed, along with the stale CmdPal WyHash third-party notice section, after confirming no Kit project references `Microsoft.Graphics.Win2D`, `Microsoft.WindowsAppSDK.AI`, `NLog`, `NLog.Extensions.Logging`, `NLog.Schema`, `System.ClientModel`, `System.Numerics.Tensors`, or `WyHash`; `Microsoft.Data.Sqlite` remains for Monitor scan status storage, and the local PowerToys-main snapshot still keeps these as upstream central pins for deleted Launcher, AdvancedPaste/OpenAI, ImageResizer AI, and CmdPal paths.
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
- Regression coverage now guards the four-module Quick Access boundary, deleted inactive Settings surfaces, GPO policy trimming, ADMX/ADML policy assets, BugReportTool removal, stale output cleanup, telemetry-free managed app projects, active managed modules without telemetry sends, deleted managed telemetry source, active native module no-op trace providers, telemetry-free build targets and headers, ModuleTemplate no-op trace defaults, Awake README telemetry-free documentation, PowerDisplay's removed settings telemetry IPC, the trimmed `Kit.Interop` IPC constant surface, the Kit-named Settings termination projection, deleted AdvancedPaste AI provider source/package/UI/enum helper remnants, removed Shortcut Conflict inactive-module special cases, the explicit SettingsFactory hotkey boundary, the removed inactive MouseUtils page conflict branch, Settings package-reference comment cleanup, Registry Preview-only SkiaSharp package pin removal, Command Palette extension package pin removal, Command Palette Adaptive Cards package pin removal, Command Palette WinGet interop package pin removal, AdvancedPaste Markdown conversion package pin removal, PowerToys Run package pin removal, deleted PowerToys Run and Registry Preview utility notice sections, PreviewPane STL and PowerAccent package pin removal, Command Palette toolkit and host package pin removal, deleted-module package pin removal, deleted-utility package pin removal, deleted Launcher/AI/CmdPal package pin removal, deleted Preview/Peek/CmdPal shared assets, deleted utility NOTICE sections, current Kit runtime wording, the sparse package active app identity boundary, active Kit UI-test launch targets, PowerDisplay runner IPC single-instancing, active-module `ModuleHelper` behavior, sparse package version/signing defaults, build/signing helper stability defaults, full active-module first-plugin docs, and the Quick Access fast-build dependency.
- Verification for this refactor used Visual Studio 18 MSBuild for `Kit.vcxproj`, `PowerToys.Settings.csproj`, `PowerToys.QuickAccess.csproj`, `PowerDisplay.csproj`, `PackageIdentity.vcxproj`, `GPOWrapper.vcxproj`, `UnitTests-CommonUtils.vcxproj`, `AwakeModuleInterface.vcxproj`, `PowerDisplayModuleInterface.vcxproj`, `LightSwitchModuleInterface.vcxproj`, `LightSwitchService.vcxproj`, `MonitorModuleInterface.vcxproj`, `ModuleTemplateCompileTest.vcxproj`, and `Settings.UI.UnitTests.csproj`; `vstest.console.exe` reported 426/426 passing CommonUtils tests in the broader 2.0.1 pass, 149/149 passing Settings UI tests after the active-module managed/native telemetry slimming pass, 150/150 passing Settings UI tests after the interop IPC constant slimming pass, 151/151 passing Settings UI tests after the Settings termination projection rename and AdvancedPaste AI provider helper cleanup, 152/152 passing Settings UI tests after removing Shortcut Conflict inactive-module special cases, 153/153 passing Settings UI tests after narrowing SettingsFactory to explicit hotkey settings, 154/154 passing Settings UI tests after deleting the inactive MouseUtils conflict branch, 155/155 passing Settings UI tests after cleaning Settings package-reference comments, 156/156 passing Settings UI tests after removing the Registry Preview-only SkiaSharp package pin, 157/157 passing Settings UI tests after removing the Command Palette extension package pin, 158/158 passing Settings UI tests after removing the Command Palette Adaptive Cards package pins, 159/159 passing Settings UI tests after removing the Command Palette WinGet interop package pin, 160/160 passing Settings UI tests after removing the AdvancedPaste Markdown conversion package pins, 161/161 passing Settings UI tests after removing the PowerToys Run package pins, 162/162 passing Settings UI tests after removing the PreviewPane STL and PowerAccent package pins, 163/163 passing Settings UI tests after removing the Command Palette toolkit and host package pins, 164/164 passing Settings UI tests after removing the deleted-module package pins, 165/165 passing Settings UI tests after removing the deleted-utility package pins, 166/166 passing Settings UI tests after removing the deleted Launcher, AI, and CmdPal package pins, 167/167 passing Settings UI tests after deleting the inactive Preview/Peek/CmdPal shared assets, 168/168 passing Settings UI tests after the Quick Access title, runtime comment, and 2.0.1 metadata finalization, 169/169 passing Settings UI tests after deleting stale utility NOTICE sections and rewording current Kit runtime logs/comments, 170/170 passing Settings UI tests after trimming the sparse package manifest to active app identities, and 173/173 passing Settings UI tests after the UITestAutomation, ModuleHelper, sparse package metadata, plugin-doc, and Quick Access fast-build cleanup. The same final pass also reported 409/409 passing CommonUtils tests after removing `ModulesRegistry.Tests.cpp`, and MSBuild reported 0 warnings and 0 errors for `Settings.UI.UnitTests.csproj`, `UnitTests-CommonUtils.vcxproj`, `logger.vcxproj`, `AwakeModuleInterface.vcxproj`, `LightSwitchModuleInterface.vcxproj`, `PowerDisplay.csproj`, `PackageIdentity.vcxproj`, and `Kit.vcxproj`.


## 2026-05-12 Version 1.2.0 Release Metadata

This pass moved Kit to `1.2.0` with improved release notes and version metadata.

- Version.props, README, README_zh, and changelog now use Kit version `1.2.0`.
- Changelog now includes detailed feature documentation and upgrade notes.
- Release metadata tests cover version string validation across all manifests.

## 2026-05-11 General Update Layout Cleanup And 1.1.6 Release Notes

This pass moved Kit to `1.1.6` after reviewing General settings update controls.

- Version.props, README, README_zh, and changelog now use Kit version `1.1.6`.
- General settings update section now hides installer download/install buttons that Kit doesn't support.
- Update check continues to expose release page links for manual installation.

## 2026-05-11 Update Check Architecture And 1.1.5 Release Notes

This pass moved Kit to `1.1.5` after refactoring the update check system.

- Version.props, README, README_zh, and changelog now use Kit version `1.1.5`.
- Update checker now compares semantic versions correctly across major/minor/patch boundaries.
- Release page URL construction now validates version format before generating links.

## 2026-05-09 Update Check Reliability And 1.1.4 Release Notes

This pass moved Kit to `1.1.4` after hardening update check error handling.

- Version.props, README, README_zh, and changelog now use Kit version `1.1.4`.
- Update checker now handles network timeouts and malformed responses gracefully.
- Settings UI shows clear error states when update check fails.

## Phase One Result

The first development phase successfully established Kit as a stable two-module shell derived from PowerToys. The runner, Settings UI, Quick Access coordinator, and build system now operate cleanly with Awake and LightSwitch as the active module set, with no runtime dependencies on inactive PowerToys modules or telemetry infrastructure.

## Decisions That Worked

- Removing inactive modules early (before adding new ones) kept the codebase focused and reduced maintenance surface.
- Keeping unit tests synchronized with code changes caught serialization and navigation regressions immediately.
- Preserving PowerToys-main as a local reference enabled selective sync of upstream framework improvements without inheriting unused features.
- Using semantic versioning with explicit changelog entries made release progression visible in git history and user-facing documentation.

## 2026-04-29 Settings Stabilization

Initial Settings UI hardening pass focused on removing PowerToys-specific surfaces.

- Dashboard now shows only Awake and LightSwitch module cards.
- Navigation sidebar removed entries for inactive modules.
- Quick Access coordinator routes only to active module settings pages.
- Resource strings cleaned up to remove references to deleted features.

## 2026-04-29 Privacy, Updater, And Worktree Review

Early Kit customization reviewing privacy, update, and development workflow.

- Removed telemetry collection infrastructure while keeping no-op compatibility hooks for module interfaces.
- Update checker now points to Kit release pages instead of PowerToys distribution endpoints.
- Established git worktree workflow for parallel feature branches during early development.
