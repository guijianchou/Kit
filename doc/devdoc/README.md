# Kit Dev Docs

Kit-specific developer notes. Kit's active module set is intentionally small: `Awake` and `Light Switch`.

## Contents

- `kit-first-plugin.md`: Kit-specific first module/plugin development path, registration checklist, and validation baseline.
- `kit-development-experience.md`: first-phase implementation notes, module integration lessons, stability risks, and next stabilization checklist.
- `powertoys-architecture.md`: PowerToys 主框架架构参考（按上游源码核对，供同步与优化参考）。
- `architecture-comparison.md`: PowerToys 与 Kit 架构对比与启动优化分析。
- `kit-architecture.md`: Kit 架构参考（含启动瓶颈与优化路线）。
- `startup-optimization-analysis.md`: 启动优化专项分析。

## Kit Notes

- The current active Kit modules are `Awake` and `Light Switch`.
- PowerToys Run and Command Palette are not currently active Kit modules.
- For new Kit features, prefer the existing PowerToys module contract unless a plugin host itself is the feature being imported.
