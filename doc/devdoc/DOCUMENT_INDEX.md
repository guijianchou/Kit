# Kit 架构文档清单

快速索引所有架构相关文档及其用途。

## 📚 完整文档列表

### 🗺️ 导航与索引
| 文档 | 用途 | 适合人群 |
|------|------|---------|
| **[ARCHITECTURE_OVERVIEW.md](ARCHITECTURE_OVERVIEW.md)** | **架构文档导航入口** | **所有人（必读）** |
| [README.md](README.md) | 文档目录索引 | 所有人 |

### 🏗️ 架构设计层
| 文档 | 用途 | 页数 | 最后更新 |
|------|------|------|----------|
| **[kit-architecture.md](kit-architecture.md)** | **Kit 架构设计理念与方向** | 8.5KB | 2026-09-13 |
| [architecture-comparison.md](architecture-comparison.md) | Kit vs PowerToys 架构对比 | 13.9KB | 2026-09-13 |
| [powertoys-architecture.md](powertoys-architecture.md) | PowerToys 上游架构参考 | 24.1KB | 2026-09-13 |

**重点**：
- `kit-architecture.md` — Kit 的核心设计理念（轻量、插件化、性能优先）
- `architecture-comparison.md` — 理解 Kit 为何这样设计（与上游对比）

### 🔧 实现层
| 文档 | 用途 | 页数 | 最后更新 |
|------|------|------|----------|
| **[kit-framework-structure.md](kit-framework-structure.md)** ★ | **主框架源码结构详解** | 34.9KB | 2026-09-13 |

**重点章节**：
- 第 1 章：源码目录结构（runner/modules/settings-ui/common）
- 第 2 章：Runner 主进程详解（main.cpp、启动流程）
- 第 5 章：模块系统实现（PowertoyModule、加载机制）
- 第 6 章：启动流程源码追踪（完整调用链）
- 第 7 章：热键与键盘钩子（CentralizedKeyboardHook）
- 第 8 章：IPC 消息流（TwoWayPipeMessageIPC）
- 第 9 章：设置存储与读写（settings.json、Repository 模式）

### 🔌 开发层
| 文档 | 用途 | 页数 | 最后更新 |
|------|------|------|----------|
| **[kit-first-plugin.md](kit-first-plugin.md)** | **新插件开发指南** | 5.7KB | 2026-09-12 |
| [kit-development-experience.md](kit-development-experience.md) | 模块集成经验与教训 | 13.8KB | 2026-09-12 |

**重点**：
- `kit-first-plugin.md` — 模块契约、注册检查清单、验证步骤
- `kit-development-experience.md` — 前人踩坑记录，值得一读

### ⚡ 性能优化层
| 文档 | 用途 | 页数 | 最后更新 |
|------|------|------|----------|
| **[startup-optimization-analysis.md](startup-optimization-analysis.md)** | **启动性能分析与优化** | 16.6KB | 2026-09-13 |

**重点**：
- 启动时间目标 <300ms
- 瓶颈分析（模块加载、Settings UI、Quick Access）
- 已实施优化（缓存元数据、延迟启动、一次性清理）

---

## 📖 推荐阅读顺序

### 快速入门（30分钟）
1. **[ARCHITECTURE_OVERVIEW.md](ARCHITECTURE_OVERVIEW.md)** (5分钟) — 文档导航
2. **[kit-architecture.md](kit-architecture.md)** (15分钟) — 架构设计
3. **[kit-framework-structure.md](kit-framework-structure.md)** 第1-2章 (10分钟) — 源码结构

### 深入理解（2小时）
1. **[kit-framework-structure.md](kit-framework-structure.md)** 全文 (60分钟) — 完整框架
2. **[startup-optimization-analysis.md](startup-optimization-analysis.md)** (30分钟) — 性能优化
3. **[architecture-comparison.md](architecture-comparison.md)** (30分钟) — 设计取舍

### 插件开发（1小时）
1. **[kit-first-plugin.md](kit-first-plugin.md)** (20分钟) — 开发规范
2. **[kit-framework-structure.md](kit-framework-structure.md)** 第5章 (20分钟) — 模块系统
3. **[kit-development-experience.md](kit-development-experience.md)** (20分钟) — 踩坑经验

### 模块与上游参考（1小时）
1. **[powertoys-architecture.md](powertoys-architecture.md)** (30分钟) — 上游参考
2. **[architecture-comparison.md](architecture-comparison.md)** (30分钟) — 架构对比与取舍

---

## 🔍 按关键词查找

### 启动流程
- [kit-framework-structure.md](kit-framework-structure.md) 第 6 章
- [startup-optimization-analysis.md](startup-optimization-analysis.md) 第 2 章

### 模块系统
- [kit-framework-structure.md](kit-framework-structure.md) 第 5 章
- [kit-first-plugin.md](kit-first-plugin.md)
- [powertoys-architecture.md](powertoys-architecture.md) 第 3 章

### IPC 通信
- [kit-framework-structure.md](kit-framework-structure.md) 第 8 章

### 热键系统
- [kit-framework-structure.md](kit-framework-structure.md) 第 7 章

### Settings UI
- [kit-framework-structure.md](kit-framework-structure.md) 第 3 章
- [powertoys-architecture.md](powertoys-architecture.md) 第 4 章

### 性能优化
- [startup-optimization-analysis.md](startup-optimization-analysis.md)
- [architecture-comparison.md](architecture-comparison.md) 第 3 章

---

## 📊 文档统计

| 类别 | 文档数 | 总大小 |
|------|--------|--------|
| 导航与索引 | 2 | ~8KB |
| 架构设计层 | 3 | ~47KB |
| 实现层 | 1 | ~35KB |
| 开发层 | 2 | ~20KB |
| 性能优化层 | 1 | ~16.6KB |
| 扩展指南与经验 | 2 | ~34KB |
| **总计** | **11** | **~160KB** |

---

## 🔄 文档依赖关系

```
ARCHITECTURE_OVERVIEW.md (导航中心)
    ├─ 设计层
    │   ├─ kit-architecture.md
    │   │   ├─ references → kit-framework-structure.md (实现验证)
    │   │   └─ references → architecture-comparison.md (设计对比)
    │   ├─ architecture-comparison.md
    │   │   ├─ references → kit-architecture.md
    │   │   └─ references → powertoys-architecture.md
    │   └─ powertoys-architecture.md (上游参考)
    │
    ├─ 实现层
    │   └─ kit-framework-structure.md ★ 核心文档
    │       ├─ implements → kit-architecture.md (设计实现)
    │       └─ references → powertoys-architecture.md (上游对照)
    │
    ├─ 开发层
    │   ├─ kit-first-plugin.md
    │   │   └─ references → kit-framework-structure.md (第 5 章)
    │   └─ kit-development-experience.md
    │       └─ references → kit-first-plugin.md
    │
    └─ 运维层
        └─ startup-optimization-analysis.md
            └─ references → kit-framework-structure.md (第 6 章)
```

---

## ✅ 文档完整性检查

- [x] 架构设计文档完整（kit-architecture.md、architecture-comparison.md）
- [x] 源码实现文档完整（kit-framework-structure.md）
- [x] 开发指南文档完整（kit-first-plugin.md）
- [x] 性能优化文档完整（startup-optimization-analysis.md）
- [x] 导航文档完整（ARCHITECTURE_OVERVIEW.md、README.md）
- [x] 所有文档有交叉引用
- [x] 所有文档有版本和更新日期

---

**最后更新**: 2026-09-14  
**文档总数**: 11  

