# Kit UI/UX 优化方案（v9）— 主框架 / Settings 壳 + 加载速度（下一步执行稿）

> 状态：**下一步执行稿（2026-10-15 第四轮：i18n 与单测闭合后）** ｜ 基线：`main@449cebf`（v2.3.13 + i18n 修复 + 单测 212/212）
> 本版：剔除已完成项（单测 16 项全部修复、备份校验 severity、中文文案纠正）；新增 runner 托盘菜单未本地化（主框架 i18n 遗留）；L5 风暴、L3/L4/L8 优化、L9 仪表保持未做；批次顺延为 9–12。
> 历史版本：v2 = `782c626`、v3 = `41f7bdd`、v4 = `748445e`、v5 = `20ec501`、v6 = `adbb06e`+`65a4866`、v7 = `db8630c`、v8 = `3ebf328`（`git show <commit>:doc/devdoc/ui-ux-refactor-plan.md` 取回）。

## 0. 现状基线

- **部署形态**：`x64\Debug` 与 `x64\Release` 均已是 **2.3.13**（Debug 已含 07:22 i18n 修复重建）；`bin\debug\2.3.13`、`bin\release\2.3.13` 已 stage；日常实例 `C:\Users\Zen\Repos\Projects\Kit`（`WinUI3Apps\Kit.Settings.exe`）仍为 **2.3.11.0**（2026-10-13 23:31）→ 部署待执行（复制 `bin\debug\2.3.13` 到 Projects\Kit，用户既有流程）。
- **2026-10-15 实测（2.3.13 runner 路径，trace 协议；Debug = 05:19 会话，Release = 05:56 会话）**：

| 阶段 | Debug | Release |
|---|---|---|
| App InitializeComponent completed | 181ms | 183ms |
| App constructed | 182ms | 194ms |
| OnLaunched | 385ms | 400ms |
| MainWindow ctor enter | 470ms | 492ms |
| titlebar extended（`ExtendsContentIntoTitleBar=true` 首触） | 535ms（+64） | 561ms（+68） |
| placement deserialized | 568ms（+33） | 592ms（+31） |
| placement set（`SetWindowPlacement`） | 615ms（+47） | 646ms（+54） |
| callbacks registered | 620ms | 652ms |
| ShellPage InitializeComponent completed | 790ms（块 170） | 819ms（块 167） |
| ShellPage navigation items bound | 791ms（+1） | 820ms（+1） |
| ShellPage ViewModel initialized | 805ms（+14） | 834ms（+14） |
| Main window created | 809ms（段 339） | 838ms（段 346） |
| Startup page navigated | 963ms | 990ms |
| Main window activated | 963ms | 991ms |
| First page loaded (DashboardPage) | 1321ms | 1353ms |
| NAV DashboardPage | 511ms（navigate 153） | 514ms（navigate 151） |

- **runner 侧**：初始化（WinMain→模块启用）129ms（Debug）/ 140ms（Release）；设置进程创建 +142ms / +135ms。
- **结论**：① **L1 结案：Release ≈ Debug（±3%）**，启动耗时由框架/XAML 段主导，Debug 日常部署即可，Release 用于发布；② 主窗段两大头细化为 placement 段 ~144-155ms（其中 `ExtendsContentIntoTitleBar` 首触 45-68ms 最大）与 ShellPage InitializeComponent 167-170ms（XAML 膨胀；导航绑定 1ms、VM 初始化 14ms 可忽略）；③ 首帧 ~1321-1353ms（trace 协议下；`off` 时更快但无 STARTUP 行可读）。
- **单测**：**212 / 212 全量通过（0 失败；2026-10-15 07:24 本机复跑验证）**，16 项历史遗留已全部闭合（断言已按现布局/文案/删除的死代码更新；`WM_CLOSE` 断言改为匹配 settings_window.cpp:690-708 的既有意图：托盘隐藏时关闭最后一个设置窗口经 WM_CLOSE 走正常拆卸）。
- **i18n（2026-10-15）**：zh-CN 文案纠正 **NetMap → 网络地图**（4 键：Shell/ModuleTitle/EnableCard/AutomationProperties）、**LightSwitch → 主题切换**（5 键，全库无残留"深浅切换"）；QuickAccess 显示名走 resw（`LightSwitch/ModuleTitle`）随同生效。**runner 托盘菜单仍为英文**（`runner.base.rc` 的 `ID_TRAY_MENU` 硬编码 "Settings"/"Documentation"/"Close"，无 zh-CN LANGUAGE 块）→ 见 §4.1。
- **验证链（本机 2.3.13）**：构建 exit 0（**注意：须 `-ExecutionPolicy Bypass`**，否则 build-common.ps1 被策略拦截而脚本仍 exit 0 不产出）；单测 212/212；模块页烟雾 PASS、仪表盘烟雾（zh-CN）PASS、NetMap 烟雾 PASS。
- **异常与噪音**：本轮全部测量/烟雾会话（05:40-05:56）first-chance 为 socket IOException ×5-10 或 0 条；仅用户 05:09 会话出现 OpenProcess 风暴（x396/8s，见 L5）。
- **日志机制（保持）**：STARTUP_TIMING / NAV_TIMING 受 `log_settings.json` 级别控制（`off` 时静默）；`Debug.WriteLine` 经 Logger 的 Trace 监听器直达日志文件、不受级别控制；复测协议：置 trace → 测量 → 恢复。
- 已定决策（保持 + 新增）：动画系统原生；chips 令牌化彩色；QA 3 处 hex 例外；AIHub 单文件；动态键族（`Text(shortKey)`、`State{suffix}`）；材质透出链（§3.1）；Dashboard `UsageProgressBar` 类别语义色板（CPU 青、内存紫、存储绿、GPU 琥珀，亮/暗双色，硬编码于 UsageProgressBar.cs）。

## 1. 执行纪律

- 只动 UI/UX 与展示层；x:Name / AutomationId / x:Uid 契约零改动；文案 en-us + zh-CN 同步；新增面板走半透明材质体系。
- 性能类改动：先按 §0 的日志机制取 STARTUP_TIMING / NAV_TIMING 基线，改后用同一协议复测（`pwsh tools\diagnostics\Get-KitDiagnostics.ps1 -Days 2`）。
- 涉及运行数据的结论必须先部署到 `Projects\Kit` 再测（或用 x64/Debug 直启并注明路径差异）。
- 核心零改动守卫与验证链同 §6；越界项单列提交。

## 2. 加载速度

### L1 Debug→Release 对照（已测结案：无显著差异）

- Release 2.3.13 已构建并 stage；runner 路径实测：主窗段 339（Debug）→ 346（Release）、激活 963 → 991、首帧 1321 → 1353，均在 ±3% 内。结论：启动耗时由框架/XAML 段主导，**换 Release 无显著启动收益**；日常维持 Debug，Release 用于发布。备注：单样本 + trace 协议，如需更严谨可再采 2-3 次。

### L2 首帧体验（已验证）

- runner 路径 2.3.13：navigate 先于 activate（navigate 段 151-156ms，随后 activated 与 navigated 同刻 963/991），首帧体验符合预期。
- 遗留：无 runner 的 `#if DEBUG` 分支仍为旧顺序（Activate→Navigate），仅开发路径受影响，低优先级。

### L3 主窗段优化（仪表已落地；优化未做）

- 两大头与细分（见 §0 表）：placement 段 144-155ms = titlebar extended 45-68 + deserialize 31-33 + SetWindowPlacement 47-54；ShellPage 段 167-170ms 几乎全在 `InitializeComponent()`（XAML 膨胀）。
- 动作 A（placement 段，MainWindow.xaml.cs:42-57）：候选：`ExtendsContentIntoTitleBar` 首触后置到首帧后（需评估视觉影响）、placement 文件预读、`SetWindowPlacement` 延后到首帧后、`AppWindow.Changed`（:57）订阅后置。
- 动作 B（ShellPage 段，ShellPage.xaml.cs:110-133）：代码段（nav 绑定 1ms + VM 初始化 14ms）已可忽略，优化必须动 ShellPage.xaml 本身：导航项图标/模板延迟、页脚精简、或评估 XAML 编译策略。
- 验收：主窗段（ctor→SetTitleBar）≤ 250ms（当前 339-346ms）。

### L4 GeneralPage 冷访问（未完成；x:Load 未加）

- 2.3.13 复测：冷访问 573ms（navigate 186）/ 652ms（navigate 269），与 2.3.11/2.3.12 持平。
- 动作：给 AI 面板区（`GeneralPageAiHubMasterCard` 起的服务配置区）加 `x:Load`（惰性元素+绑定），或把绑定改为激活后赋值。验收：GeneralPage 冷访问（navigate+loaded）≤ 250ms。

### L5 Dashboard 常驻开销（去重与死代码已完成；OpenProcess 风暴未闭合）

- 已完成（2.3.13，已剔除）：`UpdateUptimeTick()` 重复调用移除（uptime 每秒一次）；`Helper.AllowRunnerToForeground` 死代码删除（其测试断言已随 212/212 修复一并更新）。
- **OpenProcess 风暴证据**：用户 05:09 会话（NetMap 烟雾）x396/8s ≈ 51/s，`Win32Exception E_FAIL`，栈顶 `System.Diagnostics.ProcessManager.OpenProcess`；受控复跑同一 NetMap 烟雾（05:42）0 条；其余会话（含 Release 测量）0 条。
- **已排除项**（静态核查）：`RunnerHelper`（原生 OpenProcess，非托管栈）、`SystemResourceInspector`（纯 GetSystemTimes/GlobalMemoryStatusEx）、`KernelProcessRunner`（无进程调用）、`PowerToysPID` 路径、nvidia-smi（Process.Start）。
- **剩余候选**：LocalserverViewModel 资源采样（1s telemetry timer → `ProcessResourceInspector.Sample` → `GetProcessById` + `TotalProcessorTime`/`WorkingSet64`，ProcessResourceInspector.cs:108-110；Win32Exception 已捕获于 :125 但仍记录 first-chance）；`PortInspector`/`ServiceRunner`（仅 Localserver 活跃时）。**注**：当前 settings 中 Localserver 模块为 disabled，与风暴会话不符 → 未闭合，勿下结论。
- **下一步实验**：① 复跑 NetMap 烟雾 ×3 统计复现率；② 以提权 shell 运行烟雾（验证"对端不可打开"假说）；③ 给采样路径加降噪（Access-Denied 预检 + 失败缓存，或 CrashLog 忽略名单）。

### L6 CrashLog 采样（已完成，剔除）

- `RecordFirstChance` 已改为先判上限再取栈（2.3.13），原遗留关闭。

### L7 网络失败风暴（未做）

- 每次会话仍出现 socket IOException ×5-10（`AwaitableSocketAsyncEventArgs.ThrowException`）。定位来源（NetMap 地理库下载 / AIHub kernel 更新 / 更新检查在受限网络下的重试），加退避与预期失败静默。验收：FirstChanceSummary 网络类计数下降 >90%。

### L8 App 启动前段（部分完成；修正 + 字典审计未做）

- 2.3.13 已加 2 条仪表，但 "App ctor enter" 位于 `Logger.InitializeLogger` 之前 → 日志未初始化被吞（所有会话均无此行）。修正：ctor 入口先捕获 `StartupStopwatch` 值，Logger 初始化后补记。
- App InitializeComponent 段实测 ≈ 181-183ms（runner 路径；含 App.xaml 全部 11 个合并字典）。动作：审计可延后的字典（候选：`TimelineStyles` → NetMap 页面资源；`KeyVisual`/`KeyCharPresenter`/`IsEnabledTextBlock`/`ShortcutWithTextLabelControl` → 仅快捷键相关页面），移出全局后复测；211ms 框架段（ctor→OnLaunched）不在 Debug 上优化。
- 验收：App ctor 段（进程→constructed）≤ 150ms（当前 182-194ms，细分后定最终目标）。

### L9 QuickAccess 呼出延迟仪表与测量（未做）

- QuickAccess.UI 全项目无 STARTUP_TIMING 仪表；runner 侧 `quick_access_host.cpp:215/248` 已用 `CREATE_SUSPENDED` + `ResumeThread` 预热进程，但冷/热呼出的实际延迟无数据。
- 动作：给 QuickAccess.UI 加同族仪表（App ctor / MainWindow ctor / ShellPage InitializeComponent / LaunchPage Loaded），取"首次呼出（冷）"与"预热后呼出（热）"两组数字；据此定优化目标。验收：两组数字入库并写入 L10 表。

### L10 验收协议与目标

| 指标 | 现状（2026-10-15 实测） | 目标 |
|---|---|---|
| 主窗段（ctor→SetTitleBar） | 339-346ms（placement 144-155 + ShellPage IC 167-170） | ≤ 250ms（L3） |
| App ctor 段（进程→constructed） | 182-194ms | ≤ 150ms（L8 细分后定） |
| Dashboard 首帧 | ~1321-1353ms（trace 协议；NAV 511-514） | ≤ 1100ms（L3 优化后复测） |
| GeneralPage 冷访问 | 573-652ms | ≤ 250ms（L4 完成 x:Load 后） |
| QuickAccess 冷/热呼出 | 未测量（无仪表） | 取得两组数字并定目标（L9） |
| Debug vs Release | 已结案：无显著差异（±3%） | —（L1 关闭） |
| OpenProcess first-chance / 会话 | 最高 ×396/8s（来源未闭合，候选见 L5） | 定位并归零（L5 实验） |
| 网络类 first-chance / 会话 | ×5-10（2.3.13 会话） | 定位并归零（L7） |
| 单元测试 | **212/212 全通过**（07:24 复跑） | —（已闭合） |
| 复测工具 | — | `Get-KitDiagnostics.ps1 -Days 2` + §0 日志协议 |

## 3. 主框架 & Settings 壳 UI/UX（Mica + WinUI 3）

### 3.1 材质审计结论（保持 + 2.3.13 补充）

- 透出链完整、无阻断：主窗 `MicaBackdrop Kind="BaseAlt"`（MainWindow.xaml:13）→ 透明根（ShellPage.xaml:85）→ `NavigationViewContentBackground` 透明覆盖（:151）→ 内容层 `LayerFillColorDefaultBrush`（:244）→ 半透明卡片；唯一实色面为对话框内容（系统设计）；QA `DesktopAcrylicBackdrop` 被测试钉死（BuildCompatibility.cs:3097-3107），勿换类型。
- 2.3.13 补充：Dashboard 系统概览卡改用 32×32 / 24×24 圆角徽标容器（`SubtleFillColorSecondaryBrush` + `CardStrokeColorDefaultBrush`，令牌化 ✓）；进度条 6px、`CornerRadius="3"`、轨道 `ControlFillColorDefaultBrush`（半透明，融入 Mica ✓）。
- 省电/远程桌面回退、DWM 圆角阴影：系统自动；ContentDialog / Flyout / NavigationView 面板材质：系统默认，不自定义。

### 3.2 已落地与遗留

- **T1 标题栏 HC（已完成，剔除）**：2.3.13 改为 HC 时将按钮四色置 `null` 恢复系统默认（不再用 `ResetToDefault()`），风险解除；HC 视觉核验并入 §4.2 手工过检。
- **T2 动态列表过渡（已完成）**：`DynamicListTransitions` 已用于 Localserver 服务表、UDPtest TCP/UDP 表、NetMap 服务列表。
- **T3 AIHub 过渡（已完成）**：6 处非工业虚拟化 ItemsControl 已补 `SettingsCardsAnimations`。
- **T5 首屏骨架（未定，数据支撑增强）**：首帧 ~1321-1353ms（trace）中布局/数据段仍占大头；待 L3 优化后复测决定是否加骨架。

## 4. 模块页与文案遗留（降级收尾）

已闭合（本轮剔除，不再跟踪）：单测 16 项（212/212）、备份位置校验 severity（降 Warning）、中文文案（网络地图 / 主题切换）。

### 4.1 i18n 收尾（未完成）

- **runner 托盘菜单未本地化（新）**：`src/runner/runner.base.rc` 的 `ID_TRAY_MENU` 为单语言英文（"Settings"/"Documentation"/"Close"；"更新可用"项走 `IDS_UPDATE_AVAILABLE_MENU_TEXT`，需一并核对语言来源），无 zh-CN LANGUAGE 块 → 中文系统下托盘菜单显示英文。方案：为 runner 增加 zh-CN 语言资源（或改从共享资源取串）。验收：zh-CN 系统下托盘菜单显示中文。
- 字符串迁移（未动）：UDPtest 57 + 11 处、AIHubPageViewModel 232 处、AiHubViewModel 13 处 IsChinese → resw（`UDPtest_*` / `AIHub_` 前缀，PRI277 逐一核对）。

### 4.2 其余（未动）

- HealthStyles 共享化（DashboardEgressStyles vs NetMapHealthStyles 去重，键名保持）。
- NetMap 死键 ~18（`Text()` 加白名单后清理）+ `AiHub_PipelineCard` 2 键（随 AiHubSettingsTests.cs:80 一并清）。
- a11y 收尾：LiveSetting 除 NetMap 外全缺；AccessibilityView AIHub/Localserver/UDPtest 为 0 处；General HeadingLevel 0 处。
- 手工过检：浅/深/HC × 全页（含 T1 的 HC 标题栏核验）+ 键盘导航 + 系统"动画效果"关闭时全静默。
- `UsageProgressBar` 类别色依赖 `AutomationProperties.Name` 字符串匹配（中英文关键字硬编码于 DetectCategory），脆弱耦合；后续可改显式附加属性（如 `kit:UsageCategory`）或构造参数。

## 5. 下一步批次（9–12，每批原子提交）

| 批次 | 内容 | 验收 |
|---|---|---|
| 9 | 部署 2.3.13 到 `Projects\Kit`（bin 已 stage，含 i18n 修复）；L9 QuickAccess 仪表与冷/热测量；据结果定 T5 骨架 | 托盘实测 + Get-KitDiagnostics + QA 冷/热两组数字 |
| 10 | 性能优化批次：L3 两大头优化（placement 段 / ShellPage.xaml）、L8 修正（ctor-enter 补记）+ 合并字典审计、L4 加 x:Load | 构建 + 3 烟雾 + 主窗段 ≤ 250ms + GeneralPage ≤ 250ms |
| 11 | i18n 收尾：runner 托盘本地化 + 字符串迁移（§4.1）+ 死键清理 | zh-CN 托盘菜单中文 + 构建 + 源码扫描单测 |
| 12 | L5 采样降噪 + HealthStyles + a11y + 手工过检 + 文档/changelog | 全量回归 + crash.log 计数 |

## 6. 验证与回退

```bash
# 构建（本机须 -ExecutionPolicy Bypass；成功标志：exit 0 且输出含 "[BUILD] Local projects built"）
pwsh -ExecutionPolicy Bypass -File tools/build/build.ps1 -Platform x64 -Configuration Debug -Path "src/settings-ui"
```

```powershell
# 烟雾（pwsh 7 + Bypass；必须先关闭 Kit 全部进程）
pwsh -ExecutionPolicy Bypass -File tools\tests\SettingsUI\Invoke-ModulePageSmoke.ps1 -Configuration Debug
pwsh -ExecutionPolicy Bypass -File tools\tests\SettingsUI\Invoke-DashboardPageSmoke.ps1 -Configuration Debug -Language zh-CN
pwsh -ExecutionPolicy Bypass -File tools\tests\NetMap\Invoke-NetMapPageSmoke.ps1
```

```powershell
# 启动/异常基线（只读；取 STARTUP/NAV 前先按 §0 日志协议置 trace）
pwsh -File tools\diagnostics\Get-KitDiagnostics.ps1 -Days 2
```

- 单元测试：`Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.exe`（仓库根 `Debug\` 下；本机 vstest.console testhost 崩溃，用 MSTest 直接运行器；禁 `dotnet test`）。
- 部署：`tools\build\Stage-Debug.ps1` 产出 `bin\debug\<版本>` 后复制到 `C:\Users\Zen\Repos\Projects\Kit`（用户既有流程）。
- 回退点：`449cebf`（本稿基线；后续批次提交后以最新提交为准）。
