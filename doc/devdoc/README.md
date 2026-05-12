# Kit Dev Docs

Kit-specific developer notes. Kit's active module set is intentionally small: `Awake`, `Light Switch`, `Monitor`, and `PowerDisplay`.

## Contents

- `kit-first-plugin.md`: Kit-specific first module/plugin development path, registration checklist, and validation baseline.
- `kit-development-experience.md`: first-phase implementation notes, module integration lessons, stability risks, and next stabilization checklist.

## Kit Notes

- The current active Kit modules are `Awake`, `Light Switch`, `Monitor`, and `PowerDisplay`.
- PowerToys Run and Command Palette are not currently active Kit modules.
- For new Kit features, prefer the existing PowerToys module contract unless a plugin host itself is the feature being imported.
- Monitor is the current reference module for Kit-authored work: core library, worker process, native module interface, Settings model/page, Home metadata, Quick Access registration, and static registration tests.
