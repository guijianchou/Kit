# Awake / LightSwitch 复核与同步记录

日期：2026-11-16。目标是用户重新提供的 `Source/PowerToys-0.102.3101`（发布标签 v0.102.3101.0）；此前下载的 `PowerToys-main` 已排除。stable 的 `Version.props` 是 `Version=0.0.1 / ReleaseTrainVersion=0.102`，不能把该占位值当成发行版本。

## 复核结论

| 编号 | 源码核验结论 | 本次处理 |
|---|---|---|
| K4 | 两个原生接口的 `is_enabled()` 都只返回缓存值，子进程退出后继续报已启用，成立。 | 现在必须同时有活动进程句柄且零超时等待返回 `WAIT_TIMEOUT`。Awake 防止上次停止失败时另起重复进程；进程仍存活时保留所有权。Runner 的自动恢复由统一模块生命周期代理处理。 |
| K7 | 原生 disable 的等待确实发生在调用线程。报告概括为 5–10 秒不适用于这两个模块：本地常量均为 1500ms，强杀后最多再等一次。 | 保留必要的有界退出等待；Runner 的统一工作队列负责避免 UI 线程阻塞，不使用会悬空 DLL 对象的 detached 线程。 |
| K22 | Awake `set_config` 仅捕获 `std::exception`，遗漏 `winrt::hresult_error`，成立。LightSwitch 已有 `catch (...)`，不能套用此子项。 | Awake 增加 WinRT/std/未知异常边界和错误日志，启动路径也保护 filesystem 异常。 |
| K39 | LightSwitch `GetGeoLocation_Click` 已有异常保护；`SyncLocationButton_Click` 的权限请求及 `ShowAsync` 无外层处理，部分成立。 | 补齐后者保护。保留缓存页面的关闭释放、重载防回写与已有本地化逻辑。 |
| K43 | LightSwitch 创建子进程时 `bInheritHandles=TRUE` 成立；构造后从未启用时 `destroy()->disable()` 提前返回造成 semaphore 泄漏成立。原报告所谓 `g_settings` 必然与 toggle 线程竞争未得到调用链支持：原 toggle 路径不读它。 | 子进程禁继承；destroy 对剩余 semaphore/process 显式释放；设置改为实例成员并加锁，为统一代理的线程调用建立清晰边界；toggle 线程异常/WAIT_FAILED 不再逃逸或死循环。 |
| K47 | `_notifyIconData` 被 Rx/监控/设置线程与窗口线程共用，退出也从非创建线程调用 DestroyWindow/PostQuitMessage，成立。额外确认旧 SynchronizationContext 队列里执行阻塞式 `GetMessage` 后，后来投递的回调不会再被消费。 | 换成真实窗口消息投递和并发工作队列；图标、菜单、窗口清理都在创建线程完成；退出幂等；监控线程先取消并有界等待再释放 CTS；固定 WndProc delegate；GetMessage 使用 int 返回类型以区分错误 -1。移除失效的旧同步上下文类。 |
| K49（CLI部分） | CLI 曾把上游 LibraryImport 身份校验改为公共 DllImport 路径，且显式打开运行时封送；这是实现退化，不是“必然无法 NativeAOT”。生产调用原本已传全路径，不能宣称它直接用了公共文件名旁路。 | 恢复 stable 的 CLI 专用 LibraryImport + SafeHandle + stackalloc 全路径身份校验，恢复 `DisableRuntimeMarshalling=true`。公共 ManagedCommon 检查由框架任务修复。 |
| R8 | 默认 JSON 六个 PowerDisplay 键、未编译的三个旧头/源文件、两个原生项目错引 ManagedCommon.csproj 均存在。 | 删除六个无效默认键和三个孤立文件，去掉两处无必要的托管项目引用。 |
| R2 | Awake/LightSwitch 当前主产品工程已在方案内。缺少上游行为测试工程是测试覆盖缺口，不等同于活跃产品代码孤立。 | 移植 stable 的两套 LightSwitch 单元测试工程，主任务加入 Kit.slnx。 |

Awake 还同步 stable 删除启动后无条件 Reset 退出事件的改动。原生接口在等待子进程退出前保持事件句柄，避免“启动尚未创建事件时发停止信号，事件先消失”的竞争。

## stable 同步内容与保留差异

LightSwitch 新增 stable 的 `toggle-theme-hotkey` 配置，默认 Win+Ctrl+Shift+D，贯穿原生加载/保存、服务首次默认配置、Settings 模型及深复制、设置页输入、Runner 注册/注销和事件派发。Kit 已删除上游通用 ShortcutControl/冲突窗口/键盘钩子体系，因此用模块专用且 JSON 兼容的模型和聚焦时输入控件，不重新引入这些跨模块系统。按 Delete/Backspace 可清空快捷键。注册冲突会记录日志；尚未添加系统范围冲突诊断 UI。

后续真实兼容测试发现，旧 `Win+D` JSON 只含 `win/code` 时不能补入默认 `Ctrl/Shift`。现将空热键模型与“整个配置缺失时的默认值”分开：缺失的修饰键为 false，明确 code=0 保持禁用，整个热键属性缺失才使用 Win+Ctrl+Shift+D。原生读取同样容忍缺失修饰键，避免在读取时间计划前抛异常。相关模型测试验证五字段 JSON、旧配置保存/重载、默认值和禁用状态；其他六模块仍不得输出热键配置。

Runner 新文件是 `src/runner/module_hotkeys.h/.cpp`：仅注册已启用模块显式提供的快捷键，最多 32 个，忽略 key=0，使用 MOD_NOREPEAT；注册、注销、消息入口在 Runner 窗口线程执行，并检查 ID/修饰键/按键及当前模块。实际模块动作经主任务的生命周期代理串行执行。主任务负责 main/tray/general/settings 的接入。

保留 Kit 名称、目录、单例隔离、Awake 进程路径和会话身份核验、Unicode 完整 NOTIFYICONDATA、旧图标清理、Settings 页面缓存及释放修复、LightSwitch 服务停止事件。继续不启用遥测和 PowerDisplay 联动。

旧 main 才有的 Awake `allowDisplayOffWhileLocked` 不在 stable 内，未移植。stable 中 Awake 的状态计算、会话控制等功能逻辑与 Kit 已一致，无需为了“同步”回退 Kit 的身份/托盘防护。

## 验证

已完成：目标目录版本核对；两模块 118 个可比较的已跟踪文本文件对照；新增项目/XAML/资源 XML 解析；本任务范围 `git diff --check` 通过。保留的 38 个差异主要是 Kit 品牌/工程约束/已有修复、禁遥测/PowerDisplay和本轮修复，不能把它们等同于 38 个遗漏更新。

新增/移植的行为验证：

- `Awake.UnitTests/TrayThreadTests.cs`：创建真实托盘窗口，从线程池投递 24 个任务，断言在同一窗口线程执行，再从后台请求关闭并断言 HWND 已销毁。归类 Desktop，实际运行时会短暂创建测试托盘图标。
- `Settings.UI.UnitTests/ViewModelTests/LightSwitch.cs`：验证热键 IPC 的 stable JSON 契约、深复制不共享可变对象、外部设置重载不回写。删除相冲突的旧 `KitLightSwitchShouldNotRegisterOrHandleHotkeys` 源码字符串断言。
- `LightSwitch.Cli.UnitTests`：5 个文件，CLI 参数/本地化/协议、真实命名管道、服务进程全路径身份检查。
- `LightSwitchService.UnitTests`：11 个文件，状态机/时间调度/命令队列/管道/令牌身份/配置原子发布及隔离注册表键行为。PowerDisplay 默认字段断言改为必须不存在，默认字段数量为 10。

本子任务未并发运行构建或测试；以下结果已从主任务生成的日志及 TRX 核实。

| 验证 | 实际结果 | 证据 |
|---|---|---|
| 最终 Debug x64 全方案构建（build10，含父进程修复） | 成功，0 warning / 0 error | `TestResults/stable-review-build-10.log`、`build.debug.x64.all.log` |
| 最终 LightSwitchService | 147/147 通过 | `TestResults/Stable3101Final/LightSwitchService.log` |
| 最终 Settings | 311/311 通过、0 skipped，含新热键和旧配置契约 | `TestResults/Stable3101Final/Settings/settings-tests.stdout.log` 及同目录 TRX |
| Awake（此前统一轮次） | 9/9 通过，含真实托盘窗口线程测试 | `TestResults/Stable3101/Awake/stdout.log` 及同目录 TRX |
| Awake.ModuleServices（此前统一轮次） | 3/3 通过 | `TestResults/Stable3101/AwakeModuleServices/stdout.log` |
| LightSwitch.Cli（此前统一轮次） | 221/221 通过 | `TestResults/Stable3101/LightSwitchCli/stdout.log` |
| 最终原生 IPC / Quick Access | 12/12 通过；普通和延迟启动各 1000 条消息全部收到、无乱序或损坏 | `TestResults/Stable3101Final/NativeInterop.log`、`pipe.log` |
| 最终 LightSwitch 独立 smoke | 全部通过；真实服务预置停止退出、无效父 PID 返回 87；主题不变、原设置逐字节恢复并验证 | `TestResults/Stable3101Final/lightswitch-smoke-final.log` |

独立 LightSwitch smoke 在 `TestResults/Stable3101Final/lightswitch-smoke.log` 首次失败于旧夹具的“Off 时零 worker”断言；该夹具还要求零热键。stable 与当前产品在启用且计划为 Off 时仍保持一个 worker 处理手动切换。已更新 `tools/tests/NativeModules/LightSwitchSmoke.cpp` 来验证一个稳定 PID、默认热键、每次热键实际增加一次请求、切换计划不重启、禁用/销毁清理与崩溃恢复，并保留主题值不变及原配置按字节恢复的保护。

主任务重建后，`TestResults/Stable3101Final/lightswitch-smoke-retry.log` 确认上述 fake-worker 项全部通过，真实服务的预置停止事件退出也通过，但真实服务的无效父 PID 退出检查超时，该轮结果判为失败。该第二次失败确认是产品缺陷，stable 上游源码也有同样缺陷：`RunServiceWorker` 忽略非零父 PID 的 `OpenProcess` 失败，因而无父进程约束地继续运行；`_tmain` 又不读取工作线程退出码，无条件返回成功。现修为非零父 PID 无法绑定时在设置/监控/CLI 初始化前返回真实错误；已停止的父进程或预置停止事件直接成功退出。非 SCM 入口和 SCM 入口仅共用必要的线程创建、等待与退出码处理，`parentPid=0` 的原有模式保持合法。

上述两次失败作为诊断历史保留，不计为最终通过结果。包含服务修复的统一 build10 已成功；随后 `TestResults/Stable3101Final/lightswitch-smoke-final.log` 确认所有 fake-worker 生命周期和热键项通过、真实服务预置停止事件退出通过、无效父 PID 以错误码 87 退出通过。最终轮次同时确认主题注册表值未改变，原 LightSwitch 设置逐字节恢复并验证。该项已完成构建和真实 smoke 复测；这些 smoke 不覆盖真实主题切换或所有 UI 路径。

框架首轮管道 smoke 的 `1000→602` 丢消息与后续背压、接入顺序发布、保持连接直到读端断开等修复详见 `review-framework-followup.md`。最终两种 smoke 均为 1000/1000；16 上限约束连接和排序槽，整体输入/输出队列总容量仍未限制。
