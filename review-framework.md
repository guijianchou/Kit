# fix.md 复核：Runner 与公共框架

2026-11-16。原始 Kit HEAD 为 `2cb5ff9`（3.5.0）。本轮参考为用户替换后的 `Source/PowerToys-0.102.3101`，前次跟随为 v0.102.3001.0。下表的“修复”指当前工作树实现；最终构建/测试结果单独记录，不用历史 Release350 产物佐证。

## 逐项裁定

| 编号 | 裁定、处理及边界 |
|---|---|
| K1 | 确认。Quick Access 仅接收模块状态请求或单个排序字段；先挂起创建子进程，绑定实际 PID、路径、版本的管道策略后才恢复执行。撤销跨权限 WM_COMMAND 放行，改为仅激活设置页的注册消息。AI 响应入口限制 1 Mi 字符并验证托盘窗口的进程/线程。 |
| K2 | 确认身份绑定不足。Settings 同样以挂起子进程建立精确 PID 策略再 ResumeThread。保留未签名本地构建支持，未打开 Microsoft 签名限制；报告将其直接定为任意 EoP 仍需满足同用户/提权会话等前提。 |
| K3 | 确认消息窗口不能依赖图标。图标读取失败仍创建窗口并使用系统应用图标；更新标记路径亦走兜底。 |
| K4/K7 | 确认核心问题，见自研模块报告。Runner 每模块串行线程负责启停、配置和动作；UI 读期望开关与缓存活体状态，崩溃后有退避重试。Quick Access 普通禁用排队，最终退出负责 drain/join。工厂加载和最终退出仍需等待，不宣称整个生命周期无同步成本。 |
| K5 | 确认脆弱退场顺序。先关闭 Settings IPC，再封闭模块队列、注销热键、销毁模块。 |
| K10 | 确认无界连接/读取风险。限制最多 16 个处理器、5 秒读取期限、1 Mi 字符消息；拒绝奇数字节和空消息，回收完成连接后再判断容量。新增消费者 MTA 初始化。输入/输出队列并未改成整体容量有界。报告引用的 AuthenticateServer 是旧 main 特性，当前 stable 的鉴权头与原 Kit 相同，不能标为漏同步 stable。 |
| K11 | 确认。start 等待管道绑定和消费者初始化；线程或绑定失败同步抛出并回收已启动线程，不虚报 Running。 |
| K12 | 条件性风险。callback 内自调用 end 会触发自 join，但当前产品调用链未找到此用法；正常退出和部分线程启动失败纳入行为测试。未将任意 join 异常吞掉或 detach 掩盖对象生命周期；自关闭 API 合约仍是局限。 |
| K13 | 确认。主线程消息投递前用 unique_ptr 持有缓冲，只有成功交付才释放所有权。 |
| K20 | 确认。Runner 只下发 kit；接收仍兼容 powertoys。删除整包设置的 info/error 日志，保留短错误上下文。 |
| K21 | 确认。托管服务管道单活跃请求、限长读行和 30 秒期限；串行/idempotent 启停，退出等待处理器，再停止业务。已有鉴权保留，使用 Identification 身份级别。 |
| K23 | 确认。xml_escape 返回转义结果，仅转义插入的文本/属性，不转义整段 Toast XML。 |
| K26/K27 | 确认。运行时 debug 级别在 Release 也可输出；删除日志清理任务内的固定 500ms 同步等待。 |
| K28 | 部分确认。正式 AI 引擎文件为 service-settings.json，旧 settings.json 是迁移路径，不能按同路径推导日常必然覆盖。公共 json::to_file 改同目录临时写、Flush、原子替换，失败抛出并保留原文件；完整跨进程迁移事务并未重做。 |
| K30 | 确认。补入 RC 的 Quick Access 菜单项，使已有菜单更新逻辑有实际目标。 |
| K31 | 属于产品/诊断边界。自动更新空桩保留 check-only；未为清理死代码开启自动安装或全局异常接管。 |
| K32 | 确认。实例互斥对象改为不取得线程所有权的存活标记；坏设置/null 不覆盖；定时器、重载和退出加栅栏，处理原子替换 Rename。 |
| K35 | 确认。StringMatcher 及归一化后搜索入口都拒绝空白；新增真实匹配行为测试。 |
| K36 | 确认。优先已知运行目录，提权时不信任 HKCU 路径，令牌检查失败按提权处理；保留明确分开的 Kit 与兼容 API。 |
| K37 | 确认资源/标志缺陷，严重度应区分当前可达与潜伏调用。修复 token/属性列表释放、CREATE_NO_WINDOW 用按位或。新 stable 仍保留部分原缺陷，属于本地加固，不再称 stable 已修而 Kit 退化。 |
| K41 | 确认。Quick Access 生命周期改持有的串行线程，避免线程池 COM 模式冲突；请求消费与再次排队有明确顺序，退出 join。 |
| K42 | 确认。JSON 与消息调度异常边界、参数 LocalFree、WinRT/标准异常处理、二启 hwnd 校验；后台关闭回到 UI；更新失败保留上次链接，不能调度 UI 时不越线程改托盘。 |
| K43 | 确认公共进程句柄问题。未确认终止时保留追踪句柄，避免丢弃仍活跃进程身份。模块细节见两份模块报告。 |
| K44 | 确认。RunnerHelper 检查 EnumProcesses 返回值及实际数量，GetExitCodeProcess 失败即结束轮询；Worker 固定父进程句柄见自研模块报告。 |
| K48 | 确认。RestartManager 用 vector.size；FileWatcher 比较实际小写名；子进程输出管道使用 PID/tick/序号唯一名称及 FIRST_PIPE_INSTANCE；异常缓冲按完整字节数清零；Debug 移除 NDEBUG、修正引用 GUID。 |
| K49 | 部分确认。共用 helper 现在要求完整路径；真实 CLI 调用原本已传完整路径，不能称正常调用可被任意目录同名直接绕过。LightSwitch 恢复生成的 P/Invoke 和 SafeHandle；原 runtime marshalling=true 并不等于 AOT 一定构建失败。 |
| K50 | 确认。MSIX Major/Minor/Build/Revision 对齐 Kit 三段版本映射，最后一段为 0；每次重枚举包，卸载错误/取消返回 false。Kit 没有 VERSION_BUILD 宏，未照抄四段宏。 |
| K51 | 确认。局部 JSON 缺字段保留原值，自启动仅在显式 boolean startup 时变更；非提权高权限任务回退普通任务并复位设置。 |
| K56 | 部分确认。空用户列表先返回、删除未消费异步属性读取；后台激活的数字解析加异常边界。空 handler map 属未接入功能，不添加猜测行为。 |
| K57 | 部分确认。Logger 初始化幂等；ActionRunner 校验绝对 target、按 Windows 规则重组参数、释放 argv，移除对 mapping handle 的 FlushFileBuffers。多进程分别写日志目录是已有约定，未合成竞争单文件。 |
| K58 | 事实与故障结论须区分。当前稀疏包仅声明 Settings，不能据此认定未承诺的 kit:// 或任意 COM 激活失效。未新增高风险注册项；子库中性资源也不等于所有页面未汉化，本次修正可达 UDP 状态文本。 |

## 残留项裁定

| 编号 | 处理 |
|---|---|
| R1 | 删除重复序列化登记；按文件名 grep 的 125 模型数量不是可安全删除证据，保留需消费者审查的兼容类型。 |
| R2 | 原报告错误：ActionRunner 已在 Kit.slnx，Windows 路径大小写不能作为孤立工程依据，并被去提权回退使用。Update/updating 未接入活跃安装路径，工具 fixture 方案外存在合理用途；不盲删目录。 |
| R3 | 未活跃枚举存在，但可能涉及序列化/兼容数值，未据“仅七模块”重编号。 |
| R4 | 下发收敛 kit，旧键输入与 powertoy_create ABI 别名保留，避免破坏已有消费者。 |
| R5 | 按可达性分别处理，Awake.ModuleServices 的测试用途不等于可直接删除；更新/诊断空桩保持产品边界。 |
| R6 | 修正 VFT_APP，删除 8 条确无消费者英文资源。图标文件名非运行问题；上游版权保留法律归属。原 Toast CLSID 的隔离迁移须配套注册卸载，未只改一个常量。 |
| R7 | ETW 工程/标识仍在不等于遥测打开，Runner RegisterProvider 是空桩；未开启采集。 |
| R8 | 删除 LightSwitch 六个 PowerDisplay 默认字段、三份未编死文件及两个 C++ 到托管工程的错引。 |
| R9 | 包管理/编译开关属于独立工程迁移，未为消除“残留”全局改 ABI 或 Release 构建策略。 |

## stable 跟随结论

- 同步 LightSwitch 可配置热键、CLI 的 AOT 互操作与两组上游单测；Awake 保留 stable 行为并修复托盘线程归属。未带入旧 main 的锁屏显示策略。
- 依赖按 stable：.NET 包 10.0.11、Windows App SDK 2.2.0 / Foundation 2.1.0 / Runtime 2.2.0、WebView2 1.0.4022.49；已相同的 CsWinRT 2.2.0、SDK BuildTools 26100.6901、MessagePack 3.1.7、CommandLine 2.0.1 不升级到 main。保留 Kit 统一的 WIL 1.0.260126.7。
- WindowHelpers 的 SendInput 与 stable 相同；ForegroundInputInjector 500ms 是旧 main 差异，本次不作为缺失修复。
- Kit 命名空间、七模块目录、ABI、check-only 更新和遥测关闭保留。差异哈希清单用于可追溯性，不能证明所有 PowerToys 功能等价。

## 最终验证

最新 build10 全方案 x64 Debug 通过；1,937项单测通过、0失败、1项环境跳过。IPC、ABI、生命周期、共享AIHub Native AOT与真实WinUI验证见 [review-validation.md](review-validation.md)。中间失败日志保留，不计为通过。
