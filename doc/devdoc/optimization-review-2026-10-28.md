# Kit 优化审查报告（2026-10-28）

**范围与方法**：对主框架与上游同步、四个自建插件（NetMap / UDPtest / Localserver / AIHub 及共享 AI 引擎）、插件开发文档、UI/UX 与本地化做五路只读审查，对照 `Source/PowerToys-main` 上游快照（0.103 开发线）。关键结论已抽样复核（标 ★ 的条目为复核项）；行号为撰写时点（HEAD `b997817`）快照。

**基线**：Kit 3.2.0，对标上游稳定版 v0.102.3001.0。同日已完成 NetMap 停止时取消 AI 分析的修复并提交（`b997817`）。

## 一、主框架与上游同步

### 1.1 同步基建（P1，建议优先决策）

1. **Hybrid CRT 未跟随** ★ — Kit `Cpp.Build.props:80,90` 为全静态 /MT；上游快照 `Cpp.Build.props:113-129` 默认启用 `EnableHybridCRT`（VC++ 运行库静态 + 系统 ucrtbase）。`src/runner/main.cpp:443-457` 的"共享 UCRT 退出锁"注释只在 hybrid CRT 下成立，现状为注释与构建配置脱节。收敛成本约 17 行，可行性已核对（无进程级 CRT 状态修改、全自建二进制）；采纳上游块或至少改写注释。
2. **依赖版本机制缺失、版本滞后** ★ — 上游以 `DotNetRuntimePackageVersion=10.0.11` 统一驱动约 20 个包（上游 `Directory.Packages.props:7`）；Kit 硬编码 10.0.8（`Directory.Packages.props:30-38`）、WinAppSDK 2.0.1（上游 2.2.0）、WebView2 1.0.3719.77（上游 1.0.4022.49，含安全修复）。建议先引入属性机制（单提交低风险），再单独回归升级。
3. **ConformanceMode 反转** ★ — Kit `Cpp.Build.props:55` 为 false；上游 `:64` 为 true（/permissive-）。长期积累隐性差异，建议收敛并修复暴露的错误。
4. **tools/build 脚本漂移** — `tools/build/build-common.ps1` 与上游差约 268 行；上游新增 `Test-VsHasNativeTools` 工具链校验缺位。建议按需移植校验段落，不整体替换。
5. **无同步 runbook** ★ — 建议新建 `doc/devdoc/upstream-sync-runbook.md`，覆盖：基线锚定（快照提交 / ReleaseTrainVersion、Kit 版本、接口头 SHA-256）；分歧地图（永久 / 可管理分级 + 文件级清单，1.2 节可直接作为底稿）；执行顺序（props/targets → 包版本 → common → runner → settings-ui → modules → tools/build，逐层构建）；重命名重放规则（`tools/build/update-namespaces.ps1` 映射维护、powertoy_module → kit_module 搬运规则）；验证协议（build 退出码 0、Settings.UI.UnitTests 静态断言、NativeModules ABI 冒烟、启动测量、Stage-Release 校验）；禁止回灌清单（自动下载/安装更新、遥测、OOBE、installer/CI、vcpkg）；每轮记录模板；触发时机。旧 `kit-sync-status.md` 可 `git show cd1c0ba^:doc/devdoc/kit-sync-status.md` 恢复作底稿（需剔除已完成项）。

### 1.2 同步摩擦分级（要点）

- **建议收敛**：Hybrid CRT；ConformanceMode；包版本机制；长路径检查实现（Kit `Directory.Build.targets:175-187` 每工程起子进程且仅 Warning，上游 `:19-28` 用 MSBuild 属性读取 + Error）。
- **永久保留（刻意）**：vcpkg 关闭 + 内置 deps；Release 去 PDB；x64-only；品牌与标识改名（Kit.* DLL/命名空间、`%LOCALAPPDATA%\Kit`、`kit://` + `powertoys://` 双 URI、`Local.Kit.SparseApp`）；移除子系统（Update、OOBE、telemetry、installer/CI）。
- **可管理**：runner 结构分叉（`kit_module.*` 取代 `powertoy_module.*`，接口头与上游 diff 369 行，每次同步手工搬运上游对旧文件的改动）；`update-namespaces.ps1` 是改名重放关键，须持续维护；单测中约 1100 处源码文本断言 = 每次同步的同步哨兵，同步后需顺带更新。
- **Kit 领先上游**：IPC 层（`two_way_pipe_message_ipc` 的 wil RAII 句柄、`pipe_caller_auth` 持句柄鉴权）领先上游快照，移植上游改动时保留本地加固。

### 1.3 框架清理项（P2）

- 模块注册清单四处维护：`src/runner/main.cpp:64-71` 与 `src/runner/kit_module.h:69-76` 完全重复，另有 C# `KitModuleCatalog.cs` 两处，且被测试钉住；方向：main.cpp 直接迭代 KitModuleCatalog。
- 死函数 `is_known_module_registered`（`main.cpp:73-84`，全仓无调用，仅被测试断言）。
- toast/notifications 栈无生产者：三项目仍构建、runner 仍链接；`NotificationUtil.cpp:12-13` 仍含 PowerToys 仓库链接与 powertoys:// URL；`main.cpp:336-343` 残留 update_now 恒失败分支与已移除模块处理。方向：明确"保留待用"或裁剪。
- 更新器残留：`UpdateUtils.cpp:208-211` 空桩无调用者；`updateState.h:20` 字段恒空但属与 Settings 共享 schema，保留并注明边界。
- `get_general_settings()` 每次执行 Task Scheduler COM + token 查询，被托盘高频路径调用（`general_settings.cpp:175,199`）；上游同构属继承问题——会话内缓存、设置变更时失效。
- 托盘更新徽标不跨重启恢复、启动三元恒死（`tray_icon.cpp:424,428`）。
- 构建/依赖残留：EtwTrace 在册构建但零引用；expected-lite 导入无已构建消费者；`boost_regex` 全仓零消费者；`appMutex.h` KIT_BOOTSTRAPPER_MUTEX_NAME 无用户。
- AIHub 模块头仍继承 `telemetry::TraceBase`（`AIHubModuleInterface/trace.h`），是唯一残留；隐私测试名单未含它。改为 no-op 头或登记例外。
- devdoc 陈旧：`AGENTS.md:12` 模块数缺 NetMap；`ARCHITECTURE_OVERVIEW.md` 多处同步描述过期；`powertoys-architecture.md` 引用不存在的 next.md 章节。

## 二、四个自建插件

### 2.1 NetMap

- **已修复**：停止 / 禁用 / 隐藏 / 退出检测时取消运行中的 AI 分析（`b997817`，含 2 项回归测试）★。
- **[P1] MTR 快照按跳同步写盘** — `NetMapViewModel.ApplySnapshot` 对每个带 hops 的快照都调用 `NetMapAiService.SaveLatestMtrSnapshot`（内部 `JsonSerializer.SerializeToUtf8Bytes` + 同步 `File.WriteAllBytes`，`NetMapAiService.cs:123-124`）；MTR 每轮每跳都回调，路径诊断期间每秒多次全量重写小文件；`CheckRouteWithAiAsync` 已独立保存，此处冗余。同热路径 `CanAiCheck` getter 每次快照做 `File.Exists`。建议：删冗余写盘或 debounce + 后台写；缓存 File.Exists。
- **[P1] headless 服务空转、与 README 矛盾** ★ — `NetMapService/Program.cs:30-35` 创建 `NetMapSession` 后从不启动；管道命令一律固定 JSON；`KitServicePipeClient` 全仓零调用；探测实际运行在 Settings 进程内。README"独立服务承载、关闭 Settings 仍观测"与现状不符。并入 2.5 的架构决策。
- **[P2] Proxy 设置整链死码** — `NetMapProperties.cs:11-15` 的 ProxyMode / ProxyAddress 无消费者；`NetworkProbe.CreateClient` 恒用系统代理；`ApplySettings` 重建属性时清空这些字段；README 描述的 HTTP/SOCKS5 模式在代码中不存在。建议删字段改文档，或补实现。
- **[P2] 地图重绘全量重建** — 每个快照触发 `MapChanged` → `DrawPoints` 全量 LINQ 比较 + 清空重建 marker/标签（`NetMapPage.xaml.cs:228-337`）。建议版本号/脏标记。
- **[P2] 原生 `enabled` 无锁读写** — `NetMapModuleInterface/dllmain.cpp:18,38,49`（UDPtest 同型）。建议 `std::atomic<bool>`。
- **[P2] 测试缺口** — `EnrichEgressAsync` 迟到发布、`UpdateAsnAsync` 取消 / IsUpdating 状态机、`AsnDatabaseUpdater.UpdateGate` 并发。

### 2.2 UDPtest

- **[P0] 停止态线路开关在下次 Start 被忽略** ★ — 行开关 setter 只改行 VM 的 `_isEnabled`（`UDPtestLineRowViewModel.cs:96-108`），`Definition => _definition`（`:64`）只在行编辑器保存时重建（`:449`）；`StartAsync`（`UDPtestViewModel.cs:477-478`）与协调器（`ProbeCoordinator.cs:218,248`）都按定义旧值判断 → 关闭的线路仍被探测，"至少启用一条"守卫永远通过；运行中关掉后 Stop → Start 同样复现。建议 Start 时按 `_isEnabled` 重建 definitions 或 setter 同步定义，并补回归测试。
- **[P1] 故障后 pipeline 已死、状态仍 Running** — `ProbeCoordinator.cs:106-120,596-601` 异常路径不收敛状态、不发 StateChanged；workers 退出后 UI 仍"运行中"，须手动 Stop 才能重开。
- **[P1] 采样间隔不恢复、运行中不改** — 构造 / InitializeAsync 从不读回 `_refreshInterval`（重启后回显 1000ms，`UDPtestViewModel.cs:75,266-278,485`）；服务端 `UDPtestServiceConfig.IntervalMs` 无消费。建议初始化读设置；运行中改间隔给提示或热更新。
- **[P1] ViewModel 永不释放、轮询常驻** — 页面无 Unloaded/Closed 处理（对比 `NetMapPage.xaml.cs:153-163`）；`Dispose`（`UDPtestViewModel.cs:1166-1178`）零调用者；`PollNetworkIdentityAsync`（:1001-1074）模块禁用时仍每 1s 轮询，随进程存活；Dispose 还是 UI 线程同步阻塞。建议仿 NetMap 挂窗口关闭释放。
- **[P2] 长跑图表 O(n) 开销** — 每快照对 `_chartSamples` 线性 `RemoveAll`，健康卡全量过滤，重绘对一小时样本分桶排序（`UDPtestViewModel.cs:756-784,847-915`）。建议环形缓冲头部剪枝 + 重绘降频。
- **[P2] 状态字符串映射错位/死支路** — VM 映射 engine 不产出的值；健康卡匹配 "Degraded"/"Offline" 而 engine 产出 "DEGRADED"/"DOWN"（大小写不匹配，中文界面露英文）。建议共享常量/枚举。
- **[P2] 运行中删除线路不通知协调器**（`UDPtestViewModel.cs:648-666`）；**NAT 探测固定 5s 近乎连续发流量**（`ProbeCoordinator.cs:604-716`）；**死码一批**（`InitializeAsync`、`SnapshotAll`、`UDPtestLogSink`、`UDPtestPathHelper` 大部、`IntervalMs`）；**测试缺口大**（协调器/探针无覆盖，README 仍写 18 项）；建议为 `ProbeCoordinator` 增加探针工厂注入缝。

### 2.3 Localserver

- **[P1] 打开页面后每秒环境自检：反复拉起 `--version` 子进程 + 端口全表枚举** — 1s tick（`LocalserverViewModel.cs:1338,1752`）→ 每秒 `RefreshEnvironmentCard`（:1789）→ `EnvironmentInspector.CheckAsync`（:1875-1878）为每个运行时需求起一次探测子进程（`EnvironmentInspector.cs:252-263`）+ `GetExtendedTcpTable` 全表枚举（:424-472）；仅防重入、无限频。建议环境快照加会话级缓存，改为选中 / 状态变化或手动刷新时重查，或间隔 ≥30s。
- **[P1] Worker 对残留 ownership 记录每秒重扫全机进程表并刷日志** — `LocalserverWorker/Program.cs:112-139` 每 1s 调 `RecoverRunningServicesAsync`；无效记录不清理不归档（`ServiceRunner.cs:226-235,280-281`），永久每秒全机快照 + 每秒日志。建议失败退避 + 归档无效记录 + 日志去重。
- **[P2] 每秒资源采样为每个运行中服务各抓一次全机进程表** — `LocalserverViewModel.cs:1775-1787` → `ProcessResourceInspector` 每行一次 Sample（全机 Toolhelp 快照）；N 个服务 ≈ 每秒 N 次全表枚举。建议每 tick 共享一份快照或仅采样选中行。
- **[P2] 死码** — `ServiceSupervisor.StartServiceAsync/StopServiceAsync` 仅单测引用。
- **[P2] README 漂移** — 声称 OnNavigatedTo/From 控制采样（实际 Loaded/Unloaded/Activated）；列出不存在的 `Monitors/GpuInspector`；未说明 Runner 退出时 Worker 会 StopAll 全部受管服务。

### 2.4 AIHub

- **[P1] "移入回收站 / 可逆清理"承诺与 `File.Delete` 永久删除不符** ★（抽查确认）— `CacheCleanupService.cs:82` 默认 `File.Delete`；`RecycleBinHelper` 零调用死码；UI 文案与 README 仍承诺回收站（`AIHubPageViewModel.cs:727,736-737`、`AIHub/README.md:91`），新版策略文案已改口"永久删除"（`TaskPolicyDefaults.cs:356-358`）。三处口径必须统一：接回回收站，或同步改 UI/README/旧策略文案。
- **[P1] 定时审计端到端断裂** ★（抽查确认）— 页面把间隔 / 模式写入插件设置（`AIHubPageViewModel.cs:543-574,1628-1656`），Worker 读服务设置（`AIHubWorker/Program.cs:76-86,150-158`），而服务侧 `ScanIntervalHours/AuditModeIndex` 无任何生产写入方 → interval 恒 0 永不调度；服务开关又门控 Worker。建议插件统一拥有审计计划，服务开关仅控制 AI 执行。
- **[P1] 页面自带 5 分钟调度器加载后从不启动；到期执行全量而非增量** — `AIHubPageViewModel.cs:286-288,1683-1698`（定时器只在设置项 setter 里启动）、`1704-1719,1939`（固定 ManualFull，未用 `AuditSchedule` 已实现的 Scheduled 窗口）。建议复用 `AuditScheduler` 并删除页面侧重复调度。
- **[P1] Worker 每次启动立即审计一轮、不恢复历史水位** — `AuditScheduler.cs:62-104` 水位恒 null → IsDue 恒 true；应启动时用审计历史初始化。
- **[P2] Worker enabled 门控滞后且注释与实现不符**（`Program.cs:120-132`；页面切换服务开关不触发 refresh 事件）；**下载整理 500 条静默截断**（`DownloadOrganizerService.cs:79-106`，无截断标志）；**深度分析文案"最多 12 项"与实现 400 条不符**；**RunScan* 大段复制**；**开发根硬编码 `C:\Users\Zen\Repos\Codings`**（`CacheCleanupService.cs:26`）。

### 2.5 两模块共同问题与架构决策

- **[P1] 两个 headless 服务都是空壳** ★ — NetMapService / UDPtestService 启动时只构造会话 / 协调器却从不启动；管道命令固定 JSON；设置反序列化丢弃；`KitServicePipeClient` 零调用。常驻两个空转 .NET 进程。**需决策**：完成"探测下沉到服务 + Settings 经 IPC"或移除服务启动与无用端点；另 exit/refresh 事件名在 C++ 与 C# 双处硬编码，建议收敛单一来源。
- **[P2] dllmain 三函数逐字复制**（两模块），建议抽公共层；**UDPtest 缺 `is_enabled_by_default` 覆写** → 全新配置下默认启用，与既定语义矛盾（`EnabledModules.cs:593-616`）；**IsChinese 分裂**（UDPtest 68 处且 README 自称不用，NetMap 0 处）；**源码文本断言偏多**（本次 UDPtest P0 正是此类断言漏掉的），建议关键行为改行为测试；**`KitServicePipeServer` 连接 fire-and-forget**；**disable 同步等待 2s+1.5s**。

## 三、共享 AI 引擎（src/common/AiHub）

- **[P1] 每次配置读取 = 磁盘 + DPAPI 解密 + Global 互斥体 + 事务恢复** — `TaskAiEngine.cs:64-66,678-689`、`AiHubSettingsStore.cs:64-116`、`SecureKeyStore.cs:79-105`、`AiHubStorageFiles.cs:55-110`；调用遍布 UI 热路径。建议进程内缓存配置快照 + FileSystemWatcher / StateChanged 失效（钩子已存在，未接缓存）。
- **[P1] `get_status(refresh)`/`self_test` 绕过请求注册、按 requestId 取消与并发约束**，`ProbeReadinessAsync` 不占调度槽，`TestTarget` 直连 RouteDispatcher → 可超出内核并发预算（`AiHubIpcHandler.cs:82-123` vs `:142-162`）。
- **[P1] 在飞探测可回写旧缓存、fallback 未实测即报 Degraded、不验证内核安装**（`TaskAiEngine.cs:210-219,255-258,128-142,164-182`）——即 ai-service-review 五个后续点的第 1、3 点，现状未修。
- **[P2/P3]** IPC 消息三重解析（native + handler 两次）；`MapSelfTestFailure` 靠英文子串匹配；满并发返回泛化错误码，建议新增 Busy 类。

## 四、插件开发文档统一

**SSoT 结论**：以 `PLUGIN_DEVELOPMENT.md` 为唯一"如何加模块"权威规范；`doc/devdoc/kit-first-plugin.md` 降级为重定向；模块 README 是"模块现状记录"，不是接入规范。

**硬伤清单** ★（抽查确认）：

- **模块数三方冲突**：六（代码、README、src/README、devdoc/README）vs 五（`PLUGIN_DEVELOPMENT.md:9,22,764,838`、`AGENTS.md:12`）vs 二（`kit-first-plugin.md:5`）。
- **`PLUGIN_DEVELOPMENT.md` 过时点**：Localserver "尚无独立 Worker"（:111,811，实际已有）；§10.2 输出树为 2.0.15 时代；备份路径（:169 写 `Documents\Kit\Backup`，代码默认为 `%LOCALAPPDATA%\Kit\Backup`，可被注册表项覆盖 ★；`AGENTS.md:29` 同错）；Awake 参数 `--use-pt-config`（实际为 `--use-kit-config`）。
- **3 处教学命令违反 dotnet test 禁令** ★：`kit-first-plugin.md:66`、`AIHub/README.md:178,184`、`UDPtest/README.md:138`（禁令本身见 `AGENTS.md:80`、`PLUGIN_DEVELOPMENT.md:668`）。
- **LightSwitch 模块 README 缺失**（规范要求存在，最大单点缺口）。
- **Awake README 上游残留**：PowerToys 标题/作者、`PowerToys.Awake.exe`、`%LOCALAPPDATA%\Microsoft\PowerToys\Awake` 路径。
- **UDPtest README** 架构图与目录树缺 Service 进程与测试工程；**AIHub README** 树缺 AIHubWorker；**Localserver/AIHub README** 语言互链均指向自身；**DOCUMENT_INDEX** 统计 11 vs 12 且未收 AGENTS.md；`kit-development-experience.md` 建议加"历史日志"横幅。

**最小统一方案**：① PLUGIN_DEVELOPMENT 只修上述硬伤；② `kit-first-plugin.md` 压成 ≤15 行重定向；③ `AGENTS.md` 去重改引用（保留构建纪律与禁令这一权威表述）；④ 模块 README 固定 6 节模板（模块信息 / 架构与进程模型 / 配置与数据 / 生命周期与启停 / 构建与测试 / 已知限制），补 LightSwitch README、清 Awake 残留、修语言互链；⑤ 测试指令单点化（4 处 dotnet test 改成直连 exe）；⑥ 索引维护。⑦ LightSwitch.Cli.UnitTests（会话分支 `claude/relaxed-moser-7f5375`，`5a40517`）合入 main 后，需同步 §10.2 输出树与测试清单。

## 五、UI/UX 与本地化

- **[P1] zh-CN 缺 22+ 个仍被引用的键** ★（抽查确认）— 19 个备份状态消息（`General_SettingsBackupAndRestore_BackupComplete` 等，仅 en-us 存在，返回于 `SettingsBackupAndRestoreUtils.cs:283-935`，展示于 `GeneralViewModel.cs:846-955,1227`）+ 3 个更新检查标题（`General_CantCheck/UpToDate/NewVersionAvailable`）；zh 界面回退英文。补翻译 + 加 en/zh 键集合一致性单测。
- **托盘本地化只差一个文件** — 语言覆盖机制与构建脚本均已就绪，`src/runner` 只有英文 resx；新增 `Resources.zh-Hans.resx` 即可本地化托盘菜单 / Tooltip / MessageBox，无需改代码。
- **IsChinese 内联双语 350 处 / 9 文件** — `AIHubPageViewModel.cs` 232、`UDPtestViewModel.cs` 57、`AiHubAuditAnalysisService.cs` 17 等；Localserver 已清零（全 resw），可作样板。建议向 resw 收敛；NetMap AI 词表为 prompt 专用可保留但注明。
- **GeneralPage 首帧仍同步构造 AiServices** — 44 处绑定触发 `AiHubViewModel` 惰性构造：跨进程锁读 AI 配置 + 内核探测均在 UI 线程（`GeneralViewModel.cs:58`、`AiHubViewModel.cs:210-213,278`）；UpdatingSettings / GPO 同步读也在导航路径。建议后台预热或懒到展开时。
- **Localserver 采样**（同 2.3）：每 tick 全表快照 + 1Hz 环境探针仍在。
- **[P2] 本地化补键**：UDPtest 17 处 / Localserver 12 处硬编码英文 ToolTip / AutomationProperties（对照 NetMap 已配好的键模式）；UDPtest 端点占位符缺 x:Uid。
- **[P2] 无障碍**：图标按钮缺 UIA 名；装饰 FontIcon 未标 `AccessibilityView="Raw"`（各页 18-49 处）；无 HighContrast 分支；AccessKey 全 UI 0 处。
- **[P2] 性能**：`UsageProgressBar` 解析 AutomationProperties.Name 决定颜色（中英词表硬编码，建议显式 TargetCategory 属性）；`EnableDependentAnimation=true` 的 450ms 动画对上 1Hz 数据更新，建议周期数据禁用或缩至 ~150ms。
- **[P2] 响应式**：NetMap 地图列固定 `*,440` 且全页无 AdaptiveTrigger，窄窗（MinWidth 480）地图列被压没；参考 LocalserverPage 的 ControlSizeTrigger。
- **[P2] 清理**：确认无引用的死样式 / 死键——InfoBadge.xaml 双样式、TextBlock/Button 四个死样式、`KitUpdateBadgeBrush`、`ModuleListSortOptionToBooleanConverter`、`SettingsPage_NewInfoBadge`，以及约 261 个 en-only 无引用键（上游残留，删除或加注释）。

## 六、建议处理顺序（推荐）

1. **插件缺陷批次**：UDPtest 线路开关 P0 → AIHub 定时审计链路（含页面调度器与水位）→ 回收站口径统一 → Localserver 采样节流 → NetMap MTR 写入节流。**→ 已完成（2026-10-29，见文末批次记录）**
2. **同步基建批次**：Hybrid CRT + ConformanceMode + 包版本机制三件套，随后落地 runbook 与长路径检查对齐。
3. **文档统一批次**（纯文档、低风险、可一次提交）：按第四节方案。
4. **UI/UX 批次**：zh 缺键 + 托盘 zh resx + tooltip/AutomationProperties 补键；其后视情况做 IsChinese 收敛与死键清理。
5. **架构决策**：两个空壳服务"下沉或移除"（需先定方向）；共享 AI 引擎配置缓存与探测约束紧随其后。

---

*只读审查快照（HEAD `b997817`，2026-10-28）。★ 为复核项；其余条目附行号出处，实施前建议再确认一次当前行号。*

---

## 七、插件缺陷批次执行记录（2026-10-29）

按第六节顺序完成第一批 5 项，全部改动与本记录一同提交（基线 `b997817`）。

1. **UDPtest 线路开关 P0**（§2.2）— 行开关 setter 同步写回 `ProbeLineDefinition`，静默回滚同样同步定义；新增行为测试 `UDPtestLineToggleMustWriteThroughToDefinition`（`UDPtestLineRowViewModel.cs`、`UDPtest.cs`）。
2. **AIHub 定时审计链路**（§2.4）— Worker 改读插件 `settings.json`（新增 `PluginScheduleSettings`），启动水位取自审计历史最新时间戳，移除服务 `IsEnabled` 门控；页面删除从未启动的 5 分钟调度器；`AuditScheduler` 新增 `Mode`/`UpdateMode` 与 `lastCompletedUtc` 初始化；顺带修正 worker 审计历史写向 `State\`（原误写 AIHub 根，与页面读取错位）。新增 6 项单测（水位 / 模式热更 2 项 + 插件设置解析 4 项）。
3. **AIHub 回收站口径统一**（§2.4）— 判定依据：`c7c7680` 同一次提交把策略文案改为 "permanently deletes" 并将删除委托从 `RecycleBinHelper.MoveToRecycleBin` 换为 `File.Delete`，属协调过的设计切换，故统一到"永久删除"而非回接回收站。改动：页面 `CacheDescription`/`RecoverableCleanup*` 文案、根 README（中英）、模块 README（§1 表 / 架构框 / §3.1 安全边界）同步改写；`RecycleBinHelper` 标注为未接线。**保留不改**：legacy 策略常量（迁移精确匹配依赖）、changelog 历史条目。**发现**：`Safety boundary` 一组文案（`SafetyBoundaryLabel`/`ReadOnlyScan*`/`PathBoundaries*`/`RecoverableCleanup*`）在 XAML 中零绑定，属未接线死文案（已在模块 README 标注）。
4. **Localserver 采样节流**（§2.3）— 页面环境自检 30 秒节流（`IsEnvironmentCheckDue`：选中行 / 状态 / 健康探针变化立即重查）；遥测 tick 改为每 tick 共享一份 `ProcessTreeSnapshot`（`Sample` 新增加快照重载）；Worker 残留 ownership：同一记录失败退避（1→2→4→8→16→30 秒）、相同原因日志 10 分钟去重、确认记录进程全灭时归档删除记录（此后安静空转）。新增回归测试 `StaleOwnershipRecordIsArchivedOnceItsTreeIsGone`。
5. **NetMap MTR 写入节流**（§2.1）— 删除 `ApplySnapshot` 的逐跳同步写盘（仅保留 `CheckRouteWithAiAsync` 按需写盘）；`CanAiCheck` 改用 `mtrSnapshotFileExists` 缓存标志（构造函数初始化，写 / 删两处同步维护），消除每快照 `File.Exists`。新增源码断言 `NetMapMtrSnapshotMustOnlyBeSavedOnDemand`。

**验证**：AIHub 单测 355（354 通过 / 1 预置跳过）、Localserver 29/29、Settings 234/234；AIHubWorker、LocalserverWorker 及 Settings 全部编译通过。§6 其余批次（同步基建 / 文档统一 / UI/UX / 架构决策）未动。
