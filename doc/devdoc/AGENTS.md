---
description: 'Top-level AI contributor guidance for Kit - a local, self-use PowerToys-derived Windows utility workspace'
applyTo: '**'
---

# Kit – AI contributor guide

This is the top-level guidance for AI contributions to Kit. Keep changes atomic, follow existing patterns, and cite exact paths in PRs.

## Overview

Kit is a local, self-use Windows utility workspace derived from Microsoft PowerToys. The active module set is: `Awake`, `LightSwitch`, `Localserver`, `UDPtest`, `AIHub`, `NetMap`, and `Winget`.

| Area | Location | Description |
|------|----------|-------------|
| Runner | `src/runner/` | Main executable, tray icon, module loader, hotkey management |
| Settings UI | `src/settings-ui/` | WinUI configuration app communicating via named pipes |
| Modules | `src/modules/` | Active Kit utilities (each in its own subfolder) |
| Common Libraries | `src/common/` | Shared code: logging, IPC, settings, DPI, utilities |
| Build Tools | `tools/build/` | Build scripts and automation |
| Dev docs | `doc/devdoc/` | Kit-specific developer documentation |

For architecture details, module set, and stability direction, see the [project README](README.md). For version history, see the [changelog](changelog.md).

## Conventions

- Follow Kit's `KitModuleIface` and `kit_create()` plugin contract.
- Keep native module registration in the single `KitModuleCatalog` in `src/runner/kit_module.h`; keep the managed `KitModuleCatalog` and navigation routes aligned with it.
- Keep Kit storage, backup, window title, and visible text separate from official PowerToys (`%LOCALAPPDATA%\Kit\`, `Documents\Kit\Backup`, `HKCU\Software\Microsoft\Kit`).
- Do not re-enable automatic download/install or telemetry behavior.
- Keep new modules split into a testable core library, worker process, native module interface, settings model, settings page, Home metadata, and static registration tests.

## Stability and product behavior

- Optimize repeat navigation and interaction latency before minimizing memory. Retain one page/view model per active module and useful analysis snapshots; pause visibility-dependent work while a page is hidden. Keep caches bounded, and invalidate them when their inputs change.
- Settings follows the PowerToys hierarchy: section heading, then individual setting cards and expanders. Keep basic settings usable while AI Service initializes. Avoid nested whole-section layout animations and repeated visual-tree walks on each layout frame.
- Preserve live font/language switching, keyboard focus, scroll position, entered configuration, and running plugin work. Remove redundant introductions and decorative status prose; retain errors, evidence, progress, and information needed to choose an action.
- AI Service owns kernel execution, endpoint selection, concurrency and policy enforcement. AI Hub reuses those facilities and owns evidence collection, issue review and reports. Do not add a second independent Codex execution path in the UI.
- Normal log analysis is incremental. Cache by evidence content and relevant configuration/policy/language, remap both structured and narrative evidence references for each snapshot, and revisit affected issue groups when evidence changes. Include bounded related context so new evidence can establish relationships that did not previously produce an issue. Failed, cancelled or partial analysis must not advance the complete-analysis cache. Selecting an audit period automatically loads its evidence and reuses covered intervals. Keep one report action for generate, view, update or retry; do not expose a separate full reanalysis or regenerate entry.
- Each issue must distinguish observed facts from possible causes, cite its evidence, give concrete verification and remedy steps, and explain how to check the outcome. Group similar occurrences without merging unrelated causes merely because their provider or title matches.
- Windows Critical (level 1) and Error (level 2) evidence establishes a minimum High audit risk. Validate AI evidence references, preserve omitted failures, apply the floor after fresh or cached AI review, and prioritize Critical within High findings. Restore historical floors only from stored observed levels or a matching complete evidence snapshot; never infer them from event IDs alone.
- Full scan rereads the selected window and continues into the shared AI analysis service with the current model. Keep progress and cancellation continuous through both stages; log collection alone is not a completed full audit. Preserve the previous complete AI report on failed, cancelled or partial replacement. Quick refresh and period navigation remain local, incremental operations.
- A slow audit route must not silently change models on its local deadline. Preserve configured effort, report actual successful models/routes, and never reuse fallback or mixed results as proof of a completed primary-model audit.
- Weekly reports are separate from audit Full Scan: own service, progress, cancellation and mode-specific cache. Fast reuses a persisted exact-seven-day scan with no collection or new AI call; Full independently collects only the current seven days of System/Application/Setup levels 1–4 without Security and calls the shared AI Service, using that scan only as reference. Prior-period comparisons use matching saved reports, never an extra collection. Keep the dedicated bilingual template in `AIHub/chains/windows-stability-report/AGENTS.md`, editable in the weekly policy tab; preserve custom policies. Keep legacy monthly document compatibility without exposing a monthly action. Persist matching Chinese and English Markdown before rendering the matching PNG in the Mica window. Reuse unchanged recent reports; explicit Full updates recollect. Failed, partial, degraded, fallback-model or configuration-changed reviews must not overwrite complete reports. Missing coverage and omitted Top-N baseline groups are unknown, not healthy zeros. Retain reports and bounded weekly scan snapshots for at most 30 days. Keep the legacy audit-snapshot archive distinct: empty incremental scans never erase earlier evidence, and revisions replace contributions without increasing scan counts.
- NetMap fits geographic routes to the actual map viewport, including a single located hop and date-line crossings. Keep markers/labels readable as the window resizes. Map work must not alter MTR measurement behavior.

## Build

### Prerequisites

- Visual Studio 2022 17.4+ or Visual Studio 2026
- Windows 10 1803+ (April 2018 Update or newer)
- .NET 10 SDK (matches the Kit Settings/Common UI build layer)

### Build commands

| Task | Command |
|------|---------|
| First build / NuGet restore | `tools\build\build-essentials.cmd` |
| Build current folder | `tools\build\build.cmd` |
| Build with options | `build.ps1 -Platform x64 -Configuration Release` |

### Build discipline

1. One terminal per operation (build → test). Do not switch or open new ones mid-flow.
2. After making changes, `cd` to the project folder that changed (`.csproj`/`.vcxproj`).
3. Use scripts to build: `tools/build/build.ps1` or `tools/build/build.cmd`.
4. For first build or missing NuGet packages, run `build-essentials.cmd` first.
5. **Exit code 0 = success; non-zero = failure** – treat this as absolute.
6. On failure, read the errors log: `build.<config>.<platform>.errors.log`.
7. Do not start tests or launch the runner until the build succeeds.

### Build logs

Located next to the solution/project being built:

- `build.<configuration>.<platform>.errors.log` – errors only (check this first)
- `build.<configuration>.<platform>.all.log` – full log
- `build.<configuration>.<platform>.trace.binlog` – for MSBuild Structured Log Viewer

For complete details, see [Build Guidelines](tools/build/BUILD-GUIDELINES.md).

## Tests

### Test discovery

- Find test projects under `src/**/UnitTests` and `src/**/UITests`.
- Key Kit test surfaces: `Settings.UI.UnitTests` (BuildCompatibility, navigation, view-model regressions), `Kit.AiHub.UnitTests`, `Awake.ModuleServices.UnitTests`.

### Running tests

1. **Build the test project first**, wait for exit code 0.
2. Run via VS Test Explorer (`Ctrl+E, T`) or `vstest.console.exe` with filters.
3. **Avoid `dotnet test`** in this repo – use VS Test Explorer or `vstest.console.exe`.

### Test discipline

1. Add or adjust tests when changing behavior.
2. If tests skipped, state why (e.g., comment-only change, string rename).
3. Regression tests should physically assert removed code is gone (see `BuildCompatibility.cs` patterns), not just hide it from the build.

## Boundaries

### Ask for clarification when

- Ambiguous spec after scanning relevant docs.
- Cross-module impact (shared enum/struct) is unclear.
- Updater/telemetry boundary changes are involved (Kit keeps these inert).
- New `PowerToys.*` WinMD/CsWinRT dependencies appear for a copied module.

### Areas requiring extra care

| Area | Concern |
|------|---------|
| `src/common/` | ABI breaks affecting copied modules |
| `src/runner/`, `src/settings-ui/` | IPC contracts, settings schema |
| Updater entry points | Keep check-only; no auto download/install or telemetry |
| Native `KitModuleCatalog`, managed catalog and navigation routes | Native startup consumes the single catalog; keep managed keys and routes aligned when adding/removing a module |

### What not to do

- Don't merge incomplete features.
- Don't break IPC/JSON contracts without updating both runner and settings-ui.
- Don't add noisy logs in hot paths.
- Don't re-enable PowerToys automatic update download/install or settings telemetry.
- Don't introduce third-party deps without updating `NOTICE.md`.

## Validation Checklist

Before finishing, verify:

- [ ] Build clean with exit code 0
- [ ] Tests updated and passing locally
- [ ] No unintended ABI breaks or schema changes
- [ ] IPC contracts consistent between runner and settings-ui
- [ ] New dependencies added to `NOTICE.md`
- [ ] Module changes covered by static registration tests (runner list, navigation, Home, Quick Access)

## Documentation Index

- [Project README](README.md) – architecture, active module set, stability direction
- [Changelog](changelog.md) – bilingual version history
- [Kit Dev Docs](doc/devdoc/README.md) – first-module checklist, development experience notes
- [Build Guidelines](tools/build/BUILD-GUIDELINES.md)
- [Worktree Guidelines](tools/build/Worktree-Guidelines.md)
