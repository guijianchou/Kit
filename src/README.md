# Kit Source Code

This source tree keeps the PowerToys project layout so copied modules can continue to use the upstream runner, module interface, settings, and shared-library contracts. See `../README.md` for the project goal, current module list, recent Awake/Home implementation notes, and stability roadmap.

## Code Organization

- `runner` starts Kit, loads the maintained module interface DLL list, owns module lifetime, and coordinates settings IPC.
- `modules` contains the active utilities. The current Kit module set is Awake, Light Switch, and Monitor.
- `settings-ui` contains the WinUI Settings app, shared Settings controls, settings models, serialization, backup and restore helpers, and focused Settings tests.
- `common` contains shared native and managed infrastructure used by the runner, modules, and Settings.

## Compatibility Rule

Kit should prefer the existing PowerToys contracts over new local protocols. A module is considered active only after its project, module interface DLL, Settings route, Home metadata, and tests are explicitly wired into the Kit lists. Shared Settings surfaces should use `KitModuleCatalog` instead of local one-off module arrays.

Directory scanning should not be used as a shortcut for module activation unless the project deliberately moves to a manifest-based plugin model later.
