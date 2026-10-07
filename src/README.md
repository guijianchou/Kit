# Kit Source Code

This source tree keeps the PowerToys project layout and its runner, module interface, settings and shared-library contracts. See the [project README](../README.md) or [中文说明](../README_zh.md) for the framework, six active modules, their lifetimes and build instructions.

## Code Organization

- `runner` starts Kit, loads the maintained module interface DLL list, owns module lifetime, and coordinates settings IPC.
- `modules` contains Awake, Light Switch, Localserver, UDPtest, AI Hub and NetMap, including their business libraries, native interfaces and optional Workers.
- `settings-ui` contains the WinUI Settings and Quick Access apps, shared controls, settings models, serialization, backup/restore helpers and Settings tests.
- `common` contains shared native and managed infrastructure, including `AiHub` for shared AI execution. AI Hub plugin business logic lives separately in `modules/AIHub/AIHubLib`.

## Compatibility Rule

Kit should prefer the existing PowerToys contracts over new local protocols. A module is considered active only after its project, module interface DLL, Settings route, Home metadata, and tests are explicitly wired into the Kit lists. Shared Settings surfaces should use `KitModuleCatalog` instead of local one-off module arrays.

Directory scanning should not be used as a shortcut for module activation unless the project deliberately moves to a manifest-based plugin model later.
