# 2026-11-16 stable 跟随与 fix.md 实施验收

基线：Kit 3.5.0 / `2cb5ff9` 未提交工作树；前次 PowerToys v0.102.3001.0 → 本地 stable `Source/PowerToys-0.102.3101`。逐项裁定见 fix.md 第10节及各 review 报告。

## 首次同步验收结果

- 最新全方案 x64 Debug 构建 exit 0：[build10](TestResults/stable-review-build-10.log)。最终增量日志0 warnings / 0 errors；不表示既有告警债清零，build8重编仍有23条非IL风格/WinRT告警。AIHubLib恢复WarningsAsErrors；Localserver/UDPtest对IL2026/IL2050/IL3050启用错误门槛。
- 本轮实际测试共 **1,938项：1,937通过，0失败，1跳过**。按每套最终TRX计数，不累计修复前失败或重复运行，见 [result.json](TestResults/Stable3101Final/result.json)。AiHub链接测试因环境无法创建目录符号链接跳过。
- 共享AiHub Native AOT fixture编译/运行通过，覆盖加密存储、schema、禁用任务和IPC。首次运行被旧TEMP父目录ACL拒绝，改用本次测试目录后通过，没有修改旧目录ACL。
- 管道两种模式各1,000条，均完整接收、零乱序/损坏；12项原生IPC/QA行为通过，覆盖16连接压力、超时恢复、PID鉴权、启动失败和取消。
- 模块生命周期4组、跨stable ABI、AIHub 200次启停、18个真实窗口激活场景通过。LightSwitch假worker生命周期/热键和真实worker预置停止、无效父进程退出均通过；主题值不变，原LightSwitch配置逐字节恢复并校验。
- 真实WinUI模块页通过，包含AI服务编辑器、政策、主题/布局和31次插件回写；服务配置与密钥文件逐字节及SHA256不变。NetMap中文页面独立通过启停、取消、地图、MTR更新、选择和滚动位置检查。
- `git diff --check` 和 `generate-common-drift-ledger.py --check` 通过。参考哈希清单涵盖483个文件。

| 测试套件 | 通过 | 失败 | 跳过 |
|---|---:|---:|---:|
| AiHub | 517 | 0 | 1 |
| Awake | 9 | 0 | 0 |
| Awake.ModuleServices | 3 | 0 | 0 |
| CommonUtils | 401 | 0 | 0 |
| Managed Interop | 1 | 0 | 0 |
| LightSwitch CLI | 221 | 0 | 0 |
| LightSwitch Service | 147 | 0 | 0 |
| Localserver | 36 | 0 | 0 |
| Native Interop / QA | 12 | 0 | 0 |
| NetMap | 116 | 0 | 0 |
| Settings | 311 | 0 | 0 |
| UDPtest | 37 | 0 | 0 |
| Winget | 126 | 0 | 0 |

## 验证中修正的回归

1. IPC第一版16连接上限丢burst消息（602/1000）。改为接入前等待空位、按接入顺序发布，发送保持连接直到接收结束；最终两次1000/1000。
2. Localserver首轮17项失败：给已关联Process再次赋StartInfo；JSON源生成把省略的init默认值变成零/null。修复并补旧配置回归后36/36。
3. LightSwitch旧热键缺省modifier兼容问题已修。真实服务绑定无效父PID失败后仍常驻、吞线程退出码也已修；stable上游自身同样存在后者。
4. 旧测试夹具包含禁热键、Off无worker、旧AI根键、裸管道名、控制台抢占窗口和SDK11 HostModel假设。保留失败证据，按实际契约修正后重新执行，没有删除关键断言。

## 边界与未覆盖项

- K9完整服务化、R1/R3类型/枚举批量裁剪、R9全局构建迁移属于独立架构工作。K12 callback自调用end未在产品链路发现，IPC输入/输出队列总容量未统一限额。K28完整旧迁移事务、K40全目录断电事务未重做。
- 首次同步阶段只验证x64 Debug，当时没有Release发布、真实跨UAC攻击测试、长期睡眠/主题日程或真实在线AI调用。后续Release交付见末节；保留check-only更新、遥测关闭和七模块ABI。
- 为保留用户启用的自启动任务，未运行会将其指向Debug的完整Runner/托盘点击冒烟；热键和QA在原生夹具/IPC层验证。
- Settings和LightSwitch测试明确恢复并校验原配置。NetMap首次旧包装器在ShellExecute阶段挂起，中断后未保存原语言文件的内存备份，因此不能证明该文件与测试前逐字节一致；该次只写入zh-CN语言，尚未启动页面。当前语言为zh-CN，自启动仍为true。最终脚本改用直接创建进程、中完整性临时exe及每次全新报告目录，避免挂起和读取历史PASS。没有修改用户ACL或开机任务。

## 首次交付产物（历史版本）

- Runner：[x64/Debug/Kit.exe](x64/Debug/Kit.exe)，版本3.5.0.0。
- Settings：[x64/Debug/WinUI3Apps/Kit.Settings.exe](x64/Debug/WinUI3Apps/Kit.Settings.exe)。
- Runner SHA256：`fc7b8a95e001c28ecfde002271da56283c0fb5334623e4d68e5be942db691d76`。

首次同步交付时未提交或推送Git，未替换用户安装目录，`.dsh-acl-recovery/`未改动。最终状态以本报告最新章节和相应result.json为准；早期status.json保留当时失败。

## 实机反馈跟进与 3.5.1（2026-11-16）

上文的 1,937 通过来自首次 stable 同步验收，不代表本次每个 UI 修改后重跑了全部套件。用户随后报告 AI Hub 首屏空白并要求 WinGet Filters 折叠，本轮独立处理并重新验收。

旧二进制的保存标签 0/1/2 正常，`-1` 在普通导航及直接启动时均重现空白（`TestResults/ui-cold-before--1.log`、`ui-direct-before--1.log`）。修复为模型仅允许合法标签，非法保存值回退 Security Audit，由页面统一同步选中状态及三个内容区。没有捕获用户截图当时的索引，所以只确认这条相同现象路径。首次冷启动夹具混入全套流程使共享 VM 在合成配置前创建，导致后续 Fallback mode 断言失败；现将冷启动放入独立进程，原全套顺序恢复后通过。

升版前本次针对性结果：

| 验证 | 结果 | 证据 |
|---|---|---|
| 全方案 x64 Debug | exit 0，0 warning / 0 error | `TestResults/ui-followup-build.log` |
| 冷启动与取消选择恢复 | 10 个独立进程全部通过；中英文 0/1/2/-1，英文直接启动 0/-1 | `TestResults/ui-cold-after-results.json` |
| 每次真实 `SelectedIndex=-1` 注入 | 有效选择与内容恢复，0 模块消息，配置字节不变 | 上述各运行日志 |
| 完整模块页 | PASS；31 次插件写回，服务配置与密钥字节不变 | `TestResults/ui-followup-modules-en-US.log` |
| WinGet 中英文页面 | PASS；默认折叠释放 202 DIP，实际筛选、标签状态、窄弹窗及宽窗口恢复通过 | `TestResults/ui-followup-winget-en-us.log`、`ui-followup-winget-zh-CN.log` |
| Settings 单测 | 311 通过，0 失败 | `TestResults/UIFollowupSettingsBeforeBump` |

本次 ModulePage 包装脚本恢复并逐字节核验 11 个配置/策略文件（原不存在的文件核验仍不存在）；每次生成独立目录与 `restore-verification.log`。WinGet 和 Settings 单测的恢复核验亦通过。本次没有新增语言恢复缺口；此前 NetMap 中断的历史限制仍保留。

用户在回归通过后要求版本加一；已将 `src/Version.props`、稀疏包清单和当前文档升为 3.5.1 / 3.5.1.0。最终结果已核验，机器记录见 [UIFollowup351/result.json](TestResults/UIFollowup351/result.json)：

- 全方案 x64 Debug 构建 exit 0，0 errors / 30 warnings（风格、CA 与 CsWinRT1028 等告警，未声称告警债清零），日志 `TestResults/ui-followup-build-351.log`。
- 升版后 Settings 再次 311/311、0 skipped；无效标签直接启动及实际取消选择恢复、中文完整模块页、英文 WinGet 完整页面再运行均 PASS。日志分别为 `ui-followup-351-settings.log`、`ui-followup-351-cold.log`、`ui-followup-351-modules.log`、`ui-followup-351-winget.log`。
- Runner、Settings、QuickAccess、Awake、LightSwitch 服务和已构建稀疏包都核实为 3.5.1.0。LightSwitch 服务目录中此前错误项目引用留下的旧 `Kit.ManagedCommon.dll/.pdb` 经原生导入表及源码确认无消费者，移至 `TestResults/UIFollowup351/obsolete-output`，保留可恢复副本；测试目录旧 DLL 副本不计入运行目录验收。
- 当前入口：[x64/Debug/Kit.exe](x64/Debug/Kit.exe)；设置程序：[x64/Debug/WinUI3Apps/Kit.Settings.exe](x64/Debug/WinUI3Apps/Kit.Settings.exe)。Runner SHA256：`5A33DED49DF17569610E788D2D40974FF4452AB9BFA281839F0B8C9224CEBD0A`。

本轮没有 Release 发布、在线 AI 请求或真实 WinGet 安装测试。源码参考树未改；未提交或推送 Git，未替换用户安装目录。最终 UI 产物以本节为准，前面的 3.5.0 哈希和全套测试计数保留为首次同步验收历史。

## 第二轮实机反馈验证（2026-11-16，UTC 日志日期为 11-17）

版本保持 **3.5.1 / 3.5.1.0**。最终全方案 x64 Debug 构建 exit 0，当前增量构建记录 0 warnings / 0 errors；不表示之前记录的整个项目告警债全部清零。构建日志 `TestResults/ui-round2-final-build.log`。机器验收记录 `TestResults/UIRound2/result.json`。

| 验证 | 最终结果 | 证据 |
|---|---|---|
| Settings 单测 | 311 passed / 0 failed / 0 skipped，共享配置恢复核验通过 | `TestResults/UIRound2/Settings`、`ui-round2-settings.log` |
| WinGet 单测 | 131 passed / 0 failed / 0 skipped；含新增 5 项历史清理边界、保存失败、真实管道身份和运行队列隔离测试 | `TestResults/UIRound2/Winget/Winget.trx` |
| 模块页 | 英文/中文 PASS，Localserver 启用与禁用缓存返回、状态/容器/选择/展开/滚动和采样恢复均通过；完整 AI Hub/策略/主题回归保留 | `ui-round2-modules-en-US-retry.log`、`ui-round2-modules-zh-CN.log` |
| General 独立冷启动 | Enabled / Disabled / Failed PASS；配置准备期间不展示默认状态，首次完成呈现后高度分别稳定 531 / 244 / 296 DIP；真实失败重试先恢复策略编辑，再检查开关 | `ui-round2-general-enabled-final.log`、`ui-round2-general-disabled-final.log`、`ui-round2-general-failed-final.log` |
| NetMap | 中英文 PASS；busy/result、长 IPv6/标签、出口区 651 DIP 双列断点和 720px 窄窗口卡片均保持 195 DIP，地图位移 0；真实打开详情完整展示四类文本 | `ui-round2-netmap-en-US.log`、`ui-round2-netmap-zh-CN.log` |
| WinGet 页面 | 中英文 PASS；History 按钮本地化、忙/断连/禁用/重复请求门禁、删除数与列表刷新，队列行保留；Filters 回归保留 | `ui-round2-winget-en-us.log`、`ui-round2-winget-zh-CN.log`；弹窗单独渲染见 `ui-round2-winget-dialog-zh-CN.log` |
| stable 源码漂移账本 | `--check` exit 0，参考树哈希与当前账本一致 | `ui-round2-drift-check.log` |

**前后证据与中途修正：** 原 General 独立冷启动日志 `PluginLifecycle/en-US/20261117-051815-973-a95f00ffe1de470da46eca6f11122ee3` 记录可见默认态 252→560→531 DIP、下方 Diagnostics 位移；所有 expander 始终关闭。原 NetMap `netmap-before-inline.log` 记录 AI busy 卡片 195→220 DIP、地图 482→507 DIP，布局断言真实失败。NetMap 前测包装器恢复写入完成后，新增的泛型 SequenceEqual 校验发生 PowerShell 重载错误，故不声称该次完成恢复核验；已改为字节 Base64 比较，后续两个正式运行各核验五份文件。Localserver 首次夹具把 Rendering 当作 Dispatcher 已处理通知，失败后改为等待真实 StateLabel 事件与低优先级队列屏障，产品未为此改变；保持断言重跑通过。两次编译失败（AutomationLiveSetting 命名空间、readonly 字段顺序）修正后重新完整构建。独立 review 补上重试后的策略加载及不可误关闭的错误入口，最终失败恢复用例通过。

本批 ModulePage 每次核验 11 份配置/策略恢复；Settings 单测和 WinGet 包装器同样核验其备份文件，WinGet 还检查真实 service-state.json 在测试过程中未变。后续冷启动夹具固定使用 example.invalid/fixture-model，避免在截图中带入用户目标。截图来自真实 WinUI 控件；History 是 popup，单独渲染 dialog，不能用 shell 截图当成弹窗截图。

**本批边界：** 页面/IPC 通过合成数据和隔离管道验证；未启动用户 Localserver 服务、调用在线 AI、执行真实 WinGet 安装或清理用户历史。没有 Release 发布、Git 提交/推送、安装目录替换。此前旧 NetMap 包装器语言备份中断的历史限制仍保留，当前恢复成功不追溯证明旧记录。旧 1,937 单测计数属于首次 stable 同步历史，本批重跑单测合计 442 项。

AI service 配置核查结论保持：公共设置在 `%LOCALAPPDATA%\Kit\AiHub\service-settings.json`，Main/Fallback 的 URL、model、mode、effort、context 和 key 在当前用户 DPAPI 加密的 `secrets.dat`；策略为 security.md 和各任务 AGENTS.md。任务引擎按新任务读取存储配置，经 dispatcher 生成内核调用配置，没有把用户服务目标硬编码进程序；默认值、协议映射和内核下载源仍在代码中。已有任务使用提交时的配置快照。

## 本地 Release 交付（2026-11-16，UTC 日志日期为 11-17）

用户随后授权本地 Git 提交和 `bin/release` 产物。本节替代上文各历史阶段的“未 Release / 未提交”状态，版本保持 **3.5.1 / 3.5.1.0**。本次不推送、不替换用户安装目录；`Source` 参考树、被忽略的外部 `fix.md`、测试/构建产物及 `.dsh-acl-recovery/` 不纳入提交。机器记录与本地提交号见 [Release351/result.json](TestResults/Release351/result.json)。

最终源码使用构建包装器和 `/m:1` 完成 x64 Release，exit 0、30 warnings / 0 errors，见 `TestResults/Release351/build-layout-serial.log` 及对应退出码。首次全量 Release 同样通过；中间并行重建遇到共享 WinUI3Apps 语言目录被多个项目同时清理的 MSB3231（27 errors），保留 `build-layout-final.log`。单节点构建避开该构建竞争，没有将失败记录改记成功；并行清理限制仍存在。

Release 单测按各套最终 TRX 去重统计 **1,943 项：1,942 passed / 0 failed / 1 skipped**：

| 套件 | 通过 | 跳过 | 最终证据目录（TestResults/Release351/） |
|---|---:|---:|---|
| AiHub | 517 | 1 | unit-verified/AiHub |
| Awake / ModuleServices | 9 / 3 | 0 | unit-verified/Awake、AwakeModuleServices |
| LightSwitch CLI | 221 | 0 | unit-verified/LightSwitchCLI |
| Localserver / NetMap / UDPtest / Winget | 36 / 116 / 37 / 131 | 0 | unit-verified 下同名目录 |
| Settings | 311 | 0 | Settings-final |
| CommonUtils / LightSwitch Service / Native Interop | 401 / 147 / 12 | 0 | native（同一 TRX 按程序集划分） |
| Managed Interop | 1 | 0 | interop |

唯一跳过为 `ReparseDirectoriesCannotEscapeTaskOrStorageRoots`，日志明确当前环境不能创建目录链接，未据此推断具体权限原因。核心/原生套件完成于最后 GeneralPage 呈现修正前；产品随后只改 GeneralPage，最终重新构建并重跑 Settings 311 项及下述页面测试。重复运行不累加计数。早期中断的 `unit/` 无完整结果；`unit-final` 将最低通过数设为 518，但实际为 517 passed + 1 skipped，退出 9，随后以正确阈值在 `unit-verified` 重跑全部八套成功。

**首屏位移的最终修正：** 初始整理后 Release 的 General enabled 冷启动仍复现完成显示后 560→531 DIP，Diagnostics 位置变化 29 DIP，所有 expander 始终关闭。原因是测量后才启用整页模板，同时显示内容和恢复动画。最终在 opacity=0 时启用并更新绑定，等连续真实渲染帧布局一致后再显示；离页和释放均解绑 Rendering。测试在 Opacity 改为 1 的同步回调中采样，未使用轮询延迟或再测量掩盖首帧。

最终直接运行 `bin/release/3.5.1/WinUI3Apps` 内产物的 **10 个独立场景通过**：

- General enabled 中英文各两次，首显与稳定时高度均为 531 DIP，Diagnostics 位置不变；disabled 244 DIP、failed 296 DIP，加载期间界面可响应、错误重试恢复真实编辑能力。证据：`final-general-*.log`，共六次。
- 中文完整模块页，包括 Localserver 启用/禁用缓存返回、选择/展开/滚动/容器保留、AI service 编辑/重载/主题/策略和 AIHub 页面，通过；33 次插件写回且 service-settings/secrets 字节不变。证据：`final-modules-zh-ready.log`。
- AIHub 无效标签 -1 直接启动及真实取消选择恢复通过；NetMap 同行分析的宽/窄布局与详情通过；WinGet Filters 和七天历史清理界面/隔离 IPC 通过。证据：`final-aihub-cold-direct-en.log`、`final-netmap-zh.log`、`final-winget-zh.log`。

上述完整模块页第一次 `final-modules-zh.log` 失败：fresh 页面测试只等待共享 VM 和固定 150 ms，就在隐藏页主动展开检查。测试现等待真实挂载、Loaded、Opacity 和 hit-test 事件，不降低文字/尺寸/焦点断言，重跑通过。此前 `final-ui-results.json` 的该失败记录保留，最终 `result.json` 明确选用通过运行。各包装器的配置恢复核验通过，未运行用户服务、真实安装、在线 AI 请求或清理用户历史。

**交付核验：** [bin/release/3.5.1/Kit.exe](bin/release/3.5.1/Kit.exe)，应保留整个目录。共 1,060 个文件、607,590,984 字节、0 PDB；53 个自有二进制及稀疏包版本为 3.5.1.0，9 份依赖清单/2,137 项资产、资源和全目录 SHA-256 核验通过，无额外冒烟文件或 LightSwitch 托管残留。Runner 和 Settings 文件哈希与最终构建输出一致。清单为 `TestResults/FrameworkReview/Release-3.5.1.manifest.json`，独立复核为 `TestResults/Release351/delivery-verification-final.json`，运行目录检查为 `runtime-artifacts-final.log`。该检查首次因脚本签名策略未执行，随后以构建流程同样的进程级 Bypass 运行通过，未更改机器策略。

Runner SHA-256：`9551D8536A702D022BC86391815CECE97238E9869F69E2338E59EE69336889AD`。漂移账本在提交前 `--check` 通过，398 个 common 跟踪文件、304 个同路径比较、483 个参考哈希；摘要的 kit_head 是生成时父提交，不是最终交付提交号。

K10/K12/K28/K40、K9/R9 等明确限制保持。Release 构建及目录验收不等于全部库 Native AOT 发布、真实跨 UAC 攻击测试、长时安装退出、睡眠/主题日程或完整 Runner/托盘点击验证；此前 NetMap 语言恢复缺口仍保留为历史边界。

## 2026-11-17 实机反馈：UDP Test 与 AI 调度

起点 cf6969c，版本保持 3.5.1.0。当前修复为未提交工作树 Debug，历史 Release 交付不含本轮变化。完整证据与中间失败见 [review-manual-feedback-351.md](review-manual-feedback-351.md)，机器记录为 `TestResults/ManualFeedback351/result.json`。

最终全方案 Debug 构建及后续测试夹具工程构建 exit 0；AI 核心 524 通过、1 项目录链接环境跳过，Settings 321 通过、UDPtest 37 通过，共 882 通过、0 失败。测试使用模拟 Codex/Pi 原生协议的独立子进程，覆盖审计阻塞时小任务可执行、备用排队公平、总并发限制、取消/截止及进程回收。

模块页与 NetMap 中英文四次真实 UI 回归通过：UDP 返回缓存页面保留容器、选择、草稿和滚动，后台快照及时呈现；NetMap 排队/重试仍在 Proxy IP 同行，卡片 195 DIP 与地图位置保持，完整详情/窄窗和迟到进度拒绝通过。各 UI 包装器逐字节核验配置恢复，Settings 单测同样核验原配置恢复；运行时无临时 UI 测试文件。18 份正式诊断快照哈希一致，用户安装目录基准二进制未替换。

实机入口 `x64/Debug/Kit.exe`；在线远端的主服务超时和备用断流仍未验证修复。默认并发二时审计最多一槽以保留跨任务响应，总审计吞吐可能降低。一次失败夹具留下的临时目录已确认无存活进程，自动审批拒绝删除，原样保留并记录在 result.json。
