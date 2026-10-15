# Kit UI/UX 优化方案（v4）— 主框架 / Settings 壳 + 加载速度（下一步执行稿）

> 状态：**下一步执行稿** ｜ 2026-10-14 ｜ 基线：`main@41f7bdd`（v2.3.11 + 清理轮，已合入并验证）
> 本版重心（用户指定）：**主框架 Kit + Settings 壳的 UI/UX** 与 **加载速度**；证据来自 `%LOCALAPPDATA%\Kit` 日志（STARTUP_TIMING / NAV_TIMING 仪表 + crash.log），可用 `tools\diagnostics\Get-KitDiagnostics.ps1` 复现。
> 历史版本：v2 as-built 取 `git show 782c626:doc/devdoc/ui-ux-refactor-plan.md`；v3 取 `git show 41f7bdd:doc/devdoc/ui-ux-refactor-plan.md`。

## 0. 现状基线（测量）

- **部署形态**：日常运行的 `C:\Users\Zen\Repos\Projects\Kit` 由 `tools\build\Stage-Debug.ps1` 从 `x64\Debug` 发布，即 **Debug 构建**（`Stage-Release.ps1` 已存在但未使用）。
- **Runner 启动**（近 20 次会话）：min 61ms / avg 257ms / max 923ms（历史最大 1639ms）；托盘图标 74–188ms；模块逐个 1–75ms。结论：达标，保持。
- **Settings 启动**（典型会话，进程起点 = 0）：

| 阶段 | 时刻 | 增量 |
|---|---|---|
| App constructed | 65ms | 65 |
| OnLaunched | 229ms | 164（App.xaml 合并字典 + 框架初始化） |
| IPC started | 233ms | 4 |
| **Main window created** | **684ms** | **+405（最大段，无子阶段仪表）** |
| Startup page navigated | 837ms | +153 |
| **First page loaded (Dashboard)** | **1186ms** | **+349** |

- **页面冷访问 / 二次访问**（NAV_TIMING）：General 555–577 / 67–94ms（navigate 260–279ms 最慢）；Dashboard 500 / 76–129；AIHub 490–595 / 105–152；NetMap 255–285 / 20–27；LightSwitch 250 / 125–230；UDPtest 239 / 36–49；Localserver 188 / 51–65；Awake 174 / 59–86。
- **异常风暴**（crash.log 近 2 天采样计数）：OpenProcess Win32Exception ×4668（单会话最高 ×635，集中于 Dashboard 活跃期，约每秒 0.6 次）；Socket IOException ×2192；HttpRequestException ×1114 + ×216（受限网络下的重试）；其余 InvalidCast / FileNotFound / EventLog 属既有噪音。
- 已定决策（保持不动）：动画系统原生；chips 令牌化彩色；QA 3 处 hex 例外；AIHub 单文件；动态键族（`Text(shortKey)`、`State{suffix}`）；材质透出链（§3.1）。

## 1. 执行纪律

- 只动 UI/UX 与展示层；x:Name / AutomationId / x:Uid 契约零改动；文案 en-us + zh-CN 同步；新增面板走半透明材质体系。
- 性能类改动：先取 STARTUP_TIMING / NAV_TIMING 基线，改后用同一协议复测（`pwsh tools\diagnostics\Get-KitDiagnostics.ps1 -Days 2`）。
- 核心零改动守卫与验证链同 §6；越界项单列提交。

## 2. 加载速度（本版重心）

### L1 Debug→Release 对照（最大单杠杆）

- 现状运行 Debug 构建；Release 可显著降低 XAML 解析、JIT 与 IL 开销。动作：用 `Stage-Release.ps1` 构建一份 Release 部署，按同一协议测量 STARTUP_TIMING 对照；据结果决定是否切换日常部署。

### L2 首帧体验（窗口 684ms 可见，内容 1186ms 才到，空壳约 0.5s）

- 方案 A（首选，无新 UI）：把 `NavigateToSection` 前移到 `Activate` 之前，让页面构造（~150ms）发生在窗口尚不可见时；需验证首布局/Loaded 是否依赖激活，依赖则回退。
- 方案 B（配合 T5）：shell 内容区加轻量骨架（ProgressRing / 占位），把空壳期变为"有意的加载态"。

### L3 仪表化补齐（先测量后优化）

- "Main window created" +405ms 无子阶段。在 `MainWindow` 构造关键步骤（ThemeService.ApplyTheme / placement 恢复 / InitializeComponent(ShellPage) / SetTitleBar / 回调接线）插入 `LogStartupTiming` 子阶段，一轮构建定位大头，再做针对性优化。

### L4 GeneralPage 冷访问 555ms（navigate 260ms）

- 根因之一：`GeneralViewModel` 构造时即 `AiServices = new AiHubViewModel(...)`（文件 IO + `AiHubEngine.Current` 静态初始化 + kernel 状态扫描 + readiness 检查），即使 AI 面板未展开。
- 动作：AI 面板 VM 惰性化（首次展开 AI 区或进入 AIHub 页时构造）；顺带核对 `SyncLogSettingsFile` 每次构造写盘无额外 IO。验收：GeneralPage navigate < 150ms。

### L5 Dashboard 常驻开销（1s 遥测）

- 现状：`telemetryTimer` 1s + `force:true` → 每秒 `ReadCpuMemoryAsync`（含 250ms 采样窗）+ `ReadGpuTelemetryOnlyAsync`；后者在 NVIDIA 机器上 **每秒 spawn 一次 `nvidia-smi` 子进程**（`GpuInspector.SampleAsync` 每次 `Process.Start`）。
- 影响：每秒一次进程创建/回收（CPU、磁盘、杀软扫描）；且**高度疑似 OpenProcess first-chance 风暴来源**（时间吻合：×635/17min ≈ 每秒 0.6 次且仅 Dashboard 活跃期；栈为 `ProcessManager.OpenProcess`；本机对子进程句柄操作另有同类失败先例）。`DashboardProbeCache` 的 5s WaitAsync 超时也会贡献 TimeoutException 噪音。
- 动作：GPU 遥测降频至 3–5s（CPU/内存保持 1s）；`nvidia-smi` 失败/超时结果加短期缓存（避免每 tick 重复失败）；uptime 与 telemetry 两个 1s 定时器合并为一个 tick；改后以 crash.log 计数验证因果（OpenProcess 计数应大幅下降或归零）。

### L6 CrashLog 采样微调（minor）

- `RecordFirstChance` 对重复异常仍执行 `new StackTrace(ex)`；建议先按键查重、仅在首次采样时取栈（键改为 type+HRESULT 级别，site 只记录一次）。

### L7 网络失败风暴（x2192 socket + x1114/x216 Http）

- 定位来源（NetMap 地理库下载 / AIHub kernel 更新 / 更新检查在受限网络下的重试），加退避与预期失败静默、必要时先做连通性预检。验收：FirstChanceSummary 网络类计数下降 >90%。

### L8 验收协议与目标

| 指标 | 现状 | 目标 |
|---|---|---|
| Settings 首屏（First page loaded） | 940–1230ms | ≤ 800ms（L1+L2+L3 后） |
| GeneralPage 冷访问 | 555–577ms | ≤ 250ms（L4） |
| OpenProcess first-chance / 会话 | 最高 ×635 | 0（L5 后验证） |
| 网络类 first-chance / 会话 | ×146–205 | 下降 >90%（L7） |
| 复测工具 | — | `Get-KitDiagnostics.ps1 -Days 2` + STARTUP/NAV 对照 |

### 日志机制备注（观察，不列任务）

- `log_settings.json` 由 General 页构造时按 GeneralSettings 写入；用户 settings.json 无 `enable_logging` 键 → 文件当前为 `off`；runner 仅在启动时读取一次。如需"持续记录"，建议核对日志开关的持久化与 runner/Settings 两进程的一致性。

## 3. 主框架 & Settings 壳 UI/UX（Mica + WinUI 3）

### 3.1 材质审计结论（2026-10-14，保持）

- 透出链完整、无阻断：主窗 `MicaBackdrop Kind="BaseAlt"`（MainWindow.xaml:13）→ 透明根（ShellPage.xaml:85）→ `NavigationViewContentBackground` 透明覆盖（:151）→ 内容层 `LayerFillColorDefaultBrush`（:244）→ 半透明卡片（Card ×22 / SubtleFill ×60）；唯一实色面为对话框内容（系统设计）；QA `DesktopAcrylicBackdrop` 被测试钉死（BuildCompatibility.cs:3097-3107），勿换类型。
- 省电/远程桌面回退、DWM 圆角阴影：系统自动；ContentDialog / Flyout / NavigationView 面板材质：系统默认，不自定义。

### 3.2 待办

- **T1 标题栏高对比回退**：`TitleBar.WASDK.cs::UpdateCaptionButtons` 硬编码白/黑/DarkGray → 检测 HC 时跳过覆写并 `ResetToDefault()`。
- **T2 动态列表增删过渡**：App.xaml 增共享 `TransitionCollection`（AddDeleteThemeTransition + RepositionThemeTransition）用于 Localserver 服务表（:233-238）、UDPtest TCP/UDP 表（:563/:606）、NetMap 服务列表（:515）；`HopList`（ListView）系统默认已带容器过渡，核对未被覆盖即可。
- **T3 AIHub 过渡补齐**：AIHubPage 现无任何 ChildrenTransitions → 非虚拟化 ItemsControl 的 ItemsPanel 补 SettingsCardsAnimations（:820/:825/:849/:923/:1101/:1147）；x:Load 与 MaxHeight 虚拟化参数不动。
- **T5 首屏加载态**：配合 L2 的骨架/进度（若 L2 方案 A 已达标则降级为可选）。
- T4 ConnectedAnimation：不做（Win11 原生设置无此模式）。

## 4. 模块页与文案遗留（降级收尾）

### 4.1 单测 16 项修复（六族；基线 `4da08bd` 即红，非重构/清理轮引入）

| 族 | 失败测试（文件:行） | 建议动作 |
|---|---|---|
| 实验特性移除遗留 ×6 | BuildCompatibility.cs:1104 / 1721 / 3093；General.cs:131 / 458；FrameworkPrivacyDefaults.cs:331 | 按"已移除"改写断言 |
| 版本与元数据 ×2 | BuildCompatibility.cs:1342（sparse 停留 2.3.7）；General.cs:748（README 措辞） | sparse 版本随 `Version.props`（越界项单列提交）；README 断言更新 |
| GeneralPage 结构 ×2 | General.cs:158 / 707 | 按现布局更新断言 |
| 品牌与文案 ×3 | General.cs:469 / 486 / 514 | 按现文案/资产更新断言 |
| 模块目录 ×2 | General.cs:539 / 589（NetMap 后为 6 项） | 断言更新为 6 项 |
| 运行器启动清理 ×1 | BuildCompatibility.cs:1218（`WM_CLOSE`） | 核对 61d6ed4 意图后更新断言 |

### 4.2 字符串迁移

- UDPtest 57 + 11 处、AIHubPageViewModel 232 处、AiHubViewModel 13 处 IsChinese → resw（`UDPtest_*` / `AIHub_` 前缀，PRI277 逐一核对）。

### 4.3 其余

- HealthStyles 共享化（DashboardEgressStyles vs NetMapHealthStyles 去重，键名保持）。
- NetMap 死键 ~18（`Text()` 加白名单后清理）+ `AiHub_PipelineCard` 2 键（随 AiHubSettingsTests.cs:80 一并清）。
- a11y 收尾：LiveSetting 除 NetMap 外全缺；AccessibilityView AIHub/Localserver/UDPtest 为 0 处；General HeadingLevel 0 处。
- 手工过检：浅/深/HC × 全页 + 键盘导航 + 系统"动画效果"关闭时全静默。

## 5. 下一步批次（8–12，每批原子提交）

| 批次 | 内容 | 验收 |
|---|---|---|
| 8 | 加载速度：L3 仪表化 → L2 首帧 → L4 General 惰性 → L5 遥测降频（含 L1 Release 对照测量、L6） | 构建 + 烟雾 + STARTUP/NAV 复测 + crash.log 计数 |
| 9 | 壳 UI/UX：T1 标题栏 HC、T2/T3 过渡、T5 骨架（若未并入 8） | 构建 + 3 烟雾 + 手动（HC、动画开关） |
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

# 启动/异常基线（只读）
pwsh -File tools\diagnostics\Get-KitDiagnostics.ps1 -Days 2
```

- 单元测试：`Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.exe`（本机 vstest.console testhost 崩溃，用 MSTest 直接运行器；禁 `dotnet test`）。
- 回退点：`41f7bdd`（本稿基线；后续批次提交后以最新提交为准）。
