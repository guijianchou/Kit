# Kit

**Language / 语言:** [English](README.md) | 中文

---

## 1. Kit 是什么（参考项目）

Kit 是一个基于 **Microsoft PowerToys** 的本地自用 Windows 实用工具工作区。它的存在使得选定的 PowerToys 实用工具可以被修改、隔离，并与同一台机器上安装的官方 PowerToys 构建进行比较。

Kit 是一个**稳定性优先的 PowerToys 衍生工作区，而非完整的产品重新品牌化**：上游的 runner、模块接口、设置与仪表板模式保持可识别，因此导入的 PowerToys 模块可以用最少的适配代码完成验证。

| 保留 | 改变 |
| --- | --- |
| PowerToys runner / 模块接口 / 设置 / 仪表板模式 | 品牌（`Kit`）、窗口标题、可见 UI 文案 |
| `KitModuleIface` C++ 契约（含 `PowertoyModuleIface` 别名） | 设置存储迁移到 `%LOCALAPPDATA%\Kit`（非官方 PowerToys 目录） |
| 显式的模块加载模型 | 移除自动更新、下载与遥测能力 |
| 第一方模块集合：`Awake`、`Light Switch`、`Localserver`、`UDPtest`、`AI Hub` | 备份/恢复默认值使用 Kit 品牌（`Documents\Kit\Backup`、`HKCU\Software\Microsoft\Kit`） |

当前版本：`2.3.0`。

---

## 2. Kit 主架构

### 2.1 进程与组件总览

| 组件 | 可执行文件 / 工程 | 职责 |
| --- | --- | --- |
| **Runner（运行器）** | `Kit.exe`（`src/runner`） | 加载模块接口 DLL、管理模块生命周期、承载托盘图标、协调设置 IPC、启动 Settings 与 Quick Access |
| **Settings 应用** | `Kit.Settings.exe`（`src/settings-ui/Settings.UI`，WinUI 3） | Home、General、各模块页面、导航、页面级 ViewModel |
| **Quick Access** | `Kit.QuickAccess.exe`（`src/settings-ui/Settings.UI.Controls`） | 快捷操作 / 仪表板快捷键 |
| **模块接口** | `Kit.<Module>ModuleInterface.dll`（`src/modules/*/...ModuleInterface`，C++） | 加载进 runner 进程，实现 `KitModuleIface` |
| **Worker / Service** | 例如 `Kit.Awake.exe`、`Kit.LightSwitchService.exe`、`Kit.LocalserverWorker.exe`、`Kit.AIHubWorker.exe` | 由模块接口拉起的无头进程，承载各模块引擎，使行为在关闭 Settings 后依然存活 |
| **公共库** | `src/common`（interop WinMD、托管库、`Logger`、设置辅助） | runner、模块、Settings 共享的基础设施 |

`Kit.exe` 旁运行目录布局：

```
<runner dir>/
├── Kit.exe
├── Kit.<Module>ModuleInterface.dll          # 由 runner 加载
├── LocalserverWorker/Kit.LocalserverWorker.exe
├── AIHubWorker/Kit.AIHubWorker.exe
├── Kit.Awake.exe
├── Kit.LightSwitchService.exe
└── WinUI3Apps/
    ├── Kit.Settings.exe
    └── Kit.QuickAccess.exe
```

### 2.2 启动与生命周期

1. runner 启动，加载已知的模块接口 DLL（`src/runner/main.cpp` 中的 `KitKnownModules`），对每个调用 `kit_create()`，并启用设置中打开的模块。
2. runner 通过命名管道拉起 Settings 应用，并显示托盘图标。
3. Settings 中的启用/禁用通过 IPC 下发，runner 通过 `apply_module_status_update` → 模块对象的 `enable()` / `disable()` 生效。
4. 退出：消息循环结束时（托盘退出，或未开启托盘时关闭 Settings），runner 执行模块收尾（`modules().clear()` → `destroy()`），让每个模块在进程退出前清理其 worker 与服务。

### 2.3 模块接口契约（`KitModuleIface`）

定义于 `src/modules/interface/kit_module_interface.h`。模块 DLL 导出 `kit_create()` 返回实现以下方法的对象：

- `get_key()` —— 非本地化模块 ID
- `enable()` / `disable()` / `is_enabled()` —— 生命周期
- `get_config()` / `set_config()` —— 设置 JSON 模式与更新
- `call_custom_action()` —— 自定义 UI 动作
- `get_hotkeys()` / `on_hotkey()` —— 热键注册与分发
- `destroy()` —— 释放全部资源并删除实例（收尾时调用）

### 2.4 通用插件骨架

每个 Kit 模块都遵循相同的五段式骨架：

1. **原生模块接口 DLL**（C++）—— runner 侧契约，随模块启用/禁用。
2. **核心库** —— 引擎本体，托管（`LocalserverLib`、`UDPtestLib`、`AIHubLib`）或原生（`LightSwitchLib`）。
3. **可选的 Worker/Service 可执行文件** —— 在 Settings 进程之外承载引擎的无头进程。
4. **设置页 + ViewModel**（WinUI 3）—— Settings 应用中的模块 UI。
5. **注册点** —— runner `KitKnownModules`、Settings 导航/路由、Home 仪表板元数据、测试。

### 2.5 数据与日志布局

所有运行时数据位于 `%LOCALAPPDATA%\Kit` 下：

| 路径 | 用途 |
| --- | --- |
| `settings.json` | 通用设置 |
| `RunnerLogs\` | runner 日志 |
| `crash.log` | First-chance / 未处理异常日志 |
| `<ModuleKey>\` | 各模块数据：设置、状态、日志、目录（如 `Localserver\services.json`、`Localserver\State\`、`AiHub\chains\`） |
| `<ModuleKey>\Logs\<version>\` | 分版本的模块/worker 日志 |

---

## 3. 插件架构骨架解析

### 3.1 Awake —— 保持唤醒

- **骨架**：模块接口 DLL + 独立托盘可执行程序，无进程内引擎。
- **组件**：`AwakeModuleInterface.dll`（C++）→ 以 `--use-kit-config --pid <kit_pid>` 拉起 `Kit.Awake.exe`（`src/modules/awake/Awake`，C# WinExe）。
- **生命周期**：启用模块即拉起托盘程序，按配置模式（不限时 / 定时 / 电池感知）保持系统唤醒；监测 `kit_pid`，runner 退出时自行退出。
- **数据**：`%LOCALAPPDATA%\Kit\Awake\`。

### 3.2 Light Switch —— 定时主题切换

- **骨架**：模块接口 DLL + 原生核心库 + 原生 Service 可执行文件。
- **组件**：`LightSwitchModuleInterface.dll`、`LightSwitchLib`（C++ 核心）、`Kit.LightSwitchService.exe`。
- **生命周期**：启用模块即启动 Service，应用主题计划、夜间模式与切换热键；保留直接的 Quick Access 动作。
- **数据**：`%LOCALAPPDATA%\Kit\LightSwitch\`。

### 3.3 Localserver —— 本地服务编排（最深的骨架）

- **骨架**：模块接口 DLL + 托管核心库 + 无头 worker；Settings 页还承载页面级 runner。
- **组件**：`LocalserverModuleInterface.dll`、`Kit.LocalserverWorker.exe`、`LocalserverLib`（目录存储、`ServiceSupervisor`、`ServiceRunner`、所有权记录、命名作业对象）。
- **生命周期**：
  - 启用模块只让目录可用 —— **绝不自动启动**服务。每个服务（如 Deepseek、Hongguo）由用户在设置页单独开启；worker 只*收养*已在运行的进程树，而不是重新拉起。
  - worker 每 5 秒轮询：父进程是否存活？收养页面新启动的链路？是否存在模块禁用标记？
  - 禁用模块时写入 `module-disabled.flag`；worker 停止全部受管服务并优雅退出（最多 8 秒，超时强制终止）。
  - **任何**退出路径（标记、父进程退出、关闭请求）worker 都会停止全部受管服务，保证不留孤儿进程树。
- **数据**：`%LOCALAPPDATA%\Kit\Localserver\` —— `services.json`（目录）、`State\`（所有权）、`settings.json`、`module-disabled.flag`、`Logs\<version>\`。

### 3.4 UDPtest —— 网络探测引擎

- **骨架**：模块接口 DLL + 托管核心库，**无独立进程** —— 探测引擎在 Settings 页面进程内运行。
- **组件**：`UDPtestModuleInterface.dll`、`UDPtestLib`（`ProbeCoordinator`、TCP-HTTPS / UDP 回显 / STUN / NAT 类型探测、`MetricsEngine`、迷你波形遥测）。
- **生命周期**：页面启动/停止协调式探测，页面关闭时级联关闭；不涉及 worker。
- **数据**：`%LOCALAPPDATA%\Kit\UDPtest\`。

### 3.5 AI Hub —— 统一 AI 服务 + 安全审计

- **骨架**：模块接口 DLL + 托管核心库 + 无头 worker。
- **组件**：`AIHubModuleInterface.dll`、`Kit.AIHubWorker.exe`、`Kit.AIHubLib`（AI 服务引擎、kernels、任务链、安全策略、审计流水线）。
- **生命周期**：worker 承载共享 AI 服务（kernels、主/备端点、全局安全策略）；任务链携带各自的 `AGENTS.md` 策略；安全审计收集 Windows 事件日志、排序发现并执行 AI 分析。
- **数据**：`%LOCALAPPDATA%\Kit\AiHub\` —— `chains\`、`kernels\`、`requests\`、`State\`、`security.md`、`settings.json`、`secrets.dat`、`Logs\`。

---

## 4. 构建与发布

- 构建：`tools/build/build.ps1`（单工程）与 `tools/build/build-essentials.ps1`（解决方案还原 + 基础工程）；均自动检测 `x64` 并初始化 VS 环境。
- 版本来源：`src/Version.props`；生成的版本头位于 `src/common/version/Generated Files/version_gen.h`。
- 产物：x64 构建输出到 `x64/<Configuration>/`（`Kit.exe`、`WinUI3Apps\`、模块 DLL、worker）；`tools/build/Stage-Debug.ps1` / `Stage-Release.ps1` 生成暂存的测试/打包目录。
- `x64`、`Debug`、`Release`、`.vs`、`TestResults`、工程 `bin`/`obj` 与根目录 `packages` 均为可丢弃的构建状态，交付后可删除；下次构建会重新生成。

---

## 5. 模块兼容与扩展

Kit 沿用 PowerToys 的模块加载模型，而非另造插件协议。runner 通过 `src/runner/main.cpp` 中维护的 `KitKnownModules` 列表加载已知模块接口 DLL：

- `Kit.AwakeModuleInterface.dll`
- `Kit.LightSwitchModuleInterface.dll`
- `Kit.LocalserverModuleInterface.dll`
- `Kit.UDPtestModuleInterface.dll`
- `Kit.AIHubModuleInterface.dll`

对第一方模块而言这个固定列表是有意为之：避免不稳定的目录探测，让每个导入模块都成为一次明确的兼容性决策。第三方插件宿主（`plugins/\` + `manifest.json`）已规划但尚未实现。

### 导入另一个 PowerToys 模块

1. 复制模块源码，尽量保持上游工程结构。
2. 将工程与构建依赖加入 `Kit.slnx`。
3. 将其接口 DLL 加入 runner 的 `KitKnownModules` 列表。
4. 加入 Settings 导航、路由映射与页面/ViewModel。
5. 保留上游 CsWinRT 引用；从干净的 Release 树构建一次，使 `Kit.Interop` / `Kit.GPOWrapper` 投影重新生成。
6. 仅在实际需要时加入 Home 仪表板元数据与 Quick Access 行为。
7. 为 runner 列表、路由、仪表板与 Quick Access 补充静态/单元覆盖。
8. 先做定向构建验证，再做全解决方案构建。

---

## 6. 稳定方向

- 优先采用上游 PowerToys 模式与小差异，而非新的本地抽象。
- 在 runner/settings/模块兼容稳定之前，保持模块注册显式化。
- 在扩大到全解决方案构建之前，确保 Settings、runner、模块接口、Quick Access 与复制的模块工程可独立构建。
- 保持 Kit 的存储、备份、窗口标题与可见文案与已安装的官方 PowerToys 隔离。
- 不要重新启用自动下载/安装与遥测。
- 新模块拆分为可测试的核心库、worker 进程、原生模块接口、设置模型、设置页、Home 元数据与注册测试。

---

## 7. 文档

- [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) —— Kit 插件与模块开发规范：C++ 契约、注册、WinUI 3 + Mica Alt、Logo 规范、数据隔离、生命周期与模板工程。
- `doc/devdoc/kit-architecture.md` —— Kit 架构说明（轻量化插件宿主方向）。
- `doc/devdoc/powertoys-architecture.md` —— 经过工程验证的 PowerToys 框架架构参考（中文）。
- `doc/devdoc/architecture-comparison.md` —— PowerToys 与 Kit 架构对比及启动耗时优化分析（中文）。
- `doc/devdoc/kit-first-plugin.md` —— 首模块落地检查清单与验证基线。
- `doc/devdoc/kit-development-experience.md` —— 第一阶段经验总结与稳定化检查清单。
- `doc/devdoc/startup-optimization-analysis.md` —— 启动耗时优化分析报告（中文）。
- `fix.md` —— 上游差异与修复记录；`changelog.md` —— 版本历史。

## 8. 变更记录

完整版本历史见 [changelog.md](changelog.md)。
