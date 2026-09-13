# Kit Architecture Overview

> **导航指南**：本文档是 Kit 架构文档体系的入口，帮助你快速定位到需要的文档

## 文档体系结构

```
架构文档体系
├── ARCHITECTURE_OVERVIEW.md (本文档) - 导航入口
├── 设计层 - 架构理念与方向
│   ├── kit-architecture.md - Kit 架构设计（轻量插件宿主、优化路线）
│   └── architecture-comparison.md - Kit vs PowerToys 架构对比
├── 实现层 - 源码结构与机制
│   ├── kit-framework-structure.md - 主框架代码结构详解★
│   └── powertoys-architecture.md - PowerToys 上游架构参考
├── 开发层 - 插件开发与集成
│   ├── kit-first-plugin.md - 新插件开发指南
│   └── kit-development-experience.md - 模块集成经验与教训
└── 运维层 - 性能优化与同步
    ├── startup-optimization-analysis.md - 启动性能分析
    ├── kit-sync-status.md - 上游同步状态
    ├── upstream-sync-checklist.md - 同步检查清单
    └── STARTUP_DEBUG_GUIDE.md - 启动调试指南
```

---

## 快速导航

### 我想了解...

#### 🏗️ **Kit 整体架构设计**
→ 阅读 [kit-architecture.md](kit-architecture.md)
- Kit 的定位：轻量插件宿主
- 设计原则：最小化、可插拔、性能优先
- 核心组件：Runner、Settings UI、Module System
- 优化路线：启动性能、内存占用

#### 📖 **源码层级和实现细节**
→ 阅读 [kit-framework-structure.md](kit-framework-structure.md) ★ 推荐
- 完整的源码目录结构（runner/modules/settings-ui/common）
- 启动流程源码追踪（main.cpp → runner() → message loop）
- IPC 消息流实现（TwoWayPipeMessageIPC）
- 设置读写机制（settings.json、Repository 模式）
- 热键与键盘钩子（CentralizedKeyboardHook）
- 模块系统实现（PowertoyModule、load_powertoy）

#### 🔌 **开发新插件/模块**
→ 阅读 [kit-first-plugin.md](kit-first-plugin.md)
- 模块契约（PowertoyModuleIface）
- 注册检查清单（DLL 名称、powertoy_create、get_key）
- 集成验证步骤（构建、配置、测试）
- 常见问题排查

#### ⚡ **启动性能优化**
→ 阅读 [startup-optimization-analysis.md](startup-optimization-analysis.md)
- 启动时间分解（目标 <300ms）
- 瓶颈分析（模块加载、Settings UI、Quick Access）
- 已实施优化（缓存元数据、延迟启动、一次性清理）
- 待优化方向（预编译设置、进程池、异步加载）

#### 🔄 **与 PowerToys 上游同步**
→ 阅读 [kit-sync-status.md](kit-sync-status.md)
- 已同步模块清单（Awake、LightSwitch）
- 待同步优化（Settings 性能、共享库更新）
- 排除清单（不需要的模块和功能）

#### 🆚 **Kit 和 PowerToys 的区别**
→ 阅读 [architecture-comparison.md](architecture-comparison.md)
- 启用模块对比（2 vs 22+）
- 启动流程差异
- 代码修改点（GPO 跳过、调试模式优化）
- 品牌和命名变更

#### 🔍 **PowerToys 上游架构参考**
→ 阅读 [powertoys-architecture.md](powertoys-architecture.md)
- 完整的 PowerToys 架构（包含所有 22+ 模块）
- 模块契约详细说明
- Settings UI 实现
- GPO 策略机制

---

## 按角色推荐阅读路径

### 新加入的开发者
1. **先读**：[kit-architecture.md](kit-architecture.md) - 了解 Kit 是什么
2. **再读**：[kit-framework-structure.md](kit-framework-structure.md) - 掌握代码结构
3. **然后**：[kit-first-plugin.md](kit-first-plugin.md) - 如果要开发插件
4. **参考**：[STARTUP_DEBUG_GUIDE.md](STARTUP_DEBUG_GUIDE.md) - 遇到问题时

### 插件开发者
1. **必读**：[kit-first-plugin.md](kit-first-plugin.md) - 开发规范
2. **必读**：[kit-framework-structure.md](kit-framework-structure.md) 第 5 章 - 模块系统实现
3. **参考**：[powertoys-architecture.md](powertoys-architecture.md) 第 3 章 - 模块契约完整定义
4. **参考**：[kit-development-experience.md](kit-development-experience.md) - 前人踩坑经验

### 性能优化工程师
1. **必读**：[startup-optimization-analysis.md](startup-optimization-analysis.md) - 当前瓶颈分析
2. **必读**：[kit-framework-structure.md](kit-framework-structure.md) 第 6 章 - 启动流程源码追踪
3. **参考**：[architecture-comparison.md](architecture-comparison.md) - Kit 已做的优化
4. **工具**：[STARTUP_DEBUG_GUIDE.md](STARTUP_DEBUG_GUIDE.md) - 性能测量方法

### 上游同步维护者
1. **必读**：[kit-sync-status.md](kit-sync-status.md) - 当前同步状态
2. **必读**：[upstream-sync-checklist.md](upstream-sync-checklist.md) - 同步检查清单
3. **参考**：[powertoys-architecture.md](powertoys-architecture.md) - 上游架构参考
4. **参考**：[architecture-comparison.md](architecture-comparison.md) - 差异点对照

### 架构设计评审者
1. **必读**：[kit-architecture.md](kit-architecture.md) - 设计理念
2. **必读**：[architecture-comparison.md](architecture-comparison.md) - 设计取舍
3. **必读**：[kit-framework-structure.md](kit-framework-structure.md) - 实现验证
4. **参考**：[powertoys-architecture.md](powertoys-architecture.md) - 上游对照

---

## 文档维护

### 各文档更新触发条件

| 文档 | 何时更新 |
|------|---------|
| `kit-architecture.md` | 架构设计决策变更、新增核心组件 |
| `kit-framework-structure.md` | 源码结构重构、关键实现变更 |
| `powertoys-architecture.md` | PowerToys 上游大版本更新 |
| `architecture-comparison.md` | Kit 与上游差异点变化 |
| `kit-first-plugin.md` | 插件契约变更、开发流程优化 |
| `kit-development-experience.md` | 新的集成经验教训 |
| `startup-optimization-analysis.md` | 性能优化实施后、新瓶颈发现 |
| `kit-sync-status.md` | 上游同步完成、模块状态变更 |
| `upstream-sync-checklist.md` | 同步流程改进 |
| `STARTUP_DEBUG_GUIDE.md` | 新增调试工具、常见问题 |

### 文档一致性检查清单

当修改架构相关代码时，检查以下文档是否需要更新：

- [ ] 修改了 `main.cpp` 启动流程 → 更新 `kit-framework-structure.md` 第 6 章
- [ ] 修改了模块加载逻辑 → 更新 `kit-framework-structure.md` 第 5 章
- [ ] 新增/删除核心组件 → 更新 `kit-architecture.md` + `kit-framework-structure.md`
- [ ] 修改 IPC 消息格式 → 更新 `kit-framework-structure.md` 第 8 章
- [ ] 修改设置存储结构 → 更新 `kit-framework-structure.md` 第 9 章
- [ ] 实施性能优化 → 更新 `startup-optimization-analysis.md`
- [ ] 同步上游变更 → 更新 `kit-sync-status.md`
- [ ] 修改模块契约 → 更新 `kit-first-plugin.md` + `powertoys-architecture.md`

---

## 相关资源

### 主项目文档
- `../../fix.md` - 上游差异与修正清单（主目录）
- `../../fix.plan` - 分阶段任务与验收标准（主目录）
- `../../README.md` - Kit 项目主 README

### 外部参考
- [PowerToys 官方文档](https://github.com/microsoft/PowerToys/tree/main/doc)
- [PowerToys 开发者指南](https://github.com/microsoft/PowerToys/blob/main/doc/devdocs/readme.md)

---

**文档版本**: v1.0  
**最后更新**: 2026-09-13  
**维护者**: Kit 架构团队
