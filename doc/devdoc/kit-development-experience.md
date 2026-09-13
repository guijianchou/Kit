# Kit Development Experience

This note captures the lessons from turning the PowerToys-derived Kit shell into a stable local workspace.

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
- Active modules no longer keep managed telemetry write calls or module-local telemetry event source classes.
- Native module interfaces now keep only no-op trace compatibility hooks, avoiding active-module TraceLogging providers or writes.
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
