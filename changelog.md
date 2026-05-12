# Changelog

**Language / 语言:** English | [中文](#更新日志)

---

## English

### 1.1.6

- General: Restored the PowerToys-main-style version/update section at the top of General while keeping Kit's updater boundary check-only.
- General: Moved update result messaging below the version/update expander so the in-progress "Checking for updates" row follows the upstream layout.
- General: Removed the bottom About card because the version is already shown in the update section.
- Updates: Kept Kit release links on `https://github.com/guijianchou/Kit/releases` and kept automatic download/install actions hidden.
- Tests: Updated version metadata coverage for `1.1.6` and added regression checks for the cleaned General update/About layout.

### 1.1.5

- Updates: Reworked release checking back onto the upstream `UpdateState.json` boundary: the runner checks GitHub and writes state, while Settings watches/reloads that state.
- Settings: Kept manual checks in "Checking for updates" until the watched update-state file reports a newer result or the request times out, preventing cached update state from replacing an in-flight check.
- Settings: Disabled repeated Check for updates clicks while a check is running and kept the release link visible only when a newer release is available.
- Build: Made the shared update-state storage compile cleanly in the runner without pulling in the full updater project.
- Tests: Added regression coverage for the upstream-style update-state boundary, cached-state race protection, and `1.1.5` README/version/development-log metadata.

### 1.1.4

- Updates: Forced GitHub release checks to bypass HTTP cache so offline manual checks cannot reuse stale cached responses and report "up to date".
- Settings: Prevented stale cached "up to date" state from replacing an in-flight manual check result.
- Tests: Added regression coverage for no-cache release checks and `1.1.4` README/version/development-log metadata.

### 1.1.3

- General: Added an About GitHub repository link and a manual check-for-updates entry point aligned with the version text.
- Updates: Added a check-only GitHub release check against `https://github.com/guijianchou/Kit/releases`, with a daily background check and toast only when a newer release is available.
- Updates: Kept Kit's updater boundary check-only; it does not automatically download, install, or launch an updater.
- Settings: Increased the About version and repository text size from caption text to body text.
- Tests: Added regression coverage for the Kit release-check IPC path, About feedback state, and `1.1.3` README/version metadata.

### 1.1.2

- Startup: Reduced startup and first-frame work by reusing the already-loaded general settings object for initial module enablement instead of reading settings twice.
- Startup: Removed inactive OOBE/SCOOBE version-state reads and writes from Kit runner startup.
- Tray: Stopped reading `UpdateState.json` during tray initialization while keeping the update-badge API available for any future explicit updater-state integration.
- Settings: Deferred General page diagnostic cleanup, backup dry-run refresh, and search-index construction until after the first frame.
- Home: Hid Monitor's status-only activation rows from the Home Shortcuts card so Monitor no longer appears as a shortcut-only module, while it remains available in the module list, Settings page, and Quick Access settings fallback.
- Tests: Added regression coverage for the startup/load optimization boundary, Monitor Home Shortcuts filtering, and updated version metadata checks for `1.1.2`.

### 1.1.1

- Build: Aligned the Kit Settings/Common UI build layer with the local PowerToys-main .NET 10 baseline, including shared CsWinRT target framework, Quick Access, Settings UI Controls, Common UI Controls, UITestAutomation, and central package pins.
- Build scripts and developer docs now reference the .NET 10 target framework for Settings publishing and PowerToys Run plugin checklist guidance.
- Settings: Added regression coverage so the .NET 10 build layer, README version metadata, and Kit's disabled updater/telemetry boundaries do not silently drift.
- Updater boundary: Kit keeps system tray update-badge rendering for an existing Kit update state, but automatic update checks, downloads, update launches, and telemetry remain disabled.

### 1.1.0

- Imported PowerDisplay into the active Kit module set with runner loading, solution build entries, Settings navigation, Dashboard metadata, Quick Access actions, serialization, and LightSwitch profile routing.
- Settings: Multiple UI and usability improvements across different utilities.
- General: Streamlined default module states so new installations start with a lighter initial experience.
- System tray icon: Updated the monochrome PowerToys system tray icon and retained update-badge rendering for an existing Kit update state; automatic update checks and downloads remain disabled.
- PowerDisplay now uses Kit app-data paths and Kit-prefixed runtime events so it does not share state or named events with an installed official PowerToys build.

### 1.0.4

- Monitor Scan Now now follows worker-reported progress from `%LOCALAPPDATA%\Kit\Monitor\scan-progress.json` and the named scan-completed event instead of relying on a Settings-local progress timer.
- Monitor clears stale manual-scan progress before each Scan Now request so the Settings page cannot reuse an old completed or temporary progress state.
- Monitor worker writes progress snapshots from the scan pipeline, including phase, processed/total file counts, completion time, and final record count.
- Monitor module interface now resolves the worker from the module output directory and falls back to `dotnet.exe "PowerToys.Monitor.dll"` when the Debug output has no apphost `PowerToys.Monitor.exe`.
- Added regression coverage for Monitor progress file reporting, Settings progress consumption, and the module-interface worker launch fallback.

### 1.0.3

- Release builds prune native link artifacts (`*.lib`, `*.exp`, and static-library analysis markers) from the runtime output after `Kit.exe` builds.
- Release builds remove non-English runtime satellite folders and inactive AI model-provider artifacts from the active Kit output, matching the managed satellite trim.
- Added `tools\build\clean-stale-versions.ps1` for explicit cleanup of old versioned output folders while preserving the active version, `Debug`, and `Release`.
- Added `tools\build\verify-runtime-artifacts.ps1` to check versioned or `Release` outputs for link artifacts, PDBs, Foundry assets, and non-English locale folders.
- Removed unused WPF/WinForms dependencies from `Common.UI` so Settings and Quick Access do not pull WPF runtime assemblies through that shared library.
- Deleted inactive Settings module source/XAML files instead of keeping them hidden behind `Compile Remove` and `Page Remove` rules.
- Trimmed inactive common, DSC, and unused Awake service projects from `Kit.slnx` while keeping `Common.Search` because Settings search still uses it.
- Quick Access now opens a module's Settings page when a visible tile has no direct launcher action, including Monitor.

---

## 中文

## 更新日志

### 1.1.6

- 通用：将 General 顶部的版本/更新区域恢复为 PowerToys-main 风格，同时保持 Kit 的更新边界为仅检查。
- 通用：将更新结果提示移动到版本/更新 expander 下方，让检查中的 "Checking for updates" 行沿用上游布局。
- 通用：移除底部 About 卡片，因为版本号已经显示在更新区域中。
- 更新：继续使用 `https://github.com/guijianchou/Kit/releases` 作为 Kit release 链接，并保持自动下载/安装入口隐藏。
- 测试：更新 `1.1.6` 版本元数据覆盖，并新增 General 更新/About 布局清理的回归检查。

### 1.1.5

- 更新：将 release 检查重新收敛到上游 `UpdateState.json` 边界；runner 负责检查 GitHub 并写入状态，Settings 只监听并重载该状态。
- 设置：手动检查会保持 "Checking for updates" 状态，直到监听到更新状态文件里的新结果或超时，避免缓存状态覆盖正在进行的检查。
- 设置：检查期间禁用重复点击 Check for updates；仅在发现新版本时显示 release 链接。
- 构建：让共享 update-state 存储可以在 runner 中直接编译，不需要拉回完整 updater 项目。
- 测试：新增上游风格 update-state 边界、缓存状态竞态保护，以及 `1.1.5` README/版本/开发日志元数据回归覆盖。

### 1.1.4

- 更新：强制 GitHub release 检查绕过 HTTP 缓存，避免断网后的手动检查复用旧缓存并误报"已是最新"。
- 设置：避免陈旧缓存的"已是最新"状态覆盖正在进行的手动检查结果。
- 测试：新增 no-cache release 检查，以及 `1.1.4` README/版本/开发日志元数据回归覆盖。

### 1.1.3

- 通用：在 About 中添加 GitHub 仓库链接和手动检查更新入口，并与版本文本左对齐。
- 更新：新增仅检查的 GitHub release 检查，目标为 `https://github.com/guijianchou/Kit/releases`，后台每日检查一次，仅在有新版本时弹出 toast。
- 更新：保持 Kit 的更新边界为仅检查，不会自动下载、安装或启动更新程序。
- 设置：将 About 中的版本号和仓库文本从 caption 字号提升到 body 字号。
- 测试：为 Kit release 检查 IPC 路径、About 反馈状态和 `1.1.3` README/版本元数据添加回归覆盖。

### 1.1.2

- 启动：通过重用已加载的通用设置对象进行初始模块启用，而不是读取设置两次，减少了启动和首帧工作。
- 启动：从 Kit 运行器启动中删除了非活动的 OOBE/SCOOBE 版本状态读取和写入。
- 托盘：在托盘初始化期间停止读取 `UpdateState.json`，同时保持更新徽章 API 可用于任何未来的显式更新程序状态集成。
- 设置：将通用页面诊断清理、备份试运行刷新和搜索索引构建推迟到首帧之后。
- 主页：从主页快捷方式卡中隐藏了 Monitor 的仅状态激活行，因此 Monitor 不再显示为仅快捷方式模块，同时它仍然在模块列表、设置页面和快速访问设置回退中可用。
- 测试：为启动/加载优化边界、Monitor 主页快捷方式过滤以及 `1.1.2` 的更新版本元数据检查添加了回归覆盖。

### 1.1.1

- 构建：将 Kit 设置/通用 UI 构建层与本地 PowerToys-main .NET 10 基线对齐，包括共享的 CsWinRT 目标框架、快速访问、设置 UI 控件、通用 UI 控件、UITestAutomation 和中央包固定。
- 构建脚本和开发者文档现在引用 .NET 10 目标框架用于设置发布和 PowerToys Run 插件检查清单指导。
- 设置：添加了回归覆盖，以便 .NET 10 构建层、README 版本元数据和 Kit 的禁用更新程序/遥测边界不会悄悄漂移。
- 更新程序边界：Kit 保留系统托盘更新徽章渲染用于现有的 Kit 更新状态，但自动更新检查、下载、更新启动和遥测保持禁用。

### 1.1.0

- 将 PowerDisplay 导入到活动 Kit 模块集中，包括运行器加载、解决方案构建条目、设置导航、仪表板元数据、快速访问操作、序列化和 LightSwitch 配置文件路由。
- 设置：跨不同实用工具的多个 UI 和可用性改进。
- 通用：简化了默认模块状态，以便新安装以更轻的初始体验开始。
- 系统托盘图标：更新了单色 PowerToys 系统托盘图标，并保留了现有 Kit 更新状态的更新徽章渲染；自动更新检查和下载保持禁用。
- PowerDisplay 现在使用 Kit 应用数据路径和 Kit 前缀的运行时事件，因此它不与已安装的官方 PowerToys 构建共享状态或命名事件。

### 1.0.4

- Monitor 立即扫描现在遵循来自 `%LOCALAPPDATA%\Kit\Monitor\scan-progress.json` 的工作器报告进度和命名的扫描完成事件，而不是依赖于设置本地进度计时器。
- Monitor 在每次立即扫描请求之前清除陈旧的手动扫描进度，以便设置页面无法重用旧的已完成或临时进度状态。
- Monitor 工作器从扫描管道写入进度快照，包括阶段、已处理/总文件计数、完成时间和最终记录计数。
- Monitor 模块接口现在从模块输出目录解析工作器，并在调试输出没有 apphost `PowerToys.Monitor.exe` 时回退到 `dotnet.exe "PowerToys.Monitor.dll"`。
- 为 Monitor 进度文件报告、设置进度消费和模块接口工作器启动回退添加了回归覆盖。

### 1.0.3

- 发布构建在 `Kit.exe` 构建后从运行时输出中修剪本机链接工件（`*.lib`、`*.exp` 和静态库分析标记）。
- 发布构建从活动 Kit 输出中删除非英语运行时卫星文件夹和非活动 AI 模型提供程序工件，与托管卫星修剪匹配。
- 添加了 `tools\build\clean-stale-versions.ps1` 用于显式清理旧版本输出文件夹，同时保留活动版本、`Debug` 和 `Release`。
- 添加了 `tools\build\verify-runtime-artifacts.ps1` 以检查版本化或 `Release` 输出中的链接工件、PDB、Foundry 资产和非英语区域设置文件夹。
- 从 `Common.UI` 中删除了未使用的 WPF/WinForms 依赖项，以便设置和快速访问不会通过该共享库拉取 WPF 运行时程序集。
- 删除了非活动的设置模块源/XAML 文件，而不是将它们隐藏在 `Compile Remove` 和 `Page Remove` 规则后面。
- 从 `Kit.slnx` 中修剪了非活动的通用、DSC 和未使用的 Awake 服务项目，同时保留 `Common.Search`，因为设置搜索仍在使用它。
- 快速访问现在在可见磁贴没有直接启动器操作时打开模块的设置页面，包括 Monitor。
