# fix.md 进度独立复核（2026-11-16）

本次核对 fix.md 第 10 节、既有分工报告、验收记录及关键源码；没有重新机械复审 K1–K60/R1–R9 的全部历史代码。独立复核阶段只写此报告，构建与测试由主任务执行；现已补入主任务 3.5.1 最终验证结果。

## 当前结论

本轮已完成大量真实修复与 stable 跟随，并有可追溯的验证；不能概括为“fix.md 所有问题全部修完”。第 10 节比旧优先级表准确，已经区分多个误报与保留边界，但几个复合条目仍须拆开理解。用户报告的 **AI Hub 首次进入内容空白、切换 Optimization 后恢复**，现已找到并修复可稳定产生相同显示现象的路径：持久化标签索引为 -1 时，旧 3.5.0 产物把业务面板全部隐藏。修复后 10 个独立进程冷启动场景、真实 selector=-1 恢复、完整英文 Module smoke 均通过；WinGet Filters 默认折叠及相关交互在中英文 smoke 通过。截图发生瞬间的具体状态未捕获，不能据此断言截图当时一定是 -1。

此前前六步的 `[已完成]` 属于首次交付阶段；本次实机反馈已另行完成修复与回归。按用户“Debug 通过后版本 +1”的授权，源码版本已由 3.5.0 更新为 3.5.1，最终升版构建与针对性回归也已通过。`fix.md` 的旧第 1 节“待修”只是历史输入，应以第 10、11 节和最新跟进为准。

## 条目状态归类

下表的“已实现”不是逐项独立运行证明：实际覆盖以相关 TRX/smoke 和报告明确范围为准。复合编号可能同时包含修复、保留设计与未覆盖场景，不应压成一个“已修”计数。

| 编号 | 本次核对后的状态 | 应保留的限定 |
|---|---|---|
| K1–K3 | 身份绑定、受限消息、托盘创建/图标兜底已实现 | PID/路径等行为测试已有；真实跨 UAC 攻击链未做。本地未签名 Debug 支持仍保留，不应声称签名校验全部启用。 |
| K4–K8 | 生命周期监督、退场、备份保护、目录重载已实现 | 工厂加载与退出 drain/join 仍可能同步等待；不能把 K7 写成整个生命周期无等待。Localserver 重跑 36/36。 |
| K9 | 明确未做的架构迁移 | NetMap/UDPtest 返回 `engineAvailable=false` / `executionHost=Settings`；不是“已下沉业务”，也不是虚报后台引擎。 |
| K10 | 连接、读期限、单消息长度已加固；整体容量风险仍在 | 16 个连接/排序槽、5 秒读期限、1 Mi 字符消息不等于总输入/输出队列有界。详见下一节。 |
| K11 | 启动失败同步报告和启动回滚已实现并覆盖 | 不再因监听失败虚报 Running。 |
| K12 | 条件性 API 风险保留 | 当前产品调用链未找到 callback 自调用 end；自身线程 join 的边界未消除，不应记为“任意关闭都安全”。 |
| K13–K17 | 派发所有权、历史存储、合并标识、单例恢复及相关 UI readiness 调用点已修 | K17 的 10 秒是竞争上限，不能证明所有 UI 初始化路径均无卡顿；本次首屏空白由下述独立回归验证。 |
| K18 | 崩溃残留的清理机制已实现，风险窗口保留 | CLI 请求仍有明文文件；正常结束清理，崩溃残留超过一天才可能在后续启动清理，不是“磁盘无明文”。 |
| K19 | 跨账户不可解密恢复的显式降级已实现 | 保留目标端点/密钥、关闭 AI、提示复核；不承诺把旧账户 DPAPI 凭据移植成功，结构损坏仍拒绝。 |
| K20–K23 | 单向 kit 下发、托管管道收敛、ABI 异常边界、XML 按字段转义已实现 | 不应以此替代 K10 的总队列容量或 K12 的关闭边界验收。 |
| K24 | 已清理未编译的重复定义 | 原报告两个默认值同时生效的推断不成立；有效来源已统一。 |
| K25 | 已否决误报 | WingetService 没有报告声称的继承链；Logger 幂等真实问题归 K57。 |
| K26–K27 | 日志级别和清理同步等待已改 | 不代表所有日志架构统一。 |
| K28 | 原子公共写入和失败反馈已改；旧迁移完整竞争仍未重做 | 日常引擎使用 `service-settings.json`，模块用 `settings.json`；不能继续称二者日常必然互盖，也不能称所有跨进程旧迁移竞争已彻底解决。 |
| K29–K30 | 存储层计数归属、托盘菜单项已修 | 无额外完成性争议。 |
| K31 | 明确保留产品/诊断边界 | 自动更新保持 check-only，未开启自动安装或全局异常接管；不是一个仍待强制实施的功能 bug。 |
| K32–K37 | 服务宿主、kit.AIHub 路由、Guid、搜索、提权路径和资源标志已修 | K37 有潜伏调用条件，stable 自身也保留部分原缺陷；这些是本地加固，不是全部从 stable 直接同步。 |
| K38–K39 | 共享状态、正常退出刷盘、相关回调防护已实现 | K38 位置计时器子项是误报。共享 VM 修复不能代替此次非法标签状态的独立修复与验证。 |
| K40 | 普通失败路径回滚及临时目录排除已实现 | 不是整个配置目录的跨文件断电事务；极端磁盘/回滚失败不在已验证保证内。 |
| K41–K48 | 生命周期、线程归属、句柄、父进程、Job 及 NetMap 路径已实现修复 | K45 的真实父子进程和标准流回归通过；LightSwitch 无效父 PID 的真实缺陷后来另修并验证。不是所有长期运行场景已覆盖。 |
| K49–K51 | helper 全路径约束、包版本/卸载结果、局部启动配置已改 | K49 真实 CLI 原已传完整路径；不能把 helper 的宽松输入直接说成正常 CLI 可绕过。 |
| K52 | JSON/COM/AOT 与资源所有权修复；installer 取消契约保留 | STA 冲突原为潜伏条件，正常入口已有 MTA。5 秒后可终止服务 host，但不盲杀 installer、不宣称安装已取消，是安全契约；没有证据把保留契约本身定为未修产品缺陷。完整 Release/AOT 发布与真实长时安装退出并未覆盖。 |
| K53–K55 | 审计调度、UDP 文本/释放、GPU/GC/订阅问题已修 | 共享 CPU 单飞探针不绑定单个页面的取消令牌是设计，不能归入漏修。 |
| K56–K57 | 空集合、解析/资源、日志幂等和 ActionRunner 实际缺陷已修 | 空 handler map、多进程日志目录不是已承诺功能必然坏；没有新增猜测行为或合并竞争日志。 |
| K58 | 包注册/整库本地化建议未按原报告机械实施 | 尚未证明未承诺 kit:// 或 COM 激活功能损坏；应列产品范围/后续设计，不笼统记为高危 bug 未修。 |
| K59–K60 | QA 多订阅/排序覆盖、secrets.dat 刷新已修 | 对应行为回归已有；后续 UI 新问题独立追踪。 |
| R1 | 重复序列化登记已清理；类型大裁剪未做 | 文件名 grep 不构成安全删除 125 个模型的证明。 |
| R2 | “ActionRunner 孤立”统计已纠正 | ActionRunner 入方案且有调用；未接入安装的工具/测试项目不应无依据删除。 |
| R3–R5 | 枚举、旧输入和 ABI/辅助代码按兼容消费者保留 | 下发已收敛 kit；没有承诺移除全部 PowerToys 名称、导出或重编号枚举。 |
| R6 | VFT_APP 和 8 个废资源已改；其余兼容/归属保留 | Toast CLSID 改动需要配套注册/卸载迁移。当前保留是兼容迁移边界，不应无证据称产品缺陷未修；版权亦不应机械替换。 |
| R7 | 遥测仍关闭 | 工程/ETW 标识存在不证明运行时采集开启。 |
| R8 | 六个 PowerDisplay 默认键、三份死文件、两个错引已清理 | 本轮 stable 跟随有明确文件差异/哈希依据。 |
| R9 | 明确未做的工程迁移 | 全局编译开关与 native 包管理统一不属于本次安全修复的必要前提。 |

## 必须在“已修”旁明确的真实局限

1. **总 IPC 队列容量仍无界（K10 剩余风险）。** `src/common/interop/async_message_queue.h` 的 `queue_message` 仅检查 interrupted 后直接 `std::queue::push`；`two_way_pipe_message_ipc_impl.h:124` 同时持有 input/output queue。连接限流已解决当时的连接资源与 burst 丢消息，但慢消费者或生产快于消费时的总队列增长没有统一预算。已通过的两组 1000 条测试证明对应 burst 的完整与顺序，不证明持续压力下内存有界。
2. **callback 自关闭仍是条件性缺口（K12）。** `two_way_pipe_message_ipc.cpp:301` 无保护调用 `stop_started_threads`，后者在 `:335` join input thread。若在该线程自身调用 end，异常/Stopping 悬停边界仍存在；目前无产品可达调用证据，应列保留 API 风险，而非已经发生的用户事故。
3. **清理/事务保证有范围（K18/K28/K40）。** 崩溃请求明文的时间窗口、旧配置完整跨进程迁移、全目录断电事务均未因本轮普通路径测试通过而自动消失。报告已写明，应保持这些限制，不合并成“所有数据完整性问题结清”。
4. **告警门槛与验证平台有范围（K52）。** LocalserverLib/UDPtestLib 仍是 `TreatWarningsAsErrors=false`，但显式将 IL2026/IL2050/IL3050 升为错误；AIHubLib 已恢复门槛。最新增量 build10 的 0 warning 不是全量旧风格/WinRT 告警债清零。共享 AIHub AOT fixture 也不是所有库的完整 Release/AOT 发布验收。

这些与 K9/R9 等明确未做的架构迁移、K52 installer 安全契约、R6 CLSID 兼容迁移应分别列示，不能一起称为“剩余缺陷”。

## AI Hub 首屏空白：相同现象路径已复现、修复并回归

用户截图 `C:/Users/Zen/Desktop/Capture.PNG` 已目视核对：导航、AI Hub 标题、模块开关、readiness 和三项页签存在，页签下方没有业务内容。用户还报告切换 Optimization 后恢复。截图当时的 persisted tab、selector 和初始化顺序没有被捕获；下述测试已经复现并修复相同显示现象的一条确定路径，但不能把后来注入的 -1 值当作截图现场采集值。

上一轮 `ModulePageSmoke` 的流程先依次访问/启停多页，随后操作 General 的共享 AI 服务，再检查 AI Hub 政策、主题和回写。它对这些路径的 PASS 有效，但不能覆盖“全新页面首次显示且未切换页签”的所有状态；共享 VM（General/AIHub 的 `AcquireShared`）也使访问顺序成为应控制的测试条件。因此本次将每个冷启动组合放入独立进程，在任何切页前检查首帧，再单独运行完整 Module smoke。

修复前的证据：

- 旧 3.5.0 产物的独立 cold 0/1/2 以及 directRoute 0 首帧均通过；证据为 `TestResults/ui-cold-before-0.log`、`ui-cold-before-1.log`、`ui-cold-before-2.log`、`ui-direct-before-0.log`。正常的构造期暂态 selector=-1 本身不等于缺陷。
- 持久化 -1 在正常入口和 directRoute 均稳定复现：首帧 model=-1、selector=-1、Audit=Collapsed、业务内容尺寸 0×0、effectiveVisible=False，随后未切页首帧断言失败。证据为 `TestResults/ui-cold-before--1.log`、`ui-direct-before--1.log`。这是旧模型接受非法持久化索引后全部面板隐藏的确定路径。
- 保留早期失败历史：`TestResults/PluginLifecycle/en-US/20261117-040310-949-8e3a80cd53dc4d69810fc1fb9fd11ae2/module-pages-smoke.log` 的 cold 0 首帧检查通过，但同一混合流程随后出现 General Fallback mode 断言失败，整次运行没有通过。它来自 cold 初始化先建立共享 VM 后改变完整夹具假设的顺序污染；后续按独立进程隔离 cold 场景，并单独运行完整 Module 流程，已消除该夹具失败，不能把它追记为早期 PASS。

修复将持久化非法值归零，拒绝 setter 的 -1/越界值，并在 Loaded 与模型变化时按同一有效索引同步 selector 和三个业务面板；selector 无选择事件恢复模型选择，不通过双向绑定写回非法状态。只读复核未发现 Loaded/SelectionChanged 重入或缓存页重复加载的新问题。

修复后的证据：

- `TestResults/ui-cold-after-results.json` 记录 **10/10 独立进程场景 ExitCode=0**：中英文各 0/1/2/-1 共 8 个，加英文 directRoute 0/-1 共 2 个。对应 `TestResults/ui-cold-after-*.log` 与 `TestResults/ui-direct-after-*.log` 均记录未经切页的首帧内容检查 PASS。
- 每个场景在首帧检查后，向真实 Segmented 控件注入 selector=-1，均恢复有效选择和业务内容；没有往返切页，记录 `moduleMessages=0`、`settingsUnchanged=True`。每个场景结束均核验 11 份配置逐字节恢复或保持原本不存在。
- `TestResults/ui-followup-modules-en-US.log` 的完整英文 Module smoke PASS；其中实际 `kit.AIHub` 回写 31 次，`service-settings.json` 和 `secrets.dat` 字节及 SHA256 均未变，结束后 11 份配置恢复核验通过。
- `TestResults/ui-followup-winget-en-us.log` 与 `ui-followup-winget-zh-CN.log` 均 PASS：Filters 默认折叠释放 202 DIP、过滤、切换页签及窄窗口 flyout 行为通过。此项是用户追加的独立 UI 需求，不计入旧 K52 缺陷数量。
- `TestResults/ui-followup-settings.log` 及 `TestResults/UIFollowupSettingsBeforeBump` 记录 Settings 单元测试 **311 通过、0 失败**，原有共享配置恢复并核验。

以上为升版前修复产物的验证。最终 **3.5.1** 全方案 Debug 构建 exit 0，0 errors / 30 warnings；升版后又执行 Settings 311/311、无效索引直接启动及取消选择恢复、中文完整模块页、英文 WinGet 完整页面，均通过。程序集和稀疏包版本为 3.5.1.0。完整证据见 `TestResults/UIFollowup351/result.json` 和 `review-validation.md`；未将升版前的 10 个矩阵用例或前轮 1,937 项计数冒充升版后全量重跑。

## 已验证事实与尚未验证的界限

`TestResults/Stable3101Final/result.json` 的上一轮计数确为 **1938 项：1937 通过、0 失败、1 环境跳过**；不是每条 K/R 各有专用测试。`review-validation.md` 记录 build10 x64 Debug、Native IPC/QA、跨 stable ABI、生命周期、LightSwitch 实进程、共享 AIHub AOT、WinUI 与 NetMap 页面等验证。这些结果仍可保留为历史证据，不应被当前反馈抹除，也不应标作最新改动后的全量结果。

本轮已读过的 Localserver 36/36、NetMap 116/116、UDPtest 37/37、Winget 126/126 TRX 以及真实 K45/旧 JSON 配置回归结论继续有效。AIHub 模块回写在上一轮及本次完整英文 Module smoke 均确认 31 次 kit.AIHub 实际落盘、服务/密钥字节未变；早期“changed credentials”失败是旧 stub 只识别根 AIHub、计数为 0 导致短路，不能当作首屏空白的根因。

仍未覆盖：完整 Release 发布、真实跨 UAC 攻击链、长期休眠/主题日程、真实在线 AI 调用、全 Runner/托盘点击链路；用户截图发生瞬间的具体索引和初始化顺序也无法事后补采。已复现的非法索引首屏路径和真实 selector=-1 恢复已由本次 10 个场景覆盖，不再列作“未复现”。AiHub 符号链接测试 1 项因环境跳过，不能写成无遗漏全绿。NetMap 首次旧包装器未能保留原语言文件备份，不能声称历史上所有用户配置均逐字节恢复；本次明确记录的 11 份恢复核验不追溯覆盖该旧包装器局限。

本次记录同时保留前轮验收、早期失败诊断和后续独立回归，UI 修复与 3.5.1 Debug 产物验收已完成。架构不做、兼容保留或误报数量不充当“未修 bug”数量，也不以复合 K 条目计算误导性的完成百分比。

## 第二轮追加反馈验收

Localserver 缓存返回、NetMap 同行 AI 结果、General 首次配置布局及 WinGet 过期历史清理已完成，保留 3.5.1。最新增量 Debug 构建、Settings 311 项和 WinGet 131 项单测、相关中英文 WinUI 与冷启动失败恢复均通过；详见 fix.md 第 12 节、review-validation.md 最后章节和 TestResults/UIRound2/result.json。这里是用户新增的四项行为修正，不重算旧 K/R 条目的完成百分比；原架构/兼容和验证边界保持。

## 后续本地 Release 与提交验收

用户已授权本地提交和 `bin/release`。最终 3.5.1 x64 Release 单节点构建通过（30 warnings / 0 errors）；11 份最终单测 TRX 共 1,943 项：1,942 passed、0 failed、1 项目录链接环境跳过，不累计历史及重跑。核心/原生测试后仅追加 GeneralPage 呈现修正，最终重建、Settings 311 项及交付目录内 10 个独立页面场景通过。机器证据与本地提交号见 `TestResults/Release351/result.json`。

Release 首屏实测还暴露 560→531 DIP 的模板布局变化，已改为隐藏时启用控件并等待真实渲染布局稳定；精准首显回调六次冷启动均无后续位移。测试 fresh-page 的固定等待也改为真实呈现事件，保留严格可见文本断言。中间失败、并行构建清理冲突和最终通过日志均记录在 review-validation.md 最新章节。

`bin/release/3.5.1` 共 1,060 文件、0 PDB，版本、9 份依赖清单、资源和完整 SHA-256 核验通过。上文“尚未 Release”仅描述当时阶段；本次已完成本地 Release 目录验收。全部库 Native AOT 发布、真实长时安装退出等仍未覆盖；K10/K12/K28/K40 与 K9/R9 等保留项未改记完成。仅本地提交，不推送、不替换用户安装，外部 fix.md 保持忽略。
