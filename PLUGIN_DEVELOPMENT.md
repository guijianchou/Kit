# Kit 插件与模块开发规范

本规范面向 Kit 主框架和新增模块开发。优先顺序是安全、正确性、最小改动与轻量化；界面复用现有 WinUI 3 + Mica Alt 设置框架。

**更新日期：2026-09-14。** 初次 PowerToys review 基线为 Kit `3524ba3` / `2.0.10`；本轮在 Gemini 的 `2.0.19` 工作区继续复核框架、三个模块和 `fixed.md`，验收记录见第 12.6 节。本地 `source/PowerToys` 为 `f47af41de2837a9d588f84270ad521c26264ab40`，release train `0.101`；这里的“上游”指这个本地快照，不代表已核对 GitHub 后续提交。Localserver 另对照相邻的 `../Localserver` 原项目。第 12 节区分历史 review 与本次验证，其余章节规定当前模块接入方式和验收要求。

## 1. 支持范围与上游兼容边界

Kit 当前活动模块为 **Awake、LightSwitch、Localserver、UDPtest、AIHub**。本文中的“插件”默认指由 Runner 加载的原生模块接口 DLL，以及对应业务实现。Awake、LightSwitch 使用独立业务进程；Localserver 的管理逻辑目前运行在 Settings 页面的 ViewModel 中；UDPtest 采用内置高并发探测 Worker；AI Hub 则包含原生模块接口、专属独立设置界面及底层共享 `Kit.AiHub` 任务引擎。

| 接入方式 | 当前支持程度 |
| --- | --- |
| PowerToys / Kit 原生模块源码 | 支持 `KitModuleIface`、`kit_create`（兼容 `PowertoyModuleIface`、`powertoy_create` 别名）、配置 IPC、热键、设置页和 Worker 模型；移植时使用 Kit 的头文件与依赖重新编译，并完成显式注册 |
| 官方现成 ModuleInterface DLL | **不承诺所有官方二进制可直接运行，也不支持拖入目录自动安装**；当前虚表槽位已对齐本地上游，仍须验证依赖、架构、配置、事件名和宿主能力 |
| C# / C++ Worker | 支持，由原生 ModuleInterface 启动；Worker 自身不是 Runner 可直接加载的 DLL |
| PowerToys Run 的 `IPlugin` / `plugin.json` | 当前没有 Run / Launcher 宿主，不能直接运行 |
| Command Palette 扩展 | 当前没有 CmdPal 宿主，不能直接运行 |
| `Awake.ModuleServices` / `Kit.ModuleContracts` | 保留适配源码并由 Awake 测试引用；`IModuleService` 面向 CmdPal，当前没有对应运行宿主，不是 Kit 的托管插件发现接口 |

[Kit 接口头](src/modules/interface/kit_module_interface.h)（保留旧 [powertoy_module_interface.h](src/modules/interface/powertoy_module_interface.h) 重定向头供平滑过渡）已按[上游接口头](source/PowerToys/src/modules/interface/powertoy_module_interface.h) 恢复 `keep_track_of_pressed_win_key()` 和 `milliseconds_win_key_must_be_pressed()` 的原始位置，但不恢复 Win 键长按轮询。`tools/tests/NativeModules/ModuleAbiSmoke.cpp` 分别使用上游头编译 DLL、Kit 头编译宿主，验证全部虚函数槽位、热键结构体及销毁调用。该测试只证明这一契约在当前 x64 工具链下对齐，不代表上游所有插件的运行依赖都已提供。

[Runner](src/runner/main.cpp) 的 `KitKnownModules` 与 [KitModuleCatalog](src/settings-ui/Settings.UI.Library/Helpers/KitModuleCatalog.cs) 是活动模块入口。保留的历史枚举、DTO 或文档不是模块已启用的依据。上游 Runner 本身也使用显式模块列表；Kit 维护 5 个活动模块，未实现快捷动作的模块自动回退至设置页。

原生 DLL 与 Runner 共享进程和权限。Worker 隔离能限制业务进程崩溃的影响，**不构成不可信插件沙箱**；DLL 初始化、回调和销毁仍能影响主程序。

## 2. 最小项目结构与模块身份

示例新模块统一使用 `Sample`，它只是文档占位名，不是已实现模块。

```text
src/modules/Sample/
├── SampleModuleInterface/     # 必需：原生 DLL、配置与启停代理
├── SampleWorker/              # 有业务进程时增加；C++ 或 C#
├── SampleLib/                 # 确有可测试、可复用业务逻辑时增加
└── Tests/                     # 对生命周期和业务行为的必要验证

src/settings-ui/Settings.UI.Library/
├── SampleSettings.cs
├── SampleProperties.cs
└── SndSampleSettings.cs       # 使用模块设置 IPC 时增加

src/settings-ui/Settings.UI/
├── ViewModels/SampleViewModel.cs
├── SettingsXAML/Views/SamplePage.xaml
├── SettingsXAML/Views/SamplePage.xaml.cs
├── Assets/Settings/Icons/Sample.png
└── Assets/Settings/Modules/Sample.png
```

- `get_key()`、`SampleSettings.ModuleName`、JSON 模块键、数据目录、路由映射必须一致，且不随语言变化。
- 新模块 Key 使用固定的 ASCII 字母/数字名称，例如 `Sample`；禁止路径分隔符、`..`、绝对路径及保留目录名 `Settings`、`RunnerLogs` 等。现有路径 helper 不负责替调用者验证任意模块名。
- 展示名称来自资源文件，可以本地化；不要用展示名称拼接持久化路径。
- Kit 模块产物统一命名为 `Kit.<Module>ModuleInterface.dll`、`Kit.<Module>.exe`（如 `Kit.AwakeModuleInterface.dll`、`Kit.Awake.exe`、`Kit.Settings.exe` 等）；加载清单必须填写真实产物名。
- 不要求每个插件新增库、数据库、独立 UI、注册表、GPO 或服务接口。只引入当前功能需要的项目和依赖；通常复用现有设置进程即可。

### 模板使用边界

优先参考当前 [AwakeModuleInterface](src/modules/awake/AwakeModuleInterface/dllmain.cpp) 和 [LightSwitchModuleInterface](src/modules/LightSwitch/LightSwitchModuleInterface/dllmain.cpp) 的具体接入方式，并结合第 12 节的已知问题。

`tools/project_template/ModuleTemplate/` 是模板源码；`$projectname$` / `$safeprojectname$` / `$guid1$` 由 Visual Studio 实例化。项目必须位于 Kit 仓库内，以继承 `RepoRoot`、工具链和依赖配置；生成的接口 DLL 输出到 Runner 同级目录，仍须完成第 6 节的显式注册。

本轮已同步 [ModuleTemplate.zip](tools/project_template/ModuleTemplate.zip) 与目录源码，补齐 SettingsAPI、CppWinRT、`packages.config` 和可审核的 [MyTemplate.vstemplate](tools/project_template/ModuleTemplate/MyTemplate.vstemplate)，删除旧遥测与失效依赖。[模板 README](tools/project_template/README.md) 说明安装和实例化方法。根 `Kit.slnx` 已包含 `ModuleTemplateCompileTest`，输出到 `x64/Debug/tests/ModuleTemplate/`；编译骨架与通过 VS 新建项目是不同验证，结果见第 12 节。

## 3. 原生接口、热键与生命周期

以 [kit_module_interface.h](src/modules/interface/kit_module_interface.h) 为契约源（保留旧 [powertoy_module_interface.h](src/modules/interface/powertoy_module_interface.h) 转发头），不在新项目内复制、删改一份接口声明。标准导出形式为：

```cpp
#include <interface/kit_module_interface.h>

extern "C" __declspec(dllexport)
KitModuleIface* __cdecl kit_create();
```

头文件保留 `using PowertoyModuleIface = KitModuleIface;` 和旧工厂指针类型别名。Runner 优先查找 `kit_create`，再查找 `powertoy_create`；C++ 类型别名不会自动生成第二个导出函数。新模块实现 `kit_create()`，确需双导出时显式定义转发函数。

| 接口 | 新模块要求 |
| --- | --- |
| `get_name()` / `get_key()` | 返回有效、稳定生命周期的字符串；不扫描文件、联网或启动 Worker |
| `get_config(buffer, size)` | 遵循先查询容量、再写入的约定；容量不足返回 false，所需字符数包含结尾空字符。优先使用 SettingsAPI 的现有序列化实现 |
| `set_config(json)` | 校验当前模块配置，更新内存和持久化状态；必要时更新热键或通知 Worker。坏配置不能越过 DLL 边界导致 Runner 崩溃 |
| `enable()` | 可重复调用；按功能需要启动业务资源。已有活跃 Worker 时不能再启动一个；失败后状态应可恢复 |
| `disable()` | 可重复调用；停止业务、撤销监听、取消计时与任务，回收自己持有的资源 |
| `is_enabled()` | 语义是模块启用状态；与“Worker 当前存活”分开判断。支持按需运行的模块可以启用但没有 Worker |
| `destroy()` | **自身完成停止与清理，再释放对象**。Runner 的 deleter 直接调用它，不能依赖宿主必定先调用 `disable()` |
| `is_enabled_by_default()` | 与 Settings 全局开关默认值一致；新模块优先默认关闭，避免安装后产生未请求的业务动作 |
| `call_custom_action()` | 仅处理已定义动作，校验输入；不把任意字符串当作命令执行 |

[加载器](src/runner/kit_module.cpp) 实际执行 `LoadLibraryExW → GetProcAddress("kit_create")（若未找到则回退至 "powertoy_create"）→ create()`，退出时通过 [KitModuleDeleter](src/runner/kit_module.h) 销毁对象，再卸载 DLL。工厂返回初始禁用对象；初始化失败可返回 `nullptr`。即使模块配置为禁用，DLL 仍会加载，因此构造函数也必须保持轻量。

### 热键

- 普通热键通过 `get_hotkeys()` / `on_hotkey(size_t)` 接入；另一通道为 `GetHotkeyEx()` / `OnHotkeyEx()`，只选当前需要的通道。
- 当前 Runner 传给 `on_hotkey` 的是返回数组的**索引**。保持设置页、冲突检测和模块内的排列一致，不假定它就是 `Hotkey.id`。
- 禁用模块也可能收到普通热键回调，模块必须检查自身启用状态。
- 当前 `update_hotkeys()` 只在 `isShown == true` 分支注册回调；不能依赖头文件“隐藏但仍注册”的注释实现隐形热键。
- 热键、窗口消息及 IPC 回调应快速返回。磁盘扫描、进程冷启动、网络请求、长等待交给已有消息循环或模块拥有的工作线程；不可用无管理的线程无限堆积任务。

### Worker 生命周期

1. 从安装目录的确定路径启动 Worker，正确引用含空格的可执行文件路径；不要依赖任意当前目录或 PATH 搜索。默认不继承无关句柄。
2. Runner 管理的 Worker 接收父进程 PID。现有 Awake 使用 `--use-pt-config --pid <pid>`，LightSwitch 使用 `--pid <pid>`；这不是所有插件都必须照搬的参数集合。
3. Worker 监听父进程退出和本模块的命名退出事件。父进程句柄、事件、线程及取消源都必须有明确所有者；不能仅用同名进程扫描代替父进程绑定。
4. 停用时先发退出信号，等待业务清理；超时后只对本模块启动并持有句柄的进程进行兜底终止，随后回收句柄。不能按进程名杀掉官方 PowerToys 实例。
5. DLL 卸载前结束监听线程和回调，避免 DLL 已释放而回调仍在执行；失败、禁用和销毁分支都要覆盖。
6. Off / 按需模式在“首次启动、重启、重新启用、运行中切换”四种入口保持一致，不能只在 `set_config` 时停止 Worker。
7. 有持续调度职责的模块明确异常退出后的恢复入口和退避策略；无任务时不要为了“健康检查”新增高频轮询。

Kit 自 2.0.13 起恢复托盘宿主行为：关闭主 Settings 窗口后，Runner 与其独立 Worker 继续运行；存在辅助窗口时，主窗口取消关闭并隐藏，见 [MainWindow.xaml.cs](src/settings-ui/Settings.UI/SettingsXAML/MainWindow.xaml.cs)。Settings 创建失败或崩溃也不联动结束宿主。页面监听 `Window.Closed` 时必须先检查 `args.Handled`，不能在取消关闭后释放缓存页。Localserver 有活跃服务时，正常关闭 Settings 会隐藏窗口并保留管理宿主及输出管道，隐藏页面暂停资源采样；重新打开恢复采样。没有活跃服务时，先异步完成待保存配置，再释放页面。完整退出 Kit、语言/权限重启和宿主崩溃不等同于关闭窗口；Localserver 尚无独立 Worker，不能保证依赖输出管道的进程在这些情况下继续工作。

Localserver 当前是例外：真正关闭 Settings 会释放页面持有的管理资源；`ServiceRunner.Dispose()` 沿用原项目的保留服务进程语义，下次进入页面再根据 Kit 的 ownership 记录恢复管理。这不等于后台管理仍在运行，Settings 退出期间不能承诺继续采集日志、健康检查或自动重启。需要持续管理职责的后续变更，应先明确进程所有权和退出语义。

## 4. 配置、日志与数据隔离

### 4.1 当前路径与新增目录约定

**产品根目录为 `%LOCALAPPDATA%\Kit`；插件按固定 ModuleKey 分目录。** 沿用现有平铺布局，不另加 `Plugins` 层，也不迁移现有模块的数据。

```text
%LOCALAPPDATA%\Kit\
├── settings.json                       # Runner 全局配置与模块开关
├── log_settings.json                   # 框架日志配置
├── settings-placement.json             # 设置窗口位置
├── RunnerLogs\
│   └── runner-log.log                  # Runner 原生日志
├── Settings\Logs\<assembly-version>\    # Settings 托管日志，含 Localserver 服务日志
├── Awake\
│   ├── settings.json
│   └── Logs\                           # 原生接口日志和托管版本日志
├── LightSwitch\
│   ├── settings.json
│   ├── ModuleInterface\Logs\<version>\
│   └── Service\Logs\<version>\
├── Localserver\
│   ├── settings.json                   # Kit 模块设置
│   ├── services.json                  # 当前实际读取的服务目录
│   ├── services.d\                    # 可选：按服务拆分的配置
│   ├── hub.settings.json              # 移植保留的管理器参数
│   ├── secrets.dat                    # 当前用户 DPAPI 加密的秘密值
│   ├── backups\                       # 服务配置备份
│   └── State\service-<hash>.json       # 服务进程身份与恢复记录
├── UDPtest\
│   ├── settings.json                   # UDPtest 模块设置
│   └── Logs\
├── AiHub\
│   ├── settings.json                   # AI Hub 基础配置与端点
│   ├── secrets.dat                     # DPAPI 加密的 API Key 凭据
│   ├── security.md                     # 用户全局安全策略
│   ├── chains\                         # 基础任务策略目录 (AGENTS.md)
│   ├── kernels\                        # 下载验证的 Codex / Pi CLI 执行内核
│   └── requests\                       # 任务执行临时隔离工作区
└── Sample\                             # 新模块目录约定，按需创建
    ├── settings.json                   # 用户配置
    ├── State\                          # 可选：运行状态，不混入配置
    ├── Cache\                          # 可选：可重建缓存
    └── Logs\                           # 可选：本模块日志
```

上图未列出所有框架辅助文件。`Sample\State`、`Cache` 是新增数据有需要时才采用的约定，不代表框架已经实现通用存储服务。

Localserver 的实际读写入口是 `ServiceCatalogStore`，文件名仍为 `services.json`；路径 helper 中的 `LinesFilePath` 和 `OwnershipFilePath` 不代表已启用 `lines.json` 或统一 `ownership.json`。文档应跟随调用链，不能仅按常量推断数据格式。

Localserver 的服务日志复用 Settings 已初始化的 `ManagedCommon.Logger`，实际文件为 `%LOCALAPPDATA%\Kit\Settings\Logs\<assembly-version>\Log_yyyy-MM-dd.log`，用 `[Localserver]` 和服务 Id 区分来源。模块不重新初始化全局 Logger，不创建自己的日志文件或查看面板；旧 `Localserver\Logs` 目录不主动清理。

| 数据或资源 | 位置与边界 |
| --- | --- |
| 模块用户配置、业务内部状态、缓存、日志 | `%LOCALAPPDATA%\Kit\<ModuleKey>\...`；模块不得清理相邻模块或整个 Kit 根目录 |
| Settings 内运行的模块日志 | 复用 `%LOCALAPPDATA%\Kit\Settings\Logs\<assembly-version>\`，按模块和服务标识区分；不重设进程级 Logger 路径 |
| 默认设置备份 | 当前是 `%USERPROFILE%\Documents\Kit\Backup`，由宿主统一管理，用户可调整；这是产品内部数据默认根目录之外的既有例外 |
| 低完整性进程数据 | helper 保留 `%USERPROFILE%\AppData\LocalLow\Kit` 支持，仅实际需要低完整性进程时使用，不另造一套常规配置 |
| 安装资产、图标、DLL、静态资源 | 随程序部署；不在安装目录写入运行状态，不把只读资产复制成用户配置 |
| 用户主动指定的导出文件 | 使用用户选择的目标路径；不能把插件内部数据库、临时状态当成导出文件 |
| Kit 自有注册表配置 | 使用 `HKCU\Software\Microsoft\Kit` 产品空间；普通模块优先 JSON，无需求不新增注册表键 |

当前 [gpo.h](src/common/utils/gpo.h) 保留上游兼容入口，但统一返回 `not_configured`；Runner、UI、Worker 不再通过这些入口读取或应用官方 `SOFTWARE\Policies\PowerToys` 策略。这不是 Kit 自有企业策略系统。

### 4.2 使用已有路径 API

C++：

```cpp
#include <common/SettingsAPI/settings_helpers.h>

const auto moduleDirectory =
    PTSettingsHelper::get_module_save_folder_location(L"Sample");
const auto settingsFile =
    PTSettingsHelper::get_module_save_file_location(L"Sample");
```

C#：

```csharp
using Kit.Settings.UI.Library;

string settingsFile =
    SettingsUtils.Default.GetSettingsFilePath(SampleSettings.ModuleName);
```

依据：[settings_helpers.cpp](src/common/SettingsAPI/settings_helpers.cpp)、[SettingPath.cs](src/settings-ui/Settings.UI.Library/SettingPath.cs)、[SettingsUtils.cs](src/settings-ui/Settings.UI.Library/SettingsUtils.cs)。不要把 `Microsoft\PowerToys` 写入新模块路径，也不要通过修改环境变量让官方程序误用 Kit 数据。

### 4.3 写入、清理与备份

- 用户配置与频繁变化的运行状态分开保存，明确各文件的写入者；跨进程状态更新需要处理重复通知、半写入读取和并发。
- `SettingsUtils.SaveSettings` 当前使用 `WriteAllText`，不提供通用的原子写入、跨进程锁或失败确认协议。需要可靠状态持久化的模块应在自身范围内处理，不把“调用返回”当成 Worker 已应用配置。
- 优先复用 [ManagedCommon.Logger](src/common/ManagedCommon/Logger.cs) 或 [LoggerHelpers](src/common/utils/logger_helper.h)。托管 logger 会清理旧版本日志目录，所以日志目录不能放配置、数据库或备份。
- 限制日志频率和保留范围，不记录 Token、Cookie、密码、完整敏感命令行或配置正文。正常日志用于本地故障定位，不属于行为遥测。
- 清理前验证最终绝对路径位于当前模块拥有的目录；处理重解析点/链接，不能沿链接递归删除用户文件。新插件不得清理官方 PowerToys、其他模块或全局构建缓存。
- 当前[备份规则](src/settings-ui/Settings.UI.Library/backup_restore_settings.json) 收集 `*settings.json` 并排除 `log_settings.json`。新增缓存/数据库不要伪装成 settings 文件；确需备份额外数据时，单独评审规则、大小及恢复语义。

### 4.4 Mutex、事件与 IPC 命名

目录隔离之外，还必须隔离所有命名内核对象：

- 单实例 Mutex、退出/触发事件、共享内存等命名对象使用稳定的 `Local\Kit.Sample.<Purpose>-<GUID>` 等产品专属名称，保证接口与 Worker 一致；避免只用 `Sample`、`Awake` 或上游 `PowerToys` 名称。
- 命名管道沿用 `\\.\pipe\kit_*` 形式，不能把 `Local\...` 当作管道路径。会话/用户作用域及 ACL 复用当前框架实现；不为了便利扩大为所有用户可写。
- Runner ↔ Settings 继续复用已有 TwoWayPipeMessageIPC。跨 C++/C# 新增常量时才同步 [shared_constants.h](src/common/interop/shared_constants.h) 与 `src/common/interop/Constants.idl/.h/.cpp`；没有跨语言需求不新建 WinMD 层。
- 当前 Awake 单实例 Mutex 为 `Local\Kit.Awake`，显示名称仍为 `Awake`；进程状态判定同时检查 Kit 安装目录中的完整路径和会话，不接受任意同名官方进程。

### 4.5 日志接口使用规范

Kit 提供了统一的集中式日志基础设施，覆盖 C++ 原生模块接口、C++ 独立服务/Worker、C# 独立进程 Worker 以及在 Settings 宿主内运行的模块界面。所有日志输出统一受 `%LOCALAPPDATA%\Kit\log_settings.json`（由常规设置中的“日志”开关和日志级别控制）的运行时动态过滤。

#### 1. C++ 原生接口与 C++ Worker

* **头文件引用**：
  ```cpp
  #include <common/utils/logger_helper.h>
  #include <common/logger/logger.h>
  ```
* **初始化**（在 DLL `kit_create()` 或服务进程启动入口调用）：
  ```cpp
  // 参数：模块名、内部组件子路径、logger 唯一标识名
  LoggerHelpers::init_logger(L"Sample", L"ModuleInterface", "sample-module");
  ```
  * 自动绑定全局配置：根据 `%LOCALAPPDATA%\Kit\log_settings.json` 自动设置 `spdlog` 日志级别（`trace`/`debug`/`info`/`warn`/`err`/`critical`/`off`）。
  * 自动生成存储路径：`%LOCALAPPDATA%\Kit\<ModuleName>\<InternalPath>\Logs\<Version>\log.log`。
  * 自动轮转管理：保留当前版本的日志，清理陈旧版本的日志文件夹。
* **日志写入宏**：
  ```cpp
  Logger::trace("Detailed step-by-step trace: {}", value);
  Logger::info("Sample worker started successfully on pid {}", GetCurrentProcessId());
  Logger::warn("Recoverable network timeout, retrying...");
  Logger::error("Failed to parse configuration: {}", ex.what());
  ```

#### 2. C# 独立 Worker / 进程

* **命名空间引用**：
  ```csharp
  using ManagedCommon;
  ```
* **初始化**（在进程入口 `Program.cs` 调用）：
  ```csharp
  // 参数：相对路径，例如 @"\Sample\Logs"
  Logger.InitializeLogger(@"\Sample\Logs");
  ```
  * 自动从 `%LOCALAPPDATA%\Kit\log_settings.json` 读取全局日志开关与过滤级别，动态启用/禁用日志写入及级别过滤。
  * 自动生成存储路径：`%LOCALAPPDATA%\Kit\Sample\Logs\<Version>\Log_yyyy-MM-dd.log`。
* **日志写入方法**：
  ```csharp
  Logger.LogTrace("Detailed trace info");
  Logger.LogDebug("Debug parameters state");
  Logger.LogInfo("Service listening on port 8080");
  Logger.LogWarning("Fallback route activated");
  Logger.LogError("Exception caught during execution", exception);
  Logger.LogCritical("Unrecoverable error occurred", exception);
  ```

#### 3. Settings 宿主内运行的模块（如 ViewModel / Page）

* **复用宿主 Logger**：Settings 进程（`Kit.Settings.exe`）启动时已全局初始化了 `ManagedCommon.Logger`，写入目标为 `%LOCALAPPDATA%\Kit\Settings\Logs\<assembly-version>\Log_yyyy-MM-dd.log`。
* **开发规范**：
  * **禁止重新调用 `Logger.InitializeLogger`**，避免篡改整个设置宿主的日志路径与 TraceListener。
  * **建议使用模块标签前缀**：直接调用 `Logger.LogInfo("[Sample] Target connection established.")` 或 `Logger.LogError("[Sample] Save failed", ex)`，以便在统一日志中按模块筛选定位问题。

## 5. 设置模型、序列化与 IPC

### 5.1 最小有效模型

下例放入 Settings.UI.Library，对应 `name/version/properties` JSON 结构；实际属性按插件需要定义。`Version` 是配置结构版本，不能据此判断 Worker 是否存活。

```csharp
using System.Text.Json.Serialization;
using Kit.Settings.UI.Library.Interfaces;

namespace Kit.Settings.UI.Library;

public sealed class SampleSettings : BasePTModuleSettings, ISettingsConfig
{
    public const string ModuleName = "Sample";

    public SampleSettings()
    {
        Name = ModuleName;
        Version = "1.0";
    }

    [JsonPropertyName("properties")]
    public SampleProperties Properties { get; set; } = new();

    public string GetModuleName() => ModuleName;

    public bool UpgradeSettingsConfiguration() => false;
}

public sealed class SampleProperties
{
    [JsonPropertyName("limit")]
    public IntProperty Limit { get; set; } = new IntProperty(20);
}

public sealed class SndSampleSettings
{
    [JsonPropertyName("Sample")]
    public SampleSettings Settings { get; set; }
}
```

`BasePTModuleSettings` 已实现 `ISettingsConfig` 要求的 `ToJsonString()`。如果直接实现接口，则必须自行提供该方法及序列化实现。全局启用开关放在 `GeneralSettings.Enabled`，不要再在模块 Properties 中复制一个相互竞争的开关。

必须在 [Library 的 SettingsSerializationContext.cs](src/settings-ui/Settings.UI.Library/SettingsSerializationContext.cs) 添加：

```csharp
[JsonSerializable(typeof(SampleSettings))]
[JsonSerializable(typeof(SampleProperties))]
[JsonSerializable(typeof(SndSampleSettings))]
[JsonSerializable(typeof(SndModuleSettings<SndSampleSettings>))]
```

`SettingsUtils` 和 `BasePTModuleSettings` 使用这个 Library context；**只修改 UI 的 `SourceGenerationContextContext.cs` 不够**。UI 代码如果显式使用自己的 context，再在[该 context](src/settings-ui/Settings.UI/SerializationContext/SourceGenerationContextContext.cs) 注册对应类型。这里要求的是正确的源生成序列化接入，不代表整个应用已采用 Native AOT 发布。

### 5.2 正常设置更新链路

```text
Page / ViewModel
  → ShellPage.SendDefaultIPCMessage
  → Runner dispatch_received_json
  → 按稳定 ModuleKey 调用 ModuleInterface.set_config
  → 保存当前模块配置，并更新 Worker / 热键状态
```

Runner 同时接受 `kit` 和旧 `powertoys` 外层字段，并在完整设置响应中提供两者。当前 `SndModuleSettings<T>` 仍发送 `powertoys`，以下示例与这个序列化模型一致；新增调用应复用该模型，不能只改发送端字段：

```json
{
  "powertoys": {
    "Sample": {
      "name": "Sample",
      "version": "1.0",
      "properties": {
        "limit": { "value": 20 }
      }
    }
  }
}
```

在 Settings UI 的页面/VM 调用点可显式使用 Library context 序列化：

```csharp
var message = new SndModuleSettings<SndSampleSettings>(
    new SndSampleSettings { Settings = currentSettings });

string json = System.Text.Json.JsonSerializer.Serialize(
    message,
    typeof(SndModuleSettings<SndSampleSettings>),
    SettingsSerializationContext.Default);

ShellPage.SendDefaultIPCMessage(json);
```

`currentSettings` 为该页面持有的 `SampleSettings`，使用现有页面的 IPC 回调方式即可。全局开关走 `OutGoingGeneralSettings` / `general`；已有 Quick Access 单模块开关还使用 `module_status`；自定义动作使用 `action`。以 [settings_window.cpp](src/runner/settings_window.cpp) 的分派实现为准，普通模块不需要新增通用消息路由器。

读取配置可使用 `SettingsRepository<SampleSettings>.GetInstance(SettingsUtils.Default).SettingsConfig`。`ISettingsRepository<T>` **没有 `SaveSettings` 方法**。离线工具、初始化或明确负责落盘的代码使用：

```csharp
SettingsUtils.Default.SaveSettings(
    currentSettings.ToJsonString(),
    SampleSettings.ModuleName);
```

正常界面更新不要同时另走直接文件写入和 IPC，形成两个竞争写入者。文件监听只关注自己的文件、合并重复通知；后台通知更新 UI 时经 DispatcherQueue。页面释放时只退订自己的回调、销毁自己创建的 watcher；不要 Dispose/StopWatching 共享的 SettingsRepository 单例，影响其他页面的通知。

## 6. 新模块完整接入清单

按新模块的真实能力更新以下入口。显式列表是当前结构，不新增反射扫描、目录发现或另一套插件注册框架。

| 接入面 | 必须检查的文件与动作 |
| --- | --- |
| 工程与构建依赖 | [Kit.slnx](Kit.slnx)：添加实际需要的项目和 Runner 的 BuildDependency；独立构建项目与解决方案构建的依赖行为不同 |
| 原生加载 | [main.cpp](src/runner/main.cpp)：`KitKnownModules` 添加真实 DLL 文件名；当前三个活动接口 DLL 均部署在 Runner 旁 |
| 模块身份 | [ModuleType.cs](src/common/ManagedCommon/ModuleType.cs)、C++ Key、Settings.ModuleName、JSON key 保持一致 |
| 全局开关 | [EnabledModules.cs](src/settings-ui/Settings.UI.Library/EnabledModules.cs) 添加属性/default/通知；[EnabledModulesJsonConverter.cs](src/settings-ui/Settings.UI.Library/EnabledModulesJsonConverter.cs) **同时增加 Read 和 Write** |
| 活动列表与 CLI | [KitModuleCatalog.cs](src/settings-ui/Settings.UI.Library/Helpers/KitModuleCatalog.cs)：ActiveModules、ActiveSettingsModuleKeys、ActiveEnabledModuleKeys；DashboardModules 当前复用 ActiveModules |
| 标签、图标和状态映射 | [ModuleHelper.cs](src/settings-ui/Settings.UI.Library/Helpers/ModuleHelper.cs)：标签资源、图标、读开关、写开关、Key 五处映射 |
| 配置与 IPC | 第 5 节的 Settings/Properties/Snd DTO 与 Library context；UI context 按实际调用补充 |
| 页面与导航 | Page、VM、资源；[ShellPage.xaml](src/settings-ui/Settings.UI/SettingsXAML/Views/ShellPage.xaml) 添加导航项；[ShellPage.xaml.cs](src/settings-ui/Settings.UI/SettingsXAML/Views/ShellPage.xaml.cs) 调用 `NavHelper.SetNavigateTo(..., typeof(SamplePage))` |
| 字符串路由与页面映射 | [App.xaml.cs](src/settings-ui/Settings.UI/SettingsXAML/App.xaml.cs) 的 `GetPage(string)`；[ModuleGpoHelper.cs](src/settings-ui/Settings.UI/Helpers/ModuleGpoHelper.cs) 的页面类型映射 |
| 类型化深链 | [settings_window.h](src/runner/settings_window.h) 枚举、[settings_window.cpp](src/runner/settings_window.cpp) 字符串双向映射、[SettingsDeepLink.cs](src/common/Common.UI/SettingsDeepLink.cs) |
| Dashboard 内容 | [DashboardViewModel.cs](src/settings-ui/Settings.UI/ViewModels/DashboardViewModel.cs) 的模块内容分派；展示真实状态，有动作才展示动作，无动作打开设置页 |
| 图标部署 | [Kit.Settings.csproj](src/settings-ui/Settings.UI/Kit.Settings.csproj) 的大小图 **Exclude 保留清单**，以及 [Quick Access 工程](src/settings-ui/QuickAccess.UI/Kit.QuickAccess.csproj) 的小图标 Content 与 Exclude；两个工程共享 `WinUI3Apps` 输出，即使新模块没有快捷动作也必须同步，否则后续构建会删除新图标 |

按需入口：

- **Quick Access 动作**：更新 Catalog.QuickAccessModules、[QuickAccessLauncher](src/settings-ui/Settings.UI.Controls/QuickAccess/QuickAccessLauncher.cs)、[QuickAccessCoordinator](src/settings-ui/QuickAccess.UI/Services/QuickAccessCoordinator.cs) 和相关 ViewModel 提示。动作按需增加，但 AllApps 列表消费 ActiveModules，所有新增可见模块都要验证没有专属动作时的设置页回退与图标展示。
- **快捷键配置**：实现 `IHotkeyConfig`，加入 [SettingsFactory.cs](src/settings-ui/Settings.UI.Library/SettingsFactory.cs) 的显式 loader，并同步冲突提示；无快捷键则不添加这层。
- **GPO**：当前兼容入口统一不应用策略。只有功能确需管理策略时才设计完整的 native/UI/Worker 一致性，不按上游文档机械恢复官方企业策略资产。
- **跨语言事件**：只在必要时更新 interop 常量与投影；不要恢复已删除模块的事件全集。

仅设 `Tag="Sample"`、仅加 ShellViewModel 标题属性，或仅把 DLL 复制到输出目录，都不能完成当前 Kit 的注册。

## 7. Logo、图标与资源规范

### 7.1 源文件规格

以下两个 PNG 规格来自本次对 Awake、LightSwitch 文件头的实际读取。新模块沿用它们，避免每页自行决定尺寸。

| 用途 | 源码位置 | 新模块要求与使用方式 |
| --- | --- | --- |
| 导航、开关卡片、Dashboard 小图标 | `src/settings-ui/Settings.UI/Assets/Settings/Icons/<ModuleKey>.png` | **36 × 36 px，8-bit RGBA PNG，透明背景**；按单色轮廓设计，适配 BitmapIcon，不能依靠颜色才能辨认 |
| 设置页顶部模块配图 | `src/settings-ui/Settings.UI/Assets/Settings/Modules/<ModuleKey>.png` | **400 × 266 px，8-bit RGBA PNG**；保留比例，建议透明背景，不嵌入界面文字；当前 SettingsPageControl 以最大 160 DIP 宽展示 |
| Worker 的托盘/独立窗口图标 | 新模块自身 Worker 项目的 `Assets\<ModuleKey>.ico`，经项目资源或内容部署 | 仅需要托盘/窗口时增加；新 ICO 建议包含 16、20、24、32、48、64、256 px 图层，小尺寸需单独检查可辨识度 |
| 可编辑设计源 | 模块自己的 Assets 目录或现有设计目录 | 有实际设计源才保留；SVG/设计文件不会自动成为运行时图标，不为形式增加另一套资产流水线 |

小图标四周保留适当透明边距，16/20/24 DIP 下不贴边、不糊成块。大配图是说明性图片，不能当作导航图标缩小使用。状态不能只靠颜色表达，应配文字或可访问名称。

现有品牌资源由宿主拥有：

- Settings 图标：`src/settings-ui/Settings.UI/Assets/Settings/icon.ico`。
- Runner 托盘资源：`src/runner/svgs/*.ico`，由 Runner 资源/工程文件管理。
- 新插件不得为了自己的 logo 覆盖这些宿主文件或稀疏包标识。

### 7.2 XAML 与构建接入

```xml
<NavigationViewItem
    x:Name="SampleNavigationItem"
    x:Uid="Shell_Sample"
    Icon="{ui:BitmapIcon Source=/Assets/Settings/Icons/Sample.png}" />
```

随后在 ShellPage 构造函数按现有形式注册：

```csharp
NavHelper.SetNavigateTo(SampleNavigationItem, typeof(SamplePage));
```

页面上的配图使用 `ms-appx:///Assets/Settings/Modules/Sample.png`。字符串写入 `Strings/en-us/Resources.resw` 和 `Strings/zh-CN/Resources.resw`，至少包含 `Shell_Sample.Content`、`Sample.ModuleTitle`、`Sample.ModuleDescription` 及控件所需属性。

**源码有图片不代表产物有图片。** Settings 和 Quick Access 共用 `WinUI3Apps`，后者也会清理这个目录中的非白名单小图标。因此新增可见模块必须同步两个工程的保留清单和 Quick Access 的小图标 Content，即使它没有快捷动作。验收时检查实际 `WinUI3Apps\Assets\Settings\...`、PRI/XAML 引用和两种语言的展示，不只检查源码文件存在。

## 8. WinUI 3 + Mica Alt 界面规范

当前主窗口 [MainWindow.xaml](src/settings-ui/Settings.UI/SettingsXAML/MainWindow.xaml) 已采用 `<MicaBackdrop Kind="BaseAlt" />`。新模块设置页复用这个窗口，不为每个页面单独创建背景控制器或隐藏预热窗口。

- 页面继承 [NavigablePage](src/settings-ui/Settings.UI/Helpers/NavigablePage.cs)，容器保持透明；使用 [SettingsPageControl](src/settings-ui/Settings.UI/SettingsXAML/Controls/SettingsPageControl/SettingsPageControl.xaml) 与现有 SettingsCard / SettingsExpander / SettingsGroup。
- `SettingsExpander.Items` 的默认样式目标是 `SettingsCard`。日志、图表等自定义布局必须放在 `SettingsCard` 内，再按需设置 `HorizontalContentAlignment="Stretch"` / `ContentAlignment="Vertical"`；直接把 `Border` 或 `Grid` 放进 Items 会在展开时应用不兼容样式并导致 WinUI 异常。编译成功不能替代实际展开验证。
- 卡片颜色通过 [Colors.xaml](src/settings-ui/Settings.UI/SettingsXAML/Themes/Colors.xaml) 的 ThemeResource 获取。当前 `CardBackgroundFillColorDefaultBrush` 为浅色 `#80FFFFFF`、深色 `#0AFFFFFF`；描边分别为 `#18000000`、`#1AFFFFFF`。这些是基线值，不应复制成每页硬编码。
- [Card.xaml](src/settings-ui/Settings.UI.Controls/Primitives/Card.xaml) 已将外层背景设为透明，由内层 Grid 绘制一次背景和边框。不要再叠一层不透明白底或重复描边。
- 高对比度、关闭透明效果、窗口失焦时使用系统支持的回退，保证文字可读；不把截图中的 Mica 颜色当成固定颜色值。
- 复用已有间距、圆角、文字层级和窗口标题栏。新页面不显示程序集名、IPC、日志路径等实现细节，除非是用户需要的诊断操作。
- 设置操作在 UI 线程快速完成；较长工作显示真实进度、取消入口和错误状态，避免同步 IO 卡住窗口。配置变化应立即得到明确反馈，不能只改开关视觉而 Worker 状态不变。
- 为交互控件提供可访问名称、键盘焦点和合理 Tab 顺序；标题使用现有 HeadingLevel。装饰图片保持 AccessibilityView.Raw。
- 至少检查浅色/深色/高对比度、100%/150%/200% 缩放、中英文、480 DIP 最小窗口及宽窗口、键盘操作与 Narrator。本次仅核对 XAML，未完成这些运行期视觉验收。

### 8.1 模块主启用开关（Master Toggle）与选项联动禁用规范

本规范统一对齐 PowerToys / Kit 官方模块（如 Awake、LightSwitch）的标准交互与状态生命周期：

#### 1. 开关数据源与 GPO 策略判定标准
- **单一真理来源**：模块主启用状态必须持久化在 `GeneralSettings.Enabled.<ModuleName>` 中（对应 `%LOCALAPPDATA%\Kit\settings.json`），禁止在模块内部私有属性中另起独立的“启用/禁用”属性造成状态竞争。
- **严谨的 GPO 判定逻辑**：
  ```csharp
  // 必须只在明确配置为 Enabled 或 Disabled 时才判定为受组织策略接管
  public bool IsEnabledGpoConfigured => _gpoConfiguration is GpoRuleConfigured.Enabled or GpoRuleConfigured.Disabled;
  ```
  **严禁**使用 `_gpoConfiguration != GpoRuleConfigured.NotConfigured`。对于未在系统组策略模板中注册专有策略的新模块，框架 `ModuleGpoHelper` 会返回 `GpoRuleConfigured.Unavailable (-2)`。若用不等于 `NotConfigured` 判断，会导致其被误判为“已受策略管理”，触发黄色警告提示 *“This setting is managed by your organization”* 并错误锁死用户开关。

##### 2. 开关关闭时的界面选项整体联动禁用与暗化置灰（Full Grayed Out & Dimmed）
- **核心交互原则**：**当模块主开关关闭时，页面下方所有相关设置、操作按钮、卡片、列表及图表必须全部自动置灰与钝化暗化**，达到与官方 Awake、LightSwitch 原生视觉体验完全一致。
- **WinUI 3 容器暗化与置灰机制**：
  在 WinUI 3 中，纯容器与非输入控件（如 `<Border>`, `<TextBlock>`, `<ProgressBar>`, 自定义 `<Ellipse>` 状态灯、Path 曲线）即使父容器设置了 `IsEnabled="False"`，也不会自动去色或改变透明度，导致界面依然呈现亮色/高对比度的未置灰状态。
  **标准解决规范**：在设置页 XAML 中，将顶部主开关卡片及全局 `InfoBar` 下方的所有区域使用容器包裹，并**同时绑定 `IsEnabled` 与 `Opacity`**：
  ```xml
  <!-- 主开关联动禁用容器：关闭时整页内容全部自动置灰并按 WinUI 3 标准 disabled 深度（0.38）全局暗化 -->
  <ContentControl IsEnabled="{x:Bind ViewModel.IsEnabled, Mode=OneWay}"
                  Opacity="{x:Bind ViewModel.IsEnabled, Mode=OneWay, Converter={StaticResource BoolToDisabledOpacityConverter}}"
                  IsTabStop="False"
                  HorizontalContentAlignment="Stretch">
      <StackPanel Spacing="16">
          <!-- 模块下的所有卡片、仪表盘、操作按钮、数据表格与展开抽屉 -->
      </StackPanel>
  </ContentControl>
  ```
- **状态指示器去激活化（Neutral Disabled Indicators）**：
  - 模块关闭时，**严禁保留常亮的绿色状态灯或活跃标签**（如 "● ACTIVE"、"健康" 等）。
  - ViewModel 在 `IsEnabled == false` 时必须主动将状态灯和描述文本切至中性禁用态：
    - 状态文本置为 "DISABLED" / "已禁用"（如 `Localserver_HealthDisabled`）。
    - 状态圆点/画刷切为 `ControlStrongStrokeColorDefaultBrush`（中性灰），禁止使用 `SystemFillColorSuccessBrush`（绿）。
    - 状态数值与进度条置零或重置为占位符 `"—"`。

#### 3. 业务停用与彻底静止后台生命周期（Zero Background Activity）
- 当 `ViewModel.IsEnabled` 被用户置为 `false` 时：
  1. **立即停止所有后台采样与轮询定时器**（如 Localserver 的 `_telemetryTimer` 必须调用 `.Stop()`，停止 CPU、RAM、GPU 硬件高频采样；UDP Test 必须停止测试会话与后台网络探针）。
  2. **在所有后台入口与异步刷新处加入防护**：在 `OnTelemetryTimerTick`、`RefreshTelemetryAsync`、`RefreshEnvironmentAsync`、`StartAsync` 等入口顶部加入 `if (!_isEnabled) return;`，杜绝任何残余任务或导航残留使后台活动复活。
  3. **页面激活生命周期守卫**：在 `SetPageActive(bool active)` 中，仅当 `active && _isInitialized && _isEnabled` 三者同时满足时才允许启动后台轮询，关闭状态下进入页面严禁触发活动。
  4. **立即持久化与下发 IPC**：更新 `GeneralSettings.Enabled.<ModuleName> = value` 并保存设置，触发向 Runner 的配置同步 IPC消息（`ShellPage.SendDefaultIPCMessage(outgoing.ToString())`），通知原生 ModuleInterface 执行 `disable()`。
  5. **操作与行级防护**：将工具栏按钮（如 Add Line, Start all, Stop all）及子行各项操作的 `Can*` 属性绑定联动 `_parent.IsEnabled`，保证子项逻辑层面同样完全锁定。
  6. **同步级联停用所有子链路与服务（Cascade Stop on Switch Off）**：当模块主开关关闭时，必须同步（或在后台无等待立即触发）向所有正在运行的子链路/服务发出停止指令（如 Localserver 触发 `_ = StopAllLinesAsync()` 终止所有子服务；UDP Test 触发 `_ = StopAsync()` 停止所有探针 Worker 并挂起后台网络身份探测）。不得出现总开关关闭后底层服务依然在后台静默运行的情况。

### 8.2 WinUI 3 ContentDialog 与 ValueConverter 规范

#### 1. XAML 绑定与 ValueConverter 类型严格匹配
- **底层原理与常见崩溃**：WinUI 3 (Windows App SDK) 的 XAML 绑定引擎对属性类型实行严格校验。若 XAML 绑定的目标属性类型为 `Microsoft.UI.Xaml.Media.Brush`（例如 `Border.BorderBrush`、`Shape.Fill`、`Control.Foreground` 等），`IValueConverter.Convert` 返回的对象**必须严格为 `Brush` 或 `SolidColorBrush`，绝对不能返回 `Windows.UI.Color`**。
- **崩溃特征**：在 WPF 或旧 UWP 中部分类型可隐式包装，但在 WinUI 3 中返回 `Color` 会直接在底层的 COM/WinRT 接口强制转换（QueryInterface）时抛出 `0x80004002 (E_NOINTERFACE)`，导致进程在弹窗或列表渲染瞬间未处理异常闪退。
- **标准实现规范**：
  ```csharp
  // 必须返回 SolidColorBrush 或在应用层资源查找对应的 ThemeResource Brush
  public object Convert(object value, Type targetType, object parameter, string language)
  {
      if (value is FindingSeverity severity)
      {
          return severity switch
          {
              FindingSeverity.Critical => new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68)),
              FindingSeverity.High => new SolidColorBrush(ColorHelper.FromArgb(255, 249, 115, 22)),
              FindingSeverity.Medium => new SolidColorBrush(ColorHelper.FromArgb(255, 234, 179, 8)),
              FindingSeverity.Low => new SolidColorBrush(ColorHelper.FromArgb(255, 59, 130, 246)),
              _ => new SolidColorBrush(ColorHelper.FromArgb(255, 107, 114, 128)),
          };
      }
      return DependencyProperty.UnsetValue;
  }
  ```

#### 2. ContentDialog 现代宿主与安全呈现范式
- **必须绑定活动宿主 XamlRoot 与主题**：在 WinAppSDK / WinUI 3 中，弹出 `ContentDialog` 必须明确设置其 `XamlRoot` 为调用源控件的 `XamlRoot`，且必须同步其 `RequestedTheme` 与宿主 `ActualTheme`，防止在深浅色切换时弹窗主题与主界面割裂：
  ```csharp
  var dialog = new FindingDetailsDialog(finding)
  {
      XamlRoot = this.XamlRoot,
      RequestedTheme = this.ActualTheme,
  };
  await dialog.ShowAsync();
  ```
- **并发弹窗保护**：同一个 `XamlRoot` 同时只能显示一个 `ContentDialog`，若在上一个弹窗尚未关闭时尝试显示第二个弹窗，WinUI 3 会直接抛出 `InvalidOperationException`。编写异步弹窗逻辑时必须采用原子标志（如 `Interlocked.CompareExchange`）或信号量保护，杜绝重复弹窗引发异常。

### 8.3 纯正中英双语规范与无混杂标注规范 (Strict Bilingual Parity Standards)

Kit 作为同时面向双语用户的本地工具平台，必须维持严格、地道的中英双语质量标准：

#### 1. 杜绝中英混杂标注与斜杠排版
- **严禁小括号中英混注**：中文界面中**严禁**保留英文名称后加括号中文的混杂写法（例如禁止 `Awake (保持唤醒)`、`启用 Awake`、`启用 UDP Test`、`启用 AI Hub`、`Essential Policy (全局任务策略)`）。
- **纯化规范**：
  - 中文环境（`zh-CN`）：必须使用地道全中文术语，如 `保持唤醒`、`启用保持唤醒`、`启用网络探测`、`启用 AI 智能中心`、`基础任务策略`。
  - 英文环境（`en-US`）：必须使用纯正英文，如 `Awake`、`Enable Awake`、`Enable UDP Test`、`Enable AI Hub`、`Essential Policy`。
- **严禁双语斜杠并列**：禁止在界面标签、表头或按钮文案中采用 `操作建议 / Action`、`端口 / Port` 这类占位偷懒的双语混排写法，两种语言必须在各自的 `.resw` 资源中完全独立。

#### 2. UWP / WinAppSDK 资源定义规范与 x:Uid 强类型匹配
- **x:Uid 属性点号命名**：在 `Strings/zh-CN/Resources.resw` 与 `Strings/en-us/Resources.resw` 中，配合 XAML `x:Uid="MyControl"` 使用时必须遵循点号属性命名（例如 `MyControl.Text`、`MyControl.Header`、`MyControl.PlaceholderText`）。
- **属性强类型反射与防崩溃铁律（Fatal XamlParseException）**：
  - WinUI 3 XAML 引擎在 `InitializeComponent()` 期间，会通过反射将 `[Uid].[PropertyName]` 的值直接赋给对应的 XAML 控件。
  - **如果目标控件本身没有该属性**（例如对 `SettingsCard` 设置 `PlaceholderText`，或对 `Button` 设置 `Text`），WinUI 3 会抛出致命的 `XamlParseException: Unable to resolve property '...' while processing properties for Uid '...'`，并触发不可捕获的 `0xC000027B` fail-fast 崩溃闪退！
  - **规则**：`x:Uid` 必须精确标记在拥有该属性的具体子控件上（例如为嵌套在 `SettingsCard` 内的 `PasswordBox` 赋予独立的 `x:Uid="My_ApiKeyBox"`，而卡片标题只使用 `My_ApiKeyCard.Header`）。
  - **附加属性语法**：若需通过资源文件设置工具提示等附加属性，必须使用标准完整的命名空间语法：`MyButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`，绝对不能写成 `MyButton.Text` 或未限定作用域的属性名。
- **严禁在 Key 中随意使用斜杠 `/`**：WinAppSDK 的 `ResourceLoader.GetString()` 会将 `/` 解析为资源子树路径（如 `SubTree/Key`）。若平级资源键名误用斜杠，会导致根据键名查不到资源并回退为空字符串或抛出资源缺失异常。

#### 3. ViewModel 动态文本的多语种动态格式化
- **动态文案必须跟随当前运行时语言**：ViewModel 中动态拼装的诊断日志、时间戳、检测事实、根因分析和建议操作（如 `PriorityActionText`），不能在代码中硬编码单语种。
- 必须通过 `CultureInfo.CurrentUICulture.Name` 判定当前语言（如 `isZh = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)`），或者统一从 `ResourceLoader` 中检索对应词条，确保界面语言切换时，所有动态文本无需重启即可呈现正确语言。

### 8.4 WinUI 3 页面构造生命周期与 DataContext 绑定顺序

在 WinUI 3 XAML Page 的代码隐藏（Code-Behind）构造函数中，必须严格遵循初始化顺序：

```csharp
public UDPtestPage()
{
    // 1. 优先创建 ViewModel 实例并绑定 DataContext
    ViewModel = App.GetService<UDPtestViewModel>();
    DataContext = ViewModel;

    // 2. 然后再执行 InitializeComponent()
    InitializeComponent();
}
```

- **核心原因**：在调用 `InitializeComponent()` 时，XAML 解析器会立即创建 UI 元素树并对页面内的所有 `x:Bind` 和 `{Binding}` 执行首次求值（Initial Pass）。若 `DataContext` 或 `ViewModel` 在 `InitializeComponent()` 之后才赋值，初始求值时上下文为 `null`，不仅会导致闪烁，对于依赖非空上下文的转换器、事件处理或复杂属性路径，极易触发 NullReferenceException 或无效的初始状态。

### 8.5 系统主题资源安全查找与 ThemeBrushHelper 规范

在 WinUI 3 应用中，直接通过 `Application.Current.Resources[...]` 索引获取系统资源存在严重的安全隐患：

1. **系统样式与系统主题画刷不常驻在 Application 字典**：
   - `Application.Current.Resources` 仅包含应用在 `App.xaml` 中显式合并的资源字典，**不包含** WinUI 3 内部控件模板或动态系统样式（如 `AccentButtonStyle`）。
   - 在代码中直接执行 `Application.Current.Resources["AccentButtonStyle"]` 会抛出 `System.Runtime.InteropServices.COMException (0x80004005): Element not found`。
2. **样式派生标准**：
   - 若需在 XAML 中引用或重写内置系统样式，应在 `Page.Resources` 中使用 `BasedOn` 派生，例如：
     ```xml
     <Page.Resources>
         <Style x:Key="AccentHealthButtonStyle" BasedOn="{StaticResource AccentButtonStyle}" TargetType="Button" />
     </Page.Resources>
     ```
   - 在代码中若需动态替换样式，使用 `Resources.TryGetValue("AccentHealthButtonStyle", out var style)` 并进行空安全判定。
3. **安全主题画刷助手（ThemeBrushHelper）**：
   - 杜绝在 ViewModel 或代码中直接执行 `(Brush)Application.Current.Resources["CardBackgroundFillColorDefault"]`。
   - 统一使用 `ThemeBrushHelper`，通过 `TryGetValue` 安全尝试检索；若资源未就绪或未找到，自动回退到预置的 Fluent 语义画刷（如 `SuccessBrush`、`CautionBrush`、`CriticalBrush`、`AttentionBrush`、`SecondaryTextBrush` 等），确保在深色、浅色及高对比度主题切换下永不崩溃。

## 9. 轻量启动、隐私与依赖边界

### 9.1 新模块不得增加的默认启动职责

- 不递归扫描插件目录、磁盘或所有历史 Settings 类型来发现模块；沿用显式名单。
- 不在 DLL 构造、元数据查询、主窗口构造或全局热键回调中进行网络请求、业务扫描和等待 Worker 就绪。
- 禁用模块不得启动业务进程、计时器或轮询任务；按需功能只在真实使用时启动。
- 不引入自动更新下载/安装、后台版本检查、OOBE、旧 PowerToys 迁移、AI 环境检测、无关 GPO 或遥测职责。
- 新代码不注册行为遥测 Provider、不发送事件、不增加 ManagedTelemetry / EtwTrace 依赖。移植代码确需保留 Trace 调用形状时，用小范围 no-op 维持源码兼容，不要求每个全新插件都创建无用的 `trace.h/.cpp`。
- 共享库、包与版本优先复用当前工程；新增依赖必须说明当前用途。移除依赖前检查实际引用和产物，不能把源目录还在等同于库参与启动。

当前活动 Runner、Awake、LightSwitch、Localserver 的 trace 入口已为空实现或删除，旧 `common/Telemetry` 源目录仍存在。周期版本检查及重试线程已移除，仅保留用户在 Settings 主动触发的版本查询、结果状态和发布页入口，不下载或安装更新。Localserver 的硬件采样方法沿用 `Telemetry` 命名，但只读取本机状态，没有行为遥测上报。

### 9.2 性能记录与优化顺序

分别记录以下时间，不能用一个日志数字代替：

| 指标 | 起止点 |
| --- | --- |
| Runner 启动 | 进程创建到托盘/消息循环可交互 |
| 插件就绪 | 请求启用到实际功能可用，包含 Worker 初始化 |
| Settings 首次打开 | 用户触发到首个可交互页面 |
| Quick Access 首次打开 | 热键/点击到弹层可交互；与后续再次打开分开 |

记录构建版本、Debug/Release、CPU/磁盘环境、普通/管理员、模块开关、计划模式、冷/热缓存条件、样本数、进程/线程数、CPU 与私有内存。对足够样本报告中位数和 P95；样本不足时保留原始分布，不编造百分比收益。

当前 `main.cpp` 的 `STARTUP_TIMING` 使用 `steady_clock`，从 WinMain 入口开始，包含前置配置与计划任务处理；汇总名称为 `Runner initialization since WinMain`。它仍不包含进入 WinMain 前的进程加载，也没有等待 Settings 页面或 Worker 功能就绪，不能当作端到端启动性能证明。

优化优先检查每次启动的更新线程、无关迁移、计划任务重建、重复配置/策略读取和不必要 Worker。两次虚函数调用、单纯删源文件数量或把冷启动移到键盘钩子中，不能代替测量。

## 10. 构建、部署与产物路径

在 Windows PowerShell 7、Visual Studio 2026/MSBuild、对应 Windows SDK 与 .NET 10 环境构建。遵循 [BUILD-GUIDELINES.md](tools/build/BUILD-GUIDELINES.md)；不要把 WinUI/native 构建切到 WSL，也不要新增跨平台兼容层。纯文本检查可独立运行。

### 10.1 顺序构建与测试

以下命令从仓库根目录执行。Settings、两个 Awake 测试项目及原生模板编译项目已包含在 `Kit.slnx` 中；构建失败即停止，不启动应用或测试：

```powershell
& .\tools\build\build.ps1 -Platform x64 -Configuration Debug -Path .
if ($LASTEXITCODE -ne 0) { throw 'Kit build failed' }
```

仅迭代 Settings 测试时，可改为 `-Path .\src\settings-ui\Settings.UI.UnitTests` 单独构建；无需在成功的完整解决方案构建后无条件再构建一次。

定位本机 VSTest，避免固定 VS Edition 安装路径：

```powershell
$kitVsWhere = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio\Installer\vswhere.exe'
$kitVsTest = & $kitVsWhere -latest -products '*' -find 'Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe' | Select-Object -First 1
if (-not $kitVsTest) { throw 'VSTest not found' }

& $kitVsTest '.\Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.dll' /Platform:x64
if ($LASTEXITCODE -ne 0) { throw 'Settings tests failed' }

& $kitVsTest '.\x64\Debug\tests\Awake.UnitTests\Awake.UnitTests.dll' '.\x64\Debug\tests\Awake.ModuleServices.UnitTests\Awake.ModuleServices.UnitTests.dll' /Platform:x64
if ($LASTEXITCODE -ne 0) { throw 'Awake tests failed' }
```

也可使用 VS Test Explorer；本仓库沿用 VSTest，不用 `dotnet test` 替代已有验证流程。

- 独立验证某项目可给 `build.ps1 -Path` 传该项目目录。不要启动多个独立 MSBuild 同时写共享 WinMD、PDB 和 tracking 文件；使用顺序构建或同一解决方案调度。
- `build-essentials.ps1` 当前 restore 解决方案后主要构建 Runner、Settings 和 Quick Access；它不是所有新插件 Worker 已生成的证明。
- 直接构建 `Kit.vcxproj` 不会执行 `Kit.slnx` 中的全部模块 BuildDependency。新增模块必须验证 Worker、接口 DLL 和 UI 都来自本次构建。
- 使用 `Kit.Interop` / `Kit.GPOWrapper` 时保留必要项目引用，验证隔离的干净 Release 输出能重建 WinMD 和 CsWinRT 投影，不能靠旧产物或手工复制 DLL 通过。
- 测试以本次实际构建的结果为准，记录数量和失败原因；历史 `186/186` 等数字不是永久验收门槛。

### 10.2 当前实际输出

默认 x64 Debug 运行布局为：

```text
x64/Debug/
├── Kit.exe
├── Kit.AwakeModuleInterface.dll
├── Kit.LightSwitchModuleInterface.dll
├── Kit.LocalserverModuleInterface.dll
├── Kit.Awake.exe
├── LightSwitchService/
│   └── Kit.LightSwitchService.exe
├── WinUI3Apps/
│   ├── Kit.Settings.exe
│   ├── Kit.QuickAccess.exe
│   ├── LocalserverLib.dll
│   └── Assets/Settings/...
└── tests/
    ├── Awake.UnitTests/Awake.UnitTests.dll
    ├── Awake.ModuleServices.UnitTests/Awake.ModuleServices.UnitTests.dll
    ├── UnitTestsCommonUtils/Common.Utils.UnitTests.dll
    ├── ModuleTemplate/ModuleTemplateCompileTest.dll
    └── AiHub/Kit.AiHub.UnitTests.exe

Debug/x64/tests/SettingsTests/net10.0-windows10.0.26100.0/
└── Settings.UI.UnitTests.dll
```

默认 Release 对应 `x64/Release/`，测试另用 `Release/x64/tests/...`。依据各 `.vcxproj/.csproj` 的 OutDir / OutputPath 和共享 props；自定义命令行覆盖时以实际 MSBuild 属性为准。

旧文档中的 `bin/debug/<version>/`、`bin/publish/` 可以是人工交付整理位置，**当前构建脚本并未自动完成统一搬移和打包**。不能因为该目录已有文件就认定是最新构建，也不能为符合文档而删除当前 MSBuild 输出。保留可复现的构建与部署闭包后再做发布整理。

[verify-runtime-artifacts.ps1](tools/build/verify-runtime-artifacts.ps1) 可对明确指定的整理后运行目录检查遗留产物；它不验证模块可启动，也不能代替中英文 PRI、图标和依赖加载验收。

## 11. 合入与插件交付验收

| 领域 | 必须验证的行为 |
| --- | --- |
| 构建闭包 | 接口 DLL、Worker、UI、资源和必要 native/WinRT 依赖可由当前源码重建；支持的 Debug/Release 目标构建成功 |
| 注册一致性 | 新模块在 Runner、全局启用 JSON、Catalog、侧栏、Dashboard、深链、CLI 中一致；无动作的模块不暴露虚假的快捷动作 |
| 配置 | 默认值、旧配置升级、非法值、缺文件、JSON round trip、文件监听重复事件；只有新 DTO 属性而没有 converter/context 不算完成 |
| 生命周期 | 禁用不启动 Worker、重复启停、正常退出、父进程突然退出、Worker 崩溃、创建进程失败、无效退出事件、Off 重启、语言/管理员重启 |
| 并存隔离 | Kit 与官方 PowerToys 同会话运行，数据、Mutex、命名事件、退出信号和进程判定互不影响 |
| 界面 | 第 8 节的主题、缩放、语言、最小窗口和辅助功能；图标存在于实际运行产物 |
| 启动性能 | 第 9 节四类时间分别比较；记录过程和开关条件，不只比较一条 Total 日志 |
| 隐私与范围 | 新依赖/代码没有恢复遥测、后台更新、无关迁移；日志及清理仅覆盖所属范围 |

保留必要的业务和生命周期测试。现有 [BuildCompatibility.cs](src/settings-ui/Settings.UI.UnitTests/ViewModelTests/BuildCompatibility.cs) 的源码/注册检查可以防接线遗漏，但不能替代 Worker、IPC 和真实 UI 行为测试。

新模块至少在自身 README 写明用途、启停和按需语义、Key、数据目录、图标目录、依赖、构建方法、已验证配置与已知限制。不要求为了文档齐全增加插件不需要的架构层。

Monitor 已删除，其开发材料可复用的经验是：Worker 无界面运行、长任务可取消并反馈进度、配置与运行状态分离、注册点同步、WinMD 干净重建、退出资源回收。不要从旧材料恢复 Monitor 本身、数据库依赖或已经删除的模块列表。

## 12. Review、修复与验证记录

先核对本地上游、代码和资源，再按宿主稳定性、插件隔离与恢复、启动轻量化、开发模板的顺序修复。12.1 记录初次 PowerToys review，12.2–12.3 按当前代码更新，实际构建与测试结果单独列出，不把源码推演当作运行期证明。

### 12.1 与本地上游的同步进度

| 范围 | 核查结论 |
| --- | --- |
| Awake 业务核心 | 会话/锁屏检测、状态计算、配置模型和 ViewModel 与本地快照对齐；Manager 主要差异是删除遥测；Program 与 ModuleServices 保留 Kit 帮助链接、Mutex 和进程身份隔离 |
| Awake 功能 | 被动/无限/计时/到期、保持屏幕、锁屏时允许屏幕按系统策略熄灭、CLI/PID 绑定仍保留；原生接口保留 Kit 的停止等待、句柄回收和销毁清理 |
| LightSwitch 业务核心 | LightSwitchLib、ThemeScheduler、Night Light observer 与本地快照对齐；保留固定时段、日落日出及偏移、跟随夜间模式、系统/应用主题与快捷键 |
| LightSwitch 有意裁剪 | PowerDisplay 配置和事件桥接移除；forceLight/forceDark 自定义动作移除，上游对应 UI 已注释，不把它误报为可见按钮缺失 |
| 测试同步 | 已引入 Awake 两个测试项目及三个原样上游测试源文件，并补充 Mutex 并存和完整进程路径验证；LightSwitch UI 测试源码仍在，源码存在不代表已运行通过 |
| 提交覆盖 | 初次 review 中，`37bff1d` 只修改 README 与 Awake 共 11 个文件，没有 LightSwitch 变更；`3524ba3` 包含两个模块生命周期及 LightSwitch 依赖调整。后续已经推进至 `1ebfa49` / 2.0.13，不能把初次 review 的“未提交”状态当作当前状态 |
| WinUI 3 + Mica | BaseAlt 主背景、透明页面、卡片背景/边框单层绘制和主题资源调整已落地；运行期视觉与无障碍验收仍需执行 |

不沿用旧 `kit-sync-status.md` 的“Awake 落后 23 文件”“95% 同步”等历史估算；不把删除遥测/PowerDisplay 的文件差异算作必须补回的缺失。后续同步应记录上游 commit、保留功能、Kit 有意差异、未合入行为和验证结果。

### 12.2 已处理的稳定性与插件接入问题

| 优先级 | 修复与当前行为 |
| --- | --- |
| P2 | **Settings 故障隔离和退出。** [settings_window.cpp](src/runner/settings_window.cpp) 的通用失败清理不再发 Runner 退出消息；2.0.13 已恢复关闭 Settings 后 Runner 继续托盘运行，正常退出与设置重启分别处理 |
| P2 | **Settings 重开与重启交接。** IPC 指针与 PID 用同一互斥量保护；单个可 join 的生命周期线程持有子进程句柄，并等待退出或关闭事件。关闭标记与创建过程串行化，Runner 在锁外等待线程完成清理，避免退出后又创建窗口；IPC 重启请求发送后由 Runner 关闭旧 UI，再交接新实例，避免异步请求丢失和共享退出事件残留 |
| P2 | **Awake 与官方并存。** [Constants.cs](src/modules/awake/Awake/Core/Constants.cs) 使用 `Local\Kit.Awake`；[AwakeService](src/modules/awake/Awake.ModuleServices/AwakeService.cs) 按 Kit 安装路径和会话确认进程，释放所有候选 `Process` 对象 |
| P2 | **LightSwitch Off 与重复启用。** [dllmain.cpp](src/modules/LightSwitch/LightSwitchModuleInterface/dllmain.cpp) 在 Off 下只保留手动切换监听；Worker 启停统一由该监听线程处理，重复 enable 不重复创建；设置先持久化，再通知监听线程 |
| P2 | **LightSwitch 故障恢复和退出。** 活动计划 Worker 异常退出后，下次热键或 Quick Access 动作恢复调度，后续动作切换主题，沿用上游恢复语义；热键只发事件。Worker 保留提前停止信号，启动时检查父进程及停止状态，初始化失败返回非零；debounce 可取消，销毁先停止回调、join 再关闭事件 |
| P2 | **GPO 一致性。** [gpo.h](src/common/utils/gpo.h) 全部兼容入口返回 `not_configured`，删除 Runner 的局部跳过宏及强制策略分支，UI/Worker 共享同一结果 |
| P2 | **模板与规范。** 源码、ZIP、元数据及构建依赖同步；补全 Library 序列化、EnabledModules converter、导航、资产保留、路径隔离与测试接入说明 |

### 12.3 已处理的启动与依赖开销

| 项目 | 当前行为与边界 |
| --- | --- |
| 活动模块列表 | 显式注册 Awake/LightSwitch/Localserver 三个接口 DLL；Localserver 的页面、采样与管理对象按导航需要创建，未新增目录发现扫描 |
| Quick Access 延迟 | 开启或修改热键均不提前启动；首次展示通过受管理的线程池任务创建进程，重复请求合并，停用会取消排队工作并等待活动回调收尾，低级键盘钩子不执行进程冷启动 |
| 自动更新检查 | 移除启动调用、周期线程、失败重试和更新 toast；只保留明确的手动检查及发布页入口 |
| 无关旧清理 | 移除旧摄像头注册清理函数、调用及无人引用的头文件，同时删除无效更新后通知等待线程 |
| 开机启动任务 | [auto_start_helper.cpp](src/runner/auto_start_helper.cpp) 只访问 `\Kit`；已有任务的程序路径、参数和运行级别不变时不重新注册，禁用的匹配任务只改启用状态；配置变化原位更新，失败保留旧任务；拒绝覆盖无法确认属于 Kit 的任务 |
| 元数据缓存 | 删除未被调用方使用的 wrapper 缓存与 getter，继续使用模块接口；移除未经测量的毫秒节省注释 |
| Monitor 遗留依赖 | 删除 Settings 的 `Microsoft.Data.Sqlite` 引用、中央版本 pin 和 NOTICE 的对应包条目；不清理其他功能需要的依赖 |
| 性能验收 | Runner 计时覆盖到 WinMain；仍需按第 9 节分别采样 Settings、Quick Access 和 Worker 就绪时间，本轮不宣称百分比或毫秒收益 |

### 12.4 验证结果与剩余边界

2026-09-14 完成 `2.0.15` x64 Debug 构建与交付。日志接入和布局修改在升版前通过 Settings 190 项、业务契约 24 项、日志接入 10 项检查；以下为升版后的最终产物验证：

| 验证范围 | 实际结果 |
| --- | --- |
| 完整 `Kit.slnx` x64 Debug 构建 | 成功，0 错误；保留 55 条 `LocalserverLib.csproj` 既有警告，包含 AOT/裁剪、取消令牌和静态分析警告。此结果不代表 NativeAOT 发布验证通过 |
| Settings 与 Awake 测试 | 201/201 通过，其中 Settings 190 项、Awake 两个测试工程合计 11 项 |
| 原生 GPO 兼容测试 | 3/3 通过；仅运行 `GpoTests`，不把其他原生测试计为已验证 |
| Localserver 业务契约 | 24/24 通过，引用本次 `LocalserverLib.dll`，覆盖配置来源、并发保存、跨来源重排、秘密迁移、备份恢复、环形缓冲和实际端口变量；只操作测试临时目录 |
| Kit 日志接入 | 10/10 通过，使用真实 ManagedCommon Logger 与隔离 Trace listener，覆盖多服务、脱敏事件、截断、过载、写入异常、共享缩进恢复、非阻塞释放和退出等待；未启动真实服务或读取用户日志 |
| 语言与 XAML 资源 | 源码中英文各 212 个 Localserver 资源键、52 个 XAML Uid、137 个源码 literal 键通过匹配校验；最终 PRI 的 212 项均包含两种语言，旧日志/诊断折叠栏资源已删除 |
| Debug 交付 | `bin/debug/2.0.15`，1330 个文件、24 个 PDB；12 个产品二进制及 Sparse 包版本一致，运行依赖缺失为 0，复制前后 SHA-256 全部一致。保留已有旧版本交付目录 |
| 运行与界面 | 已确认 Runner 与 Settings 均从 `bin/debug/2.0.15` 启动。Windows 自动化仍出现同一窗口归属错误，未将卡片布局、缩放和完整交互计为通过；需用户实际反馈 |

构建、TRX、两组隔离回归、PRI dump 和文件哈希记录位于 `TestResults/LocalserverReview`。这些结果验证当前 Kit 产物，不用原项目测试数量代替本轮结果。

剩余边界：上游官方 DLL 的 ABI、Run/CmdPal 宿主没有新增兼容承诺；LightSwitch 既有同步主题广播可能延长禁用时的线程等待；WinUI/Mica 多主题、缩放、无障碍和完整语言/权限重启仍需运行期验收。没有为了这些后续事项新增插件发现框架、后台健康轮询或跨平台层。

### 12.5 Localserver 移植要求与当前边界

- **语言与状态分离。** XAML 文案使用 `x:Uid`，动态文案使用现有 `GetLocalized()`；分别在 `Strings/en-us/Resources.resw` 和 `Strings/zh-CN/Resources.resw` 配齐键及格式化参数。服务状态、颜色、可执行动作由枚举控制，不根据翻译后的文本判断。用户服务名、真实命令和标准输出/错误流保持原文。
- **生命周期。** 页面使用导航缓存；离页停止硬件采样，返回后恢复。服务日志转发覆盖所有已加载服务，不依赖当前页面或所选服务。缓存页只在 Settings 真正关闭时释放，取消关闭/隐藏不触发 Dispose；异步采样和环境检查防重入，并在返回 UI 前复核页面和选中服务。模块总开关约束启动与重启；**当关闭总开关时，不仅页面整体暗化置灰，还会同步触发 `StopAllLinesAsync()` 停止所有正在运行的服务与后台采样，并将状态重置为中性禁用态。RecentBars 状态进度条采样对齐为每秒 1 次（1s 节拍），条高为 20px（+20%）。**
- **日志。** 当前页面保留有界日志预览，复用采样节拍增量刷新，最多保留 2000 行；持久化统一走 Kit 日志链路。只订阅 `ServiceRunner.LogAppended` 已脱敏的 `LogLine`，经过有界后台队列批量转发至 Kit 的 `ManagedCommon.Logger`；保留时间、流类型、服务 Id 和序号。当前队列上限 512 条，正文/服务 Id 分别限 8192/256 字符并标记截断；活跃输出按 100 ms 合批，每批最多 128 条、65536 字符，空闲时不轮询。不能在 stdout/stderr 读取线程逐条同步刷盘，也不新增每行 Task 或 UI Dispatcher 操作。队列过载汇总丢弃数量；写入失败只记录计数并恢复 Trace 缩进，不泄露异常正文或中断服务输出捕获。保留核心 `LogRingBuffer` 和健康匹配流程，落盘队列不参与健康判断。重载、删除、最终释放均解绑事件；Dispose 非阻塞结束队列，进程退出最多等待 500 ms，异常退出不保证最后的排队记录落盘。
- **配置持久化。** 复用 `LoadAndMigrateSecretsAsync` 处理秘密值，保存使用 `UpdateAsync(expectedCatalog)` 保留 `services.d` 来源及并发校验。配置恢复、保存冲突和操作失败显示本地化提示；显式保存的空目录应保持为空，不反复生成示例。
- **服务链接。** 复用 `VariableExpander` 根据实际端口和配置展开模板，只有有效的 HTTP/HTTPS URI 才显示打开入口。
- **终止进程。** 强制终止和释放端口先显示明确目标及取消按钮。端口释放复用 `PortInspector.ReleaseAsync`，同时核验 PID、创建时间和当前监听端口；不能仅凭过期 PID 调用 Kill。
- **界面。** Health（健康状态）与 System（系统资源）在启用开关下方常驻，位于服务清单之前，不放进折叠栏。服务选择器属于 Health 卡；两卡内容区宽度达到 720 DIP 时并列，较窄时纵向排列，自动高度避免裁切。移除本页顶部介绍图以减少留白；服务清单与启停操作放在状态卡之后，工具栏可换行。复用宿主 Mica、主题色和字体；编辑区在服务运行时锁定，命令支持选择复制，空服务清单有明确提示。
- **并存。** 数据与恢复记录只使用 Kit 模块目录。移植保留的 `Local\LocalServerHub.Service.<hash>` Job 名包含服务 Id 与每次启动新建的随机 owner tag，正常独立启动不会仅因前缀相同复用原项目的 Job；仍拒绝已存在的命名 Job。新模块继续遵守第 4.4 节的 Kit 命名约定。

当前没有恢复原项目的独立主窗口、托盘或文件日志宿主，也未新增通用插件发现、后台服务或健康巡检框架。原项目测试只有在引用当前 Kit 产物、限制在测试目录并排除真实服务操作后，才能作为这次移植的验证证据。

### 12.6 Gemini 工作区复核与 2.0.20 Debug

本轮从 `2.0.19` 的实际源码继续复核，首次完整构建出现 544 个错误、3 个警告，不能沿用旧签署中的“全绿”结论。已修复 SettingsAPI 命名空间、Awake 重复 Interop 投影、EXE/PRI 名称、原生接口虚表槽位、加载器缓冲区、Settings IPC 线程与退出清理、Localserver 停止和取消流程，并将搜索索引改为首次查询时建立。

- 模板现在使用 `KitModuleIface` / `kit_create` / `KitSettings`，VS 显示名为 **Kit Module Template**；ZIP 的 11 个条目与源码校验一致。
- Localserver 日志预览在后续版本已恢复。本轮限制为每 2 秒增量刷新、最多 2000 行；真实日志继续走 Kit 有界日志队列。文件夹入口指向 `ManagedCommon.Logger.CurrentVersionLogDirectoryPath`，不创建另一套独立服务日志存储。
- Localserver 正常关闭窗口前会异步保存待提交编辑；有运行、启动、停止或重试中的服务时隐藏 Settings 并保留宿主与输出管道。它尚无独立后台 Worker，完整退出/重启/崩溃后的管道服务连续运行不在保证范围。
- LightSwitch 的配置和运行状态改为锁内快照。原生生命周期回归覆盖反复启停、崩溃后恢复、快速模式切换和销毁；真实 Worker 的预先停止和父进程绑定错误也已验证。
- 最终七套测试合计 319 项通过、1 项因测试环境无法创建目录符号链接而跳过：Settings 190、Localserver 生命周期 7、Awake 核心 8、ModuleServices 3、GPO 3、Interop 1、AI Hub 107。独立 Native AOT 冒烟通过；完整 Debug 构建为 0 errors / 50 warnings。独立 `bin/debug/2.0.20` 的依赖与资源校验、Runner 三次启动和 Settings 进程启动检查通过。完整证据、告警和交付清单见 [fixed.md 第 11 节](fixed.md#11-本轮源码复核与验证记录2026-09-14)。
- LocalserverLib 仍有反射 JSON 及 WinRT/裁剪分析告警。AI Hub 的 AOT 结果不能用于宣称整个 WinUI 应用或 LocalserverLib 已完成 AOT 验收，也不能通过新增全局 NoWarn 隐藏差距。
- Windows 自动化不能正确绑定当前 Kit 窗口，实际布局、展开收起、关闭保存和隐藏后恢复仍需桌面反馈。本轮未改动参考源码或开展新插件功能。

原生回归工具位于 `tools/tests/NativeModules/`。`Measure-RunnerStartup.ps1` 只测 Runner 后台初始化，并用本轮创建的精确进程句柄测试父进程退出联动；该耗时不代表 WinUI 首屏时间。交付由 `tools/build/Stage-Debug.ps1` 从当前版本产物生成，保留旧 Debug 目录，校验产物版本、依赖、资源和 SHA-256。

### 12.7 UDP Test 模块设计与生命周期规范

- **状态条容量与尺寸。** 探针行右侧 RecentBars 容量由 30 扩充为 60 条（倍增历史样本宽度），成功探针高度对齐为 20px（失败为 8px）。
- **开关同步停止。** 顶部主开关关闭时，`IsEnabled` setter 同步触发 `_ = StopAsync()` 停止所有后台探针 Worker，同时 `PollNetworkIdentityAsync` 挂起外网身份追踪，会话计时器立即停止。
- **选项锁定与灰化。** 开关关闭时，整页内容容器绑定 `Opacity=0.38`，`CanStart`、`CanStop`、`CanClear` 全部锁死为 `false`，状态灯与卡片重置为中性禁用态。

## 13. 上游文档与 Monitor 经验的使用方式

以下文档作为设计与源码索引。与本规范或当前实现冲突时，先核对相应代码，不机械执行旧文档中的路径、删除命令或固定测试数量。

| 来源 | 可复用内容 | Kit 适配要求 |
| --- | --- | --- |
| [上游 new-powertoy.md](source/PowerToys/doc/devdocs/development/new-powertoy.md) | 模块接口、Worker、设置页整体流程 | 部分 `runner/modules.h/.cpp` 等注册路径已不对应当前 Kit；改用第 6 节，不恢复 WiX/OOBE/遥测 |
| [Settings 架构](source/PowerToys/doc/devdocs/core/settings/ui-architecture.md) | WinUI / Page / VM 分工 | 使用当前 Kit 导航、两语言资源与 Mica 主题 |
| [设置实现](source/PowerToys/doc/devdocs/core/settings/settings-implementation.md)与[设置工具](source/PowerToys/doc/devdocs/core/settings/settings-utilities.md) | DTO、配置读写、仓储 | 使用 Kit 根路径、Library source generation 与当前实际 API |
| [模块通信](source/PowerToys/doc/devdocs/core/settings/communication-with-modules.md)与[Runner IPC](source/PowerToys/doc/devdocs/core/settings/runner-ipc.md) | JSON 消息与配置分派 | 保留协议字段，按 Kit 当前命名事件与进程生命周期验证 |
| [Run 插件清单](source/PowerToys/doc/devdocs/modules/launcher/new-plugin-checklist.md) | Run 宿主的插件开发方式 | 当前不可直接用于 Kit 原生模块，必须先有对应宿主 |
| [Kit first-plugin](doc/devdoc/kit-first-plugin.md) | 原生模块优先、注册及构建经验 | 文中的 `knownModules` / `src/kit` 是旧写法，当前为 `KitKnownModules` / 仓库根 |
| [Kit 开发经验](doc/devdoc/kit-development-experience.md) | 生命周期、状态/配置分离、WinMD、注册一致性 | Monitor、PowerDisplay、旧版本号和测试数量属于历史，不能当作当前活动结构 |
| [历史 devdoc 指南](doc/devdoc/AGENTS.md) | 上游小差异、显式列表、构建纪律 | 当前活动模块以本规范和源码为准：Awake/LightSwitch/Localserver |
| [原 Localserver 项目](../Localserver) | 配置来源回写、进程所有权、日志批处理与环境检查 | 复用现有逻辑并接入 Kit 语言和目录；不恢复独立窗口、托盘或另一套主框架 |

同步上游时，在插件变更说明中记录对应 commit、功能变化、Kit 必须保留的路径/事件/遥测裁剪、相关测试及未验证场景。同步目标是所需功能和契约正确，不是把已剔除的框架职责重新搬回 Kit。

## 14. AI Hub：原生模块、任务策略编排管线与移植约束

AI Hub 在 `2.2.0` 版本中已演进为完整的原生第一方模块：拥有原生模块接口 DLL（`Kit.AIHubModuleInterface.dll`，在 Runner `KitKnownModules` 中注册）、独立二级设置主页（`AIHubPage.xaml`，包含“安全审计”与“服务设置”两大功能标签页），并由底层共享库 [Kit.AiHub.csproj](src/common/AiHub/Kit.AiHub.csproj)、[IAiTaskEngine](src/common/AiHub/Models/AiTaskContracts.cs)、[TaskAiEngine](src/common/AiHub/Engine/TaskAiEngine.cs) 提供统一的高性能任务执行引擎。

全局启用状态通过 `GeneralSettings.Enabled.AIHub` 与 `%LOCALAPPDATA%\Kit\AiHub\settings.json` 同步联动。底层项目跟随 Kit 的 `net10.0-windows10.0.26100.0` 和集中包版本管理；JSON 序列化必须使用 source-generated metadata（禁止反射）。共享库的 Native AOT smoke 与整个 WinUI Settings 应用的发布方式独立验证，不宣称未测量的性能数据。

### 14.1 三种获取方式

| 插件形态 | 获取方式 | 生命周期与前提 |
| --- | --- | --- |
| Settings 内的托管页面 | `AiHubEngine.Current`，或由构造函数注入 `IAiTaskEngine` | 共享实例由宿主关闭时释放；插件只解绑自己的 `StateChanged`，不能 Dispose 单例 |
| 独立托管 Worker | `using IAiTaskEngine engine = AiHubEngine.Create();` | 同一 Windows 用户自动共享配置、DPAPI 凭据及内核；Worker 释放自己的实例，不需要 Settings 窗口常驻 |
| Runner 内原生 C++ DLL | [ai_hub_client.h](src/common/interop/ai_hub_client.h) 的 `kit::ai::request` | 在插件 Worker 线程调用；需要 Kit Settings 作为托管宿主运行；使用既有 Runner／Settings 双向命名管道 |

`AiHubEngine.Create(customDataDirectory, pluginPackagesDirectory)` 可用于测试或明确指定的独立数据根。配置、凭据、安全策略与请求临时目录属于前者；插件策略只从后者按 ModuleKey 查找。测试必须传入独立目录，不能读取真实用户的端点或密钥。

所有调用在关闭状态返回 `AiErrorCode.HubDisabled`，不探测或启动 CLI。配置更改会通过文件通知刷新 Worker 的 `StateChanged`；执行前再次读最新配置和凭据，关闭开关会取消进行中的任务。事件可能来自后台线程，UI 更新必须转发到自己的 `DispatcherQueue`。每个引擎按 `maxConcurrentAnalysis` 限制同时运行的批次，范围 1–4；它不是多个 Worker 合计的全局配额。

### 14.2 任务策略编排管线架构 (Task Policy Pipeline Pattern)

AI Hub 规定了统一、严格的 AI 任务执行管线：
```text
┌─────────────────┐     ┌──────────────────────────────────────────────┐     ┌──────────────────┐     ┌──────────────────┐
│ Execution Kernel│ ──> │ Security Policy (全局) + AGENTS.md (任务约束) │ ──> │ Analysis Summary │ ──> │ Suggested Action │
│ (Codex / Pi CLI)│     │ (%LOCALAPPDATA%\Kit\AiHub\security.md 等)   │     │ (风险/根因多语种) │     │ (PriorityAction) │
└─────────────────┘     └──────────────────────────────────────────────┘     └──────────────────┘     └──────────────────┘
```

#### 1. 分层策略体系与隔离边界
- **全局安全策略 (`security.md`)**：位于 `%LOCALAPPDATA%\Kit\AiHub\security.md`，面向用户定义整个 Kit 平台内所有 AI 任务必须遵守的全局安全红线（如禁止明文凭据外发、禁止直接生成无确认的破坏性 shell 指令、输入条目限制）。常规设置页中提供专属卡片供用户查看、编辑和一键恢复默认。
- **特定任务约束策略 (`chains/{task}/AGENTS.md`)**：位于插件或模块的 `Chains/<taskId>/AGENTS.md`（如 `diagnostic`、`security-audit` 等），用于细化特定业务场景的模型行为模式、输出 JSON 模式约束、重点审查领域及风险判定规则。
- **自动脚手架保障（Zero-Crash Scaffolding）**：由 `SecurityPolicyService.cs` 与 `TaskPolicyDefaults.cs` 统一管理。若目标策略文件在用户数据目录或部署目录中尚未创建，服务层会自动根据内置安全模板创建安全基线脚手架，防范由于文件缺失引发的 I/O 异常与启动崩溃。

#### 2. 插件策略目录与部署
```text
src/modules/Sample/SampleLib/
  Chains/
    diagnostic/
      AGENTS.md
      security.md
    config-review/
      AGENTS.md
      security.md

<Kit install directory>/
  modules/Sample/Chains/diagnostic/AGENTS.md
  modules/Sample/Chains/diagnostic/security.md
```

`pluginId`、`taskId` 必须是受限的 ASCII 单段标识，不接受绝对路径、斜杠、`..` 或重解析点。不同插件的同名任务互相隔离。缺少任务策略即拒绝执行，不隐式选择另一个任务。
在插件库中将资源部署到统一位置，不能依赖开发机的源码绝对路径：

```xml
<ItemGroup>
  <ProjectReference Include="$(RepoRoot)src\common\AiHub\Kit.AiHub.csproj" />
  <Content Include="Chains\**\*.md">
    <TargetPath>modules\Sample\Chains\%(RecursiveDir)%(Filename)%(Extension)</TargetPath>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </Content>
</ItemGroup>
```

最终 Debug／发布目录必须实际包含这些文件；只在源码目录中存在不算完成部署。原生插件如需相同策略，也部署到这一路径，不复制解析器。

#### 3. 审计发现与动态多语种建议契约 (Dynamic Localization Contract)
- 结构化审计结果（`Finding`）必须提供标准化指标：`EventId`、`Scope`、`Severity`、`DetectionFacts`（检测事实）、`RootCause`（根因分析）以及 `PriorityActionText`（首要处置建议）。
- **动态语言跟随**：ViewModel 与审计弹窗展示时，必须动态适配当前系统的 UI 语言（`CultureInfo.CurrentUICulture.Name`），确保检测事实与操作建议随系统语言自适应呈现，严禁写死单语言字符串。

### 14.3 强类型调用与 AOT

泛型类型不能由共享库预先猜测。调用方必须传 `AiTaskSchema<TInput,TOutput>`，其中包含插件自己的 `JsonTypeInfo<TInput>`、`JsonTypeInfo<TOutput>` 与必需的 `ValidateOutput`。超过一批的数据还必须提供 `MergeBatches`。禁止调用无 metadata 的 `JsonSerializer.Serialize<T>` / `Deserialize<T>` 作为兜底。

常规诊断可直接复用 [AiTaskReport](src/common/AiHub/Models/AiTaskReport.cs) 的双语 finding 和只读建议契约：

```csharp
using System.Text.Json.Serialization;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;

public sealed record ServiceInput(string State, bool? Healthy);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServiceInput))]
internal sealed partial class PluginJsonContext : JsonSerializerContext
{
}

public static class Diagnostics
{
    public static Task<AiTaskResult<AiTaskReport>> RunAsync(
        IAiTaskEngine engine,
        IReadOnlyList<ServiceInput> snapshot,
        CancellationToken cancellationToken)
    {
        // Select the private task chain and provide compile-time JSON metadata.
        return engine.ExecuteTaskAsync(
            pluginId: "Sample",
            taskId: "diagnostic",
            items: snapshot,
            schema: AiTaskReport.CreateSchema(PluginJsonContext.Default.ServiceInput),
            options: new AiTaskOptions { Language = "en-US", TimeoutSeconds = 120 },
            cancellationToken: cancellationToken);
    }
}

// In-process use: the host owns this singleton.
IAiTaskEngine shared = AiHubEngine.Current;

// Independent worker use: this worker owns the returned instance.
using IAiTaskEngine worker = AiHubEngine.Create();
```

示例最后两种获取方式放在调用方的方法体中。`Language` 应传 Kit 当前 UI 语言，只有 `en-US` / `zh-CN`；状态逻辑和错误处理使用 `AiErrorCode`，不能解析英文 `ErrorMessage`。

引擎将输入包装为 `{ "itemId": "item-000001", "data": ... }`；编号在一次调用内按输入顺序固定，跨批次不重排。插件保留本地快照，用编号映射回自己的服务名或对象，避免将名称、绝对路径或业务 ID 当成模型引用。结果只能引用当前批次的编号。输入先逐字段、逐字符串脱敏，再序列化发送，不能对已转义的整个 JSON 做正则替换。

自定义输出类型应使用 `JsonUnmappedMemberHandling.Disallow` 和必要的 required 属性。插件的 validator 必须检查必填内容、双语字段、重复引用和业务约束；合并函数返回后还会再校验。`AllowedActions` 默认仅 `skip`；需要 move/delete/restart 建议的插件必须明确声明并验证目标路径，随后在自己的执行入口重新核实当前对象和用户确认。模型返回的 `isConfirmedByUser`、命令、脚本或越界路径不能成为执行授权。

### 14.4 原生 IPC 契约

原生入口通过 `GetProcAddress` 查找 Runner 的可选 `KitAiHubRequest` 导出，不修改 `KitModuleIface` 的 vtable，也不要求现有 Awake／LightSwitch 增加虚方法。

```cpp
#include <common/interop/ai_hub_client.h>
#include <common/utils/json.h>

// Run on a plugin worker thread, never on the Runner UI or hotkey thread.
json::JsonObject request;
request.SetNamedValue(L"target", json::JsonValue::CreateStringValue(L"AiHub"));
request.SetNamedValue(L"action", json::JsonValue::CreateStringValue(L"ai_task_request"));
request.SetNamedValue(L"pluginId", json::JsonValue::CreateStringValue(L"Sample"));
request.SetNamedValue(L"taskId", json::JsonValue::CreateStringValue(L"diagnostic"));
request.SetNamedValue(L"language", json::JsonValue::CreateStringValue(L"en-US"));
request.SetNamedValue(L"timeoutSeconds", json::JsonValue::CreateNumberValue(120));

json::JsonArray items;
json::JsonObject item;
item.SetNamedValue(L"state", json::JsonValue::CreateStringValue(L"running"));
items.Append(item);
request.SetNamedValue(L"items", items);

std::wstring response;
HRESULT transport = kit::ai::request(request.Stringify().c_str(), response, 130000, cancellationEvent);
// S_OK means a response arrived; inspect isSuccess/errorCode before using payload.
```

Runner 自动写入协议 `version: 1` 和唯一 `requestId`；Settings 的 [AiHubIpcBridge](src/settings-ui/Settings.UI/Helpers/AiHubIpcBridge.cs) 已接入 App 收包入口，交给同一个 `IAiTaskEngine` 执行。响应为 `target: AiHub`、`action: ai_task_response`，保留 requestId，并提供 `isSuccess`、字符串 `errorCode`、`payload`、`usedModel`、`usedRoute`、`elapsedMilliseconds`。原生任务输出采用共享 `AiTaskReport` 契约；`get_status` 可以只查询启用状态和内核，不提交任务。

每个 Runner 最多 16 个待处理请求，发送消息上限 524,288 个 UTF-16 字符，响应上限 1,048,576 个字符。超时或调用方的取消事件会发出关联的 `ai_task_cancel`；宿主退出会唤醒等待线程，不依赖其完整任务超时。Settings 未运行返回 `ERROR_NOT_READY`，缺少入口的宿主返回 `ERROR_NOT_SUPPORTED`；插件应显示可操作的状态，不自行写凭据或启动另一套 AI 配置。此接口供 Kit 进程内可信插件使用，不是对外开放的服务端点。

### 14.5 数据、内核与错误边界

```text
%LOCALAPPDATA%\Kit\AiHub\
  settings.json               # Non-secret configuration
  secrets.dat                 # DPAPI CurrentUser encrypted credentials
  .settings-transaction.dat   # Encrypted recovery journal, only during a transaction
  security.md                 # User-editable global task policy
  kernels\                   # Verified Codex / Pi installations and update staging
  requests\                  # Unique ephemeral CLI homes and task working directories
```

配置和密钥使用同一把按数据目录区分的跨进程 mutex，并以加密恢复记录协调提交。`Update(Action<AiHubConfig>)` 在锁内读取最新快照、只更改目标字段并提交：开关不保存端点草稿，切换内核不覆盖其他 Worker 的配置。失败会报错并保留或恢复旧文件，不吞掉 DPAPI 写入失败；损坏文件不会被当成空配置覆盖。跨会话共用目录需要 `Global\Kit.AiHub.<directory-hash>` 存储锁，这是文件一致性用途，不改变插件常规命名事件的会话范围。

内核版本检查和安装只由用户操作触发，不加入 Kit 启动阶段的网络检查。选择下拉项只更改 pending 值；所选内核安装并验证、已保存端点兼容后，点击 Apply 才提交。Main／Fallback 的启用状态单独保存，Codex 只接受 Responses，Pi 支持 Responses／Chat；Codex 的 `max` 映射到其支持的 `xhigh`，Pi 保留 `max`。模型标识允许手动输入，不能把预设列表当成端点支持的完整模型集合。

只对明确可重试的网络错误、HTTP 408/429/5xx 使用已启用的备用端点。缺少内核、协议不兼容、认证、权限、输出审计失败、用户取消或本地 deadline 不触发备用请求。CLI stdout/stderr 同时有界读取，取消和超时需要回收进程树；探针只有收到严格的 `{ "issues": [] }` 才成功，exit code 0 本身不算连接成功。

`security.md` 编辑器允许保存和恢复默认值；宿主内置的脱敏、引用检查、命令禁用和路径约束不随文本删除而关闭。每份策略最多 64 KiB UTF-8 且全局策略不可为空；输入最多 2,000 项，每项编码后最多 64,000 字节，总量最多 8,000,000 字节。分批同时受条目数和 96,000 字节的数据预算限制；超限拒绝执行，不静默截断业务输入。

### 14.6 Localserver 示例与验证入口

[LocalserverAiService](src/modules/Localserver/LocalserverLib/Ai/LocalserverAiService.cs) 已按按钮选择 `diagnostic` / `config-review` 两个私有任务链。只发送服务状态、健康结果、配置是否存在、检查间隔、超时和重试策略；服务名称留在本地映射，不发送命令、环境变量、凭据、原始日志或路径。结果卡片使用双语字段跟随 Kit 语言，分析流程只提供建议。后续插件可参考这个完整消费链，不需要复制 AI Hub 实现。

参考项目为 `C:\Users\Zen\Repos\Codings\Locals`。重点对照其 `KernelManagerService`、`AiAnalysisService`、`TaskAiClient` 的版本检查、CLI 协议、探针和引用／动作审计。原 Locals 的普通设置文件仍会序列化 API key；Kit 使用独立 DPAPI 存储，不照搬该行为。原项目源码保持只读。

图标沿用第 7 节的 36×36 与 400×266 RGBA PNG，已切换为原项目标志性紫色浅色 Logo（`#A756FF`），实际位于 `Assets/Settings/Icons/AiHub.png` 和 `Assets/Settings/Modules/AiHub.png`，并加入产物裁剪保留清单。所有新增代码及示例注释使用英文，UI 文案放在 en-us／zh-CN 资源文件。

维护回归在 [AiHub.UnitTests](src/common/AiHub.UnitTests/Kit.AiHub.UnitTests.csproj)。使用独立临时目录、合成 CLI 和内存 HTTP handler 覆盖凭据事务、策略边界、脱敏、协议、分批、容灾、取消及 IPC；不得用用户真实端点替代 fixture。Native AOT smoke 位于 [AiHub.AotSmoke](tools/tests/AiHub.AotSmoke/AiHub.AotSmoke.csproj)：

```powershell
.\tools\build\build.ps1 -Platform x64 -Configuration Debug -Path src\common\AiHub.UnitTests
& .\x64\Debug\tests\AiHub\Kit.AiHub.UnitTests.exe --report-trx
dotnet publish .\tools\tests\AiHub.AotSmoke\AiHub.AotSmoke.csproj -c Release -r win-x64 -p:Platform=x64 -p:PowerToysSkipCopyOnWriteSdk=true -p:PowerToysSkipRunVSTestSdk=true -o .\TestResults\AiHubAotSmoke
& .\TestResults\AiHubAotSmoke\AiHub.AotSmoke.exe
```

最终交付还要构建 Settings 和 Runner，检查两套资源的 PRI、插件 Chains 和图标是否进入交付目录。模拟内核验证不能替代用户端点的真实兼容性反馈；不以正常 Debug build 代替 AOT publish，也不以单元测试代替 WinUI 视觉验收。
