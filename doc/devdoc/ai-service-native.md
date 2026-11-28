# AI service 原生执行与端点设置

AI service 是 Kit 的共享任务底座，AI Hub 和 NetMap 等消费者通过 `IAiTaskEngine` 提交任务。执行链始终为 `TaskAiEngine → RouteDispatcher → Codex CLI / Pi CLI`；模型请求由所选 CLI 发出。Kit 负责输入整理、策略、并发、结果校验和状态呈现，不另建 HTTP 推理客户端，也不在原生调用失败后切换到 HTTP 推理。

## Main 与 Fallback

通用设置页的 AI service 区分别提供 Main 和 Fallback 端点编辑器。两个端点各自保存 URL、凭据、模型、API 模式、推理强度和上下文窗口。填写并保存 Fallback URL 后启用备用路由；清除后保存则停用。页面修改先保留为草稿，保存后才供新任务读取，放弃修改会恢复已保存值。

`AiTargetSettings.ContextWindowTokens` 的默认值为 **256000**，Main 与 Fallback 可以分别配置。旧的 `service-settings.json` 没有该字段时，两条路由保持这个默认值；读取旧配置不会为补字段而立刻重写文件。设置值必须是 1024 到 `int.MaxValue` 之间的整数，Kit 不维护按模型划分的上下文上限。设置应与实际端点和模型匹配；填大数不会扩展服务端能力。

Codex 配置使用该值设置 `model_context_window`；Pi 模型配置使用 `contextWindow`。请求开始后使用当次配置快照，正在运行的请求不会因页面编辑改变模型或上下文。上下文由原生内核管理，Kit 不以估算 token 数裁剪输入。单条输入 64,000 字节、总输入 8,000,000 字节、每批输入 96,000 字节等宿主安全预算保持独立，不代表模型的完整上下文可全部用作输入。较小的自定义窗口可能被原生内核或端点拒绝，应检查返回的错误原因。

托管设置接口继续使用 `AiHubSettingsStore.Update(Action<AiHubConfig>)`，在最新配置上修改指定路由，避免以过期完整配置覆盖其他页面的更改。例如：

```csharp
var store = new AiHubSettingsStore();
store.Update(config =>
{
    config.GetMainTarget()!.ContextWindowTokens = 256000;
    config.GetFallbackTarget()!.ContextWindowTokens = 128000;
});
AiHubEngine.RaiseStateChanged();
```

序列化字段名为各 `targets` 条目内的 `contextWindowTokens`。模型的 `Clone()`、设置事务、缓存副本、保存和撤销均保留它。不要通过直接重写 JSON 来保存凭据：API key 仍由现有 DPAPI 凭据存储管理。

## 原生能力校验

API 模式与推理强度的下拉列表是可输入的建议列表，不是支持列表。`AiTargetSettings.IsValidNativeIdentifier` 仅检查语法：非空、最多 128 字符、以 ASCII 字母或数字开头，其余只允许 ASCII 字母、数字、下划线、点和连字符。Kit 不根据模型名或内核建立模式、推理档位白名单，也不再将 `medium` 改成 `high`、将 `xhigh` 改成 `max`。

Codex 的 API 模式原样写入 `wire_api`，推理强度原样写入 `model_reasoning_effort`。**当前 Codex 原生内核只接受 `responses` wire API**；Kit 允许保存格式正确的其他标识，并不保证 Codex 能执行这些标识。原生内核拒绝时应显示原生配置失败，不能静默换协议。后续内核版本的支持范围由该内核决定。

Pi 保留两个旧设置别名的兼容：`responses → openai-responses`、`chat → openai-completions`。其余 API 标识原样写入 Pi `models.json`；推理强度原样传给 `--thinking`。其他 API 或推理档位是否可用，由安装的 Pi 版本及模型端点确认。切换 Codex/Pi 不会替用户重写已保存的端点能力配置，因此切换后应运行连接测试。

## 重试与备用路由

一次 Main 调用先完整等待原生 CLI 的重试和最终结果。Kit 不在 CLI 外再套同路由重试循环。只有 Main 最终失败、错误允许备用接续或路由超时，并且原生进程树已完成清理，才尝试已启用的 Fallback。Fallback 仍使用当前所选的原生内核，并读取它自己的模式、模型、推理强度和上下文设置。

调用方取消、整个任务的截止时间、宿主策略失败和清理失败不会触发备用接续。端点鉴权、模型不可用等错误是否允许 Fallback，由归类结果决定；备用路由仍须成功执行并通过校验，不能只因发生了切换便报告成功。原生返回值还必须经过任务 JSON 与安全契约校验；CLI 退出码为 0 本身不等于分析成功。连接测试同样要求原生 CLI 返回约定的空探测 JSON。

路由记录在开始清理请求目录前确认截止状态。在期限内已成功取得的原生响应，不会仅因随后目录清理跨过期限而变成路由超时并重复调用 Fallback；清理失败仍使该路由失败，调用方取消仍优先于已取得的响应。宿主通过 Windows Job 终止并等待本地 CLI 进程树退出，再释放该 attempt 的资源；逻辑上的单个内核子代理并没有独立取消接口。

## 宿主管理的分析 worker

Codex 和 Pi 使用相同的宿主编排：`TaskAiEngine` 先统一脱敏输入并分配请求内唯一的 `itemId`，再按记录数量与字节预算划分证据分片。每个分片交给一个 `analysis-worker`，调用所选内核的无工具分析路径；宿主 `coordinator` 等待结果、验证并合并，不额外占用模型执行槽。worker 只分析自己的输入，不能递归创建其他 worker。

常规页保存以下服务设置，旧配置缺少字段时使用默认值：

| 配置字段 | 默认值 | 含义 |
| --- | --- | --- |
| `maxConcurrentAnalysis` | 2 | 当前引擎最多同时执行的原生分析 attempt，允许 1–4 |
| `enableSubagents` | `true` | 是否允许单个任务并发运行多个宿主 worker |
| `maxAgentsPerTask` | 3 | 单个任务同时运行的 worker 上限，允许 1–4，仍受服务总并发限制 |

这些字段位于 `AiHub/service-settings.json`。`AiTaskOptions.EnableSubagents = false` 可将单次任务限制为一个 worker；`null` 跟随服务设置，`true` 也不能绕过关闭的服务开关。NetMap 的 `route-analysis` 显式使用单 worker。每任务上限限制同时执行的数量，不限制一个任务可以先后处理的分片总数。默认总并发为 2，因此单个任务默认最多同时运行 2 个 worker。

等待队列优先接纳当前活跃 worker 较少的任务，同等占用按等待顺序处理。只有一个任务时可以使用全部允许的空闲容量，不固定空置一个槽；其他任务到来后，在已有 attempt 完成并释放容量时获得公平准入，正在执行的调用不会被强行中断。降低总并发、降低每任务上限或关闭多 worker 后，已经运行的 attempt 可完成，后续准入遵守新预算。正在运行的请求保留最初的并行度上限，增加预算或重新开启多 worker 需要新任务才能使用增加的并行度。

并发限制的范围是**当前 `TaskAiEngine` 实例**。AI Hub、NetMap 等 Settings 消费者共享同一引擎会话；它不是机器范围或跨进程的配额。`AIHubWorker` 当前只做本地定时规则审计，不运行这些 AI worker。关闭 Settings 进程会终止其中的 AI 会话，尚无独立后台 AI broker、跨进程运行恢复或任务 checkpoint 续跑。

此实现与内核自主多代理分开。两种内核都报告宿主管理 worker 能力；原生自主委派尚未接入，Codex 的协作事件不能被当成普通成功响应，Pi 也不依赖原生 agents 才能使用宿主 worker。仅安装具备相关能力的新内核版本，不会自动开启原生委派。外层 worker 并发与内核内部代理数不得各自放大而绕过宿主预算。

### 身份、进度与结果

一次调用拥有稳定的 `RunId` 和 `CoordinatorId`，每个分片有独立 `AgentId`，其 `ParentAgentId` 指向 coordinator。每次 Main/Fallback 准入生成独立 `AttemptId`；切换备用时保留原来的 Agent 身份和输入 `itemId`，只更换 attempt。`AiTaskProgress` 携带这些标识、固定角色、批次和排队/执行状态，不携带原始模型输出。诊断中的 `RequestId` 与 `RunId` 对应，并记录 Agent、Parent、Attempt 和角色；节点不是新的顶层任务，不能重复计入请求总数。

`AiTaskResult.Agents` 返回各分片的终态元数据，包括是否验证成功、错误、路由和尝试次数；规划阶段失败的节点标记为 `not-started`，已结束的请求不会留下仍在排队的节点。仅通过输出契约和任务 schema 校验的分片可以参与合并。启用 `AllowPartialResults` 后，任务超时或部分失败可保留已验证结果，并明确返回非完整成功；用户主动取消仍返回取消，不包装为完整或部分成功。

`AiTaskSchema.ReportValidatedBatch` 可观察已验证的分片结果，用于增量显示。回调收到独立副本，修改副本或抛出异常不会污染最终合并；取消后的节点不再主动发布回调。回调消费者自行切回 UI 线程，并校验当前任务及取消状态，拒绝已经排队的旧回调。回调只表示一个分片已验证，最终请求仍可能取消或失败。

## AI Hub 扫描与增量分析

扫描先收集事件、完成本地规则分析并发布发现项，再进行可用的 AI 增强分析。AI 运行时持续保留本地结果，并在 worker 校验完成后更新报告；AI 取消、超时或部分失败会明确标记分析未完成，不删除本地发现，也不把中间报告伪装为完整结论。单次扫描只产生一份审计历史，增量 worker 快照不增加扫描次数。

AI 审计仍最多选择 400 条事件，优先高严重性与近期记录；本地规则处理全部收集事件。完全相同的重复事件合并打包并保留每次出现时间和原始证据引用，每批最多 64 组，同时受 96,000 字节输入预算限制。整个 AI 审计预算为 1,800 秒，每条原生路由预算为 900 秒；本地路由超时不会静默改用备用模型，符合条件的端点失败仍可进入已配置的 Fallback。并行调度减少不必要的串行等待，但不能保证真实端点的响应速度或完整分析必定在预算内完成。

## 日志与统计接口

通用设置页的“运行统计”显示**当前进程内引擎会话**的统计，重启进程后重新计数；多个进程的数字不会合并为一个累计总数。请求统计区分成功、失败、取消、超时和部分完成，路由统计记录 Main/Fallback 调用及原生 retry。token 计数仅采用原生事件明确报告的输入、缓存读取、缓存写入与输出用量；没有用量的路由按未知处理，以 `KnownUsageRoutes / RouteAttempts` 标明覆盖范围，不把未报告等同于零消耗。内存中最多保留最近 32 条 task/route 记录。连接测试和 readiness probe 暂不纳入任务统计。

磁盘历史位于 `%LOCALAPPDATA%\Kit\AiHub\Logs\diagnostics.json`，最多 256 条且不超过 256 KiB，按最早记录先删除的方式限制大小。持久化遵守 Kit `log_settings.json` 的 `logLevel`：trace/debug/info 记录所有完成事件；warn/warning 与 err/error/critical 只记录失败、超时及部分完成，兼容 Settings 实际保存的 warn、err 缩写及 ManagedCommon 的别名，大小写不敏感；off 停止追加。关闭日志时仍保留当前会话的内存统计。历史与会话累计分开，加载历史不会把旧请求再次计入当前会话。

记录仅包含受限的元数据：请求及 worker/attempt 标识、固定角色、插件/任务、内核、路由、结果、耗时、批次数量、原生 retry 次数/上限、token 用量、上下文、退出码、HTTP 状态、阶段、归类后的原因和取消来源。日志不保存 prompt、模型输出、推理正文、API key、端点 URL 或未经处理的原生错误文本。写盘失败只增加持久化失败计数，不改变 AI 任务结果。

托管调用可读取 `TaskAiEngine.Diagnostics.GetSnapshot()`；`LoadHistory()` 单独读取有界磁盘历史。现有 IPC version 1 增加 `action: "get_diagnostics"`，响应的 `Statistics` 为会话快照。`AiTaskResult` 与任务 IPC 响应添加可选 `Diagnostics`，携带可安全呈现的失败/重试元数据，既有任务字段和协议版本保持兼容。

## 代码入口与回归范围

- 设置模型与事务存储：`src/common/AiHub/Models/AiTargetSettings.cs`、`src/common/AiHub/Storage/AiHubSettingsStore.cs`。
- 页面与草稿状态：`src/settings-ui/Settings.UI/SettingsXAML/Views/GeneralPage.xaml`、`src/settings-ui/Settings.UI/ViewModels/AiHubViewModel.cs`。
- 原生调用及事件解析：`src/common/AiHub/Engine/RouteDispatcher.cs`；任务调度、worker 身份与备用路由：`src/common/AiHub/Engine/TaskAiEngine.cs`、`TaskAiEngine.Agents.cs`；接口：`src/common/AiHub/Models/AiTaskContracts.cs`。
- 设置回归：`NativeEndpointSettingsTests`、`StorageAndPolicyTests`、`AiHubSettingsTests`，覆盖旧配置默认值、两路独立值、原生标识保留、非法输入拒绝和原文件保留。
- worker 回归入口：`AgentSettingsTests`、`ConcurrentTaskTests`、`AuditAccelerationTests`，覆盖配置兼容、公平借用、动态降低准入、身份关联、备用尝试、部分结果与取消回调；测试执行结果单独记录。

本说明描述源码行为；真实端点的模型可用性、鉴权和原生内核能力仍需用相同配置执行连接测试。构建和自动回归记录应单独注明具体版本、产物和验证范围。
