# Kit Dev Docs

Kit-specific developer notes. Kit's active module set is intentionally small: `Awake` and `Light Switch`.

> 📖 **新来的？** 从 [ARCHITECTURE_OVERVIEW.md](ARCHITECTURE_OVERVIEW.md) 开始 — 架构文档导航入口

## Contents

### Architecture & Design
- **`kit-architecture.md`**: Kit 架构参考（设计方向：轻量插件宿主、启动优化路线）
- **`kit-framework-structure.md`**: Kit 主框架结构详解（源码层级、启动流程追踪、IPC 消息流、设置读写机制）
- **`powertoys-architecture.md`**: PowerToys 主框架架构参考（按上游源码核对，供同步与优化参考）
- **`architecture-comparison.md`**: PowerToys 与 Kit 架构对比与启动优化分析

### Development & Integration
- **`kit-first-plugin.md`**: Kit-specific first module/plugin development path, registration checklist, and validation baseline
- **`kit-development-experience.md`**: first-phase implementation notes, module integration lessons, stability risks, and next stabilization checklist

### Optimization & Sync
- **`startup-optimization-analysis.md`**: 启动优化专项分析
- **`kit-sync-status.md`**: 已同步/待同步模块清单
- **`upstream-sync-checklist.md`**: 上游同步检查清单

### Troubleshooting
- **`STARTUP_DEBUG_GUIDE.md`**: 启动调试指南
- **`DiagnoseTrayIcon.ps1`**: 托盘图标诊断脚本

## Kit Notes

- The current active Kit modules are `Awake` and `Light Switch`.
- PowerToys Run and Command Palette are not currently active Kit modules.
- For new Kit features, prefer the existing PowerToys module contract unless a plugin host itself is the feature being imported.

## Quick Links

- **Architecture**: 从 `kit-architecture.md` 开始了解设计方向，然后查看 `kit-framework-structure.md` 了解代码实现
- **Development**: 新插件开发参考 `kit-first-plugin.md`
- **Performance**: 启动性能分析见 `startup-optimization-analysis.md`

