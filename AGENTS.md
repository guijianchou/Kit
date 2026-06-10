---
description: 'Top-level AI contributor guidance for Kit - a local, self-use PowerToys-derived Windows utility workspace'
applyTo: '**'
---

# Kit – AI contributor guide

This is the top-level guidance for AI contributions to Kit. Keep changes atomic, follow existing patterns, and cite exact paths in PRs.

## Overview

Kit is a local, self-use Windows utility workspace derived from Microsoft PowerToys. The active module set is intentionally small: `Awake`, `Light Switch`, `Monitor`, and `PowerDisplay`.

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

- Prefer upstream PowerToys patterns and small deltas over new local abstractions.
- Keep module registration explicit through maintained Kit lists (`KitKnownModules` in `src/runner/main.cpp`, `KitModuleCatalog`, navigation routes).
- Keep Kit storage, backup, window title, and visible text separate from the installed official PowerToys app (`%LOCALAPPDATA%\Kit\`, `Documents\Kit\Backup`, `HKCU\Software\Microsoft\Kit`).
- Do not re-enable automatic download/install or telemetry behavior.
- Keep new modules split into a testable core library, worker process, native module interface, settings model, settings page, Home metadata, and static registration tests. `Monitor` is the reference shape.

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
- Key Kit test surfaces: `Settings.UI.UnitTests` (BuildCompatibility, navigation, view-model regressions), `Monitor.UnitTests` (worker/runner registration, progress reporting).

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
| `KitKnownModules` and module catalogs | Must be updated together when adding/removing a module |

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
