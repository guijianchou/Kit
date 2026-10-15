# Kit UI/UX 优化方案（v6）— 主框架 / Settings 壳 + 加载速度（下一步执行稿）

> 状态：**下一步执行稿（2026-10-15 实测刷新）** ｜ 基线：`main@0b824cf`（v2.3.12；本轮无新代码变更，HEAD 另含文档提交 `20ec501`）
> 本版新增：对 VS Debug 构建（2026-10-15 03:32，与提交源码一致）的实测——主窗子阶段拆解、Dashboard 首帧、日志机制确认；批次 8 目标据此细化。已完成项不再保留。
> 历史版本：v2 = `782c626`、v3 = `41f7bdd`、v4 = `748445e`、v5 = `20ec501`（`git show <commit>:doc/devdoc/ui-ux-refactor-plan.md` 取回）。

## 0. 现状基线

- **部署形态**：`x64\Debug` 构建产物已是 **2.3.12**（2026-10-15 03:32 VS 构建，Kit.exe / Kit.Settings.exe 均 2.3.12.0，与提交源码一致）；日常实例 `C:\Users\Zen\Repos\Projects\Kit`（`WinUI3Apps\Kit.Settings.exe`）仍为 **2.3.11.0**（2026-10-13 23:31）；2.3.12 staging 目录（`bin\debug\2.3.12`）不存在 → 部署待执行（`tools\build\Stage-Debug.ps1` 后复制，用户既有流程）。
- **2026-10-15 实测（x64/Debug 无 runner 启动路径，进程起点 = 0）**：

| 阶段 | 时刻 | 增量 |
|---|---|---|
| App constructed | 72ms | 72 |
| OnLaunched | 283ms | +211（App.xaml 合并字典 + 框架初始化） |
| MainWindow 实例化（含 WindowEx 基类建窗） | 392ms | +109 |
| theme applied | 393ms | +1 |
| placement restored（含 `ExtendsContentIntoTitleBar` 首触） | 553ms | **+160** |
| callbacks registered | 566ms | +13 |
| InitializeComponent 完成（ShellPage 构建） | 756ms | **+190** |
| SetTitleBar 完成 | 757ms | +1 |
| First page loaded (DashboardPage) | 1274ms | **+517**（navigate 156 + 布局/渲染/数据 ~360） |

- **NAV_TIMING（2.3.12）**：DashboardPage 冷访问 516ms（navigate 156ms）——与 2.3.11 持平（Dashboard 未改动）；GeneralPage 冷访问 577–616ms（见 L4）。
- **结论**：主窗段（392→757 = **365ms**）两大头 = placement/Extends 段 160ms + ShellPage InitializeComponent 190ms；全局最大单段 = Dashboard 首帧 ~517ms。
- **异常与噪音**：本轮会话 crash.log 0 条 first-chance；无 runner 启动时出现 x15 socket（热键冲突 IPC 无对端所致，测试环境噪音，非真实场景）。
- **日志机制（实测确认）**：① STARTUP_TIMING / NAV_TIMING 走 ManagedCommon Logger，受 `log_settings.json` 级别控制（`off` 时静默）；② `Debug.WriteLine`（如 `=== PAGE LOADED ===`）经 Logger 注册的 Trace 监听器直达同一日志文件，**不受级别控制**；③ 复测协议：先把 `%LOCALAPPDATA%\Kit\log_settings.json` 置 `{"logLevel":"trace"}` 再启动，测后按需恢复。
- 已定决策（保持不动）：动画系统原生；chips 令牌化彩色；QA 3 处 hex 例外；AIHub 单文件；动态键族（`Text(shortKey)`、`State{suffix}`）；材质透出链（§3.1）。

## 1. 执行纪律

- 只动 UI/UX 与展示层；x:Name / AutomationId / x:Uid 契约零改动；文案 en-us + zh-CN 同步；新增面板走半透明材质体系。
- 性能类改动：先按 §0 的日志机制取 STARTUP_TIMING / NAV_TIMING 基线，改后用同一协议复测（`pwsh tools\diagnostics\Get-KitDiagnostics.ps1 -Days 2`）。
- 涉及运行数据的结论必须先部署 2.3.12（或更高）到 `Projects\Kit` 再测（或用 x64/Debug 直启并注明路径差异）。
- 核心零改动守卫与验证链同 §6；越界项（如 sparse 清单）单列提交。

## 2. 加载速度

### L1 Debug→Release 对照（未做；最大单杠杆）

- 现状仍为 Debug 部署；2.3.12 无 Release staging。动作：用 `Stage-Release.ps1` 构建 Release，同协议测量 STARTUP_TIMING 对照；据结果决定日常部署。

### L2 首帧体验（已落地；效果待 runner 路径确认）

- 2.3.12 已把 `NavigateToSection` 前移到 `Activate` 之前并新增 "Main window activated" 仪表（App.xaml.cs runner 路径）。
- 遗留：无 runner 的 `#if DEBUG` 分支仍是旧顺序（Activate→Navigate），如需在开发路径验证先同步该分支；真实收益待部署后从托盘打开实测。

### L3 主窗段细分与优化（数字已取得；优化未做）

- 实测拆解见 §0：**placement/Extends 段 160ms** 与 **ShellPage InitializeComponent 段 190ms** 为两大头。
- 动作 A（placement 段）：在 `ExtendsContentIntoTitleBar = true` 与 `DeserializePlacementOrDefault/SetWindowPlacement` 之间再插一个仪表定位 160ms 归属；候选优化：placement 文件预读、`SetWindowPlacement` 延后到首帧后、`AppWindow.Changed` 订阅后置。
- 动作 B（ShellPage 段）：在 ShellPage 构造内部插 4 个仪表（NavHelper×8 / SetWindowTitle / ShellViewModel ctor / ViewModel.Initialize / IPCResponseService.RegisterForIPC），定位 190ms 归属；候选优化：导航项图标/模板延迟、ShellViewModel 初始化后置。
- 验收：主窗段（ctor→SetTitleBar）≤ 250ms（当前 365ms）。

### L4 GeneralPage 冷访问（部分生效；未完成）

- 已做（2.3.12）：`AiServices` 惰性属性；`InitializeAsync`（策略加载）由 `CurrentPolicyContent` 触发。
- 未完成原因：GeneralPage.xaml **无任何 x:Load**，`x:Bind ViewModel.AiServices.*`（约 20 处）在页面加载时即求值 → `AiHubViewModel` 本体仍随页面构造；实测 navigate 260→134–232ms，但 loaded 555→577–616ms，总耗时持平。
- 动作：给 AI 面板区（`GeneralPageAiHubMasterCard` 起的服务配置区）加 `x:Load`（惰性元素+绑定），或把绑定改为激活后赋值。验收：GeneralPage 冷访问（navigate+loaded）≤ 250ms。

### L5 Dashboard 常驻开销（部分落地；含两处遗留 + 归因修正）

- 已做（2.3.12）：`GpuInspector.SampleAsync` 失败缓存 15s / 成功缓存 3s（进程 churn 降低，保留）。
- **遗留 1**：`uptimeTimer` 未移除，同时 `UpdateUptimeTick()` 又被加进 telemetry tick → uptime 每秒更新两次；二选一（建议删 uptimeTimer）。
- **归因修正**：OpenProcess 风暴（x4668/2 天）**不来自** Dashboard 遥测——修后 Dashboard 活跃 136s 会话 0 条 first-chance；今日新会话同样 0 条；风暴来源**仍待定位**。
- **未完成（bisect 协议）**：带 runner 启动，逐项只用一个功能（AIHub AI 路由 / Localserver / NetMap MTR / 热键冲突窗口 / 仅浏览页面），每轮 ≥5 分钟后比对 crash.log 的 OpenProcess 计数。静态候选：`KernelProcessRunner`、Localserver `ServiceRunner`（`Process.GetProcessById` 路径）、runner 参数 IPC 路径。Settings 进程内托管进程枚举仅剩 `Helper.AllowRunnerToForeground`（无调用者，死代码，顺带删）。

### L6 CrashLog 采样（已落地；一处 minor 遗留）

- 遗留：达到 `MaxDistinctFirstChance` 上限后仍会先捕获 StackTrace 再丢弃；先判上限再取栈即可。

### L7 网络失败风暴（未做）

- 定位来源（NetMap 地理库下载 / AIHub kernel 更新 / 更新检查在受限网络下的重试），加退避与预期失败静默、必要时先做连通性预检。验收：FirstChanceSummary 网络类计数下降 >90%。

### L8 验收协议与目标

| 指标 | 现状（2026-10-15 实测） | 目标 |
|---|---|---|
| 主窗段（ctor→SetTitleBar） | 365ms（placement 160 + ShellPage 190） | ≤ 250ms（L3） |
| Dashboard 首帧 | ~517ms（navigate 156 + 布局/数据 ~360） | ≤ 350ms（细分仪表后定，或 T5 骨架兜底） |
| Settings 首屏（runner 路径，待部署复测） | 2.3.11 为 940–1230ms | ≤ 800ms（L1+L2+L3 后） |
| GeneralPage 冷访问 | 577–616ms | ≤ 250ms（L4 完成 x:Load 后） |
| OpenProcess first-chance / 会话 | 最高 ×635（来源未定） | 定位并归零（L5 bisect） |
| 网络类 first-chance / 会话 | ×146–205 | 下降 >90%（L7） |
| 复测工具 | — | `Get-KitDiagnostics.ps1 -Days 2` + §0 日志协议 |

## 3. 主框架 & Settings 壳 UI/UX（Mica + WinUI 3）

### 3.1 材质审计结论（2026-10-14，保持）

- 透出链完整、无阻断：主窗 `MicaBackdrop Kind="BaseAlt"`（MainWindow.xaml:13）→ 透明根（ShellPage.xaml:85）→ `NavigationViewContentBackground` 透明覆盖（:151）→ 内容层 `LayerFillColorDefaultBrush`（:244）→ 半透明卡片；唯一实色面为对话框内容（系统设计）；QA `DesktopAcrylicBackdrop` 被测试钉死（BuildCompatibility.cs:3097-3107），勿换类型。
- 省电/远程桌面回退、DWM 圆角阴影：系统自动；ContentDialog / Flyout / NavigationView 面板材质：系统默认，不自定义。

### 3.2 已落地与遗留

- **T1 标题栏 HC（已落地，附风险）**：HC 时调用 `AppWindowTitleBar.ResetToDefault()`。**风险**：官方文档称其会"重置/切回系统标题栏"（即重置 `ExtendsContentIntoTitleBar`）→ HC 下可能同时出现系统标题栏与自定义 TitleBar。→ 未完成：HC 下实测；若确认，改为 ResetToDefault 后重新 `ExtendsContentIntoTitleBar = true` + `SetDragRegionForCustomTitleBar()`，或仅恢复系统颜色。
- **T2 动态列表过渡（已落地）**：`DynamicListTransitions`（AddDelete + Reposition）已用于 Localserver 服务表、UDPtest TCP/UDP 表、NetMap 服务列表；日志流式容器未加（符合计划）。
- **T3 AIHub 过渡（已落地）**：6 处非虚拟化 ItemsControl（:820/:825/:849/:923/:1101/:1147）已补 `SettingsCardsAnimations`（仅 Reposition，可接受）。
- **T5 首屏骨架（未定，价值上升）**：Dashboard 首帧 ~517ms 的布局/数据段给了骨架明确的用武之地；待 L3 细分仪表后决定。

## 4. 模块页与文案遗留（降级收尾）

### 4.1 单测 16 项修复（未动；2.3.12 复审复测仍为同样 16 项）

| 族 | 失败测试（文件:行） | 建议动作 |
|---|---|---|
| 实验特性移除遗留 ×6 | BuildCompatibility.cs:1104 / 1721 / 3093；General.cs:131 / 458；FrameworkPrivacyDefaults.cs:331 | 按"已移除"改写断言 |
| 版本与元数据 ×2 | BuildCompatibility.cs:1342（sparse 仍 2.3.7.0，版本已 2.3.12）；General.cs:748（README 措辞） | sparse 版本随 `Version.props`（越界项单列提交）；README 断言更新 |
| GeneralPage 结构 ×2 | General.cs:158 / 707 | 按现布局更新断言 |
| 品牌与文案 ×3 | General.cs:469 / 486 / 514 | 按现文案/资产更新断言 |
| 模块目录 ×2 | General.cs:539 / 589（NetMap 后为 6 项） | 断言更新为 6 项 |
| 运行器启动清理 ×1 | BuildCompatibility.cs:1218（`WM_CLOSE`） | 核对 61d6ed4 意图后更新断言 |

### 4.2 字符串迁移（未动）

- UDPtest 57 + 11 处、AIHubPageViewModel 232 处、AiHubViewModel 13 处 IsChinese → resw（`UDPtest_*` / `AIHub_` 前缀，PRI277 逐一核对）。

### 4.3 其余（未动）

- HealthStyles 共享化（DashboardEgressStyles vs NetMapHealthStyles 去重，键名保持）。
- NetMap 死键 ~18（`Text()` 加白名单后清理）+ `AiHub_PipelineCard` 2 键（随 AiHubSettingsTests.cs:80 一并清）。
- a11y 收尾：LiveSetting 除 NetMap 外全缺；AccessibilityView AIHub/Localserver/UDPtest 为 0 处；General HeadingLevel 0 处。
- 手工过检：浅/深/HC × 全页 + 键盘导航 + 系统"动画效果"关闭时全静默。
- 死代码：`Helper.AllowRunnerToForeground`（无调用者）。

## 5. 下一步批次（8–12，每批原子提交）

| 批次 | 内容 | 验收 |
|---|---|---|
| 8 | 主窗与首帧：L3 细分（placement 段 / ShellPage 段）并优化两大头；L4 加 x:Load；L5 删 uptimeTimer + OpenProcess bisect；T1 HC 实测与修复；L6 上限微调；清死代码 | 构建 + 3 烟雾 + 主窗段 ≤ 250ms + crash.log 计数 |
| 9 | 部署 2.3.12 到 `Projects\Kit`，取 runner 路径真实首屏；据结果定 L1 Release 对照与 T5 骨架 | 从托盘实测 + Get-KitDiagnostics |
| 10 | 单测 16 项修复（§4.1） | 212/212（或记录无法修复项与原因） |
| 11 | 字符串迁移（§4.2）+ 死键清理 | 构建 + 源码扫描单测 |
| 12 | HealthStyles + a11y + 手工过检 + 文档/changelog | 全量回归 |

## 6. 验证与回退

```bash
# 构建（exit 0 = 成功；失败先读 src/settings-ui/build.debug.x64.errors.log）
pwsh tools\build\build.ps1 -Platform x64 -Configuration Debug -Path "src\settings-ui"
```

```powershell
# 烟雾（pwsh 7；必须先关闭 Kit 全部进程）
pwsh -File tools\tests\SettingsUI\Invoke-ModulePageSmoke.ps1 -Configuration Debug
pwsh -File tools\tests\SettingsUI\Invoke-DashboardPageSmoke.ps1 -Configuration Debug -Language zh-CN
pwsh -File tools\tests\NetMap\Invoke-NetMapPageSmoke.ps1

# 启动/异常基线（只读；取 STARTUP/NAV 前先按 §0 日志协议置 trace）
pwsh -File tools\diagnostics\Get-KitDiagnostics.ps1 -Days 2
```

- 单元测试：`Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.exe`（本机 vstest.console testhost 崩溃，用 MSTest 直接运行器；禁 `dotnet test`）。
- 部署：`tools\build\Stage-Debug.ps1` 产出 `bin\debug\<版本>` 后复制到 `C:\Users\Zen\Repos\Projects\Kit`（用户既有流程）。
- 回退点：`0b824cf`（本稿基线；后续批次提交后以最新提交为准）。
