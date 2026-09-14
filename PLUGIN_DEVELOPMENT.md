# Kit 插件与模块开发规范

本规范面向 Kit 主框架和新增模块开发。优先顺序是安全、正确性、最小改动与轻量化；界面复用现有 WinUI 3 + Mica Alt 设置框架。

**核查基线：2026-09-13，Kit `3524ba3` / `2.0.10` 及本轮工作区修复；本地 `source/PowerToys` 为 `f47af41de2837a9d588f84270ad521c26264ab40`，release train `0.101`。** 这里的“上游”指这个本地快照，不代表已核对 GitHub 后续提交。第 12 节记录代码 review、修复与验证，其余章节规定新增模块的接入方式和验收要求。

## 1. 支持范围与上游兼容边界

Kit 当前活动模块只有 **Awake、LightSwitch**。本文中的“插件”默认指由 Runner 加载的原生模块接口 DLL，以及它管理的业务进程。

| 接入方式 | 当前支持程度 |
| --- | --- |
| PowerToys 原生模块源码 | 保留 `PowertoyModuleIface`、`powertoy_create`、配置 IPC、热键、设置页和 Worker 模型；移植时使用 Kit 的头文件与依赖重新编译，并完成显式注册 |
| 官方现成 ModuleInterface DLL | **不承诺二进制兼容，也不支持拖入目录自动安装**；Kit 接口比本地上游少了两个虚函数，虚表布局不同 |
| C# / C++ Worker | 支持，由原生 ModuleInterface 启动；Worker 自身不是 Runner 可直接加载的 DLL |
| PowerToys Run 的 `IPlugin` / `plugin.json` | 当前没有 Run / Launcher 宿主，不能直接运行 |
| Command Palette 扩展 | 当前没有 CmdPal 宿主，不能直接运行 |
| `Awake.ModuleServices` / `PowerToys.ModuleContracts` | 保留适配源码并由 Awake 测试引用；`IModuleService` 面向 CmdPal，当前没有对应运行宿主，不是 Kit 的托管插件发现接口 |

[Kit 接口头](src/modules/interface/powertoy_module_interface.h) 相比[上游接口头](source/PowerToys/src/modules/interface/powertoy_module_interface.h) 删除了 `keep_track_of_pressed_win_key()` 和 `milliseconds_win_key_must_be_pressed()`。导出函数同名不代表 ABI 相同；所有进程内模块必须与当前 Kit 接口、目标架构和工具链一起验证。

[Runner](src/runner/main.cpp) 的 `KitKnownModules` 与 [KitModuleCatalog](src/settings-ui/Settings.UI.Library/Helpers/KitModuleCatalog.cs) 是活动模块入口。保留的历史枚举、DTO 或文档不是模块已启用的依据。上游 Runner 本身也使用显式模块列表，Kit 的精简是把列表及相关界面缩到两个模块。

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
- 新 Kit 自有模块可命名为 `Kit.SampleModuleInterface.dll`、`Kit.Sample.exe`；复制的官方模块继续保留既有 `PowerToys.*` 程序集和命名空间，避免无关重命名。加载清单必须填写真实产物名。
- 不要求每个插件新增库、数据库、独立 UI、注册表、GPO 或服务接口。只引入当前功能需要的项目和依赖；通常复用现有设置进程即可。

### 模板使用边界

优先参考当前 [AwakeModuleInterface](src/modules/awake/AwakeModuleInterface/dllmain.cpp) 和 [LightSwitchModuleInterface](src/modules/LightSwitch/LightSwitchModuleInterface/dllmain.cpp) 的具体接入方式，并结合第 12 节的已知问题。

`tools/project_template/ModuleTemplate/` 是模板源码；`$projectname$` / `$safeprojectname$` / `$guid1$` 由 Visual Studio 实例化。项目必须位于 Kit 仓库内，以继承 `RepoRoot`、工具链和依赖配置；生成的接口 DLL 输出到 Runner 同级目录，仍须完成第 6 节的显式注册。

本轮已同步 [ModuleTemplate.zip](tools/project_template/ModuleTemplate.zip) 与目录源码，补齐 SettingsAPI、CppWinRT、`packages.config` 和可审核的 [MyTemplate.vstemplate](tools/project_template/ModuleTemplate/MyTemplate.vstemplate)，删除旧遥测与失效依赖。[模板 README](tools/project_template/README.md) 说明安装和实例化方法。根 `Kit.slnx` 已包含 `ModuleTemplateCompileTest`，输出到 `x64/Debug/tests/ModuleTemplate/`；编译骨架与通过 VS 新建项目是不同验证，结果见第 12 节。

## 3. 原生接口、热键与生命周期

以 [powertoy_module_interface.h](src/modules/interface/powertoy_module_interface.h) 为契约源，不在新项目内复制、删改一份接口声明。必要导出形式为：

```cpp
#include <interface/powertoy_module_interface.h>

extern "C" __declspec(dllexport)
PowertoyModuleIface* __cdecl powertoy_create();
```

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

[加载器](src/runner/powertoy_module.cpp) 实际执行 `LoadLibraryW → GetProcAddress("powertoy_create") → create()`，退出时通过 [PowertoyModuleDeleter](src/runner/powertoy_module.h) 销毁对象，再卸载 DLL。工厂返回初始禁用对象；初始化失败可返回 `nullptr`。即使模块配置为禁用，DLL 仍会加载，因此构造函数也必须保持轻量。

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

当前 Kit 在未打开辅助窗口时，关闭主 Settings 窗口会异步请求 Runner 退出，继而结束插件；存在辅助窗口时则隐藏主窗口，见 [MainWindow.xaml.cs](src/settings-ui/Settings.UI/SettingsXAML/MainWindow.xaml.cs)。Settings 创建失败或崩溃不等同用户退出，不再联动结束宿主。正常关闭、语言/权限重启和故障清理必须分别处理；新增模块不能假定用户关闭设置后仍继续常驻。

## 4. 配置、日志与数据隔离

### 4.1 当前路径与新增目录约定

**产品根目录为 `%LOCALAPPDATA%\Kit`；插件按固定 ModuleKey 分目录。** 沿用现有平铺布局，不另加 `Plugins` 层，也不迁移现有两个模块的数据。

```text
%LOCALAPPDATA%\Kit\
├── settings.json                       # Runner 全局配置与模块开关
├── log_settings.json                   # 框架日志配置
├── settings-placement.json             # 设置窗口位置
├── RunnerLogs\
│   └── runner-log.log                  # Runner 原生日志
├── Settings\Logs\<assembly-version>\    # Settings 托管日志
├── Awake\
│   ├── settings.json
│   └── Logs\                           # 原生接口日志和托管版本日志
├── LightSwitch\
│   ├── settings.json
│   ├── ModuleInterface\Logs\<version>\
│   └── Service\Logs\<version>\
└── Sample\                             # 新模块目录约定，按需创建
    ├── settings.json                   # 用户配置
    ├── State\                          # 可选：运行状态，不混入配置
    ├── Cache\                          # 可选：可重建缓存
    └── Logs\                           # 可选：本模块日志
```

上图未列出所有框架辅助文件。`Sample\State`、`Cache` 是新增数据有需要时才采用的约定，不代表框架已经实现通用存储服务。

| 数据或资源 | 位置与边界 |
| --- | --- |
| 模块用户配置、业务内部状态、缓存、日志 | `%LOCALAPPDATA%\Kit\<ModuleKey>\...`；模块不得清理相邻模块或整个 Kit 根目录 |
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
using Microsoft.PowerToys.Settings.UI.Library;

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

## 5. 设置模型、序列化与 IPC

### 5.1 最小有效模型

下例放入 Settings.UI.Library，对应 `name/version/properties` JSON 结构；实际属性按插件需要定义。`Version` 是配置结构版本，不能据此判断 Worker 是否存活。

```csharp
using System.Text.Json.Serialization;
using Microsoft.PowerToys.Settings.UI.Library.Interfaces;

namespace Microsoft.PowerToys.Settings.UI.Library;

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

外层协议仍使用上游字段名，不能因为品牌为 Kit 就单方面重命名 `powertoys`：

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
| 原生加载 | [main.cpp](src/runner/main.cpp)：`KitKnownModules` 添加真实 DLL 文件名；当前两个官方接口 DLL 均部署在 Runner 旁 |
| 模块身份 | [ModuleType.cs](src/common/ManagedCommon/ModuleType.cs)、C++ Key、Settings.ModuleName、JSON key 保持一致 |
| 全局开关 | [EnabledModules.cs](src/settings-ui/Settings.UI.Library/EnabledModules.cs) 添加属性/default/通知；[EnabledModulesJsonConverter.cs](src/settings-ui/Settings.UI.Library/EnabledModulesJsonConverter.cs) **同时增加 Read 和 Write** |
| 活动列表与 CLI | [KitModuleCatalog.cs](src/settings-ui/Settings.UI.Library/Helpers/KitModuleCatalog.cs)：ActiveModules、ActiveSettingsModuleKeys、ActiveEnabledModuleKeys；DashboardModules 当前复用 ActiveModules |
| 标签、图标和状态映射 | [ModuleHelper.cs](src/settings-ui/Settings.UI.Library/Helpers/ModuleHelper.cs)：标签资源、图标、读开关、写开关、Key 五处映射 |
| 配置与 IPC | 第 5 节的 Settings/Properties/Snd DTO 与 Library context；UI context 按实际调用补充 |
| 页面与导航 | Page、VM、资源；[ShellPage.xaml](src/settings-ui/Settings.UI/SettingsXAML/Views/ShellPage.xaml) 添加导航项；[ShellPage.xaml.cs](src/settings-ui/Settings.UI/SettingsXAML/Views/ShellPage.xaml.cs) 调用 `NavHelper.SetNavigateTo(..., typeof(SamplePage))` |
| 字符串路由与页面映射 | [App.xaml.cs](src/settings-ui/Settings.UI/SettingsXAML/App.xaml.cs) 的 `GetPage(string)`；[ModuleGpoHelper.cs](src/settings-ui/Settings.UI/Helpers/ModuleGpoHelper.cs) 的页面类型映射 |
| 类型化深链 | [settings_window.h](src/runner/settings_window.h) 枚举、[settings_window.cpp](src/runner/settings_window.cpp) 字符串双向映射、[SettingsDeepLink.cs](src/common/Common.UI/SettingsDeepLink.cs) |
| Dashboard 内容 | [DashboardViewModel.cs](src/settings-ui/Settings.UI/ViewModels/DashboardViewModel.cs) 的模块内容分派；展示真实状态，有动作才展示动作，无动作打开设置页 |
| 图标部署 | [PowerToys.Settings.csproj](src/settings-ui/Settings.UI/PowerToys.Settings.csproj) 的大小图 **Exclude 保留清单**，以及 [Quick Access 工程](src/settings-ui/QuickAccess.UI/PowerToys.QuickAccess.csproj) 的小图标 Content 与 Exclude；两个工程共享 `WinUI3Apps` 输出，即使新模块没有快捷动作也必须同步，否则后续构建会删除新图标 |

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
- 卡片颜色通过 [Colors.xaml](src/settings-ui/Settings.UI/SettingsXAML/Themes/Colors.xaml) 的 ThemeResource 获取。当前 `CardBackgroundFillColorDefaultBrush` 为浅色 `#80FFFFFF`、深色 `#0AFFFFFF`；描边分别为 `#18000000`、`#1AFFFFFF`。这些是基线值，不应复制成每页硬编码。
- [Card.xaml](src/settings-ui/Settings.UI.Controls/Primitives/Card.xaml) 已将外层背景设为透明，由内层 Grid 绘制一次背景和边框。不要再叠一层不透明白底或重复描边。
- 高对比度、关闭透明效果、窗口失焦时使用系统支持的回退，保证文字可读；不把截图中的 Mica 颜色当成固定颜色值。
- 复用已有间距、圆角、文字层级和窗口标题栏。新页面不显示程序集名、IPC、日志路径等实现细节，除非是用户需要的诊断操作。
- 设置操作在 UI 线程快速完成；较长工作显示真实进度、取消入口和错误状态，避免同步 IO 卡住窗口。配置变化应立即得到明确反馈，不能只改开关视觉而 Worker 状态不变。
- 为交互控件提供可访问名称、键盘焦点和合理 Tab 顺序；标题使用现有 HeadingLevel。装饰图片保持 AccessibilityView.Raw。
- 至少检查浅色/深色/高对比度、100%/150%/200% 缩放、中英文、480 DIP 最小窗口及宽窗口、键盘操作与 Narrator。本次仅核对 XAML，未完成这些运行期视觉验收。

## 9. 轻量启动、隐私与依赖边界

### 9.1 新模块不得增加的默认启动职责

- 不递归扫描插件目录、磁盘或所有历史 Settings 类型来发现模块；沿用显式名单。
- 不在 DLL 构造、元数据查询、主窗口构造或全局热键回调中进行网络请求、业务扫描和等待 Worker 就绪。
- 禁用模块不得启动业务进程、计时器或轮询任务；按需功能只在真实使用时启动。
- 不引入自动更新下载/安装、后台版本检查、OOBE、旧 PowerToys 迁移、AI 环境检测、无关 GPO 或遥测职责。
- 新代码不注册行为遥测 Provider、不发送事件、不增加 ManagedTelemetry / EtwTrace 依赖。移植代码确需保留 Trace 调用形状时，用小范围 no-op 维持源码兼容，不要求每个全新插件都创建无用的 `trace.h/.cpp`。
- 共享库、包与版本优先复用当前工程；新增依赖必须说明当前用途。移除依赖前检查实际引用和产物，不能把源目录还在等同于库参与启动。

当前活动 Runner、Awake、LightSwitch 的 trace 入口已为空实现或删除，旧 `common/Telemetry` 源目录仍存在。本轮移除周期版本检查及重试线程，仅保留用户在 Settings 主动触发的版本查询、结果状态和发布页入口，不下载或安装更新。

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
- 使用 `PowerToys.Interop` / `PowerToys.GPOWrapper` 时保留必要项目引用，验证隔离的干净 Release 输出能重建 WinMD 和 CsWinRT 投影，不能靠旧产物或手工复制 DLL 通过。
- 测试以本次实际构建的结果为准，记录数量和失败原因；历史 `186/186` 等数字不是永久验收门槛。

### 10.2 当前实际输出

默认 x64 Debug 运行布局为：

```text
x64/Debug/
├── Kit.exe
├── PowerToys.AwakeModuleInterface.dll
├── PowerToys.LightSwitchModuleInterface.dll
├── PowerToys.Awake.exe
├── LightSwitchService/
│   └── PowerToys.LightSwitchService.exe
├── WinUI3Apps/
│   ├── PowerToys.Settings.exe
│   ├── PowerToys.QuickAccess.exe
│   └── Assets/Settings/...
└── tests/
    ├── Awake.UnitTests/Awake.UnitTests.dll
    ├── Awake.ModuleServices.UnitTests/Awake.ModuleServices.UnitTests.dll
    ├── UnitTestsCommonUtils/Common.Utils.UnitTests.dll
    └── ModuleTemplate/ModuleTemplateCompileTest.dll

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

## 12. 本次 review、修复与验证

先核对本地上游、代码和资源，再按宿主稳定性、插件隔离与恢复、启动轻量化、开发模板的顺序修复。以下记录本轮工作区行为；实际构建与测试结果单独列出，不把源码推演当作运行期证明。

### 12.1 与本地上游的同步进度

| 范围 | 核查结论 |
| --- | --- |
| Awake 业务核心 | 会话/锁屏检测、状态计算、配置模型和 ViewModel 与本地快照对齐；Manager 主要差异是删除遥测；Program 与 ModuleServices 保留 Kit 帮助链接、Mutex 和进程身份隔离 |
| Awake 功能 | 被动/无限/计时/到期、保持屏幕、锁屏时允许屏幕按系统策略熄灭、CLI/PID 绑定仍保留；原生接口保留 Kit 的停止等待、句柄回收和销毁清理 |
| LightSwitch 业务核心 | LightSwitchLib、ThemeScheduler、Night Light observer 与本地快照对齐；保留固定时段、日落日出及偏移、跟随夜间模式、系统/应用主题与快捷键 |
| LightSwitch 有意裁剪 | PowerDisplay 配置和事件桥接移除；forceLight/forceDark 自定义动作移除，上游对应 UI 已注释，不把它误报为可见按钮缺失 |
| 测试同步 | 已引入 Awake 两个测试项目及三个原样上游测试源文件，并补充 Mutex 并存和完整进程路径验证；LightSwitch UI 测试源码仍在，源码存在不代表已运行通过 |
| 提交覆盖 | `37bff1d` 实际只修改 README 与 Awake 共 11 个文件，没有 LightSwitch 变更；`3524ba3` 才包含两个模块生命周期及 LightSwitch 依赖调整；本轮修复尚在工作区 |
| WinUI 3 + Mica | BaseAlt 主背景、透明页面、卡片背景/边框单层绘制和主题资源调整已落地；运行期视觉与无障碍验收仍需执行 |

不沿用旧 `kit-sync-status.md` 的“Awake 落后 23 文件”“95% 同步”等历史估算；不把删除遥测/PowerDisplay 的文件差异算作必须补回的缺失。后续同步应记录上游 commit、保留功能、Kit 有意差异、未合入行为和验证结果。

### 12.2 已处理的稳定性与插件接入问题

| 优先级 | 修复与当前行为 |
| --- | --- |
| P2 | **Settings 故障隔离和退出。** [settings_window.cpp](src/runner/settings_window.cpp) 的通用失败清理不再发 Runner 退出消息；正常关闭改用异步 `PostMessage`，避免 Settings 同步等 Runner、Runner 又等待 Settings 的退出环 |
| P2 | **Settings 重开与重启交接。** IPC 指针与 PID 用同一互斥量保护；单个可 join 的生命周期线程持有子进程句柄，并等待退出或关闭事件。关闭标记与创建过程串行化，Runner 在锁外等待线程完成清理，避免退出后又创建窗口；IPC 重启请求发送后由 Runner 关闭旧 UI，再交接新实例，避免异步请求丢失和共享退出事件残留 |
| P2 | **Awake 与官方并存。** [Constants.cs](src/modules/awake/Awake/Core/Constants.cs) 使用 `Local\Kit.Awake`；[AwakeService](src/modules/awake/Awake.ModuleServices/AwakeService.cs) 按 Kit 安装路径和会话确认进程，释放所有候选 `Process` 对象 |
| P2 | **LightSwitch Off 与重复启用。** [dllmain.cpp](src/modules/LightSwitch/LightSwitchModuleInterface/dllmain.cpp) 在 Off 下只保留手动切换监听；Worker 启停统一由该监听线程处理，重复 enable 不重复创建；设置先持久化，再通知监听线程 |
| P2 | **LightSwitch 故障恢复和退出。** 活动计划 Worker 异常退出后，下次热键或 Quick Access 动作恢复调度，后续动作切换主题，沿用上游恢复语义；热键只发事件。Worker 保留提前停止信号，启动时检查父进程及停止状态，初始化失败返回非零；debounce 可取消，销毁先停止回调、join 再关闭事件 |
| P2 | **GPO 一致性。** [gpo.h](src/common/utils/gpo.h) 全部兼容入口返回 `not_configured`，删除 Runner 的局部跳过宏及强制策略分支，UI/Worker 共享同一结果 |
| P2 | **模板与规范。** 源码、ZIP、元数据及构建依赖同步；补全 Library 序列化、EnabledModules converter、导航、资产保留、路径隔离与测试接入说明 |

### 12.3 已处理的启动与依赖开销

| 项目 | 当前行为与边界 |
| --- | --- |
| 活动模块列表 | 已缩为 Awake/LightSwitch；保留对这两个 DLL 的正常加载和配置处理 |
| Quick Access 延迟 | 开启或修改热键均不提前启动；首次展示通过受管理的线程池任务创建进程，重复请求合并，停用会取消排队工作并等待活动回调收尾，低级键盘钩子不执行进程冷启动 |
| 自动更新检查 | 移除启动调用、周期线程、失败重试和更新 toast；只保留明确的手动检查及发布页入口 |
| 无关旧清理 | 移除旧摄像头注册清理函数、调用及无人引用的头文件，同时删除无效更新后通知等待线程 |
| 开机启动任务 | [auto_start_helper.cpp](src/runner/auto_start_helper.cpp) 只访问 `\Kit`；已有任务的程序路径、参数和运行级别不变时不重新注册，禁用的匹配任务只改启用状态；配置变化原位更新，失败保留旧任务；拒绝覆盖无法确认属于 Kit 的任务 |
| 元数据缓存 | 删除未被调用方使用的 wrapper 缓存与 getter，继续使用模块接口；移除未经测量的毫秒节省注释 |
| Monitor 遗留依赖 | 删除 Settings 的 `Microsoft.Data.Sqlite` 引用、中央版本 pin 和 NOTICE 的对应包条目；不清理其他功能需要的依赖 |
| 性能验收 | Runner 计时覆盖到 WinMain；仍需按第 9 节分别采样 Settings、Quick Access 和 Worker 就绪时间，本轮不宣称百分比或毫秒收益 |

### 12.4 验证结果与剩余边界

构建及测试验证进行中，完成后在本节记录实际结果。

剩余边界：上游官方 DLL 的 ABI、Run/CmdPal 宿主没有新增兼容承诺；LightSwitch 既有同步主题广播可能延长禁用时的线程等待；WinUI/Mica 多主题、缩放、无障碍和完整语言/权限重启仍需运行期验收。没有为了这些后续事项新增插件发现框架、后台健康轮询或跨平台层。

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
| [Kit 开发经验](doc/devdoc/kit-development-experience.md)与[Monitor 移除记录](doc/devdoc/monitor-removal-plan.md) | 生命周期、状态/配置分离、WinMD、注册一致性 | Monitor、PowerDisplay、旧版本号和测试数量属于历史，不能当作当前活动结构 |
| [历史 devdoc 指南](doc/devdoc/AGENTS.md) | 上游小差异、显式列表、构建纪律 | 仍列 Monitor 为活动模块，本规范明确当前只保留 Awake/LightSwitch |

同步上游时，在插件变更说明中记录对应 commit、功能变化、Kit 必须保留的路径/事件/遥测裁剪、相关测试及未验证场景。同步目标是所需功能和契约正确，不是把已剔除的框架职责重新搬回 Kit。
