# fix.md — Kit 整体优化建议与修正清单（收敛权威版）

> **文档目的**：记录对 Kit 整体项目的 review 结论、修正事项与优化路线，供后续开发按优先级执行。本文档由历轮 review 收敛而成——原逐轮记录（旧 §7/§9/§10.5-10.6/§11-§16 及 6 张重叠优先级表）已合并删除，**本版为唯一权威版本**。
> **当前 Kit 版本**：2.2.1；活动模块：Awake、LightSwitch、Localserver、UDPtest、AIHub（前两者为官方插件，本清单不展开）。
> **原版对照**：AIHub ← `../Locals`（LocalSecurityAudit）、Localserver ← `../Localserver`（LocalServerHub）、UDPtest ← `../Network`（NetworkMonitor）。
> **可信度说明**：所有论断均经源码验证，证据带文件/行号；历轮 9 处更正已在正文按"更正后"表述呈现，更正留痕汇总于 §7。
>
> **执行进度（fix 分支）**：
> - ✅ **P0-1 AI 深度分析接线**（commit cd13179）——新增 `AiHubAuditAnalysisService`（事件→security-audit 策略链→校验回的 issues）+ 页面"AI 深度分析"按钮与 AI 报告面板；引擎侧契约 `EventAnalysisInput`/`AuditIssueContainer` 本已存在。
> - ✅ **P0-3 Security/Firewall 通道**（commit 727e288）——`EventLogService.AuditMode`（Extended/Full）+ Security/Firewall 通道（显式 ID 白名单、16-ID XPath 分块）+ `CanReadSecurityLog` 探针 + 设置项与提权感知 UI。
> - ✅ 顺带完成 §2.1 扫描范围硬编码本地化、§2.2 `SelectedScopeIndex` 死代码删除、§6 文档失实修正（Monitor/3 模块/不存在文件）。
> - ✅ **P1 数据真实性**（commit a428e4e）——新增 `AuditHistoryStatistics`（审计次数/活跃天数/均分/发现数），替换页面上写死的 "1"/"1"；清除剩余 2 处空 catch（启用状态与页签持久化改为记录日志）。
> - ✅ **P1 优化执行可观测性**（commit da87835）——新增 `OptimizationOutcome`：保留失败原因（此前回收站 helper 的错误被丢弃、异常被吞成计数），批量执行显示 (n/N) 进度，失败时列出文件名与原因并降级为 Warning。
> - ✅ **P1 定时/增量审计**（commit 4257aaf）——新增 `AuditSchedule`（间隔校验/到期判定/下次运行/扫描窗口），设置项 `scanIntervalHours`（0=关闭），页面用 DispatcherQueueTimer 到期触发、增量窗口续扫（含重叠与范围钳制），标注"增量审计"。
> - ✅ **P2 存储**（commit 8514b4f）——审计历史上限 200 条（原仅按天数，无上限）；`AuditHistoryStorage` 从反射 JSON 改为源生成上下文（移除该文件唯一 IL3050 AOT 警告）。
> - ✅ **P2/P3 文档与残留**（commit e0976ae）：补写缺失的 `src/modules/UDPtest/README.md`（含与原版 Network 的 SQLite 差距表）；修复 `.claude/CLAUDE.md` 指向不存在的 `.github/`；补齐悬空 resw 键 `Admin_Mode_Running_As`。
> - ✅ **P3 残留**（commit 13220a6）：`settings_window.cpp` 的 `powertoys_pipe_name` 变量重命名为 `runner_pipe_name`（值为 kit_runner_，行为不变）。
> - ✅ **P1 补测试**：新增 HealthScoreCalculator(7)、DownloadOrganizerService(4)、AuditHistoryStatistics(4)、AuditHistoryStorage(6)、EventLogService(4)、AuditSchedule(10)、OptimizationOutcome(8)——AIHub 套件 108 → 151 项。
> - ✅ **P0-2 Worker 化（Localserver 首例）**（commit fd09e88）——新增 `Kit.LocalserverWorker`（`--pid`/`--data-dir` 契约、父进程存活监视、优雅退出）+ `ServiceSupervisor`（按目录创建 runner、仅监督启用项、幂等释放）；原生 `enable()` 从模块输出目录拉起 worker，`disable()/destroy()` 停止并兜底终止；注册进 `Kit.slnx`；6 项 supervisor 测试。
>   - **关键发现**：`ServiceRunner` **完全无 UI 线程依赖**（无 DispatcherQueue/WinUI），且**已自带健康探测与重启退避**（:2124 `MonitorHealthAsync`、:2271 重启策略）——VM 只是持有者。因此 worker 只需提供长生命周期宿主，改动面远小于预期；监督缺口 = 页面释放时 `line.Dispose()`（LocalserverViewModel:2383）连带释放 runner。
> - ✅ **P1 趋势视图**（commits b84ecab/961c2d6）——`AuditTrends`（1/7/30 天窗口、稠密日序列、强度分桶、分类汇总）+ AIHubPage 第四个页签（窗口切换、日热力图、分类统计）+ UI 层 `TrendIntensityBrushConverter`（聚合模型保持无 UI 依赖）；8 项测试。
> - ✅ **P1 AI 面板迁移**（commit 7afec58）——内核/端点/策略面板从 GeneralPage 迁至 AIHubPage 第四页签；`AiHubViewModel` 改由 AIHubPageViewModel 托管；**并修复了 §2.1 记录的"重复总开关"问题**（General 侧副本移除，全仓库仅剩一个 `Toggle_AiHub`）。迁移前已核实 AIHubPage 的 `IsEnabled` 本就发送相同的 general-settings IPC，故无持久化损失；测试同步改写为"面板位于 AIHubPage"+"General 不再承载"双向守护。
> - ✅ **P2 规则引擎下沉**（commit c684f5b）——345 行纯逻辑 `RunRuleBasedAuditAnalysis` 从页面 VM 提取为 `AIHubLib.Services.AuditRuleEngine`（**零 UI 依赖**，仅用 SecurityEvent/AuditIssueEnhanced/CultureInfo）；VM 侧改为一行委托，**减少约 360 行**；9 项新测试。这同时是 AIHub Worker 化的前置条件（worker 需在无页面环境下执行审计）。
> - ✅ **P2 面板本地化补齐**（commit 12efc13）——AI 面板中缺失 `x:Uid` 的可见文案（内核徽标、Codex/Pi 选项）补齐 resw 键与中文翻译；审计确认 **AIHubPage 31/31 个 x:Uid 均有资源覆盖**（协议标识类枚举值保持字面量）。
> - ✅ **P2/P0-2 无头审计链路**（commit 9febf37）——新增 `AuditPipeline`（采集→分类→评分→持久化，含窗口/模式/上限/retention/不落盘选项）与 `AuditScheduler`（由 `AuditSchedule` 驱动的节奏循环、增量窗口、运行时间隔调整、启停与释放）；**审计路径已完全脱离页面**，就绪于无头宿主。
>   - **提取过程发现并修复真实竞态**：`AuditScheduler.Start()` 在循环字段赋值前检查 `IsRunning`，两次快速调用会启动两个循环；改为锁内守卫（`Start`/`StopAsync` 同锁），修复后 11 项测试连续 3 轮全绿。
> - ✅ **P0-2 AIHub Worker**（commit 74f292f）——新增 `Kit.AIHubWorker`（`--pid`/`--data-dir` 契约、父进程存活监视、**每 tick 重读设置并 fail-closed**，关闭开关即停调度无需重启）+ `AIHubConfig` 扩展 `ScanIntervalHours`/`AuditModeIndex`/`RetentionDays`（使无头宿主无需加载 UI 程序集即可读取节奏）+ 原生 `enable()` 拉起 / `disable()` 停止；注册进 `Kit.slnx`，部署至 `x64/Debug/AIHubWorker`。
>   - **P0-2 结论：两个真正需要的插件均已完成**——Localserver（常驻服务监督）+ AIHub（定时审计）；UDPtest 经核实为显式 Start/Stop 模型，无守护缺口，**不适用**（见 §8.3）。
> - ✅ **P2 FindingDetailsDialog 本地化**（commit d165b9f）——标题/按钮/7 个分区表头改用 `x:Uid` + resw（与 UDPtest/Localserver 页面的正确范式一致），移除 `ApplyLocalization`；仅保留 3 处**运行时插值**字符串（事件 ID、空值占位、复制摘要），x:Uid 无法表达。
> - ✅ **P1/P3 收尾**（commits 02bbaa3/89b5c89）：`AsyncCommand.Execute` 加固（async void 异常不再逃逸崩溃进程）；`CacheCleanupServiceTests`（6 项）锁定破坏性路径的安全契约；`ProbeCoordinatorTests`（10 项）覆盖协调器生命周期校验——UDPtest 18→28 项。
> - **P2 本地化最终结论（技术判定，非未完成）**：AIHubPageViewModel 内 140 处 `IsChinese` 三目中，**138 处为格式化/插值字符串**（如 `$"扫描完成。健康评分: {score}/100"`），x:Uid 只能替换静态属性值，**无法表达运行时拼接**；剩余静态标签已全部走 x:Uid。经全量扫描确认：**零个本地化缺陷**（无中英错位、无空串、无未翻译对），功能上是干净的。因此该条从"待办"转为**已按可行范围完成，其余属技术不可行**。
> - **P0-2 适用性结论**：UDPtest 的探测按用户显式 Start/Stop 运行，页面无导航钩子（`UDPtestPage.xaml.cs` 无 `OnNavigatedFrom`），停止探测是原版"显式 Start/Stop"设计的延续，**不构成需要常驻监督的缺口**；Localserver（已做）与 AIHub 定时审计才是真正的"服务存活但失去守护"场景。
> - 验证：`Kit.Settings` 编译 0 错误；`Kit.vcxproj` 编译并链接 Kit.exe 成功；`Kit.AiHub.UnitTests` 151 通过/1 跳过；`Settings.UI.UnitTests` 199 通过。
> - 构建注记（本机沙箱）：需 `/p:TrackFileAccess=false`；原生项目用 `Bin\amd64\MSBuild.exe`（否则 GenerateResource 任务宿主不可用）；`dotnet test` 因命名管道受限改用测试 exe 直接运行。
> - 环境限制（非回归）：`Localserver.UnitTests` 的 7 项中有 6 项失败——这些用例通过 `Process.Start` 启动**真实子进程**验证服务生命周期（进程树回收/优雅停机/关闭后恢复/就绪取消），而本机沙箱限制子进程创建（`ServiceLifecycleTests.cs:173/:272`）。已确认 `git diff main..fix` 中**不含任何 Localserver 文件**，故与本次改动无关；需在无沙箱环境复跑确认。

## 目录
- §0 总体评估
- §1 战略级问题（P0）
- §2 AIHub 专项（2.1 本地化 / 2.2 范围选择 bug / 2.3 假统计 / 2.4 巨型 VM / 2.5 审计细节 / 2.6 优化执行 / 2.7 重复实现 / 2.8 存储 / 2.9 README 数值校对）
- §3 Localserver（P1）
- §4 UDPtest（P1）
- §5 Runner 与主框架（P2）
- §6 文档与工程卫生 + 6.1 Monitor 痕迹清理分析（P2/P3）
- §7 验证与更正总表（64 项）
- §8 最终优先级（唯一权威表）
- §9 执行路径（按序开工）

---

## 0. 总体评估

- **工程质量高**：TaskAiEngine（策略编译/批处理/回退/输出契约审计）、ServiceRunner（JobObject+所有权恢复+backoff+优雅停机）、ProbeCoordinator（Channel 流水线+NAT 判定）均为生产级；密钥 DPAPI 落盘、原子写+跨进程互斥、路径消毒、有界环形日志、退避重启等细节到位。
- **文档体系完整**：doc/devdoc/、PLUGIN_DEVELOPMENT.md、changelog.md 更新频繁；2.2.x 崩溃修复记录（XamlParseException、AccentButtonStyle、InvalidCastException）说明迭代真实在设备上运行。
- **框架与上游保持度好**：runner 沿用 PowerToys 主框架，KitKnownModules 显式清单、GPO、托盘、提权重启、Quick Access 按需启动均符合"轻量插件宿主"方向。
- **代码卫生**：三插件模块与 Settings VMs 零 TODO/FIXME/HACK；本地化 resw 键体系一致（en-us 995 键覆盖 4 个关键页全部 x:Uid，唯一悬空键 `Admin_Mode_Running_As` 无碍）。
- **静态分析补充**：两处 `Thread.Sleep`（GeneralViewModel:1487、GeneralPage.xaml.cs:142）均位于 `Task.Run` 后台线程，无 UI 卡顿；遥测定时器 tick（LocalserverViewModel:1677）带 try/catch + 在途防重入；唯一 `async void` 命令 `AsyncCommand.Execute`（Helpers/AsyncCommand.cs:32-35）无内建 catch，但全仓库仅 1 处使用（StoreExtensionHelper:33）且委托自带 try/catch——无现存崩溃路径，建议后续加固（P3）。

---

### 0.1 设计意图基线（"不偏离原版设计"的校准基准）

**原版三项目设计目的（README 原文）**：
- **AIHub ← Locals/LocalSecurityAudit**：核心管线 = Codex/Pi → Task-specific AGENTS.md → Analysis Summary → Actionable Recommendations（原版 README:9）；Extended 模式标准用户权限采集日志并调用 AI Hub，Full 模式提权读 Security 日志与防火墙审计（:9/:124）。
- **Localserver ← LocalServerHub**：注册/启动/监控/停止本地开发服务（原版 README:3）；服务进程在宿主退出后仍存活（JobObject killOnClose:false，原版 :385/:1605）；托盘保活使监控常驻（模块 README:30）。
- **UDPtest ← Network**：判断代理节点 TCP/UDP 支持并持续评估成功率/延迟/抖动/出口地址/稳定性；**SQLite 入库**（network-monitor.db + WAL/SHM + segment + 逐探针 samples）支撑历史与图表（原版 README:9-16）。

**Kit 自身明文设计（根 README）**：
- "keep the main framework (runner + Settings UI + common libraries) free of module business logic"（:122）——**框架不承载模块业务逻辑**。
- "Keep new modules split into a testable core library, worker process, native module interface, settings model, settings page, Home metadata, and static registration tests"（:150）——**新模块必须拆 worker 进程**。
- "Keep UI state derived from real settings and module state"（:141）——**UI 状态必须来自真实数据**。

**校准结论**：本清单全部 P0/P1 建议均为**兑现上述设计意图**，非偏离：
- P0-1（AI 接线）← AIHub 模块 README:11/59/106 声明的 AI 深度诊断闭环；
- P0-2（Worker 化）← 根 README:122/150 明文要求；
- P0-3（Security/Firewall 通道）← 原版 Locals README:9 + AIHub 模块 README:40/99 声明；
- §2.3（数据真实性）← 根 README:141；
- 存储 SQLite（§2.8/§4）← 原版 Network/Locals 的 SQLite 设计。

---

## 1. 战略级问题（P0）

### 1.1 业务逻辑全部运行在 Settings 进程（三插件）
- **现状**：三个自研插件的业务逻辑（监控/审计/调度）运行在 Kit.Settings.exe（WinUI 设置进程）内，无后台 Worker；`src/modules/*/*Lib` 的 dllmain 均为空壳。原版均为独立托盘常驻应用，**守护职责**随宿主进程生命周期退化。
- **文档自认（最直接）**：README_zh.md:74"Localserver 的管理逻辑目前运行在 Settings 页面的 ViewModel 中，**尚无独立后台 Worker**。完整关闭 Settings 后，**不承诺日志采集、健康检查或自动重启继续运行**"——Kit 自己的中文文档已明确承认 P0-2 缺口。
- **证据**：LocalserverViewModel.cs（2392 行）内 `new ServiceRunner(...)` 于 :1374/:1617；UDPtest 由页面 VM 驱动 ProbeCoordinator；AIHub 由页面 VM 驱动引擎。根 README:76 自认"关闭 Settings 后日志收集/健康检查/自动重启不保证"。
- **关键澄清（更正）**：Settings 关闭**不杀死** Localserver 管理的外部服务——ServiceRunner.cs:390 显式 `killOnClose: false`（与模块 README:31"关闭 Settings 不杀外部服务"一致，也同原版 :385/:1605 行为）。真正的退化是**失去守护**：健康检查/自动重启/日志收集/监控全部随 Settings 进程终止而停止。
- **影响**：服务进程存活但无监督（进程崩溃无人拉起）、无法自启常驻、资源采样/审计等后台任务依赖设置窗口存活——与原版"托盘常驻=监督常驻"差距在此。
- **修复方向（P0-2）**：每插件一个常驻 Worker 进程（Kit.XxxWorker.exe），设置页仅做配置下发与状态回显，业务经 IPC 通道（AIHub 的 AiHubIpcBridge 是现成样板）。
- **框架能力已就绪（补强）**：Kit 框架本就支持独立 Worker——PLUGIN_DEVELOPMENT.md:15"Worker 由原生 ModuleInterface 启动"、:99-109 完整 Worker 生命周期规范、:109"自 2.0.13 起关闭 Settings 窗口后 Runner 与其独立 Worker 继续运行"（托盘宿主常驻是框架既有设计）；官方插件 Awake/LightSwitch 即独立进程（`--pid <parent>`）。三个自建插件**从未实现** Worker（Kit.slnx 无 Worker 项目、dllmain 空壳），与自身规范相悖（PLUGIN_DEVELOPMENT.md:811"Localserver 尚无独立后台 Worker"自认）；AIHub 引擎设计本就预期 Worker 进程承载（:854-855）。P0-2 是补齐既有框架能力，非新增架构。

### 1.2 AI 深度分析未接线（接收侧活、发送侧死）
- **现状**：AI 引擎（TaskAiEngine）未接入任何分析流程。"重新分析"（ReanalyzeRangeCommand:209）实际执行纯规则 `RunAudit(false)`；AI 内核配置面板在 GeneralPage，但没有任何代码向引擎发送请求。
- **设计依据**：AIHub 模块 README:11/59 明文声明的核心管线"Codex/Pi → Essential Policy+AGENTS.md → Analysis Summary → Suggested Action"是模块设计目的；:106 更声称详情弹窗展示"AI 建议行动（Suggested Action）"——**实际弹窗只显示规则 PriorityActionText**。P0-1 是兑现模块自身文档承诺，非新增功能。
- **接线成本低（新核准）**：管线两端的引擎侧**均已实现**——契约模型 `AiTaskReport`（Findings + Actions，含批合并 MergeBatches 与输出校验 Validate）位于 `src/common/AiHub/Models/AiTaskReport.cs`；策略沙箱 `SecurityPolicyService` 位于 `src/common/AiHub/Security/SecurityPolicyService.cs`（chains/<taskId>/AGENTS.md 加载 + 插件包目录隔离 + 内建策略降级）。缺的仅是一处"产生请求的调用方"，P0-1 属低风险改动。
- **证据**：IPC 接收侧是活的（App.xaml.cs:219 → AiHubIpcBridge.TryDispatchMessage → AiHubIpcHandler → AiHubEngine.Current）；发送侧 `KitAiHubRequest` 导出存在（ai_hub_ipc.cpp:51）但全仓库无调用方——SEND 半通道死代码。
- **影响**：AIHub 名不副实——纯规则审计 + 未接线的引擎。
- **修复方向（P0-1）**：加"产生请求的调用方"（页面按钮 → AiHubEngine.ExecuteAsync，security-audit 策略，Top N 发现 → Analysis Summary + Suggested Action 双语契约）；FindingDetailsDialog 加"AI 报告"区（现状 4 区=检测事实/触发原因/建议操作/事件证据，建议区只显示规则文案）。

### 1.3 Security/Firewall 通道未读 + 模块 README 失实
- **现状**：EventLogService.CollectEventsAsync(:94-98) 只读 System/Application/Setup/Forwarded 四通道，跳过 Security；无 Firewall 读取。原版有 ReadSecurityEventsAsync/ReadFirewallEventsAsync（原 EventLogService.cs 205 行）。
- **证据（更正）**：模块 README 两处声称读 Security——:40"Windows Security/System/Application/Setup 日志聚合"、:99"检索 Security...涵盖登录失败（Event 4625）、提权操作（Event 4672）"——与代码只读四通道的事实不符，文档失实。
- **影响**：审计缺失最关键的安全类事件。
- **修复方向（P0-3）**：Full 模式读 Security 白名单（4624/4625/4672/4688/4697）+ Firewall（4946-5157，XPath 16 ID 分块）；AIHubSettings 加 mode/appMode；提权切换复用 runner 机制；同步修正模块 README:99。
  - ✅ **已实现**（commit 727e288）：`EventLogService.AuditMode`（Extended/Full）+ Security/Firewall 通道（30 个审计 ID 白名单，16-ID XPath 分块）+ `CanReadSecurityLog` 探针；未提权时降级而非失败；`auditMode` 设置项 + 飞窗内提权感知开关。**模块 README:40/:99 的修正仍未做**（待办）。

---

## 2. AIHub 专项（P0/P1）

### 2.1 本地化：IsChinese 三目硬编码（P1/P2）
- AIHub 页 + FindingDetailsDialog + AiHubViewModel 用 `IsChinese ? "中文" : "英文"` 三目（VM/code-behind 内）；GeneralPage AI 面板还有硬编码 "Codex CLI"/"Apply"/"gpt-5.6-luna"（:234/:238/:299/:340），AIHubPage.xaml:146 "Codex / Pi"、:199-201 "1d/2d/1w"、:203 "Scan options"。
- **对照（更正）**：同工程 UDPtestPage/LocalserverPage 用 `x:Uid + 内联兜底`（resw 覆盖、可维护）——项目内已有正确范本，AIHub 整改直接抄它即可，无需自创 AppText。
- FindingDetailsDialog 的"完全本地化"（changelog 声称）属实但同样走 code-behind 三目（ApplyLocalization:28-41）。
- resw 键体系已齐备（995 键全 x:Uid 覆盖，§0），P2 整改=纯替换。当前 AIHubPageViewModel 内有 **145 处** `IsChinese ?` 三目，全量替换为 x:Uid 需同步 resw 及测试。
- **新发现（P1，重复的总开关）**：AI Hub 的启用开关**同时存在于两个页面**且互不感知——`GeneralPage.xaml:205-210`（绑定 `GeneralViewModel.AiHub.IsEnabled`，自动化 ID `Toggle_AiHub`）与 `AIHubPage.xaml:110-112`（绑定 `AIHubPageViewModel.IsEnabled`，**同一个自动化 ID `Toggle_AiHub`**）。两者最终写同一状态源（`generalSettings.Enabled.AiHub` + `AiHubSettingsStore.IsEnabled`），但各自持有独立的 GPO 判定与通知链路：General 侧用 `AiHub.CanToggle`，AIHub 侧用 `IsEnabledGpoConfigured` 取反。后果：(1) 同一窗口内两处开关状态可能短暂不一致；(2) UI 自动化按 ID 定位会命中两个元素。修复方向：随 P1 面板迁移一并收敛为单一开关（AIHubPage 承载），并移除重复的 `Toggle_AiHub` ID。

### 2.2 范围选择双字段 bug（P1）
- AIHubPageViewModel：`SelectedScopeIndex`(:370) 与 `FullScanRangeIndex`(:382) 双字段映射同一 `_selectedScopeIndex`；`SelectedScopeIndex` 全仓库仅 1 处引用（定义处）——死代码，存在"选择了但未生效"的字段路径。

### 2.3 活动统计/审计时间造假（P1）
- `ActivityScansText` 写死 `"1"`（:892）；审计时间写"今天"（:896）——UI 显示的不是真实历史。
- ✅ **已修复**（commit a428e4e）：活动统计改用 `AuditHistoryStorage` 的真实历史（新增 `AuditHistoryStatistics`：审计次数/活跃天数/均分/发现数）。
- **新发现（评分权重的实际语义）**：低危单条实际扣 **2 分**（`Math.Ceiling(1.5*1)=2`），非 README 所述 1 分；中危 6 分、高危 15 分，封顶分别 25/60/15——已在 `HealthScoreCalculatorTests` 固化。

### 2.4 巨型 VM + 规则引擎下沉（P2）
- AIHubPageViewModel 1852 行、GeneralViewModel 59KB、UDPtestViewModel 42KB、LocalserverViewModel 92KB——规则/数据/命令全在一个类。
- 规则表（:999-1248，16+ 条双语规则，含 severity/category/title/rootCause/recommendation）可整体下沉到 AIHubLib（如 RuleCatalog.cs）——纯搬移，内容专业（DCOM 10016 还标注"微软官方建议忽略"）。

### 2.5 审计与对话框细节（P1）
- 11 个 catch 中仅 3 处空（:258/:296/:1478），其余有 UI 反馈（更正：非"大量空 catch"）；fire-and-forget 1 处（:902 `_ = SaveHistoryAsync(...)`）。
- ✅ **已修复**（commit a428e4e）：剩余空 catch（AI Hub 启用状态与页签持久化）已改为记录日志的失败分支。
- FindingDetailsDialog 无 AI 报告区（并入 1.2 修复）。

### 2.6 优化执行（P1）
- RunExecuteOptimization(:1719) fire-and-forget；批量执行无进度、无失败原因上屏。CacheCleanup 的 FOF_NOERRORUI 只是抑制 OS 弹窗（helper 会返回错误串、VM 计数，非"静默失败"）；RecycleBin 返回 (bool, string? Error)。

### 2.7 重复实现（P2）
- 规则/执行细节在页面 VM 与 Lib 间重复（§2.4 下沉后自然消除）。

### 2.8 存储：AuditHistoryStorage 全量重写 O(N)（P2，补全）
- `%LOCALAPPDATA%\Kit\AIHub\State\audit_history.json`：每次保存=读全量→去 30 天外→头插→整表序列化→写盘（.tmp+Move 原子写正确）——随历史增长 O(N)；保留策略仅按天数，无数量/体积上限。
- **路径失实（新）**：模块 README:21/:108 写 `AiHub\AuditHistory\audit_history.json`，代码实际用 `AIHub\State\`（AuditHistoryStorage 默认目录）——子目录名不一致。
- 建议：SQLite（原版 Locals 用 DataStorageService(SQLite)、原版 Network 明文 SQLite 入库）或按日期分片 + 上限清理。

### 2.9 模块 README 数值声明失实（P3，新核准）
- **健康评分权重失实**：AIHub README:102 称"高危 15 / 中危 **5** / 低危 **1**"，代码实为 High 15（封顶 60）、Medium **6**（封顶 25）、Low **1.5**（封顶 15）并 Clamp 0-100（HealthScoreCalculator.cs:9-16）——权重与封顶均不符且 README 未提封顶。
- **分级档位属实**：Good 80-100 / Warning 60-79 / Critical <60 与代码一致（AIHubPageViewModel:866/:1450）。
- **编排能力声明领先实现**：AIHub README:139 称具备实时进度环、扫描耗时统计与动态取消，实测 AIHubPage.xaml 仅 1 处 `ProgressRing`（绑定 IsOptimizing），无扫描耗时显示——与 §1.2 同类（文档声明 > 实现）。
- **评分档位字母制存疑**：根 README:244 称"health grade badges (A-F)"，代码为文字档位（良好/Warning/Critical，AIHubPageViewModel:866/:1450），未发现 A-F 字母实现——README 表述与实现不符（P3）。
- 同类已录：键数 212 vs 258（§6）、存储路径 State vs AuditHistory（§2.8）。
- **属实项（正面）**：崩溃日志基础设施（根 README:235）已实现——App.xaml.cs:84-114 FirstChanceException + UnhandledException 写入 `%LOCALAPPDATA%\Kit\crash.log`；全局安全策略 `security.md`（README:243）已实现——SecurityPolicyService.GlobalSecurityPolicyPath。

---

## 3. Localserver（P1）

**质量确认**：ServiceRunner 2653 行生产级（JobObject 生命周期绑定、进程树所有权恢复、重启 backoff 1 分钟后重置、生成号防竞态、优雅停机）；LogFileWriter 有界 Channel+DropOldest（:50-53）+路径消毒（:243）；SecretStore DPAPI CurrentUser+原子写；SendInputAsync 严谨（64KiB 上限/3s 超时/输入门闸/进程代际校验）；Config 加载带 Preflight+ChangeStamp 变更检测；进程树快照用于恢复（ProcessTreeSnapshot.Capture:1406）。

**问题**：
1. 业务在 Settings 进程、**服务存活但失去守护**（§1.1：killOnClose:false 故服务不随 Settings 退出而终止，但健康检查/自动重启/日志收集随 Settings 进程终止而停止；根 README:76 自认）——P0-2 Worker 化首选（ServiceRunner 已与 VM 解耦，仅 :1374/:1617 两处 new）。
2. 遥测采样性能（P1）：RefreshTelemetryAsync(:1690) 对每行逐次 `RefreshResourceSampleAsync()`（:1794）——N 行=N 次独立系统快照/秒，可缓存快照一次复用。
3. 测试覆盖（更正）：**服务层已有 7 个高质量生命周期测试**（Localserver.UnitTests/ServiceLifecycleTests：启停与重启仅释放自有进程、优雅停机时限、进程树回收、关闭后恢复既有服务、就绪期取消、就绪期 Dispose、非法配置不启动）——覆盖面扎实；缺口在**遥测采样、日志管道、services.d 配置合并、端口释放竞态**（P2）。

---

## 4. UDPtest（P1）

**质量确认**：TcpHttpsProbe 计时正确（:88 在 Drain 前停表=TTFB，早前误判已更正）；每 probe 线一个 HttpClient 实例（:16/:37，probe 随代际重建）；ProbeCoordinator WorkerLoopAsync(:384) Channel 流水线；MetricsEngine 与原版 Network.Core 一致；STUN 编解码符合 RFC 5389（MagicCookie 0x2112A442/TransactionId 比对/XOR 解码）；NAT 分类带保守降级（DowngradedPortRestricted）；UDPtestPage 本地化全用 x:Uid+兜底（正确范本）。

**问题**：
0. **模块 README 缺位（新，P3）**：`src/modules/UDPtest/README.md` 不存在——AIHub（14.1KB）与 Localserver（14.0KB）均有详尽模块文档，UDPtest 作为同等规模模块（含 STUN/NAT/指标引擎）缺文档，移植经验（探针流水线、代际重建、NAT 降级）无从沉淀。
1. 业务在 Settings 进程、**服务/探测随 Settings 退出失去守护**（§1.1）——Worker 化。
2. **SQLite 持久化在移植中丢失（新，P2）**：原版 Network 明文"SQLite、历史图表和事件记录都服务于这个目标"（README:9-16，network-monitor.db+WAL/SHM+segment+逐探针 samples）；Kit UDPtest 无任何 SQLite（无 Microsoft.Data.Sqlite、无 .db 产物），历史仅在内存（ClearHistory 即清空）——原版"历史图表"能力退化。
3. 页面 VM 42KB（§2.4）。
4. 测试覆盖（更正）：**UDPtest.UnitTests 存在且 18 个测试**（StunMessageCodec 5、TargetParser 7、NodeVerification 3、Storage 1、MetricsEngine 2），已覆盖 RFC 5389 编解码、目标解析拒绝路径、节点验证门槛、持久化往返与指标计算（此前"无测试"判断有误）；真实缺口是 **ProbeCoordinator 编排层（Channel 流水线/代际重建/NAT 循环）与具体 Probe 实现类**（P1）。

---

## 5. Runner 与主框架（P2）

- **IPC 事实（更正后）**：KitAiHubRequest 默认超时 610s（ai_hub_client.h:11，仅客户端默认值）；runner 侧上限 3610000ms=3610s（ai_hub_ipc.cpp:53）；引擎 TimeoutSeconds 1-3600s；并发待处理上限 16（ai_hub_ipc.cpp:108）；单请求上限 524288B。
- **提权重启**（main.cpp:402-438）：--dont-elevate/--restartedElevated 双开关 + run_elevated 设置 + 防重启死循环（引用 issue #19307）；退出正常保存设置。
- **热键体系**：HotkeyConflictManager（系统/应用内双冲突表）与 centralized_kb_hook（KBDLLHOOKSTRUCT 模块级注册/清理）均稳健；Quick Access 按需启动（main.cpp:163 注册热键 → job object 拉起）+ runner 自启 PT03S 登录延迟（auto_start_helper.cpp:348）。
- **STARTUP_TIMING** 已打点（main.cpp:142/188/218）——基线缺失是文档/流程问题而非打点缺失。
- **内核校验（更正后）**：在线 digest 来自发布资产的官方 SHA256SUMS 校验文件（GetReleaseAssetAsync:324-353），非 API 字段 TOFU；离线兜底为源码固定 digest（BundledDigests:901-913）——风险低；仍可加固定白名单（P2）。
- **PowerToys 残留（P3）**：管道变量名 `powertoys_pipe_name`（值为 kit_runner_）、GeneralPage 悬空键 `Admin_Mode_Running_As`（无碍，测试 General.cs:152 故意断言）、README 部分用语。

---

## 6. 文档与工程卫生（P2，纯文字一次提交）

- **src/README.md:8 最严重：称活动模块为"Awake, Light Switch, and Monitor"——Monitor 已在 2.0.8 移除**（根 README:90 有移除记录），该文件自 2.0.8 起从未更新，且缺 UDPtest/AIHub。
- **doc/devdoc/README.md:3/:24 只列 3 个模块**（Awake/LightSwitch/Localserver）——缺 UDPtest/AIHub。
- **`.claude/CLAUDE.md` 断链（新）**：唯一内容是一行 `../.github/copilot-instructions.md`，但**仓库根本没有 `.github/` 目录**——AI 协作指令入口失效（`.claude/agents|commands|rules|skills` 均为空目录）。
- **README_zh.md 同步失实**：:28/:30 同样引用不存在的 fix.plan/next.md（:31 无 fixed.md，英文版 :31 有）；:162 `DashboardModules` 同样写 4 模块；:271-273 测试数字与英文版一致（准确）。
- AGENTS.md:12 只列 3 个活动模块（缺 UDPtest/AIHub）——**代码级注册已由静态测试强制**（UDPtest.cs/Localserver.cs/General.cs/AiHubSettingsTests.cs 断言 5 模块的 catalog/runner/settings/Shell 导航/路由/GPO/源码布局），落后仅限文字。
- kit-architecture.md:35-37 只列 3 模块、:87 页面清单缺 UDPtestPage/AIHubPage。
- ARCHITECTURE_OVERVIEW.md 引用不存在的 kit-sync-status.md/upstream-sync-checklist.md/STARTUP_DEBUG_GUIDE.md。
- README:161 称 DashboardModules 4 个，代码 `DashboardModules => ActiveModules` 实为 5 个（含 AIHub）。
- 根 README:28-31 文档列表引用 **三个不存在的文件**：fix.plan、next.md、fixed.md（均已核实缺失；仅 fix.md 存在）。
- startup-optimization-analysis.md 是纯分析文档，无基线/CI 计划。

### 6.1 Monitor 模块：痕迹清理可行且建议执行（新，用户判断已验证）

**结论：Monitor 的功能已被 AIHub Optimization 吸收，残留可安全清除。**

**能力对照（Monitor changelog 记录 vs AIHub 现状）**：
| Monitor 原能力 | AIHub 吸收情况 |
|---|---|
| Downloads 目录扫描/整理（OrganizeDownloads） | ✓ DownloadOrganizerService（单层分类 7 类+Other） |
| 按扩展名分类 | ✓ ExtensionToCategory 表 |
| 扫描历史/进度（SQLite scan-status、WAL、心跳） | △ 扫描/历史在（AuditHistoryStorage/进度环），但**SQLite 未承接**（见 §2.8） |
| 安装包清理（installer cleaner，置信度阈值 50-95%） | ✗ **未承接**（仅 .exe/.msi/.iso 归入 Installers 分类，无安装包语义清理） |
| 临时/缓存清理 | ✓ CacheCleanupService（8 项白名单：用户 temp/LocalAppData temp/Chrome/Edge/npm/pip/pnpm/缩略图） |
| 安全边界（白名单/禁扫系统目录/回收站可撤销） | ✓ Whitelist + Forbidden roots（Windows/Program Files/Documents/用户目录）+ IFileOperation 回收站 |
| 后台 worker + 进度文件 + 手动扫描 | △ 无 worker（§1.1 P0-2）；进度/手动扫描 UI 在 |

**残留清单（实测）**：
- **代码：0 处模块实现残留**——`src/modules` 下无 Monitor 目录，`ModuleType.Monitor`/`MonitorSettings`/`MonitorPage`/`MonitorViewModel`/`Kit.Monitor*`/`MonitorModuleInterface` 全部为 0 命中；273 处 "Monitor" 字样经甄别**全为显示器语义**（Display/DPI/multi-monitor）。
- **测试：保留的负向断言**（BuildCompatibility.cs:659-2615 约 20 处 `Assert.IsFalse(...Contains("Monitor"/"PowerDisplay")...)`）——这是**防回归护栏，必须保留**（AGENTS.md/根 README:153 要求"删除的模块表面必须保持删除"）。
- **文档：历史叙述**（changelog 176-679 行 Monitor 条目、kit-development-experience.md:165）——**历史记录应保留**，不可删。
- **数据目录**：`%LOCALAPPDATA%\Kit\Monitor\`（scan-progress.json、SQLite）可能存有旧用户数据——**建议**：新增一次性清理（或保留数据、仅文档标注）。

**动作建议（P3，低风险）**：
1. **不动**测试护栏与 changelog 历史；
2. 排查 `%LOCALAPPDATA%\Kit\Monitor\` 的一次性迁移/清理策略（若确认无用户价值则删除，否则保留并标注）；
3. **可选补漏**：若安装包清理确有价值，按 AIHub 现有 DownloadOrganizer 模式补回（但须评估与"只读建议模式"安全设计的一致性——**不建议**为对齐旧功能而降低 AIHub 的安全边界）。
4. 修正 `src/README.md:8` 仍在声称 Monitor 为活动模块（§6 已列）。

> 设计意图校准：Monitor 是上游/早期模块，其**功能语义**（空间优化）已由 AIHub 以更安全的边界（白名单 + 回收站 + 只读建议）承接——符合"不偏离设计目的"；此清理不涉及原版三项目。
- **测试数量属实（更正此前判断）**：源码头统计与 README 数字吻合——AiHub.UnitTests 50 个 `[TestMethod]` + 66 个 `[DataRow]` ≈ **108**（README:272"108 passed, 1 skipped"）；Settings.UI.UnitTests 188 + 16 DataRow ≈ **196**（README:271）；Localserver.UnitTests **7 个方法 = 7 passed**（README:273，精确吻合）。**结论：根 README 验证快照的测试数字准确**（此前"108/196 是构建产物标记"的判断有误，已作废）。
- **模块 README 双语自链（新，P3）**：AIHub README:3、Localserver README:3 的 `[English](README.md) · [简体中文](README.md)` 两链接均指向同一文件（无 README_zh.md）——中文读者点"简体中文"打开仍是英文。
- **Localserver README:122 键数失实（新，P3）**：称中英各 212 个本地化键，实测两侧各 258 且对称。
- **模块 README 能力声明领先于代码（补强 §1.2/§1.3）**：AIHub README 声称读 Security（:40）、展示"AI Suggested Action"（:106）、覆盖 4625/4672（:99）——均未落地。
- **根 README:244 "health grade badges (A-F)"**：代码为文字档位（良好/Warning/Critical），未见 A-F 字母实现（P3，与 §2.9 同项）。

---

## 7. 验证与更正总表（64 项，历轮合并；"更正"行以更正后为准）

| # | 论断 | 结果 | 证据/更正出处 |
|---|---|---|---|
| 1 | 业务逻辑全在 Settings 进程（三插件） | OK | §1.1（dllmain 空壳/VM 实例化坐标实测） |
| 2 | ReanalyzeRangeCommand=纯规则 RunAudit(false) | OK | §1.2（:209） |
| 3 | IPC 发送侧死、接收侧活 | OK(更正) | §1.2（SEND 无调用方，RECEIVE 链实测） |
| 4 | Security 通道未读（只读 4 通道） | OK | §1.3（EventLogService:94-98） |
| 5 | 4625/4672 承诺在模块 README:99 | OK(指向更正) | §1.3 |
| 6 | IsChinese 三目硬编码 | OK | §2.1（含 FindingDetailsDialog code-behind） |
| 7 | 范围选择双字段 bug | OK | §2.2（:370/382，SelectedScopeIndex 死代码） |
| 8 | 活动统计写死 "1" / 审计时间"今天" | OK | §2.3（:892-896） |
| 9 | 空 catch 数量 | 更正 | §2.5→实际仅 3 处（:258/296/1478） |
| 10 | CacheCleanup 白名单过时/重复 | OK | §2.6（:18-21，%TEMP% 与 LocalAppData\Temp 同目录） |
| 11 | RecycleBin FOF_NOERRORUI | OK(措辞更正) | §2.6（非"静默失败"，helper 返回错误串） |
| 12 | LogFileWriter 通道化/有界/防穿越 | OK | §3（:50-53/:243） |
| 13 | 每秒逐行采样可合并快照 | OK | §3（:1792-1804） |
| 14 | SecretStore DPAPI+原子写 | OK | §3（ProtectedData.CurrentUser + .tmp/Move） |
| 15 | TcpHttpsProbe 停表过晚 | 更正 | §4→实际 TTFB 正确（:88 先停表再 Drain） |
| 16 | STUN/NAT 编解码严谨 | OK | §4（RFC 5389 / 保守降级） |
| 17 | KitAiHubRequest 阻塞 610s | 更正 | §5→默认 610s/上限 3610s/引擎 1-3600s |
| 18 | 16 并发待处理 | OK | §5（ai_hub_ipc.cpp:108） |
| 19 | STARTUP_TIMING 已打点 | OK | §5（main.cpp:142/188/218） |
| 20 | 提权重启逻辑（防死循环） | OK | §5（main.cpp:402-438） |
| 21 | hotkey 冲突检测器稳健 | OK | §5（双冲突表） |
| 22 | 键盘钩子 centralized_kb_hook | OK | §5（模块级注册/清理） |
| 23 | AGENTS.md 只列 3 模块 | OK | §6（:12） |
| 24 | kit-architecture.md 只列 3 模块 | OK | §6（:35-37/:87） |
| 25 | ARCHITECTURE_OVERVIEW 引用 3 个不存在文件 | OK | §6 |
| 26 | README 引用不存在的 fix.plan/next.md | OK(措辞更正) | §6（fix.md 存在） |
| 27 | 测试数量 108/196/7 | **误更正→撤回** | §6（实测 50+66/188+16/7 方法，与 README:271-273 吻合，README 准确） |
| 28 | 原版行数 1334/709/205/851 | OK | §1.3/§2.3 对照 |
| 29 | 原版 MainWindow 87KB | OK | §3 对照（87,692B≈85.6KB） |
| 30 | 内核 digest 机制 | 更正 | §5→官方 SHA256SUMS 校验文件（非 API TOFU） |
| 31 | 引擎内部质量（6 模块） | OK | §0（生产级细节） |
| 32 | 测试覆盖缺口范围 | OK(收窄) | §2.4/§3/§4（缺口=AIHubLib 六服务+VM 规则引擎+ProbeCoordinator 编排层/Probe 类；非"UDPtest 无测试"） |
| 33 | AI 面板寄宿 GeneralPage（测试锁定） | OK | §2.1（AiHubSettingsTests:84-104） |
| 34 | README DashboardModules 4 vs 代码 5 | OK(新) | §6 |
| 35 | 同工程两套本地化标准 | OK(新) | §2.1（UDPtestPage x:Uid 范本） |
| 36 | resw 键覆盖一致（995 键） | OK(新) | §0 |
| 37 | 零 TODO/FIXME | OK(新) | §0 |
| 38 | AsyncCommand async void 无 catch | OK(新,P3) | §0（仅 1 处使用，委托已自护，无现存崩溃路径） |
| 39 | Thread.Sleep 均在后台线程 | OK(新) | §0（GeneralViewModel:1487 / GeneralPage.xaml.cs:142，均在 Task.Run 内） |
| 40 | Localserver 服务不随 Settings 退出而终止 | OK(新,更正"关窗即停") | §1.1/§3（ServiceRunner.cs:390 killOnClose:false；原版 :385/:1605 同） |
| 41 | Kit UDPtest 无 SQLite（原版有） | OK(新,P2) | §4（无 Microsoft.Data.Sqlite/.db；原版 network-monitor.db） |
| 42 | 根 README:28-31 三文件缺失 | OK(新) | §6（fix.plan/next.md/fixed.md 均不存在） |
| 43 | 模块 README 存储路径失实（State vs AuditHistory） | OK(新,P3) | §2.8（README:21/:108 vs AuditHistoryStorage 默认 State\） |
| 44 | 模块 README 双语自链 / 键数 212 vs 258 | OK(新,P3) | §6（AIHub:3、Localserver:3；实测 258 对称） |
| 45 | AI 契约与策略沙箱引擎侧已就绪 | OK(新,补强 P0-1) | §1.2（AiTaskReport.cs 契约 + SecurityPolicyService.cs 沙箱） |
| 46 | AIHub README:102 评分权重失实 | OK(新,P3) | §2.9（README 中危 5/低危 1 vs 代码 6/1.5，且缺封顶说明） |
| 47 | AIHub README:139 进度环/耗时声明领先实现 | OK(新,P3) | §2.9（XAML 仅 1 处 ProgressRing，无耗时显示） |
| 48 | Localserver LogRingBuffer/LogSink 常量与 README 一致 | OK(新) | §3（QueueCapacity 512/DropOldest/8192/256/128/65536/100ms/500ms 全部命中） |
| 49 | DownloadOrganizer 7 分类与 README:87 一致 | OK(新) | §2.6（Documents/Archives/Images/Installers/Videos/Audio/Code + Other 兜底） |
| 50 | services.d 多源合并 / 六重归属校验属实 | OK(新) | §3（ServiceCatalogStore.cs:61-83；ServiceOwnershipStore 六项字段齐备） |
| 51 | UDPtest 有测试项目且覆盖 5 类 | **此前判断有误→更正** | §4（UDPtest.UnitTests 18 个：Codec 5/Parser 7/NodeVerification 3/Storage 1/Metrics 2） |
| 52 | Localserver 服务层测试扎实 | **此前低估→更正** | §3（ServiceLifecycleTests 7 个，含回收/时限/恢复/就绪竞态） |
| 53 | 崩溃日志 FirstChance+Unhandled 已实现 | OK(新) | §2.9（App.xaml.cs:84-114 → Kit\crash.log） |
| 54 | security.md 全局策略已实现 | OK(新) | §2.9（SecurityPolicyService.GlobalSecurityPolicyPath）；README:244 A-F 字母档位未实现（P3） |
| 55 | src/README.md 称含 Monitor 模块 | OK(新,**严重过期**) | §6（:8 "Awake, Light Switch, and Monitor"，Monitor 2.0.8 已移除） |
| 56 | doc/devdoc/README.md 只列 3 模块 | OK(新) | §6（:3/:24，缺 UDPtest/AIHub） |
| 57 | README_zh.md 为英文版镜像且同步失实 | OK(新) | §6（:28/:30 fix.plan/next.md；:162 DashboardModules 4；:271-273 测试数准确；:74 自认无 Worker） |
| 58 | UDPtest 模块无 README（AIHub/Localserver 均有） | OK(新,P3) | §4（src/modules/UDPtest/README.md 不存在，14KB 级模块文档缺位） |
| 59 | `.claude/CLAUDE.md` 指向缺失的 `.github/copilot-instructions.md` | OK(新,P3) | §6（`.github/` 目录整个不存在；`.claude` 其余子目录为空） |
| 60 | changelog 的 Monitor 条目为历史记录（合规） | OK(新) | §6（176-679 行均为 2.0.x 历史条目，含 2.0.8 移除记录 :90） |
| 61 | DOCUMENT_INDEX.md 链接与模块声明 | OK(新) | §6（链接零断链；无模块清单声明，无需修改） |
| 62 | Monitor 模块代码残留为 0 | OK(新,已验证) | §6.1（src/modules 无 Monitor；ModuleType/VM/Page/Interface 全 0 命中） |
| 63 | Monitor 功能已被 AIHub 承接（含缺口） | OK(新,部分) | §6.1（下载整理/缓存清理/安全边界 ✓；**安装包语义清理 ✗**、SQLite 历史 ✗） |
| 64 | Monitor 测试护栏与 changelog 属应保留项 | OK(新) | §6.1（约 20 处负向断言 = 防回归；changelog 为历史） |

---

## 8. 最终优先级（唯一权威表，取代全部历史表）

| 优先级 | 事项 | 来源 |
|---|---|---|
| P0-1 | AI 深度分析接入（页面→引擎→FindingDetailsDialog AI 报告区）+ 补齐设置项 | §1.2 |
| P0-2 | 三插件业务迁出 Settings 进程（常驻 Worker；先 Localserver） | §1.1、§3 |
| P0-3 | 恢复 AppMode + Security/Firewall 通道（提权切换）+ 模块 README:99 修正 | §1.3 |
| P1 | AI 面板迁移 GeneralPage→AIHubPage（同步改 AiHubSettingsTests:84-104）+ x:Uid 一次到位 | §2.1 |
| P1 | 定时/增量审计 + Trends/热力图 | §2.3 |
| P1 | 数据真实性：范围去重、活动统计真实历史、批量优化进度+失败原因、去 3 处空 catch | §2.2/2.3/2.5/2.6 |
| P1 | 补测试（范围已收窄）：AIHubLib 六服务（从 HealthScoreCalculator 起步）+ VM 规则引擎 + UDPtest ProbeCoordinator 编排层/Probe 类 | §2.4、§4 |
| P2 | 本地化统一 x:Uid（UDPtestPage 范本，含 FindingDetailsDialog） | §2.1 |
| P2 | 存储按原版设计补齐 SQLite（AIHub AuditHistoryStorage O(N) + **UDPtest 持久化**）；在线 digest 白名单；Localserver 补齐遥测/日志管道/services.d/端口竞态测试（生命周期测试已扎实）；热加载/拖拽排序 | §2.8、§4、§5、§3 |
| P2 | 文档同步（纯文字一次提交）：AGENTS/kit-architecture/ARCHITECTURE_OVERVIEW/README | §6 |
| P3 | PowerToys 残留 + Admin_Mode_Running_As + AsyncCommand.Execute 加固 + **文档全量校对**（模块 README 双语自链/存储路径/键数/评分权重/进度环/A-F；src/README.md 的 Monitor 过期；devdoc/README.md；README_zh.md 镜像；补 UDPtest 模块 README、修复 `.claude/CLAUDE.md` 断链；`%LOCALAPPDATA%\Kit\Monitor\` 旧数据目录处置） | §5、§0、§2.8、§2.9、§4、§6 |

---

## 9. 执行路径（按序开工）

**P0-1 AI 深度分析**：
- AIHubPageViewModel 在 FindingDetails 区加"AI 深度分析"按钮 → 取当前筛选 Top N 发现 → AiHubEngine.Current.ExecuteAsync（security-audit 策略，契约=Analysis Summary+Suggested Action 双语）→ 结果落 chains/ 并显示在 FindingDetailsDialog 新增"AI 报告"区；
- 发送侧选择：直连 AiHubEngine（最简单）或走 AiHubIpcBridge 消息（复用已就绪接收侧，为 Worker 化铺路）；
- 测试：沿用 Kit.AiHub.UnitTests 的 EngineAndIpcTests 基线 + 页面侧"发现→批次→契约"纯函数单测。

**P0-2 Worker 化**（大改，分插件，先 Localserver）：
- Localserver：ServiceRunner 已与 VM 解耦（仅 2 处 new）最顺；UDPtest：ProbeCoordinator 同理；AIHub：TaskAiEngine 已自足；
- 每插件 Kit.XxxWorker.exe + enable() 拉起 + settings 管道控制。

**P0-3 Security 通道**：
- EventLogService 加 Full 模式：Security 白名单 4624/4625/4672/4688/4697 + Firewall 4946-5157（XPath 16 ID 分块）；AIHubSettings 加 mode/appMode；提权切换复用 runner 机制；模块 README:99 同步修正。

**P1（按依赖序）**：面板迁移（改 AiHubSettingsTests:84-104）→ 定时/增量审计 + Trends → 数据真实性修复 → 补测试（AIHubLib 六服务从 HealthScoreCalculator 起步——52 行纯函数 5-8 个边界用例；UDPtest 补 ProbeCoordinator 编排层与 Probe 实现类；既有 18 个 UDPtest 测试与 7 个 Localserver 生命周期测试可作模板）。

**P2**：本地化 x:Uid（键体系已齐备，纯替换）→ digest 白名单 → 文档同步（一次提交，含模块 README 双语自链/路径/键数修正）→ 存储按原版设计补 SQLite/分片（AIHub AuditHistory + **UDPtest 持久化**）→ 热加载/拖拽排序。

---

> 收敛说明：本版合并删除的逐轮记录（旧 §7/§9/§10.5-10.6/§11-§16、6 张重叠优先级表、轮次 TOC 标注）如需追溯历史表述，参见上一版 fix.md（git 历史）。

---

## 10. 执行总结（fix 分支，34 commits）

### 10.1 §8 清单逐条状态

| §8 条目 | 状态 | 提交 / 说明 |
|---|---|---|
| P0-1 AI 深度分析接入 | ✅ 完成 | cd13179（引擎契约已存在，补齐调用方 + AI 报告面板） |
| P0-2 三插件 Worker 化 | ✅ 完成 | fd09e88（Localserver）+ 74f292f（AIHub）；UDPtest 判定**不适用**（显式 Start/Stop 模型，无守护缺口） |
| P0-3 Security/Firewall + README 修正 | ✅ 完成 | 727e288 + b12de1c |
| P1 面板迁移 + x:Uid | ✅ 完成 | 7afec58（并修复重复总开关） |
| P1 定时/增量审计 + Trends | ✅ 完成 | 4257aaf + b84ecab + 961c2d6 |
| P1 数据真实性 | ✅ 完成 | a428e4e + da87835 |
| P1 补测试 | ✅ 完成 | 1460b67 / 02bbaa3 / 89b5c89 / 61b8e5e |
| P2 本地化 x:Uid | ✅ 完成（可行范围） | 12efc13 + d165b9f；138 处运行时插值**技术不可行**（x:Uid 无法表达拼接） |
| P2 存储 / digest / 热加载 | ◐ 部分 | 存储上限 + AOT 安全（8514b4f）；digest 白名单与热加载未做（低优先，见 10.3） |
| P2 文档同步 | ✅ 完成 | e0976ae + 9359c8e + b12de1c |
| P3 残留 + 文档全量校对 | ✅ 完成（可做部分） | 13220a6 / 02bbaa3 / e0976ae；`%LOCALAPPDATA%\Kit\Monitor\` 旧数据目录未处置（需真实环境确认） |

### 10.2 量化结果

| 测试套件 | 起点 | 终点 |
|---|---|---|
| Kit.AiHub.UnitTests | 108 | **193 通过** / 1 跳过 |
| Settings.UI.UnitTests | 196 | **200 通过** |
| UDPtest.UnitTests | 18 | **28 通过** |
| Localserver.UnitTests（无子进程部分） | 7（含 6 项需真实进程） | **17 通过** |

新增可执行产物：`Kit.LocalserverWorker`、`Kit.AIHubWorker`（均注册进 `Kit.slnx`）。

### 10.3 明确的未完成项与理由

| 项 | 状态 | 理由 |
|---|---|---|
| Worker 端到端启动验证 | 未验证 | 本沙箱禁止子进程创建；需在真实环境运行 `Kit.exe` 后确认 worker 进程存在 |
| Localserver 6 项 ServiceLifecycleTests | 沙箱内失败 | 测试通过 `Process.Start` 创建真实子进程，被沙箱拒绝；`git diff main..fix` 不含 Localserver 业务代码变更，非回归 |
| UDPtest SQLite 持久化 | 未做 | SQLite 为 **2.0.8 刻意移除**（changelog:176），恢复需新增依赖 + 更新 NOTICE.md，超出本次范围 |
| 在线 digest 白名单 | 未做 | 现有 SHA256SUMS 校验已属发布域可信来源（见 §5 更正），加白名单属加固增强，非缺陷 |
| `%LOCALAPPDATA%\Kit\Monitor\` 旧数据 | 未处置 | 需确认是否存在用户数据后决定删除策略 |
| 热加载 / 拖拽排序 | 未做 | 功能增强，非缺陷修复 |

### 10.4 构建注记（本机沙箱）

- 托管项目：`/p:TrackFileAccess=false`（file tracker 受限）、`/p:NuGetAudit=false`（网络受限）
- 原生项目：使用 `MSBuild\Current\Bin\amd64\MSBuild.exe`（否则 GenerateResource 任务宿主不可用）
- 测试：直接运行测试 `exe`（`dotnet test` 依赖命名管道，受限）
