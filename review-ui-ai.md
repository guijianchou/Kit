# fix.md 复核及修复：Settings、QuickAccess、AI Hub

日期：2026-11-16。复核基线为 `2cb5ff9`，报告中的“已修”指工作区已实现，编译和运行测试由主任务统一执行。上游以用户重新提供的 `Source/PowerToys-0.102.3101` 为准；没有将原 `PowerToys-main` 当作该版本。

| 编号 | 复核结论 | 证据与实施结果 |
|---|---|---|
| K6 | 确认，已修 | `KernelManagerService.DownloadOrUpdateAsync` 的 finally 确实无条件删除备份。现在从移动旧目录开始保护备份，仅更新成功或回滚成功解除保护；失败错误包含实际备份路径。 |
| K14 | 确认，已修 | `AuditHistoryStorage` 原为实例锁、固定 `.tmp`。加入以完整路径摘要命名的进程间互斥量、唯一临时文件、严格写前读取；界面读取可降级为空，但损坏历史不能被下一次保存覆盖。新增独立实例并发写及损坏文件保留行为测试。 |
| K15 | 确认，已修 | `AIHubPageViewModel.MergeIssues` 原只比较 EventId。现在按日志通道与 EventId 合并；AI 省略通道时仅在来源通道唯一时补齐，避免任取一个同号事件。 |
| K16 | 确认，已修 | `AiHubEngine` 原 Lazy 缓存构造异常、Shutdown 后仍返回旧对象。改为锁保护可重试实例创建，关闭时先清除实例再释放。 |
| K17 | 部分确认，针对调用点已修 | `GetReadiness` 本身可能访问磁盘和存储锁；`AIHubPageViewModel` 与 `AiHubViewModel` 的界面刷新调用已放入 Task.Run，初始化原本已在后台。不能将所有构造函数均称为必现 10 秒卡顿；10 秒是锁竞争超时上限。 |
| K18 | 确认，已修崩溃残留回收 | CLI 的文件输入确实会产生临时明文。加入后台启动清理：只处理本根目录下符合 request-GUID 规范且超过一天的目录，拒绝重解析点；活跃请求持有独占 `.active` 租约，清理跳过仍被使用的目录。正常结束仍立即清理。一天以内的崩溃残留保留到下一次符合条件的清理，未宣称磁盘无明文。 |
| K19 | 确认，已修 | 严格备份 API 仍拒绝不可解密文件；显式恢复路径允许对 DPAPI 解密失败降级，仅恢复公共偏好，保留目标当前端点/密钥并关闭 AI。普通设置仍恢复，界面显示中英警告并提示手动重启并检查 AI 配置。缺少加密载荷、损坏 JSON 等结构错误仍拒绝整包恢复。新增行为测试。 |
| K24 | 分配给 Awake/LightSwitch 子任务 | 此项属于两个原生 LightSwitch 枚举及 Settings 模型默认值。 |
| K25 | 误报维持 | WingetService 并未继承 KitModuleServiceBase，不能由不存在的继承关系推导双重初始化。 |
| K26/K27 | 归属主任务 | 实际位于 `ManagedCommon/Logger.cs`，不属于此子任务的 AI Hub 或 UI 所有权。 |
| K28 | 部分确认，未按报告删除迁移分支 | 当前正式引擎文件是 `service-settings.json`；`settings.json` 只参与旧格式迁移/净化，写时以文件独占保护并保留模块属性。版本 3 journal 必带 legacy，报告中的常规删除路径不能视为必达。原生 `save_to_settings_file` 的静默失败反馈仍需主任务/原生模块侧协调，不能将本轮称为彻底解决所有跨进程迁移竞争。 |
| K29 | 确认分层问题，已修 | `AiHubStorageFiles` 三处反向调用设置存储的版本计数。现将原子计数放回存储层，设置层读取该版本；行为保持。 |
| K33 | 确认，已修 | 三个 AIHub 设置发送点原为裸 `AIHub` 根键。新增 `SndAIHubSettings.ToModuleJsonString`，统一发送 `kit.AIHub` 路由，并以 JSON 解析行为测试验证协议。 |
| K34 | 确认，已修 | Guid 的 `N[..8]` 被解释为非法格式。改为完整 N 格式唯一 ID；运行/停止过渡期间禁止添加线路，避免已快照的探测列表与界面错位。 |
| K38 | 确认主体；子项 2 维持误报 | General/AIHub 现在引用同一份、按拥有者计数释放的 AiHubViewModel，端点和策略草稿不再两份互盖。General 每次载入回填密码框，释放页面及 watcher。Runner 退出/崩溃通知切回 UI 正常 Close 流程，先刷盘再退出，覆盖后台保活。原“隐藏冻结位置计时器”不是事实，未修改该正常分支。 |
| K39 | 确认风险，已修涉及本范围的回调 | Shell 导航失败设置 Handled 并记录错误；Localserver、Winget、NetMap、AIHub、UDPtest、Shell 搜索及 General Loaded 增加外层异常边界。General 备份/恢复的实际磁盘操作在后台执行，结果/UI 更新回到原上下文。LightSwitch 页面由另一子任务处理。错误风险不等同于每个按钮必然崩溃。 |
| K40 | 确认，已修普通失败路径 | 先生成和验证全部普通 JSON 写入计划，保存原始内容，逐文件原子替换；普通写入失败回滚已写文件，AI 事务最后提交。当前使用的解压缓存目录明确排除清理。新增“锁住普通设置文件时 AI 快照不变”的行为测试。该方案不是跨全配置目录的断电事务，磁盘严重故障导致回滚失败仍会报错；未宣称任意断电完全原子。 |
| K52（UI 资源所有权） | 配合模块子任务修复 | UDPtest 等待初始化、存储写入、身份轮询及协调器退出后再释放 TargetSettingsStore/HttpClient/信号量/CTS；Localserver 在初始化收敛后释放 HubSettingsStore。类库 AOT 与 Store 实现由模块子任务负责。 |
| K53 | 确认，已修 | AuditScheduler 的配置字段加锁读取/更新；单次执行使用非排队门闩拒绝重入；停止等待现有循环，异步释放等待审计收敛再释放资源。新增异步 Dispose 后循环终止及禁止新运行测试。 |
| K54 | 确认，已修 | 补齐真实输出 `Single-Target Stable`、`Not Monitored`、`Unknown / Internal Error`，NAT 的 Full Cone/Address Restricted/Port Restricted 映射；注销 timer 和五个协调器事件，异步收敛后释放资源。 |
| K55 | 部分确认，已修实际竞态/性能项 | GPU 缓存读取、成功/失败发布均加锁；QuickAccess 不再每次隐藏强制全堆 GC 和清空工作集，AllAppsViewModel 增加配对释放。CPU 的 250ms 探针由共享 DashboardProbeCache 单飞任务持有，页面 token 只取消 `WaitAsync`；不把一个页面的 token 传给共享任务，避免取消其它订阅者。旧探针完成后可供新页面读取属于缓存设计，不能单凭“不等待”认定 UAF。 |
| K56/K57 | 归属主任务 | 原生通知与 ManagedCommon 日志。 |
| K59 | 确认，UI 侧已修 | EnabledModules 改为去重多播和配对移除；ReloadSettings 通知全部订阅者并监听原子替换事件；QuickAccess 每次显示刷新共享设置及更新徽标。排序只发送 `general.dashboard_sort_order`，不再发送本地过时的 Startup；Runner 缺失字段保留由主任务 K51 修复。 |
| K60 | 确认，已修 | 引擎同时监听 `service-settings.json` 和 `secrets.dat`，单独写密钥也会使缓存失效并通知。新增真实文件变更测试。 |
| R1 | 有冗余，但数量不是可安全删除清单 | 已去掉重复 OutGoingGeneralSettings JsonSerializable 特性。文件名 grep 未命中不证明类型无引用，序列化生成、反射/CLI 配置和兼容性必须另行核对；未盲删约 125 个模型。 |
| R6（UI） | 确认，已清理 | 全 src 搜索确认 8 个 QuickAccent/TextExtractor/CmdNotFound 资源键只留于资源和允许列表。删除英文废键并同步测试允许列表；图标、原生 RC、CLSID 由主任务处理。 |
| R7 | 架构/构建归属主任务 | 存在 ETW 工程不等同于遥测已重新启用；须跟随实际调用路径裁定。此子任务未重新启用采集、下载或安装。 |

## 验证记录

- 2026-11-16 使用 PowerShell 已加载的 Roslyn 5.0 解析 47 个本范围及协作的变更 C# 文件：`ParsedFiles=47 SyntaxErrors=0`。这是语法检查，不是引用解析、编译或行为测试。
- 初次尝试加载 SDK 10.0.403 的 Roslyn 时出现程序集版本冲突，后续改用 PowerShell 已加载的 Roslyn 完成实际解析；未将初次返回的零错误数当作成功证据。
- 新增/修改行为用例位于 `src/common/AiHub.UnitTests/ReviewRegressionTests.cs`、`AuditHistoryStorageTests.cs`、`AuditSchedulerTests.cs`、`src/settings-ui/Settings.UI.UnitTests/ModelsTests/ReviewProtocolTests.cs`、`AiServiceBackupTests.cs` 与 `ViewModelTests/GeneralBackupCommands.cs`。
- 按主任务协调，本子任务未运行构建或测试，结果待统一 x64 Debug 构建及 vstest 后追加。

## 第二轮协作复核

- 复核主任务 `KitServicePipe`/`KitModuleServiceBase` 改动后，补上 Start/Dispose 串行与重复释放幂等、运行中 Dispose 只请求取消并由 RunAsync 收敛释放、先停止 IPC 再停止业务、启动完成才开放 IPC、设置回调退出栅栏。避免关闭后仍执行处理器或 CTS 提前销毁。
- `Winget.UnitTests/ServiceHostReviewTests.cs` 新增真实命名管道及生命周期用例：退出等待在途处理器、重复 Dispose、超长帧拒绝后继续服务、Dispose 运行中的宿主且不覆盖坏设置。
- 主任务首轮构建揭示 ServiceHost 的 StyleCop 编译错误，已修对应 using 顺序、字段顺序、花括号和空行；此处未将首轮构建描述为通过。
- 第二次语法检查覆盖 52 个相关 C# 文件，`ParsedFiles=52 SyntaxErrors=0`；`git diff --check` 对本子任务目录通过。
- `BuildCompatibility` 上游输入明确指向 `Source/PowerToys-0.102.3101`，不存在时失败，不再无声跳过；包版本从该 stable 文件解析比较，删除 stable 中不存在的 `doc/devdoc/...` 旧路径分支。原 `doc/devdocs/...`、构建模板、发布脚本均已实查存在并仍使用 .NET 10。
- `NativeModules/Build-Harness.ps1` 同样绑定该 stable 树，不再回落 `PowerToys-main` 或另一个模糊路径。General 中对旧 changelog/development-log 字样的断言是历史记录，不改写历史为本次 stable。
- K19 再审确认：默认严格备份读取不变；显式不可解密降级携带标记，Import 在同一存储锁下读取并保留目标已有端点、密钥及其它 secrets。新增测试具体断言目标 API key、URL、模型、独立 secret 均保留。降级不会把空密钥覆盖到目标。
- 第四轮构建的 UI 分析器错误已修（readonly 字段排序、块尾空行、重复 HashSet 查询）。NetMapCard 是每轮重建的不可变快照，三处样式绑定改为 OneTime，去除没有变更通知的 OneWay 警告；可变列表项仍保留通知绑定。
- K52 补充：AIHubLib 恢复 TreatWarningsAsErrors；RecycleBinHelper 由运行时 COM 封送改为 GeneratedComInterface + LibraryImport，未加 IL2050 抑制。SDK 核对另发现原 IID 错误以及缺少 SetProperties 导致虚表错位，现按 Windows SDK 完整修正。后台 MTA 调用在专用 STA 执行，显式检查 HRESULT、取消状态并配对释放 COM 引用。该辅助类仍未接入实际优化删除流程，未改变现行优化范围。
- 新增 RecycleBinHelperTests：不存在路径拒绝；真实 WindowsShell 用例只创建随机临时夹具，验证原文件消失、对应回收站元数据的完整原路径及回收载荷内容，然后仅移除该夹具的两项回收文件。统一构建和运行结果待主任务记录，尚不据此宣称测试通过。
- AuditScheduler 二次复核：重复 DisposeAsync 现在等待同一释放任务；Stop/Start 交错时也配对释放捕获的旧 CTS，IsRunning 在相同锁下读取。已有退出行为用例同时等待两次释放调用。
- Settings 首次实际运行 310 项中的 `KitSettingsLaunchFailureShouldNotLeaveLaunchInProgress` 失败来自旧源码断言强制 WM_CLOSE。复核确认现路径清空 PID/启动门闩，通过已创建的托盘隐藏窗口把读取托盘设置与 PostQuitMessage 派发到 UI 线程；主消息循环退出后仍执行 QuickAccess 停止和 Runner 模块 scope-exit 释放。测试改为守护这些线程/清理边界，避免退回工作线程 PostQuitMessage。此仍为源码约束测试，不能替代真实窗口关闭/失败注入测试；重跑由主任务执行。
- AiHub 首次完整实际测试：`TestResults/Stable3101/AiHub/Zen_R7000P_2026-11-16_18_08_28.389.trx` 记录 518 项，517 Passed、0 Failed、1 NotExecuted。新增的真实回收夹具验证（0.356 秒）、缺失路径拒绝、AuditScheduler 双释放、K19 保留目标密钥、K18 活跃/新近目录保留及 K60 单独密钥通知全部 Passed。未执行项为 `ReparseDirectoriesCannotEscapeTaskOrStorageRoots`，不计为通过。
- Settings activation smoke 的 minimized 初次失败已定位：`TestResults/stable-review-settings-activation-2.log` 证明同 PID 的隐藏 `ConsoleWindowClass` 位于目标测试窗之前，符合现有 caption 判定，激活后控制台可见而测试窗仍最小化。`bring_settings_to_front` 与 HEAD 完全一致且调用同步 ShowWindow，不能归因为本次升级或 ShowWindowAsync 等待不足。夹具现先 FreeConsole，匹配无控制台 Settings GUI；保持原生产函数、恢复/锁/路由断言，并保留候选窗口诊断。此阶段等待主任务重跑，不将夹具修正直接计为通过。
- Settings activation 修正夹具后的主任务实际重跑记录 `TestResults/stable-review-settings-activation-3.log`：18 个用例全部通过，包括真实最小化恢复、同步回调重入及锁探测；前两次失败仍保留为诊断记录。
- ModulePageSmoke 在创建 apphost 时失败，实际原因是脚本以全局 SDK 11.0.100 的 HostModel 加载进 PowerShell .NET 10.0.14；GetType 明确报缺少 System.Runtime 11.0.0.0。SDK 10.0.403 同样反射检查成功。针对六个使用该进程内 HostModel 的 UI smoke，改从已安装 SDK 列表选择最高稳定 10.0 SDK（本机 10.0.403），输出所选版本；未扩大到只运行外部编译器的其它脚本。六脚本语法解析通过，实际 UI smoke 重跑仍由主任务执行。
- ModulePageSmoke 修正 SDK 后的退出码 `0xC000027B` 已按实际日志定位：Settings 10:38:23 的启动记录证明 XAML InitializeComponent 已完成，随后 `TwoWayPipeMessageIPCManaged.Start` 在 OnLaunchedFromRunner:236 抛出 E_FAIL。六个 fixture 传裸管道名，原生 CreateNamedPipe 直接接收该字符串；本轮 listener_ready.get 把绑定失败同步返回后显现此错误。现仅给六个 fixture 的 Runner/Settings 名字补全 `\\.\pipe\`，保留随机后缀和生产启动失败保护。不是据退出模块 Microsoft.UI.Xaml.dll 推断 WinAppSDK ABI 回归；修后实际重跑仍待主任务确认。
- ModulePageSmoke 随后运行到 AI Hub 写回断言失败：旧 fixture callback 只识别顶层 AIHub，而当前 UI 三处均发送 kit.AIHub，导致 pluginWrites=0；原 `<3 || ...` 断言短路，未比较服务/凭据字节，原错误文本不能证明凭据发生变化。现 fixture 接收 kit.AIHub，写后用 JSON 比较确认 AIHub/settings.json 实际落盘后才计数，并分别验证写入次数、service-settings.json 逐字节不变、secrets.dat 逐字节不变，只输出文件名/SHA256。交叉复核确认该阶段只保存 task policy，未发现服务 Save/Update 触发；仍等待实际重跑验证字节不变，不将密文随机化当作免责理由。
- ModulePageSmoke 最终实际重跑 `TestResults/Stable3101Final/module-pages-verified.log` 以 PASS 结束：31 次 kit.AIHub 模块对象均写后读取验证落盘；service-settings.json 与 secrets.dat 的 unchanged 均为 True，前后 SHA256 分别一致。共享 AI 编辑、策略保存/丢弃、真实夹具优化、主题切换及 18 张布局渲染全部通过。SDK、完整管道名与旧协议 stub 是三项独立夹具问题；首次失败及诊断日志保留，未用测试修改替代服务/凭据不变验证。

## 首次同步验收

以上“待重跑”描述保留各轮诊断时点，最终结果以 [review-validation.md](review-validation.md) 为准。最新全方案 x64 Debug 构建通过；Settings 311/311、AiHub 517 通过及 1 项环境跳过，18 个真实窗口激活用例和实际 ModulePageSmoke 均通过。

NetMap 中文页面最终独立运行 `TestResults/Stable3101Final/netmap-page-final.log` 以 PASS 结束，使用本次新建的报告目录，覆盖启停、取消、地图/MTR 更新、选择和滚动位置保持。旧包装器在启动子进程前挂起，中断时丢失了内存中的原语言文件备份；当前语言为 zh-CN，不能声称与首次运行前逐字节一致。后续脚本已改用直接创建进程及每次全新报告目录，最终运行恢复了其自己的配置快照。完整恢复状态及未覆盖项见验收报告。

## 实机首屏反馈跟进（2026-11-16）

旧实现接受并保存 `ActiveTabIndex=-1`，使三个内容区全隐藏；该路径已在普通导航和直接启动两种真实页面测试中复现。修复后，非法持久值回退 0、setter 拒绝非法值，Page.Loaded 和合法模型变更统一同步选择与面板，控件初始化的空选择不再覆盖模型。用户截图具体触发过程未捕获，不据相同现象虚构现场因果。

10 个中英文独立冷启动场景及后续真实控件取消选择注入均通过；完整模块页也重跑通过，包括共享服务编辑、31 次 `kit.AIHub` 写回以及服务/凭据不变。冷启动独立运行，避免它在原夹具的合成配置准备前创建共享 VM。311 项 Settings 单测通过。WinGet Filters 折叠及最终 3.5.1 产物结果见 [review-validation.md](review-validation.md) 末尾。
