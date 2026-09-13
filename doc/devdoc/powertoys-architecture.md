# PowerToys 主框架架构参考

> 基于 Source/PowerToys 源码核对（当前快照），供 Kit 后续同步与优化参考。
> 
> **修订说明**：
> - **v1.0**（初版）：按上游源码逐项核实，修正了旧版中的错误（接口头路径、设置模型、AI 检测"阻塞"假设、管道命名、接口方法表等），补全了缺失部分（Settings v2、特权管道认证、GPO、遥测栈、模块清单、knownModules 列表），并删减了已过时/臆测的内容。
> - **v2.0**（增强版，2026-09-13）：基于深度源码审查，补充完整实现细节与设计模式分析：
>   - ✅ 启动流程逐函数拆解（WinMain → runner → 13 个阶段）
>   - ✅ 模块生命周期完整追踪（加载 → 注册 → 启停 → 清理）
>   - ✅ 托盘图标与设置窗口深度解析（消息路由、双击检测、IPC 管道认证）
>   - ✅ 热键系统双轨架构（RegisterHotKey + WH_KEYBOARD_LL，性能优化策略）
>   - ✅ IPC 通信完整实现（双管道模型、安全描述符、线程安全、错误恢复）
>   - ✅ Settings v2 架构详解（Repository 模式、MVVM、导航服务、热重载）
>   - ✅ 设计模式总结（25+ 模式，8 大分类：核心模式、并发、错误处理、架构、性能、安全、可维护性、互操作）
>   - ✅ 所有关键代码附带完整文件路径与行号引用

## 目录

1. [总览：进程模型](#1-总览进程模型)
2. [模块接口 PowertoyModuleIface](#2-模块接口-powertoymoduleiface)
3. [模块类型（官方分类）](#3-模块类型官方分类)
4. [Runner 详解](#4-runner-详解)
   - 4.1 启动流程（WinMain + runner 阶段）
   - 4.1a 错误处理与恢复策略
   - 4.1b 重启与提权逻辑
   - 4.2 knownModules 清单
   - 4.3 模块加载与生命周期
   - 4.4 托盘图标与设置窗口
   - 4.5 热键系统
   - 4.6 Quick Access
   - 4.7 AI 能力检测
   - 4.8 更新系统
   - 4.9 其他 Runner 组件
5. [Settings 系统（Settings v2）](#5-settings-系统settings-v2)
   - 5.1 工程结构
   - 5.1a WinUI3 应用初始化
   - 5.2 Settings 持久化架构
   - 5.3 MVVM 架构实现
   - 5.4 导航服务架构
   - 5.5 存储布局
   - 5.6 配置流
6. [IPC 与安全](#6-ipc-与安全)
   - 6.1 TwoWayPipeMessageIPC 实现详解
   - 6.2 特权管道客户端认证
7. [GPO 策略](#7-gpo-策略)
8. [遥测（ETW，Kit 不引入）](#8-遥测etw-kit-不引入)
9. [公共库（src/common）](#9-公共库srccommon)
10. [模块清单（当前 33 个目录）](#10-模块清单当前-33-个目录含-interface-共享头)
11. [版本与构建](#11-版本与构建)
12. [对 Kit 的参考要点](#12-对-kit-的参考要点)
13. [设计模式与架构模式总结](#13-设计模式与架构模式总结)
14. [参考实现与代码引用](#14-参考实现与代码引用)
15. [References](#references)

## 1. 总览：进程模型

PowerToys 是"一个常驻 Runner 进程 + 多个模块 DLL + 两个 WinUI3 辅助进程"的架构：

- PowerToys.exe（Runner，src/runner）：托盘、模块加载与生命周期、全局热键、设置窗口协调、更新检查；
- PowerToys.Settings.exe（WinUI3，src/settings-ui）：设置应用（Settings v2，MVVM）；
- PowerToys.QuickAccess.exe（WinUI3，src/settings-ui/QuickAccess.UI）：Win+Space 快速访问；
- 模块 DLL：实现 PowertoyModuleIface，导出 powertoy_create() 工厂；
- 模块附属进程：部分模块自带独立进程（Awake 的 PowerToys.Awake.exe、ImageResizer 的 WinUI3 应用、ColorPicker 的 WPF 应用等），经命名管道/事件与接口 DLL 通信。

进程间通信：Runner 与 Settings/QuickAccess 经 TwoWayPipeMessageIPC 命名管道交换 JSON；模块接口与自身附属进程之间自行约定管道/事件。

## 2. 模块接口 PowertoyModuleIface

接口头文件位置：src/modules/interface/powertoy_module_interface.h（Kit 与上游共用同一份定义，Kit 未改动）。旧文档写的 src/common/interface 已过时。

### 2.1 方法表（按当前头文件）

必选（纯虚）：

| 方法 | 语义 |
|---|---|
| get_name() | 返回本地化显示名 |
| get_key() | 返回非本地化模块 key（Runner 缓存，作 map 索引） |
| get_config(buffer, size) | 输出设置 UI 描述 JSON；两遍调用约定（buffer 为 null 或过小时返回所需大小并返回 false） |
| set_config(json) | 接收 Settings 修改后的配置（模块在此持久化） |
| enable() / disable() | 启用/禁用（disable 应尽量释放资源） |
| is_enabled() | 当前启用状态 |
| destroy() | 释放全部内存并删除对象 |

可选（带默认实现）：

| 方法 | 默认 | 语义 |
|---|---|---|
| call_custom_action(action) | 空 | Settings 自定义动作按钮回调 |
| get_hotkeys(Hotkey[], n) | 0 | 返回热键列表；Settings 变更后也会被调用以更新热键 |
| on_hotkey(hotkeyId) | false | 对应热键按下回调；返回 true 表示吞掉该按键；模块禁用时也会被调用 |
| GetHotkeyEx() / OnHotkeyEx() | nullopt / 空 | 新式热键（VK + 修饰掩码） |
| keep_track_of_pressed_win_key() / milliseconds_win_key_must_be_pressed() | false / 0 | Win 键按住跟踪（遗留 ShortcutGuide 用，新模块勿用） |
| send_settings_telemetry() | 空 | 周期上报模块设置遥测 |
| is_enabled_by_default() | true | 是否默认启用（部分模块默认关闭） |
| gpo_policy_enabled_configuration() | not_configured | 返回模块的 GPO 策略状态 |

### 2.2 热键结构

Hotkey：win / ctrl / shift / alt / key / id / isShown，带三路比较运算符（供去重与冲突检测，故意不比较 name）。

HotkeyEx：modifiersMask / vkCode / id。

另有常量 CENTRALIZED_KEYBOARD_HOOK_DONT_TRIGGER_FLAG = 0x110，用于标记"PowerToys 自身生成的输入"（如 AdvancedPaste 粘贴生成的内容），避免被钩子再次捕获。

受保护帮助函数 CreateDefaultEvent(eventName)：创建命名事件对象（模块间/模块与附属进程同步用）。

### 2.3 工厂约定

    extern "C" __declspec(dllexport) PowertoyModuleIface* __cdecl powertoy_create()

- 返回对象必须处于 disabled 状态，Runner 随后调用 enable()；
- create() 在 destroy() 之前只调用一次；失败返回 nullptr；
- Runner 对每个 DLL：LoadLibrary → 解析 powertoy_create → 创建对象 → get_key() 注册 → enable() 启动。

## 3. 模块类型（官方分类）

1. 简单模块：全部逻辑在接口 DLL 内（MousePointerCrosshairs、FindMyMouse）；
2. 外部应用启动器：接口 DLL 启动独立进程并经管道/事件通信（ColorPicker 的 WPF 应用、Awake 的 PowerToys.Awake.exe、ImageResizer 的 WinUI3 应用）；
3. 上下文处理模块：文件资源管理器 Shell 扩展（PowerRename、FileLocksmith、RegistryPreview、Peek、NewPlus、Hosts 等，多为 WinUI3Apps/ 下的 *Ext.dll 或 *ShellExtension.dll）；
4. 注册表型模块：注册预览处理器/缩略图提供者（PowerPreview 的 SVG/PDF/Markdown 等）。

另有一类托管插件：PowerToys Run（src/modules/launcher）的 C# 插件走 src/common/ModuleContracts 的托管契约（IPlugin），与 C++ 的 PowertoyModuleIface 是两套体系。

## 4. Runner 详解

### 4.1 启动流程（当前 main.cpp，已修正）

#### WinMain 阶段（main.cpp:449-653）

1. **ETW 追踪与 GDI+ 初始化**（451-456）：Trace::RegisterProvider() 启动遥测，GdiplusStartup 初始化 GDI+
2. **WinRT 与 COM 安全**（458-471）：RoInitialize(RO_INIT_MULTITHREADED)，CoInitializeSecurity 设置 SDDL 描述符
3. **命令行解析与特殊模式检测**（473-501）：should_run_in_special_mode() 检测三种特殊模式
   - **Win32ToastNotificationCOMServer**：TOAST_ACTIVATED_LAUNCH_ARG 存在时运行 COM 激活循环后退出
   - **ToastNotificationHandler**：powertoys:// URI 协议处理（cant_drag_elevated_disable、update_now、open_settings 等动作）
   - **ReportSuccessfulUpdate**：UPDATE_REPORT_SUCCESS 参数时移除更新 toast 并显示成功通知
4. **Logger 与单实例互斥**（503-505, 523-528）：init_logger() 初始化日志，create_msi_mutex() 创建 MSI 互斥量
   - 互斥量失败时调用 open_menu_from_another_instance() 向已有实例发送 WM_COMMAND 消息后退出
5. **OOBE 与版本检查**（530-553）：检查 OOBE 状态，比较版本决定是否显示 SCOOBE
6. **模块单例与更新清理**（560-568）：初始化 modules() 单例，detached 线程执行 cleanup_updates()
7. **全局设置加载**（570-573）：load_general_settings()，apply_general_settings() 应用但不保存
8. **提权检查与重启逻辑**（574-623）：复杂决策树判断是否需要提权重启
   - 如果 elevated 且 --dont-elevate 且非 run_elevated_setting：调用 schedule_restart_as_non_elevated() 后退出
   - --restartedElevated 检查防止无限重启循环
9. **Runner 函数调用或重启**（610-622）：正常情况调用 runner()，否则 schedule_restart_as_elevated()
10. **清理与重启执行**（632-649）：trace flush，msi_mutex.reset() 释放互斥量，restart_if_scheduled()

#### runner() 函数阶段（main.cpp:182-366）

1. **DPI 与调试设置**（184-190）：enable_dpi_awareness_for_this_process()，Debug 下启用 unhandled_exception_handler
2. **设置与托盘**（193-196）：load_general_settings()，start_tray_icon() 启动托盘图标窗口
3. **Quick Access 条件启动**（198-202）：根据 enableQuickAccess 设置决定是否启动 Quick Access 宿主
4. **集中式键盘钩子**（204）：centralized_kb_hook::start() 安装 WH_KEYBOARD_LL 钩子
5. **后台线程**（209-242）：spawn 4 个 detached 线程
   - 更新重启通知线程（211-219）：sleep 10s 后显示重启通知
   - 周期更新工作线程（221-223）：PeriodicUpdateWorker() 持续检查更新
   - AI 能力检测线程（228-235）：Win11+ 上运行 DetectAiCapabilitiesAsync()，30s 超时
   - MSIX 卸载线程（237-242）：uninstall_previous_msix_version_async()
6. **工作目录与 VCM 清理**（244-252）：chdir 到 exe 目录，elevated 时执行 clean_video_conference()
7. **模块加载**（256-320）：遍历 knownModules 列表（35 项）
   - 每个模块：load_powertoy(moduleSubdir) 创建实例并添加到 modules() map
   - 失败处理：Debug 记日志继续（311），Release 显示 MessageBox 继续（314-317）
8. **启用模块**（322）：start_enabled_powertoys() 根据配置和 GPO 策略启用模块
9. **事件与窗口**（324-345）：Trace::EventLaunch 记录版本，按需打开 Settings/OOBE/SCOOBE
10. **消息循环**（348）：run_message_loop() 阻塞直到退出
11. **清理**（356-364）：trace unregister，非系统会话结束时停止 Quick Access

### 4.1a 错误处理与恢复策略

**模块加载异常**（302-319）：
- try-catch 包裹每个 load_powertoy() 调用
- 失败时将模块名追加到错误消息
- Debug 模式：Logger::warn 记录警告并继续
- Release 模式：MessageBox 显示错误对话框并继续

**Runner 异常**（350-355）：
- catch std::runtime_error，转为宽字符串
- MessageBox 显示本地化错误标题
- 返回 -1

**工作目录变更失败**（78-80）：
- show_last_error_message 显示 Win32 错误码

**AI 检测失败**（154-160）：
- 捕获异常，记录日志，不阻塞启动流程

### 4.1b 重启与提权逻辑细节

**提权决策树**（574-623）：
```
is_elevated = is_process_elevated()
dont_elevate_flag = --dont-elevate cmdline arg
run_elevated_setting = general_settings.run_elevated
restarted_elevated_flag = --restartedElevated cmdline arg

IF (is_elevated AND dont_elevate_flag AND NOT run_elevated_setting):
    schedule_restart_as_non_elevated()  // 降权重启
    exit
ELSE IF (is_elevated OR NOT run_elevated_setting OR dont_elevate_flag 
         OR (NOT is_elevated AND restarted_elevated_flag)):
    run runner()  // 正常运行
    // --restartedElevated 检查防止重启循环（604-608）
ELSE:
    schedule_restart_as_elevated()  // 提权重启
    exit
```

**重启执行**（641-649）：
- WinMain 清理后检查 is_restart_scheduled()
- 释放 modules() map（643）
- 调用 restart_if_scheduled()
- 失败时记录警告并退出

### 4.2 knownModules 清单（main.cpp:256-291，共 35 项）

    PowerToys.FancyZonesModuleInterface.dll
    PowerToys.powerpreview.dll
    WinUI3Apps/PowerToys.ImageResizerExt.dll
    PowerToys.KeyboardManager.dll
    PowerToys.Launcher.dll
    WinUI3Apps/PowerToys.PowerRenameExt.dll
    WinUI3Apps/PowerToys.ShortcutGuideModuleInterface.dll
    PowerToys.ColorPicker.dll
    PowerToys.AwakeModuleInterface.dll
    PowerToys.FindMyMouse.dll
    PowerToys.MouseHighlighter.dll
    WinUI3Apps/PowerToys.MouseJump.dll
    PowerToys.AlwaysOnTopModuleInterface.dll
    PowerToys.MousePointerCrosshairs.dll
    PowerToys.AutoHideCursor.dll
    PowerToys.CursorWrap.dll
    PowerToys.PowerAccentModuleInterface.dll
    PowerToys.PowerOCRModuleInterface.dll
    PowerToys.AdvancedPasteModuleInterface.dll
    WinUI3Apps/PowerToys.FileLocksmithExt.dll
    WinUI3Apps/PowerToys.RegistryPreviewExt.dll
    WinUI3Apps/PowerToys.MeasureToolModuleInterface.dll
    WinUI3Apps/PowerToys.NewPlus.ShellExtension.dll
    WinUI3Apps/PowerToys.HostsModuleInterface.dll
    WinUI3Apps/PowerToys.Peek.dll
    WinUI3Apps/PowerToys.EnvironmentVariablesModuleInterface.dll
    PowerToys.MouseWithoutBordersModuleInterface.dll
    PowerToys.CropAndLockModuleInterface.dll
    PowerToys.CmdNotFoundModuleInterface.dll
    PowerToys.WorkspacesModuleInterface.dll
    PowerToys.CmdPalModuleInterface.dll
    PowerToys.ZoomItModuleInterface.dll
    PowerToys.LightSwitchModuleInterface.dll
    PowerToys.PowerDisplayModuleInterface.dll
    PowerToys.GrabAndMoveModuleInterface.dll
    PowerToys.AltWindowCycle.dll

WinUI3Apps/ 前缀表示该模块以 WinUI3 应用形态输出到扁平化的 WinUI3Apps 子目录（Shell 扩展 *Ext.dll、*ShellExtension.dll 等），Runner 从该子目录加载。

### 4.3 模块加载与生命周期（powertoy_module.cpp）

#### 加载流程 load_powertoy()

**完整加载链**（powertoy_module.cpp）：
1. **LoadLibraryW(module_path)**：加载模块 DLL
2. **GetProcAddress("powertoy_create")**：解析工厂函数
3. **powertoy_create()**：调用工厂函数创建模块实例
4. **PowertoyModule RAII 封装**：
   - 持有 HMODULE 和 PowertoyModuleIface* 指针
   - 析构时自动调用 destroy() + FreeLibrary()
5. **热键注册**：构造时调用 get_hotkeys() 并注册到 centralized_hotkeys
6. **添加到全局 map**：modules()[get_key()] = module_instance

**失败处理策略**：
- 任一环节失败立即 FreeLibrary() 并抛出异常
- Runner 捕获异常后：
  - Debug 模式：Logger::warn 记录日志继续
  - Release 模式：MessageBox 显示错误对话框继续

#### 全局模块注册表

**modules() 单例**（powertoy_module.cpp）：
```cpp
std::map<std::wstring, PowertoyModule>& modules()
{
    static std::map<std::wstring, PowertoyModule> instance;
    return instance;
}
```
- key 为模块的 get_key() 返回值（非本地化标识符）
- value 为 PowertoyModule RAII 包装对象
- 全局唯一，贯穿 Runner 生命周期

#### 模块启停入口

**启动路径**：
1. **start_enabled_powertoys()**（general_settings.cpp:506+）：
   - 收集每个模块的 GPO 策略状态
   - 结合 is_enabled_by_default() 和用户配置
   - 按优先级决策：GPO 强制启用 > GPO 强制禁用 > 用户配置
   - 逐个调用 module->enable()

**运行时变更路径**：
2. **apply_module_status_update()**（settings_window.cpp）：
   - Settings UI 发送模块启用/禁用消息
   - 重新执行 GPO 复核
   - 调用 module->enable() 或 module->disable()

#### 模块清理

**退出时清理**（main.cpp:643）：
- modules().clear() 触发所有 PowertoyModule 析构
- 每个析构函数调用 module->destroy() + FreeLibrary()
- 重启前也会清理（防止模块锁定 DLL 文件）

### 4.4 托盘图标与设置窗口

#### 托盘图标生命周期（tray_icon.cpp）

**初始化与创建**（453-523）：
- **start_tray_icon()**：创建托盘图标窗口
- **NOTIFYICONDATAW 初始化**（491-507）：
  - szTip：tooltip 文本
  - hIcon：主题感知图标选择（lines 462-464）
  - uCallbackMessage：自定义消息 wm_icon_notify
  - hWnd：托盘图标窗口句柄
- **Shell_NotifyIcon(NIM_ADD)**：添加托盘图标（line 510）
- **tray_icon_created 标志**：跟踪创建状态（lines 37, 264, 402, 510）

**Explorer 重启恢复**：
- **WM_WINDOWPOSCHANGING**（260-267）：窗口位置变化时重建图标
- **wm_taskbar_restart 消息**（400-404）：任务栏重建时重建图标
- **RegisterWindowMessage("TaskbarCreated")**（line 229）：注册任务栏重建消息

**清理逻辑**（231-249）：
- **WM_DESTROY 处理器**：销毁托盘图标
- **g_system_session_ending 检查**：系统会话结束时跳过 Shell_NotifyIcon 删除
  - 避免阻塞 OS 关机流程

#### 上下文菜单构建（273-358）

**动态菜单重建**：
- **LoadMenu() 按需加载**（line 293）：从资源加载菜单模板
- **Quick Access 状态变更时重建**（282-287）：
  - last_quick_access_state 跟踪状态变化（line 50）
  - 状态变化时完全重建菜单
- **菜单项本地化**（297-302）：GET_RESOURCE_STRING 宏加载本地化字符串

**动态菜单项管理**：
- **Settings 菜单文本**（305-312）：根据 Quick Access 启用状态切换文本
- **Quick Access 菜单项**（326-330）：禁用时从菜单移除
- **Update available 菜单项**（338-352）：
  - 基于 update_available 标志动态插入/移除
  - 插入位置：position 0（菜单顶部）
  - 包含分隔符
- **Bug report 菜单项**（316-317）：根据 is_bug_report_running() 启用/禁用

**菜单命令处理**（134-182）：
- **handle_tray_command()**：处理菜单命令
- **WM_COMMAND 消息路由**（254-256）：分发到命令处理器

#### 窗口过程消息路由（tray_icon_window_proc, 203-407）

**标准 Windows 消息**：
- **WM_CREATE**（224-230）：
  - 注册 wm_taskbar_restart 消息
  - 存储窗口句柄
- **WM_HOTKEY**（215-223）：转发到集中式热键系统
- **WM_QUERYENDSESSION/WM_ENDSESSION**（56-96, 205-211）：
  - 记录会话结束事件
  - 设置 g_system_session_ending 标志

**自定义消息**：
- **wm_icon_notify**（托盘图标通知）：
  - **WM_RBUTTONUP/WM_CONTEXTMENU**（273-358）：显示上下文菜单
  - **WM_LBUTTONUP**（360-375）：单击处理，带双击检测
  - **WM_LBUTTONDBLCLK**（377-385）：双击处理
- **wm_run_on_main_ui_thread**（389-399）：主线程回调执行

**双击检测机制**（360-375）：
- **GetDoubleClickTime()**：获取系统双击时间间隔
- **detached timer thread**（369-373）：启动计时器线程
- **double_click_timer_running / double_clicked 标志**（lines 47-48）：状态跟踪
- 计时器到期前收到双击则取消单击动作

**DefWindowProc**（line 406）：处理未处理消息

#### 图标更新机制（409-598）

**主题感知图标更新**：
1. **get_icon() 辅助函数**（409-433）：
   - 基于 Theme 枚举加载 SVG 图标
   - update_available 状态影响图标选择
   - 路径示例：`\svgs\PowerToysWhiteUpdate.ico`（深色主题 + 更新可用）

2. **set_tray_icon_update_available()**（533-554）：
   - Shell_NotifyIcon(NIM_MODIFY) 更新图标
   - theme_adaptive_enabled 决定使用 get_icon() 或 LoadIcon()

3. **set_tray_icon_theme_adaptive()**（556-598）：
   - 在主题感知 SVG 图标与静态资源图标间切换
   - SVG 加载失败时回退到资源图标

4. **handle_theme_change()**（436-443）：
   - ThemeListener 回调（line 511 注册）
   - 仅当 theme_adaptive_enabled 为 true 时更新图标

5. **set_tray_icon_visible()**（525-531）：
   - NIS_HIDDEN 状态标志控制可见性

#### Settings UI 集成（184-201, 129-142）

**单击行为**（192-199）：
- **enableQuickAccess 设置检查**：
  - 启用：调用 open_quick_access_flyout_window()（129-132, 194）
  - 禁用：调用 open_settings_window()（line 198）

**双击行为**（line 383）：
- 始终打开 Settings 窗口

**菜单命令**（138-142）：
- **ID_SETTINGS_MENU_COMMAND**：接受 lparam 参数指定目标设置页

**遥测集成**（190, 279, 380）：
- 所有点击类型记录遥测，包含 Quick Access 状态

**Quick Access 热键**（614-646）：
- update_quick_access_hotkey() 注册热键

#### 设置窗口启动（settings_window.cpp）

**run_settings_window() 函数**（435-658）：
1. **可执行路径构建**（line 452）：
   ```cpp
   module_folder + L"\\WinUI3Apps\\PowerToys.Settings.exe"
   ```

2. **命令行参数传递**（514-530）：
   - 管道名称（runner → settings，settings → runner）
   - Runner PID
   - 主题（light/dark/system）
   - 提权状态标志
   - 管理员状态
   - OOBE 标志
   - 可选深链接目标页面

3. **进程创建**（558-567）：CreateProcessW + STARTUPINFO

4. **等待退出**（line 619）：WaitForSingleObject(process_info.hProcess, INFINITE)

**IPC 管道设置**（455-476, 584-602）：
- **UUID 生成**：UuidCreate 为每个会话生成唯一管道名
- **命名格式**：
  - `\\.\pipe\powertoys_runner_{UUID}`
  - `\\.\pipe\powertoys_settings_{UUID}`
- **回调注册**（line 584）：receive_json_send_to_main_thread
- **主线程分发**（366-370）：dispatch_run_on_main_ui_thread
- **认证策略**（590-602）：
  - 要求 Microsoft 签名的 PowerToys.Settings.exe
  - 版本必须与 Runner 一致
  - 必须位于 WinUI3Apps 目录

**全局互斥保护**（line 36）：
- ipc_mutex 保护 current_settings_ipc 访问

**页面导航枚举**（settings_window.h:5-41）：
ESettingsWindowNames 定义 30+ 设置页：Dashboard, Overview, AlwaysOnTop, Awake, ColorPicker, CmdNotFound, LightSwitch, FancyZones, FileLocksmith, Run, ImageResizer, KBM, MouseUtils, MouseWithoutBorders, Peek, PowerAccent, PowerLauncher, PowerPreview, PowerRename, FileExplorer, ShortcutGuide, Hosts, MeasureTool, PowerOCR, Workspaces, RegistryPreview, CropAndLock, EnvironmentVariables, AdvancedPaste, NewPlus, CmdPal, ZoomIt, PowerDisplay, GrabAndMove

**深链接处理**（699-727）：
- **open_settings_window()** 接受可选 settings_window 参数
- **进程已运行时**（705-716）：
  - 发送 ShowYourself IPC 消息：`{"ShowYourself":"<page_name>"}`
  - 默认页面：Dashboard（line 714）
- **进程未运行时**（720-725）：
  - spawn 新线程调用 run_settings_window
  - 传递 settings_window 参数作为第 11+ 个命令行参数

**消息分发到模块**（194-357）：
- **dispatch_received_json()**：解析传入 JSON 并路由
  - `'general'` 键：apply_general_settings（211-213）
  - `'module_status'` 键：单模块启用/禁用（221-226）
  - `'powertoys'` 键：dispatch_json_config_to_modules（line 229）
    - 遍历所有模块，调用 send_json_config_to_module（167-192）
    - 调用 module->set_config()
    - 如果 hotkeyUpdated：remove_hotkey_records() + update_hotkeys() + UpdateHotkeyEx()（156-164）
  - `'action'` 键：dispatch_json_action_to_module（67-149）
    - 路由到 general settings actions 或 module->call_custom_action()
  - 其他键：refresh, bugreport, bug_report_status, killrunner, language, check_hotkey_conflict, get_all_hotkey_conflicts

### 4.5 热键系统

#### 双轨热键架构

PowerToys 实现了两套并行的热键系统，各有优势：

**系统 1：centralized_hotkeys.cpp（RegisterHotKey API）**
- **注册机制**（43-72）：使用 Win32 RegisterHotKey API
- **数据结构**（line 11）：
  ```cpp
  std::map<Shortcut, std::vector<Action>> registeredHotkeys;
  ```
- **热键 ID 分配**：auto-incrementing，从 1 开始
- **多动作支持**：同一快捷键可注册多个动作
- **冲突处理**（45-49）：
  - 重复注册时记录警告日志
  - 允许多个模块共享同一热键
  - 执行时仅运行第一个动作（line 106）
- **系统级注册**（53-69）：
  - 每个唯一 Shortcut 仅调用 RegisterHotKey 一次
  - 后续注册复用相同的系统热键 ID

**系统 2：centralized_kb_hook.cpp（低级键盘钩子）**
- **钩子安装**（287-291）：
  ```cpp
  SetWindowsHookExW(WH_KEYBOARD_LL, KeyboardHookProc, ...)
  ```
- **数据结构**（line 23）：
  ```cpp
  std::multiset<HotkeyDescriptor> hotkeyDescriptors;
  ```
- **高级功能**：
  - 即时热键：按下立即触发
  - 按压保持动作（press-and-hold）：可配置持续时间阈值（204-213）
- **调试器检测**（278-282）：
  - DISABLE_LOWLEVEL_HOOKS_WHEN_DEBUGGED 标志
  - 调试器附加时可禁用钩子
- **清理保证**（51-57）：DestroyOnExit 对象确保退出时清理

#### 低级键盘钩子实现细节（centralized_kb_hook.cpp）

**KeyboardHookProc 流程**（88-195）：

1. **早期退出优化**：
   - **nCode < 0**（90-93）：Windows 钩子约定，立即调用 CallNextHookEx
   - **dwExtraInfo 标志检查**（97-101）：
     - CENTRALIZED_KEYBOARD_HOOK_DONT_TRIGGER_FLAG 标记 PowerToys 生成的输入
     - 防止递归触发（避免捕获自身生成的按键）
   - **非 keydown 事件**（147-150）：仅处理 WM_KEYDOWN/WM_SYSKEYDOWN
   - **空热键检查**（160-163）：modifier-only 按下时跳过查找

2. **修饰键状态检测**（153-158）：
   - **GetAsyncKeyState** 实时轮询（不依赖 KBDLLHOOKSTRUCT flags）：
     - VK_LWIN / VK_RWIN：Win 键
     - VK_CONTROL：Ctrl
     - VK_SHIFT：Shift
     - VK_MENU：Alt
   - **Hotkey 结构**：bool 标志（win, ctrl, shift, alt）+ key code

3. **热键匹配与分发**（167-190）：
   ```cpp
   // 短暂持锁查找
   std::scoped_lock lock(hotkeysMutex);  // 167-175
   auto it = hotkeyDescriptors.find(hotkey);
   if (it != end) {
       action = it->action;  // 复制 std::function
   }
   // 释放锁后调用
   if (action && action()) {  // 177-179
       // 动作返回 true：吞掉按键
       send_dummy_key_to_prevent_start_menu();  // 182-187
       return 1;  // 190
   }
   ```
   - **mutex 最小化**：持锁仅用于查找，callback 在锁外执行
   - **防止开始菜单激活**：发送 0xFF 虚拟键 key-up，带 DONT_TRIGGER_FLAG

4. **原子状态跟踪**（line 46）：
   ```cpp
   std::atomic<DWORD> vkCodePressed;
   ```
   - 无锁读取当前按下的键

#### 按压保持（Press-and-Hold）系统（centralized_kb_hook.cpp）

**注册接口**（204-213）：
```cpp
AddPressedKeyAction(moduleName, vkCode, action, durationMs)
```
- 注册需要持续按压的动作
- durationMs：触发前必须按住的时间阈值

**计时器管理**（108-131）：
- **WM_KEYDOWN**：SetTimer 启动倒计时
- **不同键按下**：KillTimer 取消之前的计时器（121-131）
- **计时器 ID 生成**（207-210）：
  ```cpp
  timer_id = (hash(moduleName) << 16) | vkCode
  ```
  - 高 16 位：模块名哈希
  - 低 16 位：虚拟键码

**计时器回调验证**（PressedKeyTimerProc, 60-86）：
- **GetAsyncKeyState 双重检查**：验证键仍然物理按下
- **防止幽灵激活**：计时器触发但键已释放时不执行动作

#### 冲突检测系统（centralized_hotkeys.cpp）

**警告机制**（45-49）：
```cpp
if (registeredHotkeys.contains(shortcut)) {
    Logger::warn("Hotkey already registered: {}", shortcut.toString());
}
registeredHotkeys[shortcut].push_back(action);
```
- 不阻止重复注册
- 仅记录警告日志
- 维护动作向量

**执行策略**（PopulateHotkey, 98-117）：
```cpp
// 仅执行第一个动作
if (!actions.empty()) {
    try {
        actions[0]();  // line 106
    } catch (...) {
        Logger::error("Hotkey action threw exception");
    }
}
```
- 异常处理包裹
- 后续动作被忽略

#### 模块生命周期集成

**热键注册时机**：
- 模块 get_hotkeys() 返回 Hotkey[] 数组
- Runner 注册后按 id 回调 on_hotkey()
- **重要**：模块禁用时仍会收到 on_hotkey() 调用

**新式热键接口**（GetHotkeyEx/OnHotkeyEx）：
- 使用 VK + 修饰掩码（HotkeyEx 结构）
- 更细粒度控制
- Workspaces、NewPlus 等新模块使用

**热键清理**：

1. **centralized_hotkeys.cpp**（UnregisterHotkeysForModule, 74-96）：
   - 遍历 registeredHotkeys map
   - 查找指定模块的所有动作
   - 从 action vector 移除
   - 最后一个动作移除时调用 UnregisterHotKey

2. **centralized_kb_hook.cpp**（ClearModuleHotkeys, 255-274）：
   - 移除 hotkeyDescriptors 中的模块热键
   - 调用 ClearPressedKeyActions 清理按压动作

**按压动作清理**（215-253）：
- KillTimer 停止所有活动计时器
- 从 descriptors 移除
- 如果无剩余 descriptors：重置 vkCodePressed

#### 冲突检测集成（Settings v2 约定）

**顺序约定**：
- 模块 get_hotkeys() / GetHotkeyEx() 顺序
- Settings.UI.Library IHotkeyConfig.GetAllHotkeyAccessors() 顺序
- ViewModel.GetAllHotkeySettings() 顺序
- **必须完全一致**（顺序即热键 id）

**生效时机**：
- 页面加载后调用 OnPageLoaded()
- Kit 的 Awake/LightSwitch 遵循相同约定

#### 遗留 Win 键跟踪（不推荐新模块使用）

**遗留接口**：
- keep_track_of_pressed_win_key()
- milliseconds_win_key_must_be_pressed()
- ShortcutGuide 专用
- 新模块应使用 press-and-hold 系统

### 4.6 Quick Access

quick_access_host.cpp：Win+Space 触发的 WinUI3 快速访问；Runner 作为宿主协调，经命名管道与 PowerToys.QuickAccess.exe 通信；无直接快捷动作的模块回退为打开设置页。Kit 的快速访问沿用同一模型（图标白名单由裁剪 target 维护）。

### 4.7 AI 能力检测（后台线程，非阻塞）

- ai_detection.h + main.cpp：仅当 ImageResizer 启用时，后台线程调用 WinUI3Apps/PowerToys.ImageResizer.exe --detect-ai，等待至多 30 秒；结果写入缓存文件供 ImageResizer 常规启动读取；
- Settings 变更（apply_general_settings）时也会重新触发（general_settings.cpp:254/441）；
- 注意：旧文档称"阻塞式 AI 检测"已过时——当前实现明确为后台线程、不阻塞启动。

### 4.8 更新系统

- UpdateUtils.cpp：检查 GitHub releases → 通知 → 下载 → 安装；状态机 update_state（common/updating/updateState.h）：upToDate / readyToDownload / readyToInstall / errorDownloading / networkError，持久化到 updateState.json（store() 以文件锁防止并发修改）；
- settings_telemetry.cpp：周期性触发模块设置遥测上报；
- Kit：更新保持 check-only（不自动下载安装），遥测全部移除。

### 4.9 其他 Runner 组件

- OOBE / SCOOBE：首次运行向导（runner 参数 openOobe/openScoobe；Kit 移除）；
- restart_elevated / RestartManagement：提权重启；
- auto_start_helper：开机自启注册；
- unhandled_exception_handler：Debug 下异常栈回溯；
- bug_report：BugReportTool 启动（Kit 不引入）。

## 5. Settings 系统（Settings v2）

### 5.1 工程结构

- src/settings-ui/Settings.UI（PowerToys.Settings.exe，WinUI3 unpackaged .NET 应用，MVVM）：
  - Views：XAML 页面（GeneralPage、DashboardPage、各模块页、SearchResultsPage、ShellPage）；
  - ViewModels：ShellViewModel、GeneralViewModel、各模块 ViewModel；
  - Controls：ShortcutControl、SettingsGroup、Timeline、TitleBar 等；
  - Serialization：SourceGenerationContext 源生成 JSON 序列化；
  - 通信：TwoWayPipeMessageIPCManaged 与 Runner 建连，IPCMessageReceivedCallback 收到消息后经 ShellPage.ShellHandler.IPCResponseHandleList 分发；
- src/settings-ui/Settings.UI.Library（PowerToys.Settings.UI.Lib.dll）：
  - 设置模型：每模块 *Settings.cs + *Properties.cs（ISettingsConfig）；
  - SettingsRepository<T> 单例：读/写 + FileSystemWatcher 热加载（SettingsChanged 事件）；
  - SettingsUtils：GetSettingsFilePath(moduleName, fileName) 拼路径、JSON 读写、损坏文件回退默认值并重建；
  - SettingsBackupAndRestoreUtils / SettingsFactory / 模块注册目录 / GPO 帮助；
- src/settings-ui/QuickAccess.UI（PowerToys.QuickAccess.exe）；
- src/settings-ui/Settings.UI.Controls / Common.UI.Controls / Common.UI（共享控件；Common.UI 保留 WPF/WinForms 依赖，Settings v2 不再使用）。

### 5.1a WinUI3 应用初始化详解

#### 应用启动流程（App.xaml.cs）

**Application 入口**（84-104）：
1. **Logger 初始化**：配置日志系统
2. **语言覆盖**：设置 UI 语言
3. **未处理异常注册**：UnhandledException handler
4. **NativeEventWaiter**：监听 Runner 终止事件

**窗口单例管理**（111-139, 226-249）：
- **懒加载模式**：首次访问时创建窗口
- **IPC 管道设置**：与 Runner 进程双向通信
- **延迟导航**：避免 flyout 消失问题

#### MainWindow 构造流程（MainWindow.xaml.cs:26-136）

**构造器阶段**（26-136）：

1. **启动时间遥测**（28-29）：
   ```csharp
   var bootTime = DateTime.Now;
   ```

2. **主题服务初始化**（33-34）：
   ```csharp
   var themeManager = new ThemeManager();
   themeManager.Initialize(this);
   ```

3. **标题栏定制**（line 36）：
   ```csharp
   ExtendsContentIntoTitleBar = true;
   ```

4. **提权状态传播**（38-39）：
   ```csharp
   ShellPage.IsElevated = isElevated;
   ShellPage.IsUserAnAdmin = isAdmin;
   ```

5. **窗口位置反序列化**（41-51）：
   - 从上次会话恢复窗口位置和大小
   - 使用 LayoutSettings 持久化

6. **IPC 回调注册**（57-108）：
   - **SetDefaultSndMessageCallback**（57-61）：常规设置变更
   - **SetRestartAdminSndMessageCallback**（64-68）：提权重启请求
   - **SetCheckForUpdatesMessageCallback**（71-74）：更新检查请求
   - **SetOpenMainWindowCallback**（77-81）：模块触发窗口激活
   - **SetUpdatingGeneralSettingsCallback**（84-108）：
     - 模块启用/禁用
     - 冲突预防机制

7. **IPCMessageReceivedCallback 设置**（114-131）：
   ```csharp
   App.IPCMessageReceivedCallback = (string msg) => {
       var success = JsonObject.TryParse(msg, out JsonObject json);
       if (success) {
           foreach (Action<JsonObject> handle in 
                    ShellPage.ShellHandler.IPCResponseHandleList) {
               handle(json);
           }
       }
   };
   ```

8. **组件初始化**（line 110）：
   ```csharp
   InitializeComponent();  // XAML 加载
   ```

#### ShellPage 根容器初始化（ShellPage.xaml.cs:113-145）

**核心初始化步骤**（113-145）：

1. **SettingsRepository 单例**（line 118）：
   ```csharp
   var settingsRepository = SettingsRepository<GeneralSettings>
       .GetInstance(settingsUtils);
   ```

2. **ViewModel 构造与注入**（line 119）：
   ```csharp
   ViewModel = new ShellViewModel(settingsRepository);
   ```

3. **DataContext 绑定**（line 121）：
   ```csharp
   DataContext = ViewModel;
   ```

4. **导航框架初始化**（line 123）：
   ```csharp
   ViewModel.Initialize(shellFrame, navigationView, KeyboardAccelerators);
   ```

5. **IPC 响应处理器注册**（127-128）：
   ```csharp
   ShellHandler.IPCResponseHandleList.Add(ReceiveMessage);
   ```

6. **NavigationView 层次结构**（136-144）：
   - 构建父子导航项
   - 支持嵌套页面导航

#### IPC 通信架构（双向委托模式）

**消息发送通道**（ShellPage.xaml.cs:147-168）：

1. **SendDefaultIPCMessage**（147-151）：
   ```csharp
   public static void SendDefaultIPCMessage(string msg) {
       defaultSndMSGCallback?.Invoke(msg);
   }
   ```
   - 广播设置变更到 Runner

2. **SendRestartAdminIPCMessage**（164-168）：
   - 触发提权重启

3. **其他专用通道**：
   - CheckForUpdates
   - OpenMainWindow

**消息接收处理**（337-350）：

**ReceiveMessage handler**（337-350）：
```csharp
private void ReceiveMessage(JsonObject json) {
    if (json.TryGetValue("ShowYourself", out var value)) {
        string pageName = value.GetString();
        // 激活指定页面
        Navigate(GetPageType(pageName));
    }
}
```

**IPC Manager 设置**（App.xaml.cs:226-233）：
```csharp
var ipcManager = new TwoWayPipeMessageIPCManaged(
    pipeNameRunner,
    pipeNameSettings,
    (string message) => {
        // 收到消息时调用 IPCMessageReceivedCallback
        App.IPCMessageReceivedCallback?.Invoke(message);
    }
);
```

### 5.2 Settings 持久化架构

#### SettingsRepository 单例模式（SettingsRepository`1.cs）

**泛型单例实现**（15-48）：
```csharp
public sealed class SettingsRepository<T> : ISettingsRepository<T>
    where T : class, ISettingsConfig, new()
{
    private static SettingsRepository<T>? instance;
    private static readonly object lockObject = new object();

    public static SettingsRepository<T> GetInstance(ISettingsUtils settingsUtils) {
        if (instance == null) {
            lock (lockObject) {
                if (instance == null) {
                    instance = new SettingsRepository<T>(settingsUtils);
                }
            }
        }
        return instance;
    }

    private SettingsRepository(ISettingsUtils settingsUtils) {
        // 私有构造器防止直接实例化
    }
}
```

**线程安全保证**：
- 双重检查锁定（double-checked locking）
- lock 确保仅创建一个实例
- 每个泛型类型 T 独立单例

#### 懒加载机制（112-132）

**SettingsConfig 属性**（115-119）：
```csharp
public T SettingsConfig {
    get {
        if (settingsConfig == null) {
            settingsConfig = settingsUtils.GetSettingsOrDefault<T>(settingFileName);
        }
        return settingsConfig;
    }
}
```

**GetSettingsOrDefault 实现**（SettingsUtils.cs:92-116）：
1. 尝试从磁盘读取 JSON
2. 反序列化为类型 T
3. 失败时返回 `new T()`（默认设置）
4. 缓存到 settingsConfig 字段

#### FileSystemWatcher 热重载（55-92）

**Watcher 初始化**（55-78）：
```csharp
private void InitializeWatcher() {
    var settingsDirectory = Path.GetDirectoryName(settingPath);
    watcher = new FileSystemWatcher(settingsDirectory) {
        NotifyFilter = NotifyFilters.LastWrite,
        Filter = Path.GetFileName(settingPath),
        EnableRaisingEvents = true
    };
    watcher.Changed += Watcher_Changed;
}
```

**变更处理与重试逻辑**（80-92）：
```csharp
private void Watcher_Changed(object sender, FileSystemEventArgs e) {
    const int maxRetries = 5;
    for (int i = 0; i < maxRetries; i++) {
        try {
            settingsConfig = settingsUtils.GetSettingsOrDefault<T>(settingFileName);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return;
        } catch (IOException) {
            // 文件被其他进程锁定
            Thread.Sleep(100);
        }
    }
}
```

**重试策略**：
- 最多 5 次尝试
- 每次 100ms 延迟
- 处理文件锁定场景

#### SettingsUtils 持久化层（SettingsUtils.cs）

**SaveSettings 实现**（208-232）：
```csharp
public void SaveSettings(string jsonSettings, string moduleName, string fileName) {
    var settingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "PowerToys", moduleName
    );
    Directory.CreateDirectory(settingsFolder);
    var settingsPath = Path.Combine(settingsFolder, fileName);
    
    File.WriteAllText(settingsPath, jsonSettings, Encoding.UTF8);
}
```

**GetSettingsOrDefault 容错**（92-116）：
```csharp
public T GetSettingsOrDefault<T>(string moduleName, string fileName) 
    where T : class, ISettingsConfig, new()
{
    try {
        var json = File.ReadAllText(settingsPath);
        // Trim('\0') 解决 NTFS 损坏问题 (line 193)
        return JsonSerializer.Deserialize<T>(json.Trim('\0'), context);
    } catch (Exception ex) {
        Logger.LogError("Settings file corrupted, using defaults", ex);
        return new T();  // 回退到默认值
    }
}
```

**Native AOT 兼容**（188-205）：
```csharp
[JsonSerializable(typeof(GeneralSettings))]
[JsonSerializable(typeof(AwakeSettings))]
// ... 所有设置类型
public partial class SettingsSerializationContext : JsonSerializerContext { }
```
- 源生成器支持 AOT 编译
- 避免运行时反射

**设置升级支持**（79-82）：
```csharp
public void UpgradeSettingsConfiguration<T>(string moduleName, 
    Func<T, T> upgradeFunc) where T : ISettingsConfig
```
- 迁移旧版本设置格式

### 5.3 MVVM 架构实现

#### Observable 基类（Observable.cs:10-28）

**INotifyPropertyChanged 实现**：
```csharp
public class Observable : INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T storage, T value, 
        [CallerMemberName] string? propertyName = null) {
        if (Equals(storage, value)) {
            return false;  // 防止冗余通知
        }
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
```

**CallerMemberName 特性**：
- 编译器自动填充属性名
- 消除手动字符串错误

#### ViewModel 层次结构

**PageViewModelBase**（PageViewModelBase.cs:18）：
```csharp
public abstract class PageViewModelBase : Observable, IDisposable {
    // 热键冲突管理
    // IDisposable 模式
}
```

**ShellViewModel**（ShellViewModel.cs:25-80）：
```csharp
public class ShellViewModel : Observable {
    private readonly GeneralSettings _generalSettingsConfig;
    
    public ShellViewModel(ISettingsRepository<GeneralSettings> settingsRepository) {
        _generalSettingsConfig = settingsRepository.SettingsConfig;
    }
    
    // 导航状态管理
    private bool _isBackEnabled;
    public bool IsBackEnabled {
        get => _isBackEnabled;
        set => Set(ref _isBackEnabled, value);
    }
}
```

**构造器注入模式**：
- 接收 `ISettingsRepository<T>` 依赖
- 解耦 ViewModel 与持久化层
- 单元测试友好

#### DataContext 绑定流程

**典型页面构造器**（DashboardPage.xaml.cs:39）：
```csharp
public DashboardPage() {
    var settingsUtils = SettingsUtils.Default;
    var repository = SettingsRepository<GeneralSettings>.GetInstance(settingsUtils);
    ViewModel = new DashboardViewModel(repository);
    DataContext = ViewModel;
    InitializeComponent();
}
```

**XAML 绑定**：
```xml
<TextBlock Text="{x:Bind ViewModel.Title, Mode=OneWay}" />
```
- x:Bind 编译时绑定（比 Binding 快）
- Mode=OneWay/TwoWay 控制方向

#### ViewModel 间通信

**ShellHandler 静态引用**（ShellPage.xaml.cs:122）：
```csharp
ShellPage.ShellHandler = this;  // 静态字段存储
```

**跨组件访问**：
```csharp
ShellPage.ShellHandler.SignalGeneralDataUpdate();
```

**SignalGeneralDataUpdate**（246-253）：
```csharp
public void SignalGeneralDataUpdate() {
    if (shellFrame.Content is IRefreshablePage page) {
        page.RefreshEnabledState();
    }
}
```

**IRefreshablePage 接口**：
- 允许页面响应外部更新
- Settings 变更时刷新 UI

### 5.4 导航服务架构（NavigationService.cs）

#### 静态服务模式（15-81）

**Frame 管理**（21-36）：
```csharp
private static Frame? _frame;

public static Frame Frame {
    get => _frame;
    set {
        if (_frame != null) {
            _frame.Navigated -= OnNavigated;
        }
        _frame = value;
        if (_frame != null) {
            _frame.Navigated += OnNavigated;
        }
    }
}
```

**Navigate 方法**（56-81）：
```csharp
public static bool Navigate(Type pageType, object? parameter = null) {
    // 防止重复导航
    if (Frame.Content?.GetType() == pageType && 
        Equals(parameter, lastParamUsed)) {
        return false;
    }
    lastParamUsed = parameter;
    return Frame.Navigate(pageType, parameter);
}
```

**泛型重载**（83-85）：
```csharp
public static bool Navigate<T>(object? parameter = null) 
    where T : Page {
    return Navigate(typeof(T), parameter);
}
```

#### 导航初始化（ShellViewModel.cs:82-93）

**Initialize 方法**（82-93）：
```csharp
public void Initialize(Frame frame, NavigationView navigationView, 
    KeyboardAccelerator[] keyboardAccelerators) {
    
    NavigationService.Frame = frame;
    NavigationService.NavigationFailed += Frame_NavigationFailed;
    NavigationService.Navigated += Frame_Navigated;
    
    // 注册 BackRequested 处理器
    // 构建导航层次结构
}
```

**事件处理器**：
- **NavigationFailed**：处理导航错误
- **Navigated**：更新 UI 状态（后退按钮、选中项）
- **BackRequested**：触发 GoBack

#### 导航执行路径

**静态 Navigate 方法**（ShellPage.xaml.cs:225-228）：
```csharp
public static void Navigate(Type pageType) {
    NavigationService.Navigate(pageType);
}
```

**跨窗口导航**（MainWindow.xaml.cs:146-149）：
```csharp
public void NavigateToSection(string section) {
    ShellPage.Navigate(GetPageType(section));
}
```

**IPC 触发导航**（ShellPage.xaml.cs:343-347）：
```csharp
// OpenMainWindowCallback 内
if (json.TryGetValue("page", out var pageName)) {
    Navigate(GetPageType(pageName.GetString()));
}
```

#### 页面生命周期钩子

**NavigationService.Navigated 事件**：
```csharp
NavigationService.Navigated += (s, e) => {
    IsBackEnabled = NavigationService.CanGoBack;
    UpdateNavigationViewSelection(e.Content);
};
```

**IRefreshablePage.RefreshEnabledState**（248-252）：
```csharp
if (shellFrame.Content is IRefreshablePage page) {
    page.RefreshEnabledState();
}
```

**PageViewModelBase.OnPageLoaded**（34-38）：
- 页面级初始化钩子
- 热键冲突检查

#### 导航状态管理（ShellViewModel.cs:43-65）

**状态属性**：
```csharp
private bool _isBackEnabled;
public bool IsBackEnabled {
    get => _isBackEnabled;
    set => Set(ref _isBackEnabled, value);
}

private object? _selected;
public object? Selected {
    get => _selected;
    set => Set(ref _selected, value);
}
```

**NavigationView 同步**（ShellPage.xaml.cs:322-334）：
```csharp
private void UpdateNavigationViewSelection(object content) {
    var item = FindNavigationItemForPage(content);
    if (item != null) {
        ViewModel.Selected = item;
        // 展开父节点（327-333）
    }
}
```

**默认页面恢复**（352-355）：
```csharp
public void EnsurePageIsSelected() {
    if (ViewModel.Selected == null) {
        Navigate(typeof(DashboardPage));
    }
}
```

#### 导航参数

**参数传递**（NavigationService.cs:56）：
```csharp
public static bool Navigate(Type pageType, object? parameter = null)
```

**重复导航检测**（59, 66）：
```csharp
private static object? lastParamUsed;

if (Frame.Content?.GetType() == pageType && 
    Equals(parameter, lastParamUsed)) {
    return false;  // 防止重复导航
}
```

**参数类型**：
- **NavigationParams**（ShellPage.xaml.cs:509-511）：元素特定导航
- **SearchResultsNavigationParams**（492, 642）：搜索结果

### 5.2 存储布局（已修正）

- 根目录：%LOCALAPPDATA%/Microsoft/PowerToys/；
- 全局配置：根下 settings.json（startup、enabled、theme、update 等；native 侧 PTSettingsHelper::load_general_settings）；
- 每模块配置：%LOCALAPPDATA%/Microsoft/PowerToys/<模块名>/settings.json（模块专属；native 模块经 set_config 接收，Settings v2 经 SettingsRepository 读写）；
- 注意：旧文档"general_settings.json 内含 module_settings 嵌套对象"是早期版本模型，当前已改为每模块独立 JSON。

### 5.3 配置流

1. 模块 get_config() 输出"设置 UI 描述"JSON：由 src/common/SettingsAPI/settings_objects.h 的 PowerToysSettings::Settings 构造（add_bool_toggle、add_int_spinner、add_string、add_multiline_string、add_color_picker、add_hotkey、add_choice_group、add_dropdown、add_custom_action、add_header_szLarge、set_description / set_icon_key / set_overview_link / set_video_link，serialize_to_buffer 两遍调用）；读取侧用 PowerToyValues（load_from_settings_file / from_json_string、get_bool_value 等、save_to_settings_file）；
实际例子（AwakeModuleInterface/dllmain.cpp 的 get_config / set_config）：

    virtual bool get_config(wchar_t* buffer, int* buffer_size) override
    {
        PowerToysSettings::Settings settings(hinstance, get_name());
        settings.set_description(MODULE_DESC);
        return settings.serialize_to_buffer(buffer, buffer_size);
    }

    virtual void set_config(const wchar_t* config) override
    {
        PowerToysSettings::PowerToyValues values =
            PowerToysSettings::PowerToyValues::from_json_string(config, get_key());
        values.save_to_settings_file();  // 无需自定义处理时直接持久化
    }

2. Settings UI 渲染该描述；用户修改 → SettingsRepository 写盘（每模块 settings.json）；
3. 经 IPC 把变更发给 Runner → dispatch_json_config_to_modules → 模块 set_config()；
4. 自定义动作：Settings 按钮 → call_custom_action()；
5. 深链接：powertoys:// 协议（Kit 同时支持 kit://）。
6. 特例存储：PowerToys Run 自行监听 %LOCALAPPDATA%/Microsoft/PowerToys/Launcher/settings.json；Keyboard Manager 与 Settings 共享 default.json（以命名文件互斥量防写竞争）。

## 6. IPC 与安全

### 6.1 TwoWayPipeMessageIPC 实现详解

#### 架构概览（two_way_pipe_message_ipc.cpp）

**双向通信模型**（170-179）：
- **双管道设计**：使用两个单向命名管道实现全双工通信
- **input_pipe**：本进程作为 server，接受连接并读取消息
- **output_pipe**：本进程作为 client，连接到远端 server 并写入消息

**专用线程池**（309-317）：
```cpp
input_pipe_thread   -> start_named_pipe_server()    // line 316
output_queue_thread -> consume_output_queue_thread() // line 312
input_queue_thread  -> consume_input_queue_thread() // line 314
```

#### 命名管道创建与安全（931-968, 579-711）

**CreateNamedPipe 参数**（945-963）：
- **FILE_FLAG_FIRST_PIPE_INSTANCE**：确保独占所有权
- **PIPE_ACCESS_DUPLEX | WRITE_DAC**：双向访问 + DACL 修改权限
- **PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE**：消息模式（自动边界保持）
- **PIPE_WAIT**：同步操作模式
- **PIPE_REJECT_REMOTE_CLIENTS**：拒绝远程连接

**安全描述符构建**（create_pipe_security_attributes, 579-711）：

1. **DACL 授权策略**：
   - **Server SID**（FILE_ALL_ACCESS）：
     - Elevated：Administrators 组
     - Non-elevated：当前用户 SID
   - **SYSTEM**（FILE_ALL_ACCESS）：系统账户完全访问
   - **Clients**（PipeClientAccess, 9-14）：
     ```cpp
     FILE_READ_DATA | FILE_READ_ATTRIBUTES | READ_CONTROL |
     FILE_WRITE_DATA | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE
     ```

2. **登录 SID 会话隔离**（GetLogonSID, 493-571）：
   - 提取进程的 logon SID
   - 将管道访问限制在当前会话
   - 防止跨会话攻击

3. **出站客户端标志**（two_way_pipe_message_ipc.h:8）：
   ```cpp
   FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION
   ```
   - **SECURITY_IDENTIFICATION**：防止服务端模拟客户端

#### 消息帧格式（773-796, 801-805, 420-429）

**宽字符串消息模式**：
- **传输单元**：`std::wstring`（wchar_t）
- **边界保持**：PIPE_TYPE_MESSAGE 自动处理
- **BUFSIZE**：1024 字节块（line 8）

**读取流程**（783-796）：
```cpp
do {
    success = ReadFile(pipe, buffer, BUFSIZE, &bytesRead, NULL);
    message.append(buffer, bytesRead / sizeof(wchar_t));
} while (!success && GetLastError() == ERROR_MORE_DATA);
```
- 动态扩展缓冲区直到 ReadFile 完成
- 移除尾部 null 填充（801-805）

**写入流程**（440, 453-457）：
```cpp
DWORD bytesToWrite = lstrlen(message) * sizeof(WCHAR);
WriteFile(pipe, message, bytesToWrite, NULL, &overlapped);
```
- 计算字节大小（宽字符）
- 单次 overlapped WriteFile

**管道模式设置**（420-429）：
- **SetNamedPipeHandleState**：output pipe 设为 PIPE_READMODE_MESSAGE
- 确保读写两端模式一致

#### 线程安全与同步（two_way_pipe_message_ipc_impl.h:128-143）

**五重互斥锁保护**：

1. **lifecycle_mutex**（128）：
   - 保护 lifecycle_state 状态机（NotStarted/Starting/Running/Stopping/Stopped）
   - 配合 lifecycle_stopped 条件变量协调关闭（224-248, 280-306）

2. **pipe_connect_handle_mutex**（131）：
   - 保护 current_connect_pipe_handle
   - ConnectNamedPipe 操作期间持锁（334-338, 980-991）
   - 支持安全取消

3. **output_pipe_mutex**（132）：
   - 保护 active_output_pipe_handle
   - overlapped write 期间持锁（354-358, 446-452）
   - 协调取消操作

4. **connection_handlers_mutex**（133）：
   - 保护 connection_handlers vector
   - spawn/reap handler threads 时序列化（849-851, 871-886, 901-911）

5. **AsyncMessageQueue 内部锁**（async_message_queue.h:11-14, 26-36）：
   - queue_mutex + message_ready 条件变量
   - 生产者-消费者协调

**无锁原子标志**（line 140）：
```cpp
std::atomic<bool> closed;
```
- 快速关闭信号，无需持锁

#### 双向通信协议（170-179, 481-491, 1045-1064）

**输入管道（Server 角色）**：
1. **CreateNamedPipe**：创建监听管道（931-968）
2. **ConnectNamedPipe**：等待客户端连接（line 987）
3. **ReadFile 循环**：读取消息直到边界（783-796）
4. **回调分发**：
   ```cpp
   input_queue.queue(message);  // line 807
   // consume_input_queue_thread 处理：
   callback(message);           // line 1060
   ```

**输出管道（Client 角色）**：
1. **CreateFile + WaitNamedPipe**：连接到远端 server（372-405）
   - ERROR_PIPE_BUSY 时重试，WaitNamedPipe(100ms)
2. **SetNamedPipeHandleState**：设置 PIPE_READMODE_MESSAGE（420-429）
3. **send() 入队**：
   ```cpp
   output_queue.queue(message);  // line 219
   ```
4. **consume_output_queue_thread 消费**（481-491）：
   - 从队列取消息
   - 调用 send_pipe_message overlapped write

#### 错误处理与自动恢复（1005-1013, 846-866, 370-405）

**自动监听器替换**（1005-1013）：
```cpp
// 接受连接后立即创建替换监听器
HANDLE replacement = CreateNamedPipe(..., FILE_FLAG_FIRST_PIPE_INSTANCE, ...);
while (replacement == INVALID_HANDLE_VALUE && transient_error) {
    Sleep(10);
    replacement = CreateNamedPipe(...);
}
// 然后 spawn handler thread 处理已接受的连接
```
- 瞬态失败时 10ms 延迟重试
- 确保服务始终可用

**连接处理器管理**（846-866, 869-895）：
- **spawn_connection_handler**：为每个连接创建新线程
- **reap_finished_connection_handlers**：
  - 定期清理已完成的线程
  - joinable() 检查 + join()
  - 自动从 connection_handlers vector 移除

**出站客户端重试**（370-405）：
```cpp
while (true) {
    pipe = CreateFile(pipeName, ...);
    if (pipe != INVALID_HANDLE_VALUE) break;
    if (GetLastError() == ERROR_PIPE_BUSY) {
        WaitNamedPipe(pipeName, 100);  // PipeWaitIntervalMs
        continue;
    }
    return;  // 其他错误：静默失败
}
```

**overlapped write 错误处理**（459-474）：
```cpp
if (!WriteFile(..., &overlapped)) {
    if (GetLastError() == ERROR_IO_PENDING) {
        GetOverlappedResult(pipe, &overlapped, &bytesWritten, TRUE);  // 阻塞等待
    }
}
```

**关闭时取消**（319-343, 897-920）：
- **CancelIoEx**：取消所有 active handles 上的 pending I/O（lines 337, 357, 906）
- **joinable() 检查**：仅 join 可 join 的线程
- **handler threads 启动失败**：finished 立即标记并 reap（861-866）

**静默失败策略**：
- send_pipe_message 连接失败时直接返回，不抛异常（lines 390, 403, 428, 464）
- 确保上层调用不被阻塞

#### 生命周期状态机（224-248, 280-306）

**状态转换**：
```
NotStarted -> Starting -> Running -> Stopping -> Stopped
```

**start() 流程**（224-248）：
```cpp
{
    std::scoped_lock lock(lifecycle_mutex);
    if (lifecycle_state != NotStarted) throw;
    lifecycle_state = Starting;
}
// spawn 3 threads
lifecycle_state = Running;
```

**stop() 流程**（280-306）：
```cpp
{
    std::scoped_lock lock(lifecycle_mutex);
    if (lifecycle_state == Stopped) return;
    lifecycle_state = Stopping;
}
closed = true;  // 原子信号
// cancel all I/O
// join threads
{
    std::scoped_lock lock(lifecycle_mutex);
    lifecycle_state = Stopped;
    lifecycle_stopped.notify_all();
}
```

### 6.2 特权管道客户端认证 pipe_caller_auth（安全重点，新增）

#### 威胁模型

**同用户攻击场景**：
- 攻击者进程与受信任子进程共享：
  - 相同用户 SID
  - 相同完整性级别
  - 相同登录会话
- **DACL 无法区分**：传统管道安全描述符不足以防御

#### fail-closed 认证方案（pipe_caller_auth.h/cpp）

**认证时机**（762-769）：
```cpp
if (caller_policy.enabled) {
    bool authenticated = interop_auth::AuthenticateClient(
        pipe, caller_policy, caller_cache, logReject
    );
    if (!authenticated) {
        return;  // 静默拒绝，handler 早退
    }
}
// 仅认证通过后才分发消息
```

**二进制身份校验项**（pipe_caller_auth.h:29-54）：

1. **镜像目录约束**：
   - 必须位于 `<module_directory>/WinUI3Apps`
   - 防止从任意路径加载

2. **基名白名单**：
   - 例如：仅允许 `PowerToys.Settings.exe`

3. **版本完全一致**：
   - 客户端版本必须与 Runner 完全相同
   - 防止降级攻击

4. **Authenticode 签名**（Release 模式）：
   - 要求 Microsoft 数字签名
   - Debug 模式豁免（开发便利）

**CallerPolicy 结构**（pipe_caller_auth.h:31-50）：
```cpp
struct CallerPolicy {
    bool enabled;
    std::optional<DWORD> exactPid;           // PID 绑定
    std::vector<std::wstring> allowedBasenames;
    std::wstring requiredDirectory;
    std::wstring requiredVersion;
    bool requireMicrosoftSignature;
};
```

#### 验证缓存机制（pipe_caller_auth.h:62-139）

**VerificationCache 实现**：
- **缓存键**：(PID, 进程创建时间)
  - 创建时间防止 PID 复用攻击
- **TTL**：60 秒
- **线程安全**：内部 mutex 保护
- **避免重复校验**：相同进程的后续连接直接返回缓存结果

**缓存失效触发**：
- TTL 过期
- 进程退出（PID 复用时创建时间不匹配）

#### 拒绝日志记录

**logReject 回调**（line 768）：
- 注入的日志函数
- 记录拒绝原因（路径、版本、签名等）
- 不向客户端泄露拒绝细节（fail-closed）

#### Runner 集成示例（settings_window.cpp:590-602）

```cpp
CallerPolicy policy;
policy.enabled = true;
policy.allowedBasenames = {L"PowerToys.Settings.exe"};
policy.requiredDirectory = module_folder + L"\\WinUI3Apps";
policy.requiredVersion = get_product_version();
policy.requireMicrosoftSignature = !is_debug_build();

ipc->start(pipe_name_runner, pipe_name_settings, policy);
```

#### Kit 现状与规划

**当前状态**：
- 未接入 pipe_caller_auth 机制
- 依赖基础 DACL 保护

**未来加固**（next.md §12.2 已列为 P1）：
- 同步 pipe_caller_auth 实现
- 应用到 Settings UI 和 Quick Access 连接
- 防御同名用户攻击面

## 7. GPO 策略

- 注册表位置：HKLM 与 HKCU 的 SOFTWARE/Policies/PowerToys；策略值命名 ConfigureEnabledUtility<模块>（按模块），全局 ConfigureGlobalUtilityEnabledState；PowerLauncher 插件白名单 PowerLauncherIndividualPluginEnabledList；
- 状态枚举 gpo_rule_configured_t：wrong_value(-3) / unavailable(-2) / not_configured(-1) / disabled(0) / enabled(1)；
- 消费方：native 侧 common/utils/gpo.h 工具函数 + 模块接口重写 gpo_policy_enabled_configuration()；C# 侧 common/GPOWrapper（COM 投影 PowerToys.GPOWrapper），Settings UI 用它禁用被策略管理的设置项；
- 策略资产：src/gpo/assets（PowerToys.admx / .adml）；
- Runner 在 start_enabled_powertoys()（general_settings.cpp:506 起）决定启停：先收集各模块 gpo_policy_enabled_configuration() 到映射，结合 is_enabled_by_default()（非默认启用模块需用户显式开启），再按优先级：GPO 强制启用 > GPO 强制禁用 > 用户配置；运行时 apply_general_settings 走同一逻辑；
- Kit：保留 gpo.h 与 GPOWrapper 基建，策略资产已裁剪到活动模块（Awake/LightSwitch）。

## 8. 遥测（ETW，Kit 不引入）

- C++：common/Telemetry/EtwTrace（TraceLoggingWriteWrapper、TraceBase.h、ProjectTelemetry.h、TraceLoggingDefines.h），provider 为 Microsoft.PowerToys，Trace::EventLaunch 等事件；
- C#：common/ManagedTelemetry（Microsoft.PowerToys.Telemetry 程序集，PowerToysTelemetry.Log.WriteEvent），AdvancedPaste/Awake 等托管模块使用；
- settings_telemetry：周期上报模块设置；
- Kit：全部不引入（AGENTS.md 边界）；但 Awake 插件"原样复制"需要 Kit 侧提供 ManagedTelemetry 编译支持（见 next.md §13.4）。

## 9. 公共库（src/common）

- version：version.h + version.vcxproj 生成 version_gen.h（MAJOR/MINOR/REVISION/BUILD）；
- logger：spdlog 封装 + logger_settings.h（按模块独立日志名）；
- Telemetry：EtwTrace / TraceBase / ProjectTelemetry / TraceLoggingDefines（C++ ETW）；
- interop：TwoWayPipeMessageIPC + pipe_caller_auth + PowerToys.Interop（C++/C# 互操作 WinMD）；
- SettingsAPI（src/common/SettingsAPI/）：settings_objects.h 的 PowerToysSettings（设置 UI 描述构造）与 PowerToyValues（设置读写）、settings_helpers.h；
- utils：json.h、gpo.h、appMutex.h、elevation.h、processApi.h、resources.h、os-detect.h、clean_video_conference.h、process_path.h、window.h 等；
- updating：updating.cpp、updateState.cpp、installer.cpp（更新状态机与安装器，Kit 保留 check-only）；
- notifications / display / comUtils / actionRunner / GPOWrapper；
- ManagedCommon、ManagedCsWin32（C# 基础库）；
- ModuleContracts：托管模块契约（PowerToys Run 插件）；
- Common.UI：遗留 WPF/WinForms 共享库；Common.Search：搜索匹配（StringMatcher）。

各库与 Kit 的同步状态见 next.md §12。

## 10. 模块清单（当前 33 个目录，含 interface 共享头）

| 模块目录 | 形态 | Kit 状态 |
|---|---|---|
| awake | 接口 + 独立进程 + 服务 | 保留（复制基线） |
| LightSwitch | 接口 + 服务 | 保留（Kit 自研形态） |
| powerdisplay | 接口 + 服务 + 设置页 | 已移除（对应 Kit 的 Monitor，已在 2.0.8 剔除） |
| interface | 共享接口头 powertoy_module_interface.h | 保留（共用） |
| AdvancedPaste / AltWindowCycle / alwaysontop / cmdNotFound / cmdpal / colorPicker / CropAndLock / EnvironmentVariables / fancyzones / FileLocksmith / GrabAndMove / Hosts / imageresizer / keyboardmanager / launcher / MeasureTool / MouseUtils / MouseWithoutBorders / NewPlus / peek / poweraccent / PowerOCR / powerrename / previewpane / registrypreview / ShortcutGuide / Workspaces / ZoomIt | 各种形态 | 不引入 |

## 11. 版本与构建

- 版本：src/common/version/version.h + version.vcxproj（生成 version_gen.h，宏 VERSION_MAJOR / MINOR / REVISION / BUILD），tools/build/versionSetting.ps1 更新版本号；get_product_version() 用 if constexpr (VERSION_BUILD != 0) 决定是否追加第 4 段，get_std_product_version() 恒输出 4 段（Kit 的 version.h 仍是硬编码 .0，见 fix.md）；
- C++ 编译：Cpp.Build.props（/utf-8、协程弃用静默等，Kit 已同步）；
- WinUI3Apps/ 子目录：Settings/QuickAccess 及若干 *Ext DLL 通过各自 csproj 的 OutputPath 设为 $(Platform)/$(Configuration)/WinUI3Apps（如 PowerToys.Settings.csproj:18），统一输出到该子目录；Runner 的 knownModules 以 WinUI3Apps/ 前缀相对加载；
- 依赖：Directory.Packages.props 中央包管理（Kit 已采用）。

## 13. 设计模式与架构模式总结

基于深度代码审查，PowerToys 框架运用了以下设计模式和最佳实践：

### 13.1 核心设计模式

#### Factory Pattern（工厂模式）
- **位置**：powertoy_module_interface.h, powertoy_module.cpp
- **实现**：
  ```cpp
  extern "C" __declspec(dllexport) PowertoyModuleIface* __cdecl powertoy_create()
  ```
- **用途**：模块 DLL 动态加载，解耦 Runner 与具体模块实现

#### RAII (Resource Acquisition Is Initialization)
- **位置**：powertoy_module.cpp - PowertoyModule 类
- **实现**：
  - 构造器：LoadLibrary + GetProcAddress + create()
  - 析构器：destroy() + FreeLibrary()
- **优势**：自动资源管理，异常安全

#### Singleton Pattern（单例模式）
- **应用场景**：
  1. **modules() 全局注册表**（powertoy_module.cpp）：
     ```cpp
     std::map<std::wstring, PowertoyModule>& modules() {
         static std::map<std::wstring, PowertoyModule> instance;
         return instance;
     }
     ```
  2. **SettingsRepository<T>**（SettingsRepository`1.cs:33-48）：
     - 泛型单例，每个设置类型独立实例
     - 双重检查锁定，线程安全

#### Repository Pattern（仓储模式）
- **位置**：Settings.UI.Library - SettingsRepository<T>
- **职责**：
  - 抽象数据访问层
  - 统一 CRUD 接口
  - FileSystemWatcher 自动刷新
- **优势**：解耦 ViewModel 与持久化逻辑

#### Observer Pattern（观察者模式）
- **应用场景**：
  1. **INotifyPropertyChanged**（Observable.cs）：
     - ViewModel 属性变更通知 UI
  2. **FileSystemWatcher**（SettingsRepository`1.cs:55-92）：
     - 外部修改触发 SettingsChanged 事件
  3. **ThemeListener**（tray_icon.cpp:436-443, line 511）：
     - 系统主题变更触发图标更新

#### Callback/Delegate Pattern（回调/委托模式）
- **位置**：MainWindow.xaml.cs:57-108
- **实现**：
  ```csharp
  SetDefaultSndMessageCallback(msg => { /* ... */ });
  SetRestartAdminSndMessageCallback(msg => { /* ... */ });
  ```
- **用途**：IPC 消息发送通道，解耦消息生产与传输

#### Strategy Pattern（策略模式）
- **位置**：热键系统
- **实现**：
  - **策略 1**：RegisterHotKey API（centralized_hotkeys.cpp）
  - **策略 2**：WH_KEYBOARD_LL 钩子（centralized_kb_hook.cpp）
- **选择依据**：功能需求（即时触发 vs 按压保持）

#### Producer-Consumer Pattern（生产者-消费者模式）
- **位置**：TwoWayPipeMessageIPC
- **实现**：
  - **AsyncMessageQueue**（async_message_queue.h:26-36）：
    - queue_mutex + message_ready 条件变量
  - **output_queue_thread**：消费 output_queue
  - **input_queue_thread**：消费 input_queue

### 13.2 并发与同步模式

#### Multiple Reader, Single Writer（多读单写）
- **位置**：centralized_kb_hook.cpp:167-190
- **实现**：
  ```cpp
  {
      std::scoped_lock lock(hotkeysMutex);
      action = hotkeyDescriptors.find(hotkey)->action;  // 持锁查找
  }
  // 释放锁
  if (action) action();  // 锁外执行
  ```
- **优势**：最小化锁持有时间，提高并发性能

#### Lock-Free Programming（无锁编程）
- **位置**：centralized_kb_hook.cpp:46, 140
- **实现**：
  ```cpp
  std::atomic<DWORD> vkCodePressed;
  std::atomic<bool> closed;
  ```
- **用途**：快速状态检查，避免锁竞争

#### Condition Variable（条件变量）
- **应用场景**：
  1. **lifecycle_stopped**（two_way_pipe_message_ipc_impl.h:129）：
     - 协调关闭流程
  2. **message_ready**（async_message_queue.h:13）：
     - 队列空时等待，新消息到达时唤醒

### 13.3 错误处理模式

#### Fail-Fast（快速失败）
- **位置**：powertoy_module.cpp - load_powertoy()
- **策略**：LoadLibrary/GetProcAddress 任一失败立即抛异常
- **上层处理**：Runner 捕获后记录日志/显示错误对话框，继续加载其他模块

#### Fail-Closed（失败关闭）
- **位置**：pipe_caller_auth.cpp:762-769
- **策略**：认证失败时静默拒绝连接，不泄露拒绝原因
- **安全性**：防止攻击者探测允许的客户端特征

#### Retry with Exponential Backoff（重试与退避）
- **位置**：
  1. **SettingsRepository watcher**（80-92）：5 次重试，100ms 间隔
  2. **Pipe listener replacement**（two_way_pipe_message_ipc.cpp:1006-1012）：瞬态失败时 10ms 重试
- **适用场景**：文件锁定、资源暂时不可用

#### Graceful Degradation（优雅降级）
- **应用场景**：
  1. **AI 检测失败**（main.cpp:154-160）：记录日志，不阻塞启动
  2. **SVG 图标加载失败**（tray_icon.cpp:556-598）：回退到资源图标
  3. **设置文件损坏**（SettingsUtils.cs:92-116）：返回默认设置

### 13.4 架构模式

#### Layered Architecture（分层架构）
```
┌─────────────────────────────────────┐
│  Presentation Layer (Settings UI)  │ ← MVVM
├─────────────────────────────────────┤
│  Application Layer (Runner)        │ ← 模块加载、热键、IPC
├─────────────────────────────────────┤
│  Business Layer (Modules)          │ ← PowertoyModuleIface 实现
├─────────────────────────────────────┤
│  Persistence Layer (SettingsUtils) │ ← JSON 序列化、文件 I/O
└─────────────────────────────────────┘
```

#### Plugin Architecture（插件架构）
- **宿主**：Runner
- **插件接口**：PowertoyModuleIface
- **加载机制**：动态 DLL 加载
- **通信**：接口调用 + IPC 消息
- **生命周期**：create → enable → disable → destroy

#### Event-Driven Architecture（事件驱动架构）
- **事件源**：
  - 键盘钩子（WH_KEYBOARD_LL）
  - 托盘图标通知（WM_ICON_NOTIFY）
  - FileSystemWatcher
  - ThemeListener
- **事件处理**：回调函数、消息循环

#### MVVM (Model-View-ViewModel)
- **Model**：GeneralSettings, AwakeSettings 等（Settings.UI.Library）
- **View**：XAML 页面（GeneralPage.xaml, DashboardPage.xaml）
- **ViewModel**：GeneralViewModel, DashboardViewModel
- **绑定**：x:Bind, INotifyPropertyChanged

### 13.5 性能优化模式

#### Lazy Initialization（懒加载）
- **应用场景**：
  1. **SettingsConfig 属性**（SettingsRepository`1.cs:115-119）
  2. **Quick Access**（main.cpp:198-202）：仅在启用时启动

#### Cache with TTL（带过期时间的缓存）
- **位置**：pipe_caller_auth.h:62-139 - VerificationCache
- **键**：(PID, 进程创建时间)
- **TTL**：60 秒
- **用途**：避免重复的客户端认证开销

#### Early Exit Optimization（早退优化）
- **位置**：centralized_kb_hook.cpp:88-195
- **策略**：
  1. nCode < 0 立即返回（90-93）
  2. dwExtraInfo 标志检查（97-101）
  3. 非 keydown 事件跳过（147-150）
  4. 空热键跳过（160-163）
- **收益**：减少无效处理，降低钩子开销

#### Detached Background Threads（分离后台线程）
- **位置**：main.cpp:211-242
- **线程**：
  - 更新检查
  - AI 检测
  - MSIX 卸载
  - 更新清理
- **特点**：detached 线程不阻塞主流程

### 13.6 安全模式

#### Defense in Depth（纵深防御）
- **层级**：
  1. **DACL**：基于 SID 的访问控制
  2. **Logon SID**：会话隔离
  3. **Binary Identity**：二进制身份认证（pipe_caller_auth）
  4. **SECURITY_IDENTIFICATION**：防止模拟攻击

#### Principle of Least Privilege（最小权限原则）
- **管道客户端权限**（two_way_pipe_message_ipc.cpp:9-14）：
  - 仅授予必要的 FILE_READ_DATA, FILE_WRITE_DATA
  - 不授予 FILE_ALL_ACCESS

#### Input Validation（输入验证）
- **位置**：pipe_caller_auth.cpp
- **验证项**：
  - 镜像路径（必须在 WinUI3Apps 目录）
  - 基名白名单
  - 版本一致性
  - 数字签名

### 13.7 可维护性模式

#### Dependency Injection（依赖注入）
- **位置**：Settings UI ViewModels
- **实现**：
  ```csharp
  public ShellViewModel(ISettingsRepository<GeneralSettings> settingsRepository)
  ```
- **优势**：可测试性、松耦合

#### Interface Segregation（接口隔离）
- **示例**：IRefreshablePage, ISettingsConfig
- **原则**：小而专注的接口，避免臃肿

#### Separation of Concerns（关注点分离）
- **体现**：
  - Runner：生命周期管理
  - Settings UI：用户界面
  - Modules：业务逻辑
  - Common libraries：共享基础设施

### 13.8 跨语言互操作模式

#### COM Interop（COM 互操作）
- **位置**：PowerToys.Interop WinMD
- **投影**：C++ TwoWayPipeMessageIPC → C# TwoWayPipeMessageIPCManaged
- **用途**：Native/Managed 边界

#### P/Invoke（平台调用）
- **使用场景**：
  - SetWindowsHookExW
  - RegisterHotKey
  - CreateNamedPipe
  - Shell_NotifyIcon

---

## 14. 参考实现与代码引用

本文档补充的实现细节基于以下关键源文件：

### Runner 核心
- [main.cpp](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\runner\main.cpp):449-653, 182-366
- [powertoy_module.cpp](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\runner\powertoy_module.cpp)
- [tray_icon.cpp](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\runner\tray_icon.cpp):203-407, 453-523
- [settings_window.cpp](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\runner\settings_window.cpp):435-658, 194-357

### 热键系统
- [centralized_kb_hook.cpp](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\runner\centralized_kb_hook.cpp):88-195, 287-291
- [centralized_hotkeys.cpp](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\runner\centralized_hotkeys.cpp):43-72, 74-96

### IPC 与安全
- [two_way_pipe_message_ipc.cpp](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\common\interop\two_way_pipe_message_ipc.cpp):170-179, 931-968, 579-711
- [pipe_caller_auth.cpp](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\common\interop\pipe_caller_auth.cpp):762-769
- [pipe_caller_auth.h](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\common\interop\pipe_caller_auth.h):29-54, 62-139

### Settings UI
- [App.xaml.cs](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\settings-ui\Settings.UI\SettingsXAML\App.xaml.cs):84-104, 226-249
- [MainWindow.xaml.cs](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\settings-ui\Settings.UI\SettingsXAML\MainWindow.xaml.cs):26-136
- [ShellPage.xaml.cs](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\settings-ui\Settings.UI\SettingsXAML\Views\ShellPage.xaml.cs):113-145, 147-168, 337-350
- [SettingsRepository`1.cs](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\settings-ui\Settings.UI.Library\SettingsRepository`1.cs):15-48, 55-92, 112-132
- [SettingsUtils.cs](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\settings-ui\Settings.UI.Library\SettingsUtils.cs):67-116, 188-232
- [Observable.cs](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\settings-ui\Settings.UI.Library\Helpers\Observable.cs):10-28
- [NavigationService.cs](C:\Users\Zen\Repos\Codings\Kit\source\PowerToys\src\settings-ui\Settings.UI\Services\NavigationService.cs):15-85

## 12. 对 Kit 的参考要点

- 已保留：模块接口契约、Runner 骨架、Settings v2、TwoWayPipeMessageIPC、热键系统、GPO 基建、update 状态机（check-only）；
- 已移除：OOBE/SCOOBE、遥测、BugReportTool、AI 全家桶、除 Awake/LightSwitch 外的模块、VCM 清理（clean_video_conference 可删，见 fix.md）；
- 待同步/待决策：pipe_caller_auth（P1）、每模块 settings.json 的存储布局与 Settings v2 的 Repository/序列化对齐、logger_settings / shared_constants / EtwTrace 等共享库漂移（next.md §12）、ManagedTelemetry 编译支持（§13.4）、版本方案（VersionBuildSuffix）与构建脚本（§4/§5）；
- 优化参考：Kit 启动优化专项见 startup-optimization-analysis.md；与上游的逐项对比见 architecture-comparison.md。

## References

### 官方架构文档
- Source/PowerToys/doc/devdocs/：
  - core/architecture.md：进程模型、模块系统总览
  - core/runner.md：Runner 启动流程与生命周期
  - core/settings/readme.md：Settings v2 架构（project-overview、ui-architecture、viewmodels、settings-implementation、gpo-integration、runner-ipc、communication-with-modules 等子文档）
  - modules/interface.md：PowertoyModuleIface 接口规范

### 关键源码文件（本文档引用的实现）
- **Runner 核心**：
  - src/runner/main.cpp：启动流程、模块加载、消息循环
  - src/runner/powertoy_module.cpp：模块加载与生命周期管理
  - src/runner/settings_window.cpp：Settings 窗口启动、IPC 消息分发
  - src/runner/tray_icon.cpp：托盘图标、上下文菜单、Explorer 重启恢复
  - src/runner/centralized_hotkeys.cpp：RegisterHotKey API 热键系统
  - src/runner/centralized_kb_hook.cpp：WH_KEYBOARD_LL 低级键盘钩子

- **模块接口**：
  - src/modules/interface/powertoy_module_interface.h：PowertoyModuleIface 定义

- **IPC 与安全**：
  - src/common/interop/two_way_pipe_message_ipc.cpp：双向命名管道实现
  - src/common/interop/two_way_pipe_message_ipc.h：接口定义
  - src/common/interop/two_way_pipe_message_ipc_impl.h：实现细节
  - src/common/interop/pipe_caller_auth.cpp：客户端二进制身份认证
  - src/common/interop/pipe_caller_auth.h：认证策略定义
  - src/common/interop/async_message_queue.h：异步消息队列

- **Settings UI**：
  - src/settings-ui/Settings.UI/SettingsXAML/App.xaml.cs：应用入口与初始化
  - src/settings-ui/Settings.UI/SettingsXAML/MainWindow.xaml.cs：主窗口与 IPC 回调
  - src/settings-ui/Settings.UI/SettingsXAML/Views/ShellPage.xaml.cs：根容器与导航
  - src/settings-ui/Settings.UI/Services/NavigationService.cs：导航服务
  - src/settings-ui/Settings.UI.Library/SettingsRepository`1.cs：设置仓储模式实现
  - src/settings-ui/Settings.UI.Library/SettingsUtils.cs：JSON 持久化层
  - src/settings-ui/Settings.UI.Library/Helpers/Observable.cs：MVVM Observable 基类
  - src/settings-ui/Settings.UI.Library/ViewModels/ShellViewModel.cs：Shell ViewModel

- **其他公共库**：
  - src/common/utils/gpo.h：GPO 策略读取工具
  - src/common/SettingsAPI/settings_objects.h：设置 UI 描述构造器

### Kit 相关文档
- doc/devdoc/kit-architecture.md：Kit 架构设计与定位
- doc/devdoc/kit-framework-structure.md：Kit 主框架源码结构详解
- doc/devdoc/architecture-comparison.md：Kit vs PowerToys 架构对比
- doc/devdoc/startup-optimization-analysis.md：启动性能优化专项分析
- doc/devdoc/kit-sync-status.md：上游同步状态追踪

---

**文档版本**：v2.0（增强版）  
**最后更新**：2026-09-13  
**更新内容**：
- 新增第 4.1a-4.1b 节：错误处理与重启逻辑详解
- 新增第 4.3 节：模块加载与生命周期完整流程
- 新增第 4.4 节：托盘图标与设置窗口深度解析（生命周期、上下文菜单、IPC、认证）
- 新增第 4.5 节：热键系统双轨架构与实现细节
- 新增第 5.1a 节：WinUI3 应用初始化详解
- 新增第 5.2-5.4 节：Settings 持久化架构、MVVM 实现、导航服务
- 新增第 6.1 节：TwoWayPipeMessageIPC 完整实现（管道创建、安全描述符、消息帧、线程安全、生命周期）
- 新增第 6.2 节：pipe_caller_auth 认证方案详解
- 新增第 13 章：设计模式与架构模式总结（8 大类，25+ 模式）
- 新增第 14 章：参考实现与代码引用（完整文件路径 + 行号）
- 所有新增内容基于深度源码审查，包含实际代码片段和行号引用