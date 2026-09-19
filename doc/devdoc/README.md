# Kit Dev Docs

Kit-specific developer notes. Kit's active module set is: `Awake`, `LightSwitch`, `Localserver`, `UDPtest`, and `AIHub`.

> 📖 **新来的？** 从 [ARCHITECTURE_OVERVIEW.md](ARCHITECTURE_OVERVIEW.md) 开始 — 架构文档导航入口

## Contents

### Architecture & Design
- **`kit-architecture.md`**: Kit 架构参考（设计方向：轻量插件宿主、KitModuleIface 规范、启动优化路线）
- **`kit-framework-structure.md`**: Kit 主框架结构详解（源码层级、启动流程追踪、IPC 消息流、设置读写机制）
- **`powertoys-architecture.md`**: PowerToys 主框架架构参考（按上游源码核对，供同步与优化参考）
- **`architecture-comparison.md`**: PowerToys 与 Kit 架构对比与启动优化分析

### Development & Integration
- **`kit-first-plugin.md`**: Kit-specific first module/plugin development path, registration checklist, and validation baseline
- **`kit-development-experience.md`**: first-phase implementation notes, module integration lessons, stability risks, and next stabilization checklist

### Optimization & Diagnostics
- **`startup-optimization-analysis.md`**: 启动优化专项分析

## Kit Notes

- The current active Kit modules are `Awake`, `LightSwitch`, `Localserver`, `UDPtest`, and `AIHub`.
- All official modules use the modern `KitModuleIface` / `kit_create()` contract.
- Settings and Quick Access surfaces are powered by `Kit.Settings` and `Kit.QuickAccess`.

## Quick Links

- **Architecture**: 从 `kit-architecture.md` 开始了解设计方向，然后查看 `kit-framework-structure.md` 了解代码实现
- **Development**: 新插件开发参考 [PLUGIN_DEVELOPMENT.md](../../PLUGIN_DEVELOPMENT.md) 与 `kit-first-plugin.md`
- **Performance**: 启动性能分析见 `startup-optimization-analysis.md`

