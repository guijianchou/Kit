# AI service / AI Hub review（2026-09-30）

## 执行底座补充 review（2026-10-02）

本轮按“现有任务更稳定更快：并发、流式进度、失败恢复”收敛，修改共享引擎及回归测试。

- **并发排队**：此前单个请求通过 `Task.WhenAll` 一次排入全部批次，后来的短任务需要等待整个大任务队列。现在每个请求只保持配置并发数以内的批次参与现有 FIFO 调度，结果仍按输入顺序合并。并发上限属于同一个 `TaskAiEngine` 实例，不是跨进程的系统总上限；改善的是竞争时的排队响应，没有宣称模型推理本身加速。
- **进度隔离**：增加 `Queue` 阶段，隔离直接从 `IProgress.Report` 抛出的订阅方异常，避免展示进度失败改变任务结果。异步 UI 回调自身的异常仍需调用方处理。
- **流式阶段**：读取 Codex/Pi 原生 JSON 事件，实时报告开始、推理、生成阶段和 Pi 重试信息；重复阶段去重，沿用诊断数量上限。不把模型正文或推理内容放入诊断。本轮只接通服务层阶段信号，没有新增 Settings UI 阶段文案。
- **原生恢复**：Pi 的失败 assistant 消息可能出现在内核自动重试之前。可恢复传输失败现在允许后续完整成功消息覆盖；认证、配置和策略违规仍拒绝，只有重试成功标记而没有有效最终输出也不会算成功。继续复用内核重试，未新增外层重试循环。
- **退出确认**：终止 Windows Job 后，仅等待根进程退出不足以确认子进程已结束。现在在原有清理期限内检查 Job 活跃进程归零，再允许返回及备用路由接续；清理失败不进入备用路由。

没有改动 PowerToys 主框架、插件 ABI、IPC 或设置 JSON 格式，也没有新增依赖。适配集中于 Kit 的任务执行和原生事件解析；后续内核升级仍需运行协议回归，不能保证任意上游协议变化自动兼容。

参考：[Codex 非交互执行](https://developers.openai.com/codex/noninteractive)、[Pi 会话和重试](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/src/core/agent-session.ts)、[Pi JSON 事件转换](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/src/modes/json-event.ts)。

本轮使用合成 CLI 子进程验证调度、事件、取消、超时及进程树清理，没有调用真实模型端点，没有替换运行中的 Kit 或更新发布压缩包。两项超时测试的路由预算由 1 秒调整为 3 秒，给 Codex 预检及夹具创建子进程留出时间；生产超时配置未改。

验证：通过仓库构建脚本完成 AI Hub 测试项目 Debug x64 构建，退出码 0；完整回归 312 项，311 通过，1 项因环境无法创建目录符号链接跳过，0 失败。报告：`TestResults/AiServiceReview/ai-service-full-final.trx`。本轮未进行完整 Kit 发布构建或真实端点性能测试。

范围：AI service 是 Kit 内部共享底座，目前消费者是 AI Hub；不新增外部服务、接口层或宿主。

## 本次修复

- `AiHubViewModel.RunOperationAsync`：原生 UI 回调可能没有托管同步上下文；异步完成后在后台线程通知 `CanEdit`，触发 WinUI `Control.set_IsEnabled` 跨线程异常，中断命令状态刷新。现在整条操作链捕获 UI dispatcher 上下文。真实 WinUI 回归中，原实现稳定失败，修复后恢复正常。
- 服务配置改为 `AiHub/service-settings.json`。Windows 上 `AiHub/settings.json` 与插件的 `AIHub/settings.json` 是同一文件；插件保存标签页时曾覆盖服务配置。仅迁移有效旧服务配置，保留旧文件和 DPAPI 凭据；事务恢复兼容版本 1、2。插件文件变化不再使服务状态缓存失效。
- 服务配置保存保留已有审计间隔、模式和保留天数。任务策略不再按“少于 2500 字节”判定过期并覆盖用户内容。
- AI Hub：新增服务设置入口、重新检测的进行中状态及重复调用保护；安全审计标题与操作分行，历史统计折叠；优化页精简重复卡片，候选项出现后展示选择统计；任务策略默认选中安全审计，并显示保存结果。

## 后续优化点（review 结论，未扩大本次实现范围）

1. **优先：检测结果和执行能力的含义需要一致。** `TaskAiEngine.ProbeReadinessAsync` 在主端点失败后，只检查备用配置是否合法就报告 `Degraded`，没有实际检测备用端点；`ValidateConfiguration` 也不验证内核是否已安装。因此 UI 的“备用可用”结论比证据更强。建议分别报告配置有效、主端点验证、备用端点验证，且未检测备用时不要宣称它可用。
2. **优先：后台审计与共享服务的开关、配置归属不一致。** `AIHubWorker.Program` 从共享 `AiHubConfig` 读取审计间隔/模式，并用服务 `IsEnabled` 门控后台审计；页面使用插件配置。关闭 AI 服务后，页面仍可做规则扫描，但 Worker 会暂停。后续应让插件统一拥有审计计划，服务开关仅控制 AI 执行；本次仅保留旧字段，避免保存时丢失。
3. **检测调用应复用已有生命周期约束。** `AiHubIpcHandler` 的 `get_status(refresh)` 和 `self_test` 在 `_requests` 注册与数量限制前处理，按 requestId 取消及并发限制不覆盖它们。`ProbeReadinessAsync` 本身也没有复用执行调度器；配置变更使缓存失效后，较早开始的探测仍可能写回旧结果。下一步应验证关闭/取消/改配置期间的探测行为，而不是新增独立检测框架。
4. **原生与托管调用的能力边界需要写清楚。** 托管 `IAiTaskEngine.ExecuteTaskAsync<TInput,TOutput>` 已支持调用方提供类型元数据、输出校验和批次合并，足够复用。原生 `ai_hub_ipc.cpp` 依赖运行中的 Settings IPC，`AiHubIpcHandler` 的输出固定为 `AiTaskReport`；不能据此承诺所有内部插件都支持任意输出契约。等有第二个实际原生消费者时，再按其需求扩展现有协议。
5. **残留的领域约束应在出现实际消费者时拆分。** `OutputContractAuditor.KnownActions` 固定为 `skip/move/delete/restart`；`AiServiceSelfTest` 对具体 `TaskAiEngine` 做类型判断，即使读取 readiness 已属于接口能力。前者应由真实任务契约决定，后者可在下一次检测链路修改时直接调用接口。

参考源码：

- [共享执行与检测](../src/common/AiHub/Engine/TaskAiEngine.cs)
- [IPC 请求处理](../src/common/AiHub/Engine/AiHubIpcHandler.cs)
- [服务自检](../src/common/AiHub/Engine/AiServiceSelfTest.cs)
- [输出审计](../src/common/AiHub/Security/OutputContractAuditor.cs)
- [后台审计](../src/modules/AIHub/AIHubWorker/Program.cs)
- [原生 IPC](../src/runner/ai_hub_ipc.cpp)

## 配置与退出行为

无需强制清空。现有完整服务配置可迁移；已被旧版插件覆盖的端点信息无法凭空恢复，需从备份恢复或重新填写。为排除旧状态影响，可停止受管服务、从托盘退出 Kit，将 `%LOCALAPPDATA%\Kit` 整体改名备份后再启动。首次测试先用新配置，不立即还原旧 JSON。迁移不会删除凭据、内核、策略或历史。

当前 `show_tray_icon=true`。Kit 与参考 `Source/PowerToys-main` 的 `MainWindow.Window_Closed` 都只关闭 Settings，Runner 留在托盘继续承载模块；属于一致行为，未改为点击 X 就结束所有模块。Kit 在隐藏托盘时有额外的 Runner 退出路径，Localserver 有后台服务保活逻辑；这些是源码中已有的产品差异，不能把“Runner 仍存在”本身当作泄漏证据。

## 验证范围

构建：Debug x64 共享库、Settings、AIHubWorker、AI Hub 测试项目均通过；Worker 与 Settings 的共享库 SHA-256 一致。AI Hub 测试共 227 项：226 成功，1 项因环境不支持创建目录链接而跳过，0 失败。WinUI 测试使用实际页面/控件、模拟 Runner 配置落盘；覆盖开关、异步忙碌恢复、编辑保存、导航、任务策略反馈及宽窄窗口渲染。测试前后备份并恢复用户配置。检测与清理不调用真实 AI 端点，不执行文件整理或清理。自动桌面工具此前把 Kit Settings 窗口识别为 Windows Settings，故不以其交互结果作为修复依据。

WinUI 回归记录：`TestResults/PluginLifecycle/aihub-final-smoke.log`。测试报告：`TestResults/PluginLifecycle/ai-service-final.trx`。宽窄布局渲染：同目录 `aihub-tab-*-preview.png`（WinUI 内容渲染叠加中性底色，不含桌面 Mica 合成效果）。

## VS Release 复现与补充验证（2026-09-30）

- `Kit.slnx` 的 Runner 依赖包含其他模块，却漏掉 AI Hub 原生模块和 Worker。用户构建后的目录中两者都缺失；Runner 日志记录 `MODULE_LOAD_FAILED`、`0x8007007E`。现已补齐两项依赖，并定向编译生成缺失组件。
- 仓库带有可继承的 Windows Low Mandatory Level 标签，`x64` 中实际 EXE 也继承该标签。本次运行日志回退到 `AppData/LocalLow/Kit`，并记录写入正常配置目录被拒绝。对现有 `x64` 目录执行 `icacls .\x64 /setintegritylevel "(OI)(CI)M"` 后，子目录和 EXE 恢复正常标签；未降低配置目录权限。该修复是本机文件系统状态，不随 Git 提交传播。若删除输出目录或整理到同样继承低标签的 `bin` 目录，需再次检查产物标签。清空旧配置不能修复权限问题。
- 此前 UI 测试只把测试宿主 EXE 单独改为正常标签，因而未覆盖真实启动产物的权限问题。测试脚本现保留输出目录的标签，支持 `-Configuration Release`，使用 Runner 启动参数，并要求报告以 PASS 结束，避免 Release 无参数退出被误认为成功。
- 补编后的实际 Release Runner 已加载全部五个模块，未出现加载失败或日志回退。Release WinUI 测试完成两轮开关、展开编辑器、端点编辑保存、无同步上下文回调、异步忙碌恢复、插件写回、策略反馈及布局检查，结果 PASS。配置在测试后按原始字节恢复，未调用真实 AI 端点。

本地证据位于 `TestResults/AIService-ReleaseRegression/`：`native-build.log`、`worker-build.log`、`ui-smoke-console.log`、`runner-startup.json`。这不是完整重新编译或发布包验收；此前全量命令行构建在稀疏包步骤失败，后续完整编译与实机验证由用户完成。本次不创建 GitHub Release 或发布压缩包。
