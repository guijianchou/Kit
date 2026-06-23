# Kit Monitor Module

Monitor is the first Kit-authored module built on the PowerToys runner and module-interface model. It ports the earlier Python Downloads monitor into a managed core library, a worker process, a native module interface, and a PowerToys-style Settings surface.

## Project Layout

- `MonitorLib` contains testable core behavior: category rules, smart filename rules, SHA1 hashing, incremental scan reuse, Python-compatible CSV persistence, duplicate grouping, recursive Downloads scanning, category organization, installer-cleanup primitives, scan progress snapshots, and scan status persistence.
- `Monitor` builds the Monitor worker as `PowerToys.Monitor.exe` when an apphost is present and as `PowerToys.Monitor.dll` in apphost-less Debug outputs. Use `--scan-once` for a one-shot scan. The worker writes `scan-progress.json`, persists scan status, and exits cleanly when the module interface disables it.
- `MonitorModuleInterface` builds `PowerToys.MonitorModuleInterface.dll`. The runner loads it from the fixed `KitKnownModules` list. It exposes key `Monitor`, starts the worker on enable, falls back to `dotnet.exe "PowerToys.Monitor.dll"` when the worker apphost is missing, signals the shared exit event on disable, and supports simple custom actions for scan, organize, and installer cleanup requests.
- `Tests/Monitor.UnitTests` covers the core library and static project-shape requirements.

## Settings And Home Integration

Monitor settings live in `Settings.UI.Library` as `MonitorSettings`, `MonitorProperties`, and `SndMonitorSettings`. The Settings app exposes `MonitorPage` and `MonitorViewModel`, and Home/Quick Access register Monitor through the same maintained lists used by Awake and LightSwitch.

Manual registration points are covered by `Settings.UI.UnitTests/ViewModelTests/MonitorSettingsRegistration.cs` so future module imports do not silently miss runner, solution, Home, Quick Access, or Settings wiring.

The current Settings page exposes manual scan, Monitor status, `OrganizeDownloads`, `CleanInstallers`, `Run in background`, Downloads folder selection, hash algorithm selection, and a same-row scan progress indicator. `Run in background` controls only the persistent worker launched by the runner. Manual Scan remains a one-shot action and does not require background mode. The progress indicator reads worker-written snapshots from `%LOCALAPPDATA%\Kit\Monitor\scan-progress.json`, and the Status section reads `%LOCALAPPDATA%\Kit\Monitor\monitor-status.db`, shows All/30d/7d history, and marks stale running scans as failed after the shared stale-run timeout.

## Current Behavior

The current parity target is the Python implementation baseline:

- Recursively scan the Downloads folder while skipping reparse points.
- Write and read `results.csv` with the Python column shape.
- Reuse SHA1 values for unchanged files.
- Categorize by smart rules and extension maps.
- Preserve duplicate records for analytics.
- Move files into category folders with conflict-safe names when `OrganizeDownloads` is enabled.
- Provide installer cleanup primitives without touching real registry state in tests.
- Let one-shot Scan Now apply the current action toggles through the same worker cycle: both `OrganizeDownloads` and `CleanInstallers` are off by default, so a fresh scan only indexes files. When organization is disabled, scan-only passes do not create category folders or move files.
- Clean installers from the Downloads root and the `Programs` category folder when `CleanInstallers` is enabled; this opt-in path reads uninstall registry entries visible to the current process to match installed software names and versions.
- Report scan progress phases and completion state to Settings through a JSON progress file and persisted scan status.
- Persist recent manual and background scan status in SQLite for the Settings Status section.

## Future Work

- Add real installed-software discovery behind a testable provider.
- Expand worker failure and cancellation detail in Settings.
- Add richer Home shortcut state now that worker scan status exists.
- Consider a file-system watcher if future requirements need faster-than-interval scans.
