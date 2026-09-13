# PowerToys 主框架架构参考

> 基于 Source/PowerToys 源码核对（当前快照），供 Kit 后续同步与优化参考。
> 修订说明：本版按上游源码逐项核实——修正了旧版中的错误（接口头路径、设置模型、AI 检测"阻塞"假设、管道命名、接口方法表等），补全了缺失部分（Settings v2、特权管道认证、GPO、遥测栈、模块清单、knownModules 列表），并删减了已过时/臆测的内容。

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

1. logger 初始化、单实例互斥（common/utils/appMutex.h）；
2. DPI 感知（PerMonitorV2）、COM 安全初始化（CoInitializeSecurityWithAppId）；
3. 解析命令行参数（elevated、open_settings、settings_window、open_oobe、open_scoobe、show_restart_notification 等）；
4. chdir 到可执行目录；clean_video_conference()（Video Conference Mute 遗留清理，Kit 未发布过 VCM，可删）；
5. 托盘图标 start_tray_icon()（窗口类 + NOTIFYICONDATA + 任务栏重建监听）；
6. 集中式键盘钩子与热键初始化；update_quick_access_hotkey();
7. PeriodicUpdateWorker()（更新检查线程，Kit 保持 check-only）；
8. DetectAiCapabilitiesAsync()（后台线程，非阻塞，见 4.7）；
9. 加载 35 个已知模块 DLL（knownModules 列表，见 4.2）；Debug 下失败仅记日志继续，Release 下弹 MessageBox；
10. start_enabled_powertoys()：按 enabled 配置 + GPO 过滤后逐个 enable()；
11. Trace::EventLaunch() + PTSettingsHelper::save_last_version_run()；
12. 按需 open_settings_window() / OOBE / SCOOBE；
13. run_message_loop()：处理托盘消息、lowlevel_keyboard_event、跨实例消息；
14. 退出：disable 全部模块 → destroy() → FreeLibrary → 清理。

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

- load_powertoy()（powertoy_module.cpp）：LoadLibraryW → GetProcAddress(powertoy_create) → create() 构造对象 → PowertoyModule RAII（持有 HMODULE 与 PowertoyModuleIface*，析构时 destroy() + FreeLibrary()）；任一环节失败则 FreeLibrary 并抛异常，Runner 捕获后 Debug 记日志 / Release 弹窗；
- 构造时注册热键（get_hotkeys() → centralized_hotkeys 注册）；
- modules() 全局单例：map（key 为 get_key() 返回值）；
- 启停入口：start_enabled_powertoys()（启动）与 apply_module_status_update()（Settings 消息触发的运行时启停）。

### 4.4 托盘图标与设置窗口

- tray_icon.cpp：WM_ICON_NOTIFY、左键单击/双击区分、右键菜单（.rc 资源 + 本地化字符串）；点击后经 IPC 发 JSON 消息 ShowYourself（flyout 或 Dashboard）；
- settings_window.cpp：open_settings_window() 以 ShellExecute 启动 PowerToys.Settings.exe，命令行传两个管道名 + Runner pid + 目标页面；页面由 ESettingsWindowNames 枚举描述（settings_window.h）：Dashboard、Overview、AlwaysOnTop、Awake、ColorPicker、CmdNotFound、LightSwitch、FancyZones、FileLocksmith、Run、ImageResizer、KBM、MouseUtils、MouseWithoutBorders、Peek、PowerAccent、PowerLauncher、PowerPreview、PowerRename、FileExplorer、ShortcutGuide、Hosts、MeasureTool、PowerOCR、Workspaces、RegistryPreview、CropAndLock、EnvironmentVariables、AdvancedPaste、NewPlus、CmdPal、ZoomIt、PowerDisplay、GrabAndMove；
- Runner 收到 Settings 消息后的分发（settings_window.cpp 消息处理函数）：按消息类型分流——apply_module_status_update（模块启用/禁用，含 GPO 复核）、dispatch_json_config_to_modules（配置更新 → 模块 set_config）、dispatch_json_action_to_module（自定义动作 → call_custom_action）。

### 4.5 热键系统

- centralized_kb_hook.cpp：WH_KEYBOARD_LL 低级键盘钩子；性能优化——跳过 PowerToys 自身生成的按键（dwExtraInfo 标志）、无按键按下时早退、避免重复处理；
- centralized_hotkeys.cpp：热键注册/注销、冲突检测（HotkeyConflictDetector / HotkeyConflictManager）、update_hotkeys；
- 模块 get_hotkeys() 返回 Hotkey[]，Runner 注册后按 id 回调 on_hotkey()；模块禁用时同样会收到 on_hotkey；
- 新式 GetHotkeyEx()/OnHotkeyEx()（VK + 修饰掩码）供需要更细粒度控制的模块（如 Workspaces、NewPlus）；
- 冲突检测集成（Settings v2 约定）：模块 get_hotkeys() / GetHotkeyEx() 的热键顺序 与 Settings.UI.Library 中 IHotkeyConfig.GetAllHotkeyAccessors() 顺序、ViewModel.GetAllHotkeySettings() 顺序必须一致（顺序即热键 id），页面加载后调用 OnPageLoaded() 生效——Kit 的 Awake/LightSwitch 沿用同一约定。

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

### 6.1 TwoWayPipeMessageIPC

- native：src/common/interop/two_way_pipe_message_ipc.cpp（C++，投影为 PowerToys.Interop WinMD）；
- managed：TwoWayPipeMessageIPCManaged（C# 侧，Settings/QuickAccess 使用）；
- 管道命名：powertoys_runner_<uuid>（Runner 为 server）、powertoys_settings_<uuid>（Settings 为 server）；Runner 启动 Settings.exe 时把两个管道名 + Runner pid 作为命令行参数传入；
- 消息为 JSON 字符串：ShowYourself（flyout/Dashboard）、模块配置、启用/禁用、深链接等；
- Settings 侧：ShellPage.xaml.cs 持有静态 IPC 委托（SendDefaultMessage、RestartAsAdmin、CheckForUpdates），MainWindow.xaml.cs 初始化；配置消息的 name 字段：General 页为 general，模块页为 powertoy；
- Runner 到 Settings 的响应经 ShellPage.ShellHandler.IPCResponseHandleList 内的处理函数分发（如 CheckForUpdates 结果）。

### 6.2 特权管道客户端认证 pipe_caller_auth（安全重点，新增）

- 位置：src/common/interop/pipe_caller_auth.h/.cpp；
- 问题：同用户攻击者与受信任子进程共享 SID、完整性级别与登录会话，管道 DACL 无法区分两者；
- 方案：Runner 对连接上来的进程按"二进制身份"认证后才分发消息，fail-closed；
- 校验项：镜像目录（必须位于 <模块目录>/WinUI3Apps）、允许的镜像基名（如 PowerToys.Settings.exe）、与 Runner 完全一致的版本号（防降级）、Release 下要求 Microsoft Authenticode 签名（Debug 豁免）；
- 每进程（pid + 进程创建时间）缓存判定；拒绝时经注入的 logReject 回调记录；
- Kit 现状：未接入该机制；若未来要加固同名用户攻击面可同步（next.md §12.2 已列 P1）。

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

## 12. 对 Kit 的参考要点

- 已保留：模块接口契约、Runner 骨架、Settings v2、TwoWayPipeMessageIPC、热键系统、GPO 基建、update 状态机（check-only）；
- 已移除：OOBE/SCOOBE、遥测、BugReportTool、AI 全家桶、除 Awake/LightSwitch 外的模块、VCM 清理（clean_video_conference 可删，见 fix.md）；
- 待同步/待决策：pipe_caller_auth（P1）、每模块 settings.json 的存储布局与 Settings v2 的 Repository/序列化对齐、logger_settings / shared_constants / EtwTrace 等共享库漂移（next.md §12）、ManagedTelemetry 编译支持（§13.4）、版本方案（VersionBuildSuffix）与构建脚本（§4/§5）；
- 优化参考：Kit 启动优化专项见 startup-optimization-analysis.md；与上游的逐项对比见 architecture-comparison.md。

## References

- 官方架构文档（Source/PowerToys/doc/devdocs/）：core/architecture.md、core/runner.md、core/settings/readme.md（含 project-overview、ui-architecture、viewmodels、settings-implementation、gpo-integration、runner-ipc、communication-with-modules 等子文档）、modules/interface.md；
- 关键源码：src/runner/main.cpp、src/runner/powertoy_module.cpp、src/runner/settings_window.cpp、src/runner/tray_icon.cpp、src/runner/centralized_hotkeys.cpp、src/runner/centralized_kb_hook.cpp、src/modules/interface/powertoy_module_interface.h、src/common/interop/two_way_pipe_message_ipc.cpp、src/common/interop/pipe_caller_auth.cpp、src/common/utils/gpo.h、src/settings-ui/Settings.UI.Library/SettingsUtils.cs。