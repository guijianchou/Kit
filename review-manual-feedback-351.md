# 3.5.1 实机反馈：UDP Test 与 AI 等待

实机窗口为 2026-11-16 23:20–23:32（America/Los_Angeles），后续修复于 11-17 继续。起点 `cf6969c`。正式日志快照 `TestResults/ManualFeedback351/20261117T073403Z` 包含 18 个文件及 SHA-256 清单；未清理或改写用户日志/配置。用户安装目录 `C:/Users/Zen/Repos/Projects/Kit` 的 Runner、Settings.dll 哈希与上一轮 Release351/result.json 一致。

## 已确认原因

1. **UDP 列表是重新呈现，不是每次读盘重载。** UDPtestPage 使用 Required 缓存；TCP/UDP 集合仅在首次初始化建立，导航返回不重新 PopulateLines。两张 ItemsPanel 仍有 DynamicListTransitions（AddDelete/Reposition），并且相同启用状态、重复激活会再次发通知/排队刷新。本轮取消这两张表的入口动画，并使无变化启用通知、重复激活幂等，继续保留离页期间的采样与回页最新快照。
2. **NetMap 被共享调度容量阻塞。** Settings 日志显示 23:25:45 进入 Queue，23:30:38 才 Execute，排队约 293.7 秒。持久诊断 task 耗时 306,346 ms，而同请求 main route 仅 12,618 ms，最后成功。不是 NetMap 模型本身计算了五分钟。
3. **AIHub 两批占满容量并贯穿备用尝试。** 公共配置 `maxConcurrentAnalysis=2`；审计 134 条事件硬分成 50/50/34 三批，前两批并行主服务各约 301.5 秒超时后，继续持有全局槽进行备用服务重试。备用各五次断流，约 229.6/230.9 秒失败，第三批才启动。Settings 正常关闭前第三批仍在等待，快照无该审计 task 终态。
4. **显示只计成功批次，隐藏其他阶段。** completed 仅在输出通过验证后递增，所以两批失败仍是 0/3。NetMap 只把排队/执行日志写到磁盘，页面固定显示“分析中”；审计页面从英文诊断文本猜状态，并发批次还会覆盖同一状态字段。

Codex/Pi 本身没有被证实缺乏并发能力。Kit 通过独立原生进程调用它们，上述容量限制发生在 Kit 调度层。主服务未输出有效结果、备用 StreamDisconnected 的具体网络/网关/服务端原因没有足够证据；日志没有 HTTP 状态，不能据此断言认证或网络错误。实机选择的 effort 为 max，原审计固定 50 条绕过了 effort 分批，是增加单批负担的因素，但不是服务超时的独立根因证明。

## 修复范围

- 多批任务最多占配置容量减一（下限一），给另一个任务保留容量；总进程上限不增加，配置为一时仍遵守串行。默认容量二时，单次审计只用一个槽，这是优先保证跨任务响应的吞吐取舍。主线路完整清理后，备用线路释放旧槽并重新排队，不能连续占用容量挡住已有等待者。
- 审计使用 effort 对应的动态分批（max/xhigh 每批最多 15 条），不修改用户模型、effort、服务地址或超时设置，也不丢弃已选事件。
- 进度新增成功/失败/进行中/排队、当前批次、主/备用服务、真实重试数、总耗时和排队耗时。排队每十秒报告；UI 只使用类型化字段，未知重试数不伪造为零，失败数不计作成功。NetMap 继续用 Proxy IP 同行状态，完整信息放在原详情/提示中。

## 其他日志

16 份正式模块/Runner/Settings 文本日志没有 Error/Fatal/Critical 或 Warning 标签，未发现测试夹具路径。Awake、LightSwitch、Localserver、NetMap、UDPtest、Winget 均有正常启停，Winget 检测成功。Runner 命令 40001 是托盘退出，LightSwitch 两次 disabling 是关闭与销毁阶段的幂等清理。

crash.log 记录 EventLogNotFoundException 的 FirstChance 采样和汇总 14 次，随后是 SessionEnd；没有 Unhandled 条目。此类可选事件通道不存在时的已处理异常不是崩溃证据。不把未发现错误写成所有操作都经过功能验证。

旧日志还有无序号的 retryAttempt=0/unknown 随后接 turn.failed；不足以证明额外执行一次重试。本轮新 UI 只展示正的已观察序号，备用线路重置计数；未扩大为未经证明的内核重试修复。

## 验证

### 当前构建与真实界面

- 最终产品全方案 x64 Debug 构建 `build-debug-complete.log` exit 0；增量构建摘要为 0 warnings / 0 errors。后续仅修改测试夹具，`build-fixture-complete.log` exit 0。产品仍为 3.5.1.0。
- 模块页中英文 `modules-zh-CN.log`、`modules-en-US.log` 均通过：UDP 同一 page/VM/集合/行/容器，选择、编辑草稿和滚动不丢失；重复激活/启用状态没有多余行刷新，离页后台快照与历史在返回时应用。
- NetMap 中英文 `netmap-zh-CN-final.log`、`netmap-en-US-final.log` 均通过：排队、备用重试进度可见，卡片维持 195 DIP、地图位置不变，1200/720 窗口和 651 DIP 出口区断点、IPv6/长文本详情、旧请求迟到进度拒绝均覆盖。另检查 `netmap-ai-retry-progress.png`，同行进度未遮挡 IP 或改变纵向布局。
- 模块页每次核验恢复 11 个配置文件，NetMap 每次核验恢复 5 个配置文件（逐字节相同或保持原本不存在）；Settings 单测恢复结果另见最终记录。

### 中间失败与调整

- 初始构建遇到 CancellationToken 参数顺序及测试样式错误，修正后重新构建通过；Settings 原静态断言仍要求旧签名，已更新，最终 321 项通过。
- 第一轮 AI 测试 522 通过、2 失败、1 跳过：部分结果截止测试旧夹具假设默认容量二时审计使用两槽；改为容量三，继续严格验证两个成功批次及截止语义。回收站夹具读取刚生成的自有元数据遭共享锁，增加仅针对共享/锁冲突的有界读取重试，未放宽可恢复内容断言。
- 第二轮 AI 测试 523 通过、1 失败、1 跳过：`CancellationDuringBlockedInputTerminatesTheProcessTree` 的自有 `children.json` 读取/Dispose 遭共享锁。该文件已由写端关闭后原子发布；改用共享读取并仅对共享锁重试最多 500 ms，保留进程退出及 JSON 验证。最终整套重跑 524 通过、0 失败、1 跳过。
- 首次英文 NetMap 在 720 宽度的详情文本加载/尺寸组合断言失败；加详细诊断后原断言通过，未捕获失败当次具体字段值。旧夹具仅等待 150/100 ms，现按 Flyout Opened/Closed 及加载状态同步，再验证原严格文本/宽度断言，中英文均通过。未据此宣称发现产品布局缺陷。
- 一次测试工程直接调用脚本被 PowerShell 签名策略阻止，没有执行构建；记录标为 not-run，随后通过标准 `pwsh -ExecutionPolicy Bypass -File` 运行 wrapper 成功。

测试使用合成配置及模拟 Codex/Pi 原生协议的独立子进程，证明 Kit 调度/回收/进度行为，不代表真实在线模型请求已经成功。在线服务端超时/断流仍需实机复测；默认容量二时单个审计只用一槽，多批总吞吐可能降低。未改用户模型、effort、端点、超时或安装目录。旧 `bin/release/3.5.1` 交付不包含本轮修改。

### 最终验收与实机入口

最终记录为 `TestResults/ManualFeedback351/result.json`：AI 核心 524 通过、Settings 321 通过、UDPtest 37 通过，共 882 通过、0 失败、1 项目录链接环境跳过；计数仅取各套最后一份 TRX。Settings 单测的原配置恢复校验通过，四次真实 UI 回归的恢复也通过。18 份诊断快照 SHA-256 校验一致；common 台账已按当前工作树复算并通过 `--check`。先前失败记录均保留。

实机启动 `C:/Users/Zen/Repos/Codings/Kit/x64/Debug/Kit.exe`，保持同目录完整运行时；版本 3.5.1.0。Runner SHA-256 `5A33DED49DF17569610E788D2D40974FF4452AB9BFA281839F0B8C9224CEBD0A`，Settings.dll `0EA044406ACF79B63ADD01CA936579437F91FE7F9DA9503A848DD43E82DF1732`，AiHub.dll `4C80B1164D90CBC4BB69763D6F71EB2AF184DAC8DF4853E00F687EF4903CC7EC`。测试结束无 Kit/测试进程，运行时内无临时 UI 测试文件。用户安装目录两份基准二进制哈希未变；本轮不属于已交付的历史 Release，尚未新提交 Git。

第二轮失败留下的自有临时目录 `%LOCALAPPDATA%/Temp/KitAiHubTests/b193aedd68f544b594dc4e3b04221ace` 已核验无运行进程，7 个测试文件；自动审批以 blocked by policy 拒绝删除，保留该目录，没有绕过策略。

建议实机同时运行 AIHub 审计和 NetMap AI check，确认后者可及时开始、前者显示明确阶段；再切换 UDP Test 页面确认 TCP/UDP 列表不重播。若在线仍超时，新进度及诊断可区分排队、主服务等待和备用重试，不能仅凭本地用例判定网关问题已消失。
