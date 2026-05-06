# Kit Plugin Development Docs

This folder mirrors the plugin and extension development documentation copied from `PowerToys-main/doc`.

The files are kept close to the upstream PowerToys layout so links, examples, and future comparisons remain easy to follow. Treat them as reference material for compatibility work; Kit's active module set is intentionally small and currently includes `Awake`, `Light Switch`, `Monitor`, and `PowerDisplay`.

## Contents

- `kit-first-plugin.md`: Kit-specific first module/plugin development path, registration checklist, and validation baseline.
- `kit-development-experience.md`: first-phase implementation notes, module integration lessons, stability risks, and next stabilization checklist.
- `thirdPartyRunPlugins.md`: upstream community plugin list for PowerToys Run.
- `modules/launcher`: PowerToys Run architecture, plugin checklist, project structure, debugging, telemetry, and built-in plugin notes.
- `modules/cmdpal`: Command Palette extension local-development notes, value model notes, anatomy docs, and initial SDK design assets.

## Kit Notes

- The current active Kit modules are `Awake`, `Light Switch`, `Monitor`, and `PowerDisplay`.
- PowerToys Run and Command Palette are not currently active Kit modules.
- These docs are useful when evaluating whether to import a plugin host or extension SDK later.
- Keep upstream copies intact when possible; add Kit-specific notes in separate files if behavior diverges.
- For new Kit features, prefer the existing PowerToys module contract unless the plugin host itself is the feature being imported.
- Monitor is the current reference module for Kit-authored work: core library, worker process, native module interface, Settings model/page, Home metadata, Quick Access registration, and static registration tests.
