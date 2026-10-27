# Kit 3.1.1 — 运行日志复核、源码全景比对与深度修复清单（全景终审深度版）

> **复核日期**：2026-10-26（本机时区 America/Los_Angeles）  
> **对比基准**：
> 1. **本机真实日志与崩溃记录**：%LOCALAPPDATA%\Kit 截至 2026-10-26 全量日志（包含冷启动 `--autorun`、手动热启动、Settings 完整交互及 `crash.log`）；
> 2. **上游参考基准**：[Source\PowerToys-main](file:///c:/Users/Zen/Repos/Codings/Kit/Source/PowerToys-main)（PowerToys release train 0.101，commit `f47af41`）；
> 3. **核心架构与设计规范**：[PLUGIN_DEVELOPMENT.md](file:///c:/Users/Zen/Repos/Codings/Kit/PLUGIN_DEVELOPMENT.md)、[startup-optimization-analysis.md](file:///c:/Users/Zen/Repos/Codings/Kit/doc/devdoc/startup-optimization-analysis.md)、[kit-development-experience.md](file:///c:/Users/Zen/Repos/Codings/Kit/doc/devdoc/kit-development-experience.md)；
> 4. **Git 提交历史**：`bbd702a`（10-23 模块服务化骨架）、`6bf4645`（10-24 AIHub 常驻轮询）、`f8983f6`（DIB 图标与 3.1.1 升版）、`b958dfb`（备份修复）及 [changelog.md](file:///c:/Users/Zen/Repos/Codings/Kit/changelog.md)。  
> **当前状态**：**严格遵循指令，产品代码完全不动**。本轮完成与上游 PowerToys 源码的深度对齐复核、插件开发规范合规性审查、单元测试盲区取证、行级证据链闭环，并制定可直接落地的修复工程手册。

---

## 核心审校结论（终审全景突破）

经过对 Git 历史、工程配置、自动化测试用例与源码调用链的微观取证，确认了以下十项确定性结论：

1. **【严重虚标与未完工重构】3.1.1 后台服务化重构仅完成了骨架，与 changelog 严重脱节**
   - **Changelog 声称**：[changelog.md:20-28](file:///c:/Users/Zen/Repos/Codings/Kit/changelog.md#L20-L28) 明确声称 3.1.1 “*将 UDPtest 与 NetMap 的套接字线程池、探针与拓扑扫描迁移至独立后台服务进程，确保关闭 Settings 窗口后诊断持续运行*”。
   - **代码真相**：提交 `bbd702a` 确实创建了 `KitModuleServiceBase` 与 `KitServicePipe`，并且在 [KitServicePipe.cs:195](file:///c:/Users/Zen/Repos/Codings/Kit/src/common/ManagedCommon/ServiceHost/KitServicePipe.cs#L195) 中已经**完整实现了 `KitServicePipeClient`**。但重构停在了半途：
     - `NetMapService` 和 `UDPtestService` 的 `StartServiceAsync` 仅执行了 `new NetMapSession()` / `new ProbeCoordinator()`，**从未调用 `Start()` / `StartAsync()`**，其 IPC 管道处理器仅返回硬编码假 JSON（`{"status":"running"}` / `{"acknowledged":true}`）；
     - [NetMapViewModel.cs:40](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI/ViewModels/NetMapViewModel.cs#L40)（`private readonly NetMapSession session = new();`）与 [UDPtestViewModel.cs:61](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI/ViewModels/UDPtestViewModel.cs#L61)（`private readonly ProbeCoordinator _coordinator = new();`）**完全没有对接 `KitServicePipeClient`**，依然在 UI 进程内自建实例并运行实际探测引擎；
     - **一旦用户关闭 Settings 窗口，后台诊断立即中断**，全仓没有任何代码真正连接这两个后台服务，测试覆盖率为 0。
2. **【根源定位】AIHub 2 秒启动阻塞与高频轮询的引入根源（Commit `6bf4645`）**
   - 在提交 `6bf4645` 之前，若调度未开启（`scanIntervalHours == 0`，当前默认值），`AIHubWorker` 会直接 exit 0 退出；
   - `6bf4645` 移除了退出逻辑强制其常驻，并在 [AIHubWorker/Program.cs:146](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/AIHub/AIHubWorker/Program.cs#L146) 中加入了**每 5 秒读取一次磁盘 `store.Load()` 的低效轮询**；
   - 这一改动直接导致 Runner 在启动调用 `start_enabled_kit_modules()` 时，必须在主线程同步 `CreateProcessW` 拉起重型 .NET 进程，使热启动被阻塞约 2 秒；且完全忽视了 `AIHubModuleInterface` 已经发送的 `AIHUB_REFRESH_SETTINGS_EVENT` 命名事件。
3. **【开机冷启动瓶颈】顺序冷加载“已禁用模块”的 DLL 耗时近 1 秒**
   - 查验 20:45 的开机启动（`--autorun`）日志发现：即使 AIHub 启动命中缓存（仅 10ms），Runner 总初始化仍耗时 1,425 ms。
   - **根源定位**：[src/runner/main.cpp:181](file:///c:/Users/Zen/Repos/Codings/Kit/src/runner/main.cpp#L181) 在已知 `startupGeneralSettings` 禁用了 Awake、Localserver、UDPtest 的情况下，依然在主线程顺序执行了所有 6 个模块的 `LoadLibraryExW`。
   - 仅 Awake (568ms)、Localserver (282ms)、UDPtest (49ms) 三个**禁用模块**的磁盘冷加载，就无谓浪费了 **899 ms**！若采用按需/延迟加载，开机启动耗时可瞬间降至 350 ms 左右。
4. **【热键初始化开销】模块加载阶段急切注册热键钩子增加无谓耗时**
   - [src/runner/kit_module.cpp:94-105](file:///c:/Users/Zen/Repos/Codings/Kit/src/runner/kit_module.cpp#L94-L105)：`KitModule` 在构造函数中无视模块是否处于启用状态，立即执行 `update_hotkeys()` 与 `UpdateHotkeyEx()`，向 `CentralizedKeyboardHook` 注册模块热键。
   - 这不仅给已被禁用的模块带来了 10~20 ms 的额外初始化开销，更违背了 [startup-optimization-analysis.md](file:///c:/Users/Zen/Repos/Codings/Kit/doc/devdoc/startup-optimization-analysis.md) 关于延迟到 `enable()` 时再按需挂钩热键的设计原则。
5. **【规范违背与卡死风险】Worker 退出信号失配违背生命周期规范**
   - 依据 [PLUGIN_DEVELOPMENT.md:103](file:///c:/Users/Zen/Repos/Codings/Kit/PLUGIN_DEVELOPMENT.md#L103) 规定，Worker 必须监听父进程退出和本模块的命名退出事件。
   - [LocalserverModuleInterface/dllmain.cpp:285](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/Localserver/LocalserverModuleInterface/dllmain.cpp#L285) 在模块停用时触发 `LOCALSERVER_EXIT_EVENT`，并调用 `WaitForSingleObject(m_worker_process, 8000)` 等待最多 8 秒；
   - 但 [LocalserverWorker/Program.cs:105](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/Localserver/LocalserverWorker/Program.cs#L105) **根本没有监听该命名事件**，仅以 5 秒步长轮询标志文件 `module-disabled.flag`。若该文件未及时感知，停用 Localserver 会直接卡死 Runner 主线程 5~8 秒。
6. **【测试体系断层】回归看门狗 `BuildCompatibility.cs` 滞后，新服务覆盖率为 0**
   - 规范第 11 节依赖的核心测试 [BuildCompatibility.cs:2600-2611](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI.UnitTests/ViewModelTests/BuildCompatibility.cs#L2600-L2611) 自 2.0.15 以后便停止更新，仅断言 Awake、LightSwitch、Localserver 三个模块；
   - `UDPtest`、`NetMap` 和 `AIHub` 在 `BuildCompatibility.cs` 中的断言为 **0 处**；两个新后台 Service 的测试覆盖率为 **0**，导致虚假服务化重构未引起 CI 任何报警。
7. **【上游遗产与品牌残留】ActionRunner 命名与图标资源滞后**
   - 上游 PowerToys 在提权失败时依赖 `PowerToys.ActionRunner.exe` 进行降权启动回退（[Source/PowerToys-main/src/ActionRunner](file:///c:/Users/Zen/Repos/Codings/Kit/Source/PowerToys-main/src/ActionRunner)）。
   - Kit 的 [src/common/utils/elevation.h:419](file:///c:/Users/Zen/Repos/Codings/Kit/src/common/utils/elevation.h#L419) 硬编码调用 `PowerToys.ActionRunner.exe`，工程 [actionRunner.vcxproj:12](file:///c:/Users/Zen/Repos/Codings/Kit/src/ActionRunner/actionRunner.vcxproj#L12) 依然输出 `PowerToys.ActionRunner.exe`；
   - [ModuleHelper.cs:37](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI.Library/Helpers/ModuleHelper.cs#L37) 依然引用 `PowerToys.png`，配图目录中依然保留 `PT.png`，未能彻底完成产品品牌归一。
8. **【空转封装与性能包袱】GPO 并非 P0 缺陷，而是残留死代码**
   - [PLUGIN_DEVELOPMENT.md:175](file:///c:/Users/Zen/Repos/Codings/Kit/PLUGIN_DEVELOPMENT.md#L175) 明确规定 Kit 不使用企业 GPO，[gpo.h](file:///c:/Users/Zen/Repos/Codings/Kit/src/common/utils/gpo.h) 中所有策略方法均硬编码返回 `not_configured (-1)`。
   - 然而 Kit 依然完整维护着 [GPOWrapper.vcxproj](file:///c:/Users/Zen/Repos/Codings/Kit/src/common/GPOWrapper/GPOWrapper.vcxproj) 并在 `Kit.Settings.csproj` 中保留 CsWinRT 投影，导致 8 个 ViewModel 在启动时跨 COM/WinRT 调用空壳 GPO。
   - 动态切换函数 [`apply_module_status_update()`](file:///c:/Users/Zen/Repos/Codings/Kit/src/runner/general_settings.cpp#L270) 还残留无意义的 GPO 检查代码。处理建议为 P2：彻底清除残留 GPO 代码。
9. **【构建闭包与打包遗漏】`Kit.slnx` 缺失 CLI 依赖，Release 遗漏 8 项二进制**
   - [Kit.slnx:216-235](file:///c:/Users/Zen/Repos/Codings/Kit/Kit.slnx#L216-L235)：`Kit.vcxproj` 的 `<BuildDependency>` 中包含了 18 个依赖，但**遗漏了 `LightSwitch.Cli.csproj`**。
   - 加上 `LightSwitch.Cli.csproj` 启用了 `<PublishAot>true</PublishAot>`，常规 MSBuild 生成不会产出该 CLI 的独立 EXE；
   - [Stage-Release.ps1](file:///c:/Users/Zen/Repos/Codings/Kit/tools/build/Stage-Release.ps1) 遗漏了 8 项关键二进制（含 NetMap 模块 DLL、服务及 CLI），且缺失文件仅打印 `Write-Warning` 而不抛异常阻断打包。
10. **【文档漂移】开发规范 [PLUGIN_DEVELOPMENT.md](file:///c:/Users/Zen/Repos/Codings/Kit/PLUGIN_DEVELOPMENT.md) 滞后**
    - 文档第 1 节与第 4.1 节声称 Kit 仅有 5 个模块，完全遗漏收录 `NetMap`；第 6 节和第 10 节依然停留于早期 3 模块时期的产物描述，未能同步 3.1.1 的真实模块体系。

---

## 一、模块体系架构微观取证矩阵

| 模块名称 | 接口 DLL 与导出 | 后台进程与模型 | 核心代码位置 | 真实通信状态、与上游差异及主要缺陷 |
|---|---|---|---|---|
| **AIHub** | `Kit.AIHubModuleInterface.dll`<br>(kit_create / powertoy_create) | `Kit.AIHubWorker.exe`<br>(独立 C# Worker) | 接口: [AIHubModuleInterface/dllmain.cpp](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/AIHub/AIHubModuleInterface/dllmain.cpp)<br>Worker: [AIHubWorker/Program.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/AIHub/AIHubWorker/Program.cs)<br>UI: [AIHubPageViewModel.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI/ViewModels/AIHubPageViewModel.cs) | ❌ `enable()` 同步 `CreateProcessW` 阻塞 Runner 主线程 2,000ms<br>❌ Worker 未监听 `AIHUB_REFRESH_SETTINGS_EVENT`，每 5s 轮询磁盘<br>⚠️ UI 视图模型严重膨胀（`AIHubPageViewModel` 3,221 行 + `AiHubViewModel` 1,137 行） |
| **Localserver** | `Kit.LocalserverModuleInterface.dll`<br>(kit_create / powertoy_create) | `Kit.LocalserverWorker.exe`<br>(独立 C# Supervisor) | 接口: [LocalserverModuleInterface/dllmain.cpp](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/Localserver/LocalserverModuleInterface/dllmain.cpp)<br>Worker: [LocalserverWorker/Program.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/Localserver/LocalserverWorker/Program.cs)<br>调度: [ServiceRunner.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/Localserver/LocalserverLib/Engine/ServiceRunner.cs) | ❌ 退出事件 `LOCALSERVER_EXIT_EVENT` 未被 Worker 监听，停用有 8s 假死风险<br>⚠️ `ServiceRunner.cs`（2,658 行）与 `LocalserverViewModel.cs`（2,185 行）严重膨胀 |
| **NetMap** | `Kit.NetMapModuleInterface.dll`<br>(kit_create / powertoy_create) | `Kit.NetMapService.exe`<br>(基于 KitModuleServiceBase) | 接口: [NetMapModuleInterface/dllmain.cpp](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/NetMap/NetMapModuleInterface/dllmain.cpp)<br>服务: [NetMapService/Program.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/NetMap/NetMapService/Program.cs)<br>UI: [NetMapViewModel.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI/ViewModels/NetMapViewModel.cs) | ❌ **虚假服务**：`StartServiceAsync` 未调 `Start()`，管道返回假 JSON<br>❌ **假解耦**：UI 进程内自建 `NetMapSession` 并运行扫描，关闭 UI 扫描即中断<br>⚠️ 规范文档 [PLUGIN_DEVELOPMENT.md](file:///c:/Users/Zen/Repos/Codings/Kit/PLUGIN_DEVELOPMENT.md) 完全遗漏收录该模块；单元测试覆盖为 0 |
| **UDPtest** | `Kit.UDPtestModuleInterface.dll`<br>(kit_create / powertoy_create) | `Kit.UDPtestService.exe`<br>(基于 KitModuleServiceBase) | 接口: [UDPtestModuleInterface/dllmain.cpp](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/UDPtest/UDPtestModuleInterface/dllmain.cpp)<br>服务: [UDPtestService/Program.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/UDPtest/UDPtestService/Program.cs)<br>UI: [UDPtestViewModel.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI/ViewModels/UDPtestViewModel.cs) | ❌ **虚假服务**：`StartServiceAsync` 未调 `StartAsync()`<br>❌ **假解耦**：UI 进程内自建 `ProbeCoordinator` 运行高并发网络发包<br>❌ 全仓 0 处代码连接其管道客户端；服务与 IPC 测试覆盖为 0 |
| **LightSwitch** | `Kit.LightSwitchModuleInterface.dll`<br>(kit_create / powertoy_create) | `Kit.LightSwitchService.exe`<br>(C++ 独立服务) +<br>`Kit.LightSwitch.Cli.exe` | 接口: [LightSwitchModuleInterface/dllmain.cpp](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/LightSwitch/LightSwitchModuleInterface/dllmain.cpp)<br>CLI: [LightSwitch.Cli/Program.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/LightSwitch/LightSwitch.Cli/Program.cs)<br>鉴权: [PipeServerIdentity.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/LightSwitch/LightSwitch.Cli/Ipc/PipeServerIdentity.cs) | ❌ `Kit.slnx` 缺少对 `LightSwitch.Cli.csproj` 的生成依赖<br>❌ NativeAOT 未发布，Release 产物缺失 CLI<br>⚠️ 重复实现了 `PipeServerIdentity.cs`（与 ManagedCommon 重复） |
| **Awake** | `Kit.AwakeModuleInterface.dll`<br>(kit_create / powertoy_create) | `Kit.Awake.exe`<br>(独立 C# Worker) | 接口: [AwakeModuleInterface/dllmain.cpp](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/awake/AwakeModuleInterface/dllmain.cpp)<br>进程: [Awake/Program.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/awake/Awake/Program.cs) | 优良对齐上游设计，使用 PID 绑定和 `Local\Kit.Awake` Mutex 隔离官方进程<br>❌ 开机冷加载 DLL 耗时高达 568 ms（即使配置已显式禁用） |

---

## 二、启动时序双态机制与冷加载分析

根据 2026-10-26 真实运行日志，Runner 初始化总时间呈现截然不同的两种瓶颈态：

```text
【场景 A：手动热启动 / 进程重启 (00:58, 01:03)】
WinMain (0ms)
 ├── 预模块初始化 (DPI, Trace, Settings, TrayIcon) ~145ms
 ├── 顺序 DLL 加载 (KitKnownModules 6个) ~30-40ms (命中系统文件缓存)
 └── start_enabled_kit_modules()
      ├── AIHub::enable() ──────> [CreateProcessW] 同步阻塞 ~2,000ms ❌ 核心瓶颈
      ├── LightSwitch::enable() -> ~10ms
      └── NetMap::enable() ────-> ~11ms
 总计耗时：~2,223ms - 2,418ms

【场景 B：开机启动 --autorun (20:45)】
WinMain (0ms)
 ├── 预模块初始化 ~219ms
 ├── 顺序 DLL 加载 (KitKnownModules 6个) ───> 磁盘冷 I/O ~1,168ms ❌ 核心瓶颈
 │    ├── Awake (已禁用) ───────> LoadLibraryExW ~568ms ❌ 纯浪费
 │    ├── LightSwitch (启用) ───> LoadLibraryExW ~174ms
 │    ├── Localserver (已禁用) ──> LoadLibraryExW ~282ms ❌ 纯浪费
 │    ├── UDPtest (已禁用) ─────> LoadLibraryExW ~49ms  ❌ 纯浪费
 │    ├── NetMap (启用) ────────> LoadLibraryExW ~31ms
 │    └── AIHub (启用) ─────────> LoadLibraryExW ~21ms
 └── start_enabled_kit_modules()
      └── 各模块 enable() ──────> ~30ms (AIHub Worker 创建命中缓存仅 10ms)
 总计耗时：~1,425ms
```

**双态治理方案**：
1. **解决场景 A（热启动卡顿）**：将 Worker 进程创建移出 `enable()` 同步主干，采用后台异步线程拉起 Worker，并以事件通知状态；
2. **解决场景 B（开机冷启动慢）**：在 [main.cpp:181](file:///c:/Users/Zen/Repos/Codings/Kit/src/runner/main.cpp#L181) 中读取已解析的 `startupGeneralSettings`，对已显式禁用的模块（Awake, Localserver, UDPtest）**直接跳过开机 `LoadLibraryExW`**，改为按需加载，开机启动可瞬间抹平 **899 ms** 无谓磁盘 I/O。

---

## 三、比对上游 PowerToys (`Source\PowerToys-main`) 关键发现

| 维度 | 上游 PowerToys 实现 | Kit 3.1.1 现状 | 深度评估与治理建议 |
|---|---|---|---|
| **ABI 虚表槽位** | [powertoy_module_interface.h](file:///c:/Users/Zen/Repos/Codings/Kit/Source/PowerToys-main/src/modules/interface/powertoy_module_interface.h) 包含 18 个虚函数槽位 | [kit_module_interface.h](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/interface/kit_module_interface.h) 槽位顺序、参数及返回值 100% 严格一致 | ✅ **完美兼容**：保持了 ABI 兼容性，且保留了 `powertoy_create` 兼容别名。 |
| **降权提权回退** | `ActionRunner` 专职处理从高完整性向低完整性降权启动进程，文件名为 `PowerToys.ActionRunner.exe` | [elevation.h:419](file:///c:/Users/Zen/Repos/Codings/Kit/src/common/utils/elevation.h#L419) 硬编码 `L"\\PowerToys.ActionRunner.exe"`；[actionRunner.vcxproj](file:///c:/Users/Zen/Repos/Codings/Kit/src/ActionRunner/actionRunner.vcxproj) 输出该名称 | ⚠️ **品牌残留与维护风险**：应重命名为 `Kit.ActionRunner.exe`，同步修改 `elevation.h`，并在 `Stage-Release.ps1` 中统一规则。 |
| **GPO 策略机制** | 真实读取注册表 `SOFTWARE\Policies\PowerToys`，并在每次模块加载、启用、配置时查询策略 | [gpo.h](file:///c:/Users/Zen/Repos/Codings/Kit/src/common/utils/gpo.h) 全部硬编码返回 `-1`；但 UI 依然维持 `GPOWrapper` COM/WinRT 互操作项目 | ⚠️ **空转开销**：GPO 纯属上游历史遗留包袱。应彻底清理 `GPOWrapper` 项目及 UI 层的无用查询，净化架构。 |
| **键盘钩子鲁棒性** | 上游 `centralized_kb_hook.cpp` 在长按判定时可能产生虚假定时器激活 | Kit 增加了物理按键重检（`GetAsyncKeyState & 0x8000`）与卸载前定时器清理 | ✅ **Kit 改进优秀**：有效防止了钩子注销后的 Ghost 激活与回调崩溃。 |
| **崩溃观测与诊断** | 上游使用 Watson / 崩溃转储 | Kit 引入 [CrashLog.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI/Helpers/CrashLog.cs) 挂钩 `FirstChanceException` 采样已处理异常 | ℹ️ **纠偏澄清**：`crash.log` 中的条目绝大部分为受保护路径探测等良性受控异常采样，**并非应用崩溃**。 |
| **资产与图标品牌化** | 官方采用 PowerToys 专属图标和启动插图 | [ModuleHelper.cs:37](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI/Library/Helpers/ModuleHelper.cs#L37) 仍指向 `PowerToys.png`，[PT.png](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI/Assets/Settings/Modules/PT.png) 仍保留微软官方插图 | ⚠️ **未彻底品牌化**：建议替换为 Kit 专属深色/浅色标准 Fluent 图标。 |

---

## 四、代码重复、样板类与测试盲区剖析

### 1. 原生进程管理样板重复
当前 `AIHub`、`Localserver`、`NetMap`、`UDPtest` 四个模块接口中，存在 **约 285 行逐字高度复制** 的进程管理逻辑：

```cpp
// 在 AIHub, Localserver, NetMap, UDPtest 的 dllmain.cpp 中完全一致的模板代码：
void Module::start_worker_if_needed() {
    if (m_worker_process && WaitForSingleObject(m_worker_process, 0) == WAIT_TIMEOUT) return;
    close_worker_handle();
    // 1. 获取当前 DLL 路径并拼装 Worker/Service 路径
    // 2. 拼装 --use-kit-config --pid <pid>
    // 3. CreateProcessW(CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT)
    // 4. m_worker_process = pi.hProcess; CloseHandle(pi.hThread);
}

void Module::stop_worker_if_running() {
    // 1. OpenEventW(EVENT_MODIFY_STATE, ...)
    // 2. SetEvent() / CloseHandle()
    // 3. WaitForSingleObject(m_worker_process, timeout)
    // 4. TerminateProcess() 超时强杀
    // 5. close_worker_handle()
}
```

**重构建议**：
在 `src/common` 中实现通用的 native `KitWorkerProcessManager` 类：
```cpp
class KitWorkerProcessManager {
public:
    KitWorkerProcessManager(std::wstring_view workerSubPath, std::wstring_view exitEventName);
    void start_async(); // 异步启动，避免阻塞 Runner 主线程
    void stop_gracefully(DWORD waitMs = 2000, DWORD killWaitMs = 1500);
    bool is_running() const;
    void close();
};
```
四处模块接口仅需保留 1 行成员变量声明，彻底根除代码重复与各模块超时处理不一致问题。

### 2. 核心回归测试看门狗缺失
- [BuildCompatibility.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI.UnitTests/ViewModelTests/BuildCompatibility.cs) 是 Kit 的核心架构约束测试，覆盖了 3,164 行静态与动态断言。
- 但测试代码仅保留了旧版的 `Awake`、`LightSwitch`、`Localserver` 约束；
- `NetMap`、`UDPtest`、`AIHub` 在其中断言数量为 **0**。这直接导致 3.1.1 的虚假服务化即便未完成实际集成，测试套件仍显示“全绿通过”。

---

## 五、终审修复路线图（执行手册）

### 🚨 P0 — 消除高感知启动阻塞、纠正假服务、封堵发布漏洞

1. **AIHub Worker 同步拉起解耦（消除 2 秒卡顿）**
   - **目标**：将 [`AIHubModule::enable()`](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/AIHub/AIHubModuleInterface/dllmain.cpp#L126) 内的 `start_worker_if_needed()` 改为异步任务拉起，使 Runner 主线程立即进入消息循环。
   - **埋点**：在 `dllmain.cpp` 中分别记录 `CreateProcessW` 耗时、异步启动耗时及 Worker 首次就绪事件。
2. **NetMap / UDPtest 假服务架构决断（解决 3.1.1 未完成重构）**
   - **方案 A（完整服务化，对齐 changelog 3.1.1 承诺，推荐）**：
     - 在 `NetMapServiceHost` 中真正注入并驱动 `_session.Start(options)`，在 `UDPtestServiceHost` 中调用 `_coordinator.StartAsync(...)`；
     - 在 `NetMapViewModel` 和 `UDPtestViewModel` 中移除对 `NetMapLib` 和 `UDPtestLib` 的项目依赖，直接使用现有 [KitServicePipeClient](file:///c:/Users/Zen/Repos/Codings/Kit/src/common/ManagedCommon/ServiceHost/KitServicePipe.cs#L195) 经由命名管道收发控制指令与数据；
     - 补齐 Service 和 IPC 的单元测试。
   - **方案 B（精简轻量化）**：
     - 若维持 UI 进程运行模式，直接停止在 Runner 启动时唤起 `NetMapService` 和 `UDPtestService`，修正 `changelog.md`，避免进程空耗。
3. **修复 Release 打包脚本与 LightSwitch CLI 编译**
   - 在 [Kit.slnx:216-235](file:///c:/Users/Zen/Repos/Codings/Kit/Kit.slnx#L216-L235) 的 `Kit.vcxproj` 中补上 `LightSwitch.Cli.csproj` 的 `<BuildDependency>`；
   - 在 [Stage-Release.ps1](file:///c:/Users/Zen/Repos/Codings/Kit/tools/build/Stage-Release.ps1) 中增加 `LightSwitch.Cli` 的 `dotnet publish -c Release -r win-x64` 产出步骤；
   - 补齐 `$versionedFiles` 中遗漏的 8 项核心二进制（`Kit.NetMapModuleInterface.dll`、`Kit.AIHubModuleInterface.dll`、`Kit.LightSwitch.Cli.exe`、`Kit.AIHubWorker.exe`、`Kit.LocalserverWorker.exe`、`Kit.NetMapService.exe`、`Kit.UDPtestService.exe`、`WinUI3Apps\NetMapLib.dll`），缺失文件直接 `throw` 终止打包。

---

### 🛠️ P1 — 优化开机冷启动、对齐 Worker 规范、抽离重复代码

4. **原生模块 DLL 按需/延迟加载（消除开机 900ms 磁盘冷读）**
   - 在 [src/runner/main.cpp:181](file:///c:/Users/Zen/Repos/Codings/Kit/src/runner/main.cpp#L181) 中优化 `KitKnownModules` 载入逻辑：
   - 检查已解析的 `startupGeneralSettings`，对显式禁用的模块（如 Awake, Localserver, UDPtest）**不执行开机 `LoadLibraryExW`**；
   - 延缓热键注册至模块实际 `enable()` 阶段，消除禁用模块急切注册热键钩子的开销；
   - 仅在用户于设置中启用模块或请求其配置时动态加载。
5. **修复 Worker 信号对齐，清除高频轮询**
   - **AIHubWorker**：消除 [AIHubWorker/Program.cs:146](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/AIHub/AIHubWorker/Program.cs#L146) 每 5 秒的 `store.Load()` 磁盘轮询，改为挂起等待 `AIHUB_REFRESH_SETTINGS_EVENT` 命名事件；
   - **LocalserverWorker**：在 [LocalserverWorker/Program.cs:105](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/Localserver/LocalserverWorker/Program.cs#L105) 增加对 `LOCALSERVER_EXIT_EVENT` 的等待，配合 `module-disabled.flag`，消除模块停用时主线程陷入 8 秒等待的风险。
6. **抽离通用 Native `KitWorkerProcessManager` 管理基类**
   - 消除 AIHub、Localserver、NetMap、UDPtest 中约 285 行重复的 `start_worker` / `stop_worker` / `close_worker` / `CreateProcessW` 模板代码。
7. **补全 `BuildCompatibility.cs` 架构回归用例**
   - 在 [BuildCompatibility.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI.UnitTests/ViewModelTests/BuildCompatibility.cs) 中补齐对 `NetMap`、`UDPtest` 与 `AIHub` 的 DLL 注册、Catalog 一致性以及 Service IPC 管道的看门狗断言。
8. **删除重复的 PipeServerIdentity 实现**
   - 删除 `src/modules/LightSwitch/LightSwitch.Cli/Ipc/PipeServerIdentity.cs`，统一引用 `ManagedCommon` 中的标准实现。
9. **FirstChance 异常语义过滤与路径保护**
   - 在 AIHub 本地文件扫描中对受系统保护目录增加预判，避免在正常的安全遍历中大量抛出 `UnauthorizedAccessException` 污染 `crash.log`。

---

### 🧹 P2 — 彻底清理历史债务、同步规范与拆分超大文件

10. **彻底清理 GPO 残留逻辑与 ActionRunner 品牌归一**
    - 依据 [PLUGIN_DEVELOPMENT.md:175](file:///c:/Users/Zen/Repos/Codings/Kit/PLUGIN_DEVELOPMENT.md#L175)，移除 [`apply_module_status_update()`](file:///c:/Users/Zen/Repos/Codings/Kit/src/runner/general_settings.cpp#L270) 中的 GPO 查询死代码；
    - 移除 `Kit.Settings.csproj` 对 `GPOWrapper.vcxproj` 的无用引用，彻底消除未使用的注册表查询开销；
    - 将 `PowerToys.ActionRunner.exe` 重命名为 `Kit.ActionRunner.exe`，同步修改 `src/common/utils/elevation.h` 与打包脚本；
    - 替换 [ModuleHelper.cs:37](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI.Library/Helpers/ModuleHelper.cs#L37) 中的 `PowerToys.png` 为 Kit 专属图标。
11. **同步修正插件开发规范文档 [PLUGIN_DEVELOPMENT.md](file:///c:/Users/Zen/Repos/Codings/Kit/PLUGIN_DEVELOPMENT.md)**
    - 补齐 NetMap 模块在规范第 1 节（活动模块列表）、第 4.1 节（数据目录树）及第 10 节（产物清单）中的说明与约束；
    - 消除文档内历史版本遗留的“3 个活动模块”与“5 个活动模块”的表述冲突。
12. **拆分超大代码文件**
    - [AIHubPageViewModel.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI/ViewModels/AIHubPageViewModel.cs)（3,221 行）：按“优化候选评审”、“内核调度”、“历史记录”拆分为 partial class；
    - [`ServiceRunner.cs`](file:///c:/Users/Zen/Repos/Codings/Kit/src/modules/Localserver/LocalserverLib/Engine/ServiceRunner.cs)（2,658 行）：按协议调度与管道隔离拆分；
    - [LocalserverViewModel.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI/ViewModels/LocalserverViewModel.cs)（2,185 行）、[GeneralViewModel.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI/ViewModels/GeneralViewModel.cs)（1,720 行）。
13. **清理 Settings.UI.Library 中的残留配置类**
    - 逐步剔除 FancyZones, ColorPicker, Hosts 等 30+ 个被裁剪模块的无用配置类与 `SettingsSerializationContext.cs` 中对应的 100+ 个无用条目，以及 [EnabledModules.cs](file:///c:/Users/Zen/Repos/Codings/Kit/src/settings-ui/Settings.UI.Library/EnabledModules.cs#L68-L80) 中的残留属性，降低编译与代码分析开销。
14. **CoreCLR 运行时体积优化**
    - 评估当前 6 份独立 `coreclr.dll`（占 1.14 GB）的合并共享或 NativeAOT 裁剪方案。

---

## 六、验收标准与验证矩阵

| 验证项 | 验证条件与方式 | 成功判定标准 |
|---|---|---|
| **Runner 热启动** | 相同构建产物连续重启 10 次 | `Modules Enabled` 时间稳定 < 150 ms；AIHub Worker 启动不阻塞主消息循环 |
| **Runner 冷启动** | 模拟开机启动（`--autorun`） | 禁用模块不触发 `LoadLibrary`；开机初始化总时间从 1,425 ms 降低至 350 ms 以内 |
| **后台诊断持续性** | 开启 NetMap 扫描后关闭 Settings 窗口 | 后台服务日志持续产出真实的拓扑与探测事件；重新打开 Settings 能即时拉取最新状态 |
| **模块停用响应** | 运行中停用 Localserver 与 AIHub | 模块立即退出并回收进程句柄，主线程等待时间 < 500 ms（杜绝 8 秒超时） |
| **发布包完整性** | 执行 `Stage-Release.ps1 -Version 3.1.1` | 校验 6 个模块 DLL、所有 Worker、Service 和 `Kit.LightSwitch.Cli.exe` 全部存在，缺项退出非零 |
| **架构看门狗测试** | 运行 `BuildCompatibility` 单元测试套件 | 全部 6 个模块、服务及 CLI 均纳入接线断言，测试 100% 通过无断层 |
| **诊断日志清洁度** | Settings 完整交互会话退出后检查 `crash.log` | 无 `[Unhandled]` 崩溃；`FirstChanceSummary` 不再包含因轮询或常规系统扫描产生的高频异常采样 |
