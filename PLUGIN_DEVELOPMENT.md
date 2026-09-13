# Kit 插件与模块开发指南 (Plugin & Module Development Guide)

本指南面向 Kit 的模块与插件开发者，深入剖析 Kit 的模块化体系架构，并基于官方 **Microsoft PowerToys**（`source/PowerToys`）与 **Kit** 生产级源码（`Awake`、`LightSwitch` 等），提供从零构建高性能、零遥测（Zero Telemetry）、现代化 WinUI 3 界面插件的全流程开发规范。

---

## 目录

1. [架构概述与核心设计原则](#1-架构概述与核心设计原则)
2. [双层进程模型与运行时拓扑](#2-双层进程模型与运行时拓扑)
3. [核心 C++ 契约：PowertoyModuleIface 接口规范](#3-核心-c-契约powertoymoduleiface-接口规范)
4. [零遥测强制规范 (Zero Telemetry Standard)](#4-零遥测强制规范-zero-telemetry-standard)
5. [后台工作进程 (Worker) 与看门狗生命周期](#5-后台工作进程-worker-与看门狗生命周期)
6. [配置存储与 IPC 规范](#6-配置存储与-ipc-规范)
7. [WinUI 3 + Mica Alt 设置前端开发](#7-winui-3--mica-alt-设置前端开发)
8. [Runner 注册与加载机制](#8-runner-注册与加载机制)
9. [从零开始：构建一个完整插件实战](#9-从零开始构建一个完整插件实战)
10. [单元测试、调试与构建产物规范](#10-单元测试调试与构建产物规范)

---

## 1. 架构概述与核心设计原则

Kit 继承并改造了 Microsoft PowerToys 的实用工具模块化规范。与庞大笨重的官方 PowerToys 相比，Kit 确立了如下核心工程底线：

1. **绝对零遥测 (Zero Telemetry)**：彻底清除所有 ETW (`TraceLogging`) 遥测 Provider，不链接任何遥测上报库，严禁收集或回传任何用户数据与行为事件。
2. **严格存储隔离 (Storage Isolation)**：所有模块配置、日志和运行时数据严格存放在 `%LOCALAPPDATA%\Kit\` 目录下，禁止读写或污染官方 `%LOCALAPPDATA%\Microsoft\PowerToys\`。
3. **轻量与按需唤醒 (Lightweight & On-Demand)**：后台 Worker 进程必须支持空闲挂起或彻底退出（如 LightSwitch 在 Off 模式下完全终止调度服务，而不是常驻占用内存）。
4. **现代化 WinUI 3 + Mica Alt 设计语言**：设置前端完全对齐 Windows 11 Fluent 规范，采用半透明 Mica Alt 背景层，并经 Kit 定制解决卡片过白、边框重影等 UI/UX 缺陷。

---

## 2. 双层进程模型与运行时拓扑

Kit 的实用工具采用经典的 **“进程隔离与接口代理”** 架构：

```
+-------------------------------------------------------------------------+
|                               Kit.exe                                   |
|                           (主运行器 Runner)                              |
|                                                                         |
|   +--------------------------+        +-----------------------------+   |
|   | PowerToys.AwakeModule    |        | PowerToys.LightSwitchModule |   |
|   | Interface.dll            |        | Interface.dll               |   |
|   | [PowertoyModuleIface]    |        | [PowertoyModuleIface]       |   |
|   +-------------+------------+        +--------------+--------------+   |
+-----------------|------------------------------------|------------------+
                  | CreateProcess                      | CreateProcess
                  | (--pid <Kit_PID>)                  | (--pid <Kit_PID>)
                  v                                    v
   +------------------------------+     +-------------------------------+
   |    PowerToys.Awake.exe       |     | PowerToys.LightSwitch         |
   |      (独立业务 Worker)        |     | Service.exe (后台服务 Worker)  |
   +------------------------------+     +-------------------------------+
                  ^                                    ^
                  | 读取/写入配置                       | 读取/写入配置
                  v                                    v
       %LOCALAPPDATA%\Kit\Awake\             %LOCALAPPDATA%\Kit\LightSwitch\
                  ^                                    ^
                  +-----------------+------------------+
                                    | IPC / 文件同步
                                    v
                   +----------------------------------+
                   |     PowerToys.Settings.exe       |
                   |      (WinUI 3 + Mica Alt 前端)    |
                   +----------------------------------+
```

### 角色分工

1. **主运行器 (`Kit.exe`)**：管理应用托盘、系统级全局热键钩子（Centralized Keyboard Hook）、URI 协议唤醒，并通过 `LoadLibraryW` 动态加载各模块的接口 DLL。
2. **模块接口代理 (`*.ModuleInterface.dll`)**：C++ 编写的动态链接库。作为 `Kit.exe` 进程内的轻量插件代理，实现 `PowertoyModuleIface` 接口，负责启停控制、热键注册与进程看门狗管理。
3. **业务工作进程 (`*.exe`)**：独立的可执行文件（C++ 或 C#/.NET 10）。负责实际的系统级逻辑（如设置系统保活状态、监控日落日出切换明暗主题等）。与 Runner 进程隔离，避免单个插件崩溃拖垮整个主进程。
4. **设置应用 (`PowerToys.Settings.exe`)**：独立的 WinUI 3 进程。提供现代化交互界面，直接通过文件 IO 或 IPC 操作 `%LOCALAPPDATA%\Kit\` 下的 JSON 设置。

---

## 3. 核心 C++ 契约：PowertoyModuleIface 接口规范

所有 Kit 模块接口 DLL 必须在头文件中包含 `<interface/powertoy_module_interface.h>`，并导出一个符号名为 `powertoy_create` 的工厂方法。

### 3.1 核心纯虚函数一览

```cpp
#include <interface/powertoy_module_interface.h>

class PowertoyModuleIface
{
public:
    // 1. 本地化展示名称（显示于 UI、提示框等）
    virtual const wchar_t* get_name() = 0;

    // 2. 非本地化唯一标识 Key（如 L"Awake"、L"LightSwitch"，对应配置路径与注册表）
    virtual const wchar_t* get_key() = 0;

    // 3. 导出配置结构体 JSON 序列化描述（用于给 Runner 传递配置元信息）
    virtual bool get_config(wchar_t* buffer, int* buffer_size) = 0;

    // 4. 接收 Runner 下发的新配置 JSON 字符串并落盘生效
    virtual void set_config(const wchar_t* config) = 0;

    // 5. 模块启用（拉起 Worker、注册系统资源）
    virtual void enable() = 0;

    // 6. 模块禁用（向 Worker 发送退出信号、释放系统资源，尽量归零内存）
    virtual void disable() = 0;

    // 7. 查询当前启用状态
    virtual bool is_enabled() = 0;

    // 8. 模块销毁并释放 C++ 对象实例
    virtual void destroy() = 0;

    // 可选方法：
    // 响应前端自定义按钮动作（如 "立即触发扫描"、"重置数据"）
    virtual void call_custom_action(const wchar_t* /*action*/){};

    // 注册全局热键
    virtual size_t get_hotkeys(Hotkey* /*buffer*/, size_t /*buffer_size*/) { return 0; }

    // 响应全局热键按下（返回 true 则拦截并吞咽该热键消息）
    virtual bool on_hotkey(size_t /*hotkeyId*/) { return false; }

    // GPO 策略支持（返回当前模块的组策略配置值）
    virtual powertoys_gpo::gpo_rule_configured_t gpo_policy_enabled_configuration()
    {
        return powertoys_gpo::gpo_rule_configured_not_configured;
    }
};

// 工厂函数导出规范：
extern "C" __declspec(dllexport) PowertoyModuleIface* __cdecl powertoy_create();
```

### 3.2 模块生命周期时序

1. **加载阶段**：`Kit.exe` 启动 -> `LoadLibraryW(L"PowerToys.YourModuleInterface.dll")` -> `GetProcAddress("powertoy_create")` -> 调用 `powertoy_create()` 实例化对象。
2. **初始化阶段**：调用 `get_key()` 检查配置是否启用。若启用，调用 `enable()`；若包含热键，调用 `get_hotkeys()` 注册到全局按键监听链。
3. **运行阶段**：
   - 用户在 Settings 中修改设置 -> 触发 `set_config(json)`。
   - 用户按下快捷键 -> 触发 `on_hotkey(id)`。
   - 用户点击自定义动作 -> 触发 `call_custom_action(action)`。
4. **卸载阶段**：`Kit.exe` 退出 -> 先调用 `disable()` 发出优雅终止信号 -> 调用 `destroy()` 释放所有堆对象 -> `FreeLibrary` 卸载 DLL。

---

## 4. 零遥测强制规范 (Zero Telemetry Standard)

官方 PowerToys 在几乎每个模块中都强制继承 `TraceBase` 并通过 ETW 注册 Provider：

```cpp
// ❌ 禁止在 Kit 中书写官方 PowerToys 的遥测代码：
#include <common/Telemetry/TraceBase.h>
TRACELOGGING_DEFINE_PROVIDER(g_hProvider, "Microsoft.PowerToys", ...);
TraceLoggingWrite(g_hProvider, "Awake_Enable", ...);
```

### 4.1 Kit 标准 `trace.h` 模板

在你的 ModuleInterface 及 Worker 项目中，必须建立完全内联、编译期 constexpr 优化的 `trace.h`：

```cpp
#pragma once

#include <string>

class Trace
{
public:
    static void RegisterProvider() noexcept {}
    static void UnregisterProvider() noexcept {}

    // 将所有遥测上报方法打桩为纯空函数 (No-op)
    static void EnableModule(bool /*enabled*/) noexcept {}
    static void SendSettingsTelemetry() noexcept {}
    static void EventLaunch(const std::wstring& /*version*/, bool /*elevated*/) noexcept {}
};
```

### 4.2 Kit 标准 `trace.cpp` 模板

```cpp
#include "pch.h"
#include "trace.h"

// 零遥测：无任何 ETW 初始化和符号导出，完全由编译器内联消除
```

> [!IMPORTANT]
> 绝对不要在工程中引入 `src/common/Telemetry` 或链接 `EtwTrace`。如果上游合并代码带来了 `TraceLoggingWrite`，必须将其清理并重构为 Kit 的 no-op 桩。

---

## 5. 后台工作进程 (Worker) 与看门狗生命周期

为了保证系统的稳定性和无孤儿进程残留，业务进程的生命周期管理必须遵守严格的 **PID 看门狗** 与 **命名事件 (Named Event) 优雅退出** 机制。

### 5.1 模块接口侧：拉起进程与优雅退出

参考 `src/modules/awake/AwakeModuleInterface/dllmain.cpp` 的标准实现：

```cpp
// 启动 Worker
bool launch_process()
{
    unsigned long runner_pid = GetCurrentProcessId();
    // 强制传递主进程 PID，供 Worker 进程进行生命周期心跳监听
    std::wstring args = L"--use-pt-config --pid " + std::to_wstring(runner_pid);
    std::wstring app_path = L"PowerToys.MyWorker.exe";
    std::wstring cmd = app_path + L" " + args;

    STARTUPINFO si = { sizeof(si) };
    return CreateProcessW(app_path.c_str(), cmd.data(), NULL, NULL, FALSE, 0, NULL, NULL, &si, &m_process_info);
}

// 优雅关闭 Worker
void disable()
{
    if (m_enabled || m_process_info.hProcess)
    {
        // 1. 创建并触发特定于本模块的命名退出事件
        auto exit_event = CreateEventW(nullptr, FALSE, FALSE, L"Local\\Kit_MyModule_Exit_Event");
        if (exit_event)
        {
            SetEvent(exit_event);
            CloseHandle(exit_event);

            // 2. 限时等待进程正常退出（例如 1500ms）
            DWORD wait_result = WaitForSingleObject(m_process_info.hProcess, 1500);
            if (wait_result == WAIT_TIMEOUT)
            {
                // 3. 超时强杀兜底，防止挂死
                TerminateProcess(m_process_info.hProcess, 1);
            }
        }
        close_process_handles();
        m_enabled = false;
    }
}
```

### 5.2 Worker 侧：PID 守护线程与退出事件监听

在 Worker 进程（无论是 C++ 还是 C#）的初始化阶段，必须启动看门狗：

```csharp
// C# Worker 示例（参考 Awake/Program.cs）
if (args.Contains("--pid"))
{
    int parentPid = int.Parse(args[args.IndexOf("--pid") + 1]);
    Task.Run(async () =>
    {
        try
        {
            var parentProcess = Process.GetProcessById(parentPid);
            await parentProcess.WaitForExitAsync();
        }
        catch
        {
            // 父进程已不存在
        }
        finally
        {
            // Runner 退出，Worker 必须自我终结，绝不逗留
            Environment.Exit(0);
        }
    });
}

// 监听命名退出事件
var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Kit_MyModule_Exit_Event");
Task.Run(() =>
{
    exitEvent.WaitOne();
    Environment.Exit(0);
});
```

---

## 6. 配置存储与 IPC 规范

### 6.1 存储路径与命名空间

Kit 统一使用 `%LOCALAPPDATA%\Kit\` 存储目录：

```
%LOCALAPPDATA%\Kit\
├── settings.json                   # 全局设置（通用、模块开关）
├── Awake\
│   └── settings.json               # Awake 模块设置
├── LightSwitch\
│   └── settings.json               # LightSwitch 模块设置
└── <YourModule>\
    ├── settings.json               # 你的模块设置
    └── logs\                       # 模块日志
```

在 C++ 接口中，直接使用辅助类获取存储路径：

```cpp
#include <common/SettingsAPI/settings_helpers.h>

std::filesystem::path configPath(PTSettingsHelper::get_module_save_folder_location(L"MyModule"));
configPath.append(L"settings.json");
```

### 6.2 C# Settings.UI 侧的仓储与 AOT 序列化支持

在 `src/settings-ui/Settings.UI.Library` 中创建配置实体类：

```csharp
public class MyModuleSettings : ISettingsConfig
{
    public string Version { get; set; } = "1.0";
    public string Name { get; set; } = "MyModule";
    public MyModuleProperties Properties { get; set; } = new();

    public string GetModuleName() => Name;
    public bool UpgradeSettingsConfiguration() => false;
}

public class MyModuleProperties
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;
}
```

> [!IMPORTANT]
> **必须注册到 Source Generator 上下文！**
> 打开 `src/settings-ui/Settings.UI/SerializationContext/SourceGenerationContextContext.cs`，添加你的类型特性：
> ```csharp
> [JsonSerializable(typeof(MyModuleSettings))]
> ```

读取与更新设置：

```csharp
// 在 ViewModel 中获取单例仓储：
ISettingsRepository<MyModuleSettings> repository = 
    SettingsRepository<MyModuleSettings>.GetInstance(SettingsUtils.Default);

MyModuleSettings currentSettings = repository.SettingsConfig;

// 修改并保存：
currentSettings.Properties.IntervalSeconds = 120;
repository.SaveSettings(currentSettings);
```

---

## 7. WinUI 3 + Mica Alt 设置前端开发

Kit 2.0.10 经过深度定制，UI/UX 全面升级为基于 **Mica Alt** 材质的精致质感界面（参考 `Locals` 视觉规范）。

### 7.1 页面基类与命名空间规范

新建设置页 `MyModulePage.xaml`，必须继承自 `local:NavigablePage`：

```xml
<?xml version="1.0" encoding="utf-8" ?>
<local:NavigablePage
    x:Class="Microsoft.PowerToys.Settings.UI.Views.MyModulePage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:Microsoft.PowerToys.Settings.UI.Controls"
    xmlns:local="using:Microsoft.PowerToys.Settings.UI.Helpers"
    xmlns:tkcontrols="using:CommunityToolkit.WinUI.Controls"
    xmlns:ui="using:CommunityToolkit.WinUI"
    xmlns:viewModels="using:Microsoft.PowerToys.Settings.UI.ViewModels"
    AutomationProperties.LandmarkType="Main">

    <Grid>
        <controls:SettingsPageControl
            x:Uid="MyModule"
            ModuleImageSource="ms-appx:///Assets/Settings/Modules/MyModule.png">
            <controls:SettingsPageControl.ModuleContent>
                <StackPanel Orientation="Vertical" Spacing="8">
                    
                    <!-- 主开关卡片 -->
                    <tkcontrols:SettingsCard
                        x:Uid="MyModule_EnableSettingsCard"
                        HeaderIcon="{ui:BitmapIcon Source=/Assets/Settings/Icons/MyModule.png}">
                        <ToggleSwitch IsOn="{x:Bind ViewModel.IsEnabled, Mode=TwoWay}" />
                    </tkcontrols:SettingsCard>

                    <!-- 参数配置卡片组 -->
                    <controls:SettingsGroup Header="常规配置">
                        <tkcontrols:SettingsCard Header="轮询间隔 (秒)" Description="后台 Worker 的心跳检测频率">
                            <NumberBox Value="{x:Bind ViewModel.IntervalSeconds, Mode=TwoWay}" SpinButtonPlacementMode="Compact" />
                        </tkcontrols:SettingsCard>

                        <!-- 可展开设置项 -->
                        <tkcontrols:SettingsExpander Header="高级选项" Description="自定义系统行为拦截规则">
                            <tkcontrols:SettingsExpander.Items>
                                <tkcontrols:SettingsCard Header="开机静默启动">
                                    <ToggleSwitch IsOn="{x:Bind ViewModel.SilentStart, Mode=TwoWay}" />
                                </tkcontrols:SettingsCard>
                            </tkcontrols:SettingsExpander.Items>
                        </tkcontrols:SettingsExpander>
                    </controls:SettingsGroup>

                </StackPanel>
            </controls:SettingsPageControl.ModuleContent>
        </controls:SettingsPageControl>
    </Grid>
</local:NavigablePage>
```

### 7.2 Mica Alt 半透明卡片优化规范

Kit 解决了官方 WinUI 3 卡片在 Mica Alt 底色上“发白刺眼”的顽疾。全局主题资源已在 `src/settings-ui/Settings.UI/SettingsXAML/Themes/Colors.xaml` 中深度覆盖：

- `CardBackgroundFillColorDefaultBrush`：浅色主题为 `#90FFFFFF`，深色主题为 `#0DFFFFFF`。
- `CardStrokeColorDefaultBrush`：浅色主题采用柔和的 `#1F000000` 描边。
- 自定义卡片如果使用 `Card.xaml` 原语控件，背景已被设为 `Transparent`，彻底消除了内外容器双层渲染的重影问题。

### 7.3 路由与导航系统注册

添加页面后，需要在前端完成 4 处注册：

1. **路由注册 (`src/settings-ui/Settings.UI/SettingsXAML/App.xaml.cs`)**：
   ```csharp
   private static Type GetPageType(string pageKey)
   {
       return pageKey switch
       {
           "Dashboard" => typeof(DashboardPage),
           "General" => typeof(GeneralPage),
           "Awake" => typeof(AwakePage),
           "LightSwitch" => typeof(LightSwitchPage),
           "MyModule" => typeof(MyModulePage), // 👈 新增
           _ => typeof(DashboardPage),
       };
   }
   ```

2. **导航栏视图 (`src/settings-ui/Settings.UI/SettingsXAML/Views/ShellPage.xaml`)**：
   ```xml
   <NavigationViewItem
       x:Name="MyModuleNavigationItem"
       Content="{x:Bind ViewModel.MyModuleNavigationTitle}"
       Tag="MyModule">
       <NavigationViewItem.Icon>
           <ui:BitmapIcon ShowAsMonochrome="True" Source="/Assets/Settings/Icons/MyModule.png" />
       </NavigationViewItem.Icon>
   </NavigationViewItem>
   ```

3. **ViewModel 绑定 (`src/settings-ui/Settings.UI/ViewModels/ShellViewModel.cs`)**：
   ```csharp
   public string MyModuleNavigationTitle => ResourceLoaderInstance.ResourceLoader.GetString("Shell_MyModule/Content");
   ```

4. **多语言资源 (`src/settings-ui/Settings.UI/Strings/en-us/Resources.resw`)**：
   ```xml
   <data name="Shell_MyModule.Content" xml:space="preserve">
       <value>My Module</value>
   </data>
   <data name="MyModule.ModuleTitle" xml:space="preserve">
       <value>My Module</value>
   </data>
   <data name="MyModule.ModuleDescription" xml:space="preserve">
       <value>Description of what My Module does.</value>
   </data>
   ```

---

## 8. Runner 注册与加载机制

Kit 运行器采用显式注册列表加载模块，避免不安全且难以掌控的文件系统盲扫。

### 8.1 静态注册至 Runner

打开 `src/runner/main.cpp`，定位到 `KitKnownModules` 数组：

```cpp
namespace
{
    constexpr std::wstring_view KitKnownModules[] = {
        L"PowerToys.AwakeModuleInterface.dll",
        L"PowerToys.LightSwitchModuleInterface.dll",
        L"PowerToys.MyModuleInterface.dll", // 👈 在此注册你的模块接口 DLL
    };
}
```

在 `runner()` 启动流程中，Kit 会自动执行：
```cpp
for (auto moduleSubdir : KitKnownModules)
{
    auto pt_module = load_powertoy(moduleSubdir);
    modules().emplace(pt_module->get_key(), std::move(pt_module));
}
start_enabled_powertoys(startupGeneralSettings);
```
当 `startupGeneralSettings` 中该模块的状态为 `enabled` 时，其 `enable()` 方法将立即被触发。

---

## 9. 从零开始：构建一个完整插件实战

以构建一个名为 `SystemCleaner` 的插件为例：

### 第一步：创建 C++ 模块接口项目
1. 复制 `tools/project_template/ModuleTemplate` 为 `src/modules/SystemCleaner/SystemCleanerModuleInterface`。
2. 配置 `vcxproj`，引入 `powertoy_module_interface.h`、`common` 基础库。
3. 实现 `dllmain.cpp`，定义 `class SystemCleaner : public PowertoyModuleIface`。
4. 提供零遥测 `trace.h` 与 `trace.cpp`。
5. 导出 `powertoy_create`。

### 第二步：创建业务 Worker 进程
1. 在 `src/modules/SystemCleaner/SystemCleanerWorker` 新建控制台或无窗口后台应用。
2. 接收 `--pid <pid>` 参数，初始化看门狗线程。
3. 监听 `Local\Kit_SystemCleaner_Exit` 退出事件。

### 第三步：集成 WinUI 3 前端
1. 在 `src/settings-ui/Settings.UI.Library` 中编写 `SystemCleanerSettings.cs`。
2. 在 `SourceGenerationContextContext.cs` 中标注 `[JsonSerializable(typeof(SystemCleanerSettings))]`。
3. 在 `src/settings-ui/Settings.UI` 中创建 `SystemCleanerPage.xaml` 与 `SystemCleanerViewModel.cs`。
4. 在 `ShellPage.xaml`、`ShellViewModel.cs`、`App.xaml.cs` 与 `Resources.resw` 中注册导航与字符串。

### 第四步：加入工程与构建
1. 在 `Kit.slnx` 解决方案中加入新项目。
2. 在 `src/runner/main.cpp` 的 `KitKnownModules` 中添加 `L"PowerToys.SystemCleanerModuleInterface.dll"`。

---

## 10. 单元测试、调试与构建产物规范

### 10.1 单元测试基线要求

任何新增模块均不得破坏 Kit 的全量单元测试。运行测试命令：

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe' 'Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.dll' /Platform:x64
```
**必须保持 186/186 (100%) 全部通过**。
若添加了配置项测试，请在 `src/settings-ui/Settings.UI.UnitTests` 对应的测试集中补充断言。

### 10.2 构建与版本化产物结构 (`bin/debug/<version>/`)

遵循 Kit 规范，本地调试构建产物必须统一汇聚在 `bin/debug/<version>/`，中间目录与日志严禁散落在源码根目录。

**标准目录拓扑示例 (2.0.10)：**
```
bin/debug/2.0.10/
├── Kit.exe                                         # 核心主运行器
├── PowerToys.AwakeModuleInterface.dll              # Awake 接口 DLL
├── PowerToys.LightSwitchModuleInterface.dll        # LightSwitch 接口 DLL
├── PowerToys.SystemCleanerModuleInterface.dll      # 你的插件接口 DLL
├── PowerToys.Awake.exe                             # Awake 后台进程
├── LightSwitchService/
│   └── PowerToys.LightSwitchService.exe            # LightSwitch 服务进程
├── WinUI3Apps/
│   ├── PowerToys.Settings.exe                      # 设置应用前端
│   └── Assets/                                     # 图标与静态资源
└── Assets/                                         # 托盘与系统图标
```

通过这一套严谨的架构，开发者能够以最小的成本复用官方 PowerToys 庞大的生态体系，同时坐享 Kit 带来的纯净、无遥测、极致轻量与高颜值的 Fluent UI 体验。
