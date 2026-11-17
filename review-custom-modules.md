# 自研模块复核与实施记录

基线：Kit `2cb5ff9`；本次按用户指定 `Source/PowerToys-0.102.3101`，旧报告的 `PowerToys-main / 0.103` 不作为本轮同步目标。以下结论来自源码复核；构建与测试由主任务统一执行，已核对的最终验证结果见文末。

| 编号 | 裁定与源码依据 | 已实施 / 剩余 |
|---|---|---|
| K4 | 确认缓存 `is_enabled` 无法反映 Worker 退出；但“永久假死”还取决于 Runner 是否周期监督，不是仅改返回值即可自愈。 | AIHub、Localserver、UDPtest、NetMap、Winget 读取活体进程状态；enable 再探活且只在启动成功后置已启用。AIHub/Winget 去除模块内部异步启动，与 Runner 统一队列配合，避免完成前虚报启用。Runner 代理新增每模块串行executor：1秒探活，失败以1~60秒指数退避重试，稳定30秒后复位。用户禁用/退出撤销重试。注意 Runner代理 is_enabled 表示已接受的开关意图，is_running 才是最后活体快照，避免崩溃把持久化开关改false。 |
| K7 | 确认同步等待在调用线程执行。 | 已新增 module_lifecycle.h 与 KitModule代理，启停/配置/动作/热键都在单模块executor串行；UI只读原子意图/活体快照、短锁配置/热键缓存，不再进入DLL生命周期互斥。begin_module_shutdown先停止全部模块接收和重试，析构drain并join后才destroy/卸载。set_config pending计数防止异步完成前回送旧配置；完成后在UI更新热键并通知设置。 |
| K8 | 确认只在启动读目录且 native 刷新事件无消费者。 | ServiceSupervisor 新增变更戳驱动 ReloadAsync；每次恢复之前重载，新服务创建runner，停用/移除的已停止项释放；仍活跃的旧项保留至停机以免漏杀，活体定义不强行换身份。Worker 消费刷新事件；暂时损坏文件保留旧目录并重试，不阻断退出清理。 |
| K9 | 事实成立、分级过重：两个服务显式返回 engineAvailable=false / executionHost=Settings，且拒绝不支持命令，未谎称业务已在 Worker。 | 属于架构选择，不为“修bug”贸然迁移两套探测引擎与 UI 生命周期。本轮保留既有宿主与明确能力声明；完整服务化未做。 |
| K22 | 确认 AIHub / Localserver / UDPtest 仅 catch(std::exception&) 无法兜住所有 WinRT JSON 异常。 | 改为 native ABI 边界 catch-all，并记录简短警告；NetMap/Winget 原已有兜底。 |
| K43 | 确认 AIHub OpenEvent 可能早于 CLR 创建事件，且多个模块提前置启用；Winget 还会丢弃未确认退出句柄。 | AIHub 启动前创建并持有同名 AutoReset 退出事件，早停信号保留；删除重复未用常量。五模块修复启用状态；Winget 仅确认退出后关闭句柄，防止失败停机后盲目再启第二实例。公共 KitWorkerProcessManager / LightSwitch 由其他任务负责。 |
| K44 | 确认 LocalserverWorker / AIHubWorker 周期重新 GetProcessById 存在 PID 复用窗口。 | 入口解析后打开一次 Process.SafeHandle，后续检查同一身份，父进程已不存在则退出；不传 PID 仍允许独立运行。RunnerHelper 由主任务负责。 |
| K45 | **部分确认**：Process.Start 后 job.Assign 确有派生逃逸窗口，但已有 AttachExistingDescendantsAsync 补挂，风险不能等同完全无约束；TryKillProcessTree 重新按 PID 开进程未二次验真属实。先前记录将 HttpClient 释放概括为本项既定缺陷不准确，删除该旁支裁定；本项只核对启动挂 Job 与 PID 强杀身份。 | 强杀使用同一个已打开进程句柄，重新校验持久化 ownerTag、StartTime、映像与环境所有权标签，缺身份则拒绝。新增 JobProcess 原生启动器：CREATE_SUSPENDED 创建、指定仅3个stdio继承句柄、job.Assign成功后才ResumeThread；失败终止尚未执行的进程。保留标准流、环境块、工作目录和Windows参数引用语义，删除原有2秒后代补挂扫描。新增真实fixture首条逻辑检查指定Job归属、立即派生子进程回收以及Unicode参数/环境/输入输出回归。 |
| K46 | 确认 NetMap 同步Dispose释放在途探针、锁内外部回调、池化客户端过早释放、IPv6空成功与取消误吞。 | Dispose触发取消，IAsyncDisposable/Completion等待所有启动代次收敛后释放Probe；事件有序锁外派发；HTTP池租约使退休连接最后一个请求归还后才销毁；IPv6走Ping实现，非法IP抛参数异常且取消原样传播。 |
| K52 | 存储反射/AOT和SemaphoreSlim未释放属实。WinGetComFactory STA模式冲突是真潜伏缺陷，当前产品调用链经 WingetBoundedOperation.Task.Run 使用MTA，不能称正常路径必现。CLI 不杀installer是显式安全语义；native 5s只终止host而不代表取消installer。 | Localserver/UDPtest存储改生成的JsonTypeInfo，泛型enum转换器替代反射工厂，JsonPrimitiveConverter显式写JSON原语；两Store实现IDisposable。COM初始化容忍已有公寓，只有成功初始化时匹配CoUninitialize。保留原有安装结果/取消语义并纠正native注释。LocalserverLib/UDPtestLib 对 IL2026、IL2050、IL3050 恢复 WarningsAsErrors 门槛，既有无关风格告警仍按原策略；安装器长时退出策略保持既有契约。 |
| K54 | Lib确实返回报告中的字符串，属于稳定状态词汇，直接改Lib字符串会影响其他消费者。 | UI映射与生命周期由UI任务处理；本任务保持状态协议不变。 |
| R4/R9 | 兼容导出powertoy_create与native packages.config存在，但不构成运行错误。 | 不在本轮盲删ABI别名，不未经构建切换全部native依赖格式。 |

## 新增行为回归

- `ServiceSupervisorTests`：新增/改名/禁用目录更新及枚举/布尔配置源生成序列化往返。
- `SessionTests`：DisposeAsync等待跨代次请求、外部事件回调跨线程读取状态不死锁。
- `ServiceClientReuseTests`：新代次退休旧HTTP连接时，不释放仍在途的请求。
- `DiagnosticsTests`：无效IP报错，预取消的IPv6路径不伪报完成。
- `WingetBoundedOperationTests`：已有STA公寓调用COM包装时不抛ChangedMode且不破坏公寓。
- UDPtest既有存储往返测试保留并释放Store。

本分工遵照主任务安排未独立运行构建与整套测试，避免并发修改共享产物。范围内 `git diff --check` 通过；主任务执行后的 TRX 已回读核对，最终结果见文末。

## Runner 生命周期补充（K4 / K7）

- `KitModule::operator->` 现在返回 Runner 代理；DLL ABI没有变更。
- 每模块1个持有线程，初始化MTA；状态/配置缓存不持锁执行DLL代码；队列上限256，待执行的启停合并为最后意图，配置动作保持FIFO。
- 构造阶段仍同步加载DLL/工厂/初始schema，退出阶段仍等待drain；本项解决运行时启停阻塞，不声称首次DLL加载与退出清理无成本。
- 新增 `tools/tests/NativeModules/ModuleLifecycleTests.cpp` 4组真实fake行为检查：慢启动时UI读写不阻塞、配置串行、快速开关合并、退出保留配置并join、崩溃重启与禁用禁止重启、失败退避与异常后继续工作。
- 既有 `Invoke-AIHubLifecycleSmoke.ps1` / `AIHubLifecycleSmoke.cpp` 更新为真实 ModuleLifecycle 队列驱动：每次从生产 DLL 提取未改写的方法体，用 inert worker/handle 阻塞启动，验证 UI 响应、禁用序列化、析构 join、失败不虚报启用、崩溃探活/恢复和 200 次开关。不再硬匹配或注入旧启动线程；该测试不覆盖真实 Win32 进程/事件实现。
- 测试编译与执行仍由主任务统一完成；此处未把源码检查当作行为验证。

## 首轮回归后的修正

主任务首次执行 Localserver：33 项，16 通过、17 失败；未将这一轮记作通过。现有 DLL 运行诊断确认两处本轮回归：原生创建进程后再设置已关联 Process.StartInfo 会抛 InvalidOperationException；STJ 源生成对 init-only 属性的缺省构造参数会覆盖初始化默认值（Version=2 被覆盖为 0，集合/Health/Restart 被覆盖为 null）。前者删除无用 StartInfo 赋值；后者仅把持久化模型带初始化默认值的属性改为普通 setter，保留 required 身份属性和其余 init、源生成与原序列化选项。新增最小配置/嵌套默认、稀疏旧 HubSettings 和缺 ProcessChain 的旧 ownership 回归。修正已纳入主任务 build9，并由最终 Localserver 36/36 回归验证。

## 已核对的最终验证

主任务 build9 已通过；以下结果已直接读取 TRX 的计数与用例结果确认。没有把首次失败、源码检查或仅新增测试当作通过。

| 套件 | 结果 | 证据 |
|---|---:|---|
| Localserver（修正后重跑） | 36 / 36，0 失败 | `TestResults/Stable3101Final/Localserver/Zen_R7000P_2026-11-16_18_32_48.366.trx` |
| NetMap（首轮） | 116 / 116，0 失败 | `TestResults/Stable3101/NetMap/Zen_R7000P_2026-11-16_18_09_02.567.trx` |
| UDPtest（首轮） | 37 / 37，0 失败 | `TestResults/Stable3101/UDPtest/Zen_R7000P_2026-11-16_18_09_04.914.trx` |
| Winget（首轮） | 126 / 126，0 失败 | `TestResults/Stable3101/Winget/Zen_R7000P_2026-11-16_18_09_11.117.trx` |

K45 两项真实原生进程回归均已通过：`FirstInstructionAndImmediateChildBelongToTheOwnershipJob` 验证首条托管逻辑及立即派生子进程位于指定命名 Job，停机回收子进程；`SuspendedLaunchPreservesArgumentsEnvironmentAndRedirectedStreams` 验证空参数、空格、引号、尾反斜杠、Unicode 参数/环境/标准输入输出和工作目录。

默认配置/旧记录三项回归均已通过：`MinimalCatalogPreservesOmittedAndNestedDefaults`、`SparseLegacyHubSettingsPreserveDefaultsAndExplicitValues`、`LegacyOwnershipWithoutAProcessChainRemainsRecoverable`。首次 Localserver 的 17 项失败保留为真实失败记录，已按上节两项运行诊断根因修复；最终 36 项包含原有生命周期测试与新增兼容测试，均通过。
