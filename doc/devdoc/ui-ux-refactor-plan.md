# Kit UI/UX 优化方案（v5）— 主框架 / Settings 壳 + 加载速度（下一步执行稿）

> 状态：**下一步执行稿（2.3.12 复审后）** ｜ 2026-10-14 ｜ 基线：`main@0b824cf`（v2.3.12）
> 本版变更：2.3.12（`0b824cf`）已落地 v4 的 L2 / L3 / L4（部分）/ L5（部分）/ L6 与 T1 / T2 / T3；本稿已剔除完成项，保留未完成项与复审新发现。
> 历史版本：v2 = `782c626`、v3 = `41f7bdd`、v4 = `748445e`（`git show <commit>:doc/devdoc/ui-ux-refactor-plan.md` 取回）。

## 0. 现状基线

- **部署形态**：`C:\Users\Zen\Repos\Projects\Kit` 仍为 **2.3.11（Debug）**；2.3.12 已提交但尚未重新 stage 部署 → 真实运行数据需先部署（`tools\build\Stage-Debug.ps1` 后复制，用户既有流程）。
- **2.3.12 复审验证（2026-10-14 深夜，本机实测）**：构建 exit 0；ModulePage / Dashboard（en+zh）/ NetMap 四项烟雾全 PASS；单测 212 项 = 196 通过 / 16 失败（与基线 `4da08bd` 完全相同的 16 项，见 §4.1）。
- **启动时序基线（2.3.11 实测，保留）**：Runner 61–923ms（avg 257ms）；Settings 主窗创建 +405ms（2.3.12 已插 6 个子阶段仪表，数字待部署后读）；首屏 First page loaded 940–1230ms。
- **2.3.12 实测（烟雾/临时会话，非部署路径）**：GeneralPage 冷访问 navigate 134–232ms / loaded 577–616ms（修前 navigate 260–279 / loaded 555–577）→ 总耗时基本持平，见 §2 L4；Dashboard 冷访问 ~500ms 未变。
- **异常风暴现状**：OpenProcess first-chance 风暴（x4668/2 天）来源**未确认**（v4 的 nvidia-smi 归因已被实测否定，见 §2 L5）；网络类（socket/Http）风暴仍在，未处理（§2 L7）。

## 1. 执行纪律

- 只动 UI/UX 与展示层；x:Name / AutomationId / x:Uid 契约零改动；文案 en-us + zh-CN 同步；新增面板走半透明材质体系。
- 性能类改动：先取 STARTUP_TIMING / NAV_TIMING 基线，改后用同一协议复测（`pwsh tools\diagnostics\Get-KitDiagnostics.ps1 -Days 2`）。
- 涉及运行数据的结论必须先部署 2.3.12（或更高）到 `Projects\Kit` 再测，避免用旧构建的数据下结论。
- 核心零改动守卫与验证链同 §6；越界项（如 sparse 清单）单列提交。

## 2. 加载速度

### L1 Debug→Release 对照（未做；最大单杠杆）

- 现状仍为 Debug 部署；2.3.12 无 Release staging（`TestResults\FrameworkReview` 里只有 `Debug-2.3.12.manifest.json`）。动作：用 `Stage-Release.ps1` 构建 Release，同协议测量 STARTUP_TIMING 对照；据结果决定日常部署。

### L2 首帧体验（已落地；效果待部署后确认）

- 2.3.12 已把 `NavigateToSection` 前移到 `Activate` 之前并新增 "Main window activated" 仪表（App.xaml.cs）。
- 遗留：真实收益需从托盘（runner 路径）打开一次 2.3.12 实测；若空壳仍明显，再评估 T5 骨架。

### L3 仪表化（已落地；产出动作未完成）

- 2.3.12 已在 `MainWindow` 插入 6 个子阶段：ctor enter / theme applied / placement restored / callbacks registered / InitializeComponent completed / SetTitleBar completed。
- 遗留：**部署后跑一次并读取子阶段数字**，定位 405ms 的大头，再决定是否做针对性优化。

### L4 GeneralPage 冷访问（部分生效；未完成）

- 已做：`AiServices` 改为惰性属性（`??=`）；`InitializeAsync`（策略加载）改为由 `CurrentPolicyContent` 触发的 `EnsurePoliciesLoaded`。
- 复审发现：GeneralPage.xaml **无任何 x:Load**，`x:Bind ViewModel.AiServices.*`（约 20 处绑定）在页面加载时即求值 → `AiHubViewModel` 本体（设置读取、引擎静态初始化、kernel 状态、readiness）仍随 GeneralPage 构造，仅策略加载被真正延后。实测 navigate 变快但 loaded 变慢，**冷访问总耗时基本持平（~577–616ms vs 修前 555–577ms）**。
- 动作：给 AI 面板区（`GeneralPageAiHubMasterCard` 起的服务配置区）加 `x:Load`（惰性元素+绑定），或把绑定改为激活后赋值。验收：GeneralPage 冷访问（navigate+loaded）≤ 250ms。

### L5 Dashboard 常驻开销（部分落地；含两处遗留 + 归因修正）

- 已做：`GpuInspector.SampleAsync` 加失败缓存（15s）与成功缓存（3s），nvidia-smi 进程 churn 由每秒一次降至最多每 3s 一次（保留此修复）。
- **遗留 1（清理）**：`uptimeTimer` 未移除，同时 `UpdateUptimeTick()` 又被加进 telemetry tick → uptime 每秒更新两次；二选一（建议删 uptimeTimer，由 telemetry tick 承担）。
- **归因修正（重要）**：v4 的"OpenProcess 风暴源自 nvidia-smi 每秒 spawn"**未被实测证实**——2.3.12 下 Dashboard 活跃 136s 的会话 first-chance 为 **0 条**，修前的短会话（40–60s，Dashboard 打开）同样为 0；而用户 17 分钟会话为 x635（首个异常出现在会话第 5 秒，紧随 `get_all_hotkey_conflicts` 派发）。风暴来源**仍待定位**。
- **未完成（bisect 协议）**：带 runner 启动，逐项只用一个功能（AIHub AI 审计/优化路由 / Localserver / NetMap MTR / 热键冲突窗口 / 仅浏览页面），每轮 ≥5 分钟后比对 crash.log 的 OpenProcess 计数收敛到具体来源。静态排查剩余候选：`KernelProcessRunner`（AI kernel 子进程，`WaitForExitAsync`/`HasExited`）、Localserver `ServiceRunner`（`Process.GetProcessById` 路径）、runner 参数 IPC 路径（`RunnerHelper.WaitForPowerToysRunner`）。注：Settings 进程内已确认的托管进程枚举仅剩 `Helper.AllowRunnerToForeground`（无调用者，疑似死代码，可顺带删除）。

### L6 CrashLog 采样（已落地；一处 minor 遗留）

- 已做：先按 (type, HRESULT) 查重、仅首次取栈（`FirstChancePrefixToKey`）。
- 遗留 2（minor）：达到 `MaxDistinctFirstChance` 上限后仍会先捕获 StackTrace 再丢弃；先判上限再取栈即可。

### L7 网络失败风暴（未做）

- 定位来源（NetMap 地理库下载 / AIHub kernel 更新 / 更新检查在受限网络下的重试），加退避与预期失败静默、必要时先做连通性预检。验收：FirstChanceSummary 网络类计数下降 >90%。

### L8 验收协议与目标（数字待部署 2.3.12 后重取）

| 指标 | 2.3.11 现状 | 目标 |
|---|---|---|
| Settings 首屏（First page loaded） | 940–1230ms | ≤ 800ms（L1+L2+L3 后） |
| GeneralPage 冷访问 | 555–577ms（2.3.12 持平） | ≤ 250ms（L4 完成 x:Load 后） |
| OpenProcess first-chance / 会话 | 最高 ×635（来源未定） | 定位并归零（L5 bisect） |
| 网络类 first-chance / 会话 | ×146–205 | 下降 >90%（L7） |
| 复测工具 | — | `Get-KitDiagnostics.ps1 -Days 2` + STARTUP/NAV 对照 |

### 日志机制备注（观察，不列任务）

- `log_settings.json` 由 General 页构造时按 GeneralSettings 写入（2.3.12 已加"内容不变则不写"）；用户 settings.json 无 `enable_logging` 键 → 文件当前为 `off`；runner 仅在启动时读取一次。如需"持续记录"，建议核对日志开关的持久化与两进程一致性。

## 3. 主框架 & Settings 壳 UI/UX（Mica + WinUI 3）

### 3.1 材质审计结论（2026-10-14，保持）

- 透出链完整、无阻断：主窗 `MicaBackdrop Kind="BaseAlt"`（MainWindow.xaml:13）→ 透明根（ShellPage.xaml:85）→ `NavigationViewContentBackground` 透明覆盖（:151）→ 内容层 `LayerFillColorDefaultBrush`（:244）→ 半透明卡片；唯一实色面为对话框内容（系统设计）；QA `DesktopAcrylicBackdrop` 被测试钉死（BuildCompatibility.cs:3097-3107），勿换类型。
- 省电/远程桌面回退、DWM 圆角阴影：系统自动；ContentDialog / Flyout / NavigationView 面板材质：系统默认，不自定义。

### 3.2 已落地（剔除）与遗留

- **T1 标题栏 HC（已落地，附风险）**：`UpdateCaptionButtons` 现于 HC 时调用 `AppWindowTitleBar.ResetToDefault()` 并返回。**风险**：官方文档称 `ResetToDefault()` 会"重置/切回系统标题栏"（即重置 `ExtendsContentIntoTitleBar`）→ HC 下可能同时出现系统标题栏与自定义 TitleBar（拖拽区、1px 顶边、布局破坏）。→ **未完成**：HC 下实测；若确认，改为 ResetToDefault 后重新 `ExtendsContentIntoTitleBar = true` + `SetDragRegionForCustomTitleBar()`，或仅恢复系统颜色（不调用 ResetToDefault）。
- **T2 动态列表过渡（已落地）**：App.xaml 新增 `DynamicListTransitions`（AddDeleteThemeTransition + RepositionThemeTransition），已用于 Localserver 服务表、UDPtest TCP/UDP 表、NetMap 服务列表；日志流式容器未加（符合计划）。
- **T3 AIHub 过渡（已落地）**：6 处非虚拟化 ItemsControl（:820/:825/:849/:923/:1101/:1147）已补 `SettingsCardsAnimations`（仅 Reposition；findings 整批替换场景可接受，未加 AddDelete）。
- **T5 首屏骨架（未定）**：L2 方案 A 已落地；是否仍需要骨架，待 runner 路径实测首帧后决定。

## 4. 模块页与文案遗留（降级收尾）

### 4.1 单测 16 项修复（未动；2.3.12 复审复测仍为同样 16 项）

| 族 | 失败测试（文件:行） | 建议动作 |
|---|---|---|
| 实验特性移除遗留 ×6 | BuildCompatibility.cs:1104 / 1721 / 3093；General.cs:131 / 458；FrameworkPrivacyDefaults.cs:331 | 按"已移除"改写断言 |
| 版本与元数据 ×2 | BuildCompatibility.cs:1342（sparse 仍 2.3.7.0，版本已 2.3.12，差距扩大）；General.cs:748（README 措辞） | sparse 版本随 `Version.props`（越界项单列提交）；README 断言更新 |
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
| 8 | 2.3.12 收尾：L4 加 x:Load、L5 删 uptimeTimer + OpenProcess bisect、T1 HC 实测与修复、L6 上限微调、清死代码 | 构建 + 3 烟雾 + NAV 复测 + crash.log 计数 |
| 9 | 部署 2.3.12 到 `Projects\Kit`，读真实首屏与 MainWindow 子阶段数字；据结果定 L1 Release 对照与 T5 | 从托盘实测 + Get-KitDiagnostics |
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
- 部署：`tools\build\Stage-Debug.ps1` 产出 `bin\debug\<版本>` 后复制到 `C:\Users\Zen\Repos\Projects\Kit`（用户既有流程）。
- 回退点：`0b824cf`（本稿基线；后续批次提交后以最新提交为准）。
