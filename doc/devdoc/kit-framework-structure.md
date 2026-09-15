# Kit 主框架结构详解

> 本文档基于当前 Kit 源码完整梳理主框架的代码结构、数据流和关键组件
> 与 kit-architecture.md 的关系：本文聚焦**代码层面的实现细节**，architecture.md 聚焦**架构设计与方向**

## 目录

1. [源码目录结构](#1-源码目录结构)
2. [Runner 主进程详解](#2-runner-主进程详解)
3. [Settings UI 详解](#3-settings-ui-详解)
4. [公共库层级](#4-公共库层级)
5. [模块系统实现](#5-模块系统实现)
6. [启动流程源码追踪](#6-启动流程源码追踪)
7. [热键与键盘钩子](#7-热键与键盘钩子)
8. [IPC 消息流](#8-ipc-消息流)
9. [设置存储与读写](#9-设置存储与读写)
10. [Quick Access 机制](#10-quick-access-机制)

---

## 1. 源码目录结构

```
src/
├── runner/                         # Kit.exe 主进程
│   ├── main.cpp                    # 入口点，启动流程编排
│   ├── kit_module.cpp/h            # 模块加载与生命周期管理
│   ├── general_settings.cpp/h      # 全局设置读写与缓存
│   ├── tray_icon.cpp/h             # 系统托盘图标与菜单
│   ├── settings_window.cpp/h       # Settings 窗口启动与 IPC
│   ├── centralized_kb_hook.cpp/h   # 底层键盘钩子 (WH_KEYBOARD_LL)
│   ├── centralized_hotkeys.cpp/h   # 热键注册与分发
│   ├── hotkey_conflict_detector.cpp/h  # 热键冲突检测
│   ├── quick_access_host.cpp/h     # Quick Access 宿主协调
│   ├── auto_start_helper.cpp/h     # 开机自启注册
│   ├── restart_elevated.cpp/h      # 提权重启
│   ├── RestartManagement.cpp/h     # 重启管理
│   ├── UpdateUtils.cpp/h           # 更新检查（check-only）
│   ├── trace.cpp/h                 # ETW 遥测（Kit 保留接口）
│   └── ActionRunnerUtils.h         # 动作执行工具
│
├── modules/
│   ├── interface/
│   │   └── kit_module_interface.h  # 模块契约定义 (KitModuleIface)
│   ├── awake/                      # Awake 模块
│   │   ├── AwakeModuleInterface/   # DLL 接口层
│   │   └── PowerToys.Awake/        # C# 独立进程
│   ├── LightSwitch/                # LightSwitch 模块
│   │   ├── LightSwitchModuleInterface/  # DLL 接口层
│   │   └── LightSwitchService/     # C++ 后台守护服务
│   └── Localserver/                # Localserver 模块
│       ├── LocalserverModuleInterface/  # DLL 接口层
│       └── LocalserverLib/         # 核心托管业务类库
│
├── settings-ui/
│   ├── Settings.UI/                # Kit.Settings.dll (WinUI3)
│   │   ├── App.xaml.cs             # WinUI3 应用入口
│   │   ├── MainWindow.xaml.cs      # 主窗口，IPC 初始化
│   │   ├── Views/                  # XAML 页面
│   │   │   ├── ShellPage.xaml      # Shell 容器（导航+搜索）
│   │   │   ├── DashboardPage.xaml  # 仪表板
│   │   │   ├── GeneralPage.xaml    # 通用设置
│   │   │   ├── AwakePage.xaml      # Awake 设置页
│   │   │   ├── LightSwitchPage.xaml # LightSwitch 设置页
│   │   │   ├── LocalserverPage.xaml # Localserver 设置页
│   │   │   └── SearchResultsPage.xaml # 搜索结果
│   │   ├── ViewModels/             # MVVM 视图模型
│   │   │   ├── ShellViewModel.cs   # 导航逻辑
│   │   │   ├── GeneralViewModel.cs # 通用设置 VM
│   │   │   ├── AwakeViewModel.cs   # Awake VM
│   │   │   ├── LightSwitchViewModel.cs
│   │   │   └── LocalserverViewModel.cs
│   │   ├── Controls/               # 自定义控件
│   │   │   ├── ShortcutControl.xaml # 热键输入
│   │   │   ├── SettingsGroup.xaml  # 设置分组
│   │   │   └── TitleBar.xaml       # 自定义标题栏
│   │   └── Serialization/          # JSON 源生成
│   │       └── SourceGenerationContext.cs
│   ├── Settings.UI.Library/        # 设置业务逻辑库
│   │   ├── SettingsRepository.cs   # 泛型设置仓储 <T>
│   │   ├── SettingsUtils.cs        # 文件读写工具
│   │   ├── ViewModels/             # 每模块设置模型
│   │   │   ├── GeneralSettings.cs  # 全局设置
│   │   │   ├── AwakeSettings.cs
│   │   │   ├── LightSwitchSettings.cs
│   │   │   └── LocalserverSettings.cs
│   │   └── Helpers/                # IPC、热键、GPO 帮助
│   ├── QuickAccess.UI/             # Kit.QuickAccess.exe (WinUI3)
│   └── Settings.UI.Controls/       # 共享控件库
│
├── common/                         # 公共基础库
│   ├── version/                    # 版本号生成
│   │   ├── version.h               # 版本宏定义
│   │   └── version.vcxproj         # 生成 version_gen.h
│   ├── logger/                     # spdlog 封装
│   │   ├── logger.h/.cpp
│   │   └── logger_settings.h       # 每模块日志配置
│   ├── interop/                    # C++/C# 互操作
│   │   ├── two_way_pipe_message_ipc.cpp/h  # IPC 管道
│   │   ├── PowerToys.Interop/      # WinMD 投影
│   │   └── shared_constants.h      # 共享常量（APPDATA_PATH等）
│   ├── SettingsAPI/                # 设置 API（C++）
│   │   ├── settings_objects.h      # PowerToysSettings::Settings
│   │   └── settings_helpers.h      # 辅助函数
│   ├── utils/                      # 工具函数集
│   │   ├── appMutex.h              # 单实例互斥
│   │   ├── elevation.h             # 提权检测
│   │   ├── gpo.h                   # GPO 策略读取
│   │   ├── json.h                  # JSON 包装
│   │   ├── processApi.h            # 进程 API
│   │   ├── resources.h             # 资源字符串
│   │   ├── os-detect.h             # OS 版本检测
│   │   ├── process_path.h          # 进程路径
│   │   ├── window.h                # 窗口工具
│   │   └── clean_video_conference.h # VCM 清理
│   ├── hooks/                      # 钩子库
│   ├── notifications/              # Toast 通知
│   ├── updating/                   # 更新状态机
│   ├── Display/                    # DPI 感知
│   ├── COMUtils/                   # COM 初始化
│   ├── GPOWrapper/                 # GPO C# 包装
│   ├── ManagedCommon/              # C# 公共库
│   ├── ManagedCsWin32/             # CsWin32 包装
│   ├── Telemetry/                  # ETW 遥测（保留接口）
│   ├── Themes/                     # 主题颜色
│   ├── Common.UI/                  # WPF 遗留控件
│   ├── Common.UI.Controls/         # 共享控件
│   └── Common.Search/              # 搜索匹配（StringMatcher）
│
├── gpo/                            # GPO 策略资产
│   └── assets/                     # .admx/.adml 模板
│
├── ActionRunner/                   # 动作执行器
├── PackageIdentity/                # 包标识
├── logging/                        # 日志配置
└── Update/                         # 更新工具
```

---

## 2. Runner 主进程详解

### 2.1 main.cpp 核心结构

```cpp
// 关键常量定义
namespace {
    const wchar_t KIT_DISPLAY_NAME[] = L"Kit";
    const wchar_t KIT_RUNNER_TITLE[] = L"Kit - runner";
    
    // 硬编码模块清单（第一方模块）
    constexpr std::wstring_view KitKnownModules[] = {
        L"PowerToys.AwakeModuleInterface.dll",
        L"PowerToys.LightSwitchModuleInterface.dll",
    };
    
    // 白名单校验
    bool is_known_module_registered(std::wstring_view moduleName);
    
    // URI 协议处理（kit:// 和 powertoys://）
    std::optional<std::wstring_view> get_uri_protocol_payload(std::wstring_view value);
}

// 主运行循环
int runner(bool isProcessElevated, 
           bool openSettings,
           std::string settingsWindow,
           bool showRestartNotificationAfterUpdate,
           const json::JsonObject& startupGeneralSettings);

// 入口点
int wWinMain(HINSTANCE hInstance, HINSTANCE, PWSTR lpCmdLine, int nCmdShow);
```

### 2.2 启动阶段计时

main.cpp 使用 `log_timing` lambda 在关键阶段插桩：

```cpp
auto start_time = std::chrono::high_resolution_clock::now();
auto log_timing = [&start_time](const char* stage) {
    auto now = std::chrono::high_resolution_clock::now();
    auto duration = std::chrono::duration_cast<std::chrono::milliseconds>(now - start_time).count();
    Logger::info("STARTUP_TIMING: {} at {}ms", stage, duration);
};

// 使用示例
DPIAware::EnableDPIAwarenessForThisProcess();
log_timing("DPI Awareness");

Trace::RegisterProvider();
log_timing("Trace Provider");

// ... 更多阶段
```

**已记录的启动阶段**（按时间顺序）：
1. DPI Awareness
2. Trace Provider
3. Load Settings
4. Tray Icon
5. Update Worker
6. Quick Access Hotkey（仅注册热键，不启动进程）
7. Tray Icon Visible
8. Keyboard Hook
9. Chdir
10. Video Conference Cleanup（仅提权且首次）
11. Module Loaded: <module_name>（每个模块）
12. Modules Enabled
13. Event Launch
14. **Total startup time**（总时长）

### 2.3 clean_video_conference_once 优化

```cpp
// Kit 优化：一次性清理标记，节省 10-20ms
void clean_video_conference_once() {
    const wchar_t* regKey = L"Software\\Microsoft\\PowerToys\\Kit";
    const wchar_t* regValue = L"VideoConferenceCleanupDone";
    
    DWORD cleanupDone = 0;
    // 读取标记，若未完成则执行清理并写入标记
    if (cleanupDone == 0) {
        clean_video_conference();
        // 写注册表标记为 1
    }
}
```

### 2.4 模块加载逻辑

```cpp
// 遍历硬编码清单
for (auto moduleSubdir : KitKnownModules) {
    try {
        auto pt_module = load_powertoy(moduleSubdir);  // → powertoy_module.cpp
        modules().emplace(pt_module->get_key(), std::move(pt_module));
        Logger::info(L"STARTUP_TIMING: Module Loaded: {}", moduleSubdir);
    }
    catch (...) {
        std::wstring errorMessage = KIT_MODULE_LOAD_FAIL + moduleSubdir;
#ifdef _DEBUG
        // Debug: 仅记录警告继续（快速迭代开发）
        Logger::warn(L"Debug mode: {}", errorMessage);
#else
        // Release: 弹窗报错
        MessageBoxW(NULL, errorMessage.c_str(), ...);
#endif
    }
}

// 启动已启用的模块
start_enabled_powertoys(startupGeneralSettings);  // → general_settings.cpp
```

### 2.5 消息循环

```cpp
int run_message_loop() {
    MSG msg;
    while (GetMessage(&msg, nullptr, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }
    return static_cast<int>(msg.wParam);
}
```

---

## 3. Settings UI 详解

### 3.1 WinUI3 应用入口

**App.xaml.cs**:
```csharp
public partial class App : Application
{
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 解析命令行参数：pipe_to_runner, pipe_from_runner, runner_pid, settings_window
        m_window = new MainWindow(pipeFromRunner, pipeToRunner, runnerPid, settingsWindow);
        m_window.Activate();
    }
}
```

**MainWindow.xaml.cs**:
```csharp
public MainWindow(string pipeFromRunner, string pipeToRunner, int runnerPid, string settingsWindow)
{
    // 初始化 TwoWayPipeMessageIPCManaged
    ShellPage.SetPipeMessageDelegate(new Func<string, string>(SendPipeMessage));
    
    // 设置静态委托（供 ViewModel 调用）
    ShellPage.SendDefaultIPCMessage = ...;
    ShellPage.SendRestartAsAdminIPCMessage = ...;
    ShellPage.SendCheckForUpdatesIPCMessage = ...;
}
```

### 3.2 Shell 容器结构

**ShellPage.xaml** 关键元素：
```xml
<UserControl>
    <Grid>
        <!-- 标题栏（包含搜索框） -->
        <controls:TitleBar Grid.Row="0">
            <AutoSuggestBox x:Name="SearchBox" />
        </controls:TitleBar>
        
        <!-- 导航视图 -->
        <NavigationView x:Name="navigationView" Grid.Row="1"
                        PaneDisplayMode="LeftCompact"
                        IsPaneOpen="True">
            <NavigationView.MenuItems>
                <NavigationViewItem x:Uid="Shell_Dashboard" />
                <NavigationViewItemSeparator />
                <NavigationViewItem x:Uid="Shell_Awake" />
                <NavigationViewItem x:Uid="Shell_LightSwitch" />
            </NavigationView.MenuItems>
            
            <NavigationView.PaneFooter>
                <!-- 底部设置按钮 -->
                <NavigationViewItem x:Uid="Shell_Settings" />
            </NavigationView.PaneFooter>
            
            <Frame x:Name="shellFrame" />
        </NavigationView>
    </Grid>
</UserControl>
```

### 3.3 导航机制

**ShellViewModel.cs**:
```csharp
private readonly Dictionary<string, Type> _pages = new()
{
    { "Dashboard", typeof(DashboardPage) },
    { "General", typeof(GeneralPage) },
    { "Awake", typeof(AwakePage) },
    { "LightSwitch", typeof(LightSwitchPage) },
};

public void Navigate(string pageKey)
{
    var pageType = _pages[pageKey];
    _navigationService.NavigateTo(pageType);
}
```

### 3.4 设置 Repository 模式

**SettingsRepository<T>**:
```csharp
public class SettingsRepository<T> : ISettingsRepository<T> where T : ISettingsConfig, new()
{
    private T _settingsCache;
    private FileSystemWatcher _watcher;
    
    public event EventHandler<T> SettingsChanged;
    
    public T SettingsConfig => _settingsCache ??= LoadOrDefault();
    
    private T LoadOrDefault()
    {
        var filePath = SettingsUtils.GetSettingsFilePath(moduleName, fileName);
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<T>(json);
        }
        return new T();  // 默认值
    }
    
    public void SaveSettingsToFile()
    {
        var json = JsonSerializer.Serialize(_settingsCache);
        SettingsUtils.SaveSettings(json, moduleName, fileName);
    }
}
```

使用示例：
```csharp
// AwakeViewModel.cs
private readonly ISettingsRepository<AwakeSettings> _settingsRepository;

public AwakeViewModel()
{
    _settingsRepository = SettingsRepository<AwakeSettings>.GetInstance();
    _settingsRepository.SettingsChanged += OnSettingsChanged;
}
```

---

## 4. 公共库层级

### 4.1 核心依赖关系

```
runner.exe
  ├─> common/logger (spdlog)
  ├─> common/SettingsAPI (PowerToysSettings)
  ├─> common/interop (TwoWayPipeMessageIPC)
  ├─> common/utils (gpo, elevation, json, appMutex...)
  ├─> common/notifications
  ├─> common/updating (UpdateUtils)
  └─> common/version

Settings.exe
  ├─> Settings.UI.Library
  │     ├─> common/ManagedCommon
  │     ├─> common/GPOWrapper
  │     └─> PowerToys.Interop (TwoWayPipeMessageIPCManaged)
  └─> Settings.UI.Controls

Module DLL
  ├─> modules/interface (powertoy_module_interface.h)
  ├─> common/SettingsAPI
  └─> common/logger
```

### 4.2 shared_constants.h

```cpp
namespace CommonSharedConstants
{
    // Kit 特有：应用数据目录名
    inline const wchar_t* APPDATA_PATH = L"Kit";
    
    // IPC 管道名前缀
    inline const wchar_t* RUNNER_PIPE_PREFIX = L"powertoys_runner_";
    inline const wchar_t* SETTINGS_PIPE_PREFIX = L"powertoys_settings_";
    
    // 互斥量名称
    inline const wchar_t* POWERTOYS_RUNNER_MUTEX = L"Local\\PowerToysRunnerMutex";
    inline const wchar_t* KIT_MSI_MUTEX_NAME = L"Local\\KitMSIInstallMutex";
}
```

### 4.3 logger_settings.h

```cpp
namespace LogSettings
{
    // 每模块独立日志名
    inline const std::wstring_view runnerLoggerName = L"runner";
    inline const std::wstring_view settingsLoggerName = L"settings";
    inline const std::wstring_view awakeLoggerName = L"awake";
    inline const std::wstring_view lightSwitchLoggerName = L"lightswitch";
    
    // 日志路径：%LOCALAPPDATA%/Kit/RunnerLogs/
    inline const std::wstring_view runnerLogPath = L"RunnerLogs\\runner-log.txt";
}
```

---

## 5. 模块系统实现

### 5.1 PowertoyModule RAII 包装

**powertoy_module.h**:
```cpp
class PowertoyModule
{
public:
    PowertoyModule(PowertoyModuleIface* pt_module, HMODULE handle);
    ~PowertoyModule()
    {
        if (pt_module)
        {
            pt_module->destroy();  // 调用模块析构
        }
        if (handle)
        {
            FreeLibrary(handle);   // 卸载 DLL
        }
    }
    
    // Kit 优化：缓存不可变元数据
    std::wstring cached_name;
    std::wstring cached_key;
    bool cached_default_enabled;
    
    // 委托方法
    void enable() { pt_module->enable(); }
    void disable() { pt_module->disable(); }
    bool is_enabled() const { return pt_module->is_enabled(); }
    json::JsonObject json_config() const;
    void set_config(const std::wstring& config) { pt_module->set_config(config.c_str()); }
    
private:
    winrt::com_ptr<PowertoyModuleIface> pt_module;
    HMODULE handle;
    HotkeyConflictDetector::HotkeyConflictManager& hkmng;
    
    void update_hotkeys();
    void UpdateHotkeyEx();
    void remove_hotkey_records();
};
```

### 5.2 加载流程

**powertoy_module.cpp**:
```cpp
PowertoyModule load_powertoy(const std::wstring_view filename)
{
    // 1. LoadLibrary
    auto handle = winrt::check_pointer(LoadLibraryW(filename.data()));
    
    // 2. GetProcAddress("powertoy_create")
    auto create = reinterpret_cast<powertoy_create_func>(GetProcAddress(handle, "powertoy_create"));
    if (!create) {
        FreeLibrary(handle);
        winrt::throw_last_error();
    }
    
    // 3. 调用工厂函数
    auto pt_module = create();
    if (!pt_module) {
        FreeLibrary(handle);
        winrt::throw_hresult(E_POINTER);
    }
    
    // 4. 包装为 RAII 对象
    return PowertoyModule(pt_module, handle);
}
```

### 5.3 全局模块注册表

```cpp
std::map<std::wstring, PowertoyModule>& modules()
{
    static std::map<std::wstring, PowertoyModule> modules;
    return modules;
}

// 注册：main.cpp
modules().emplace(pt_module->get_key(), std::move(pt_module));

// 查询：general_settings.cpp
auto& mod = modules().at(L"Awake");
mod.enable();
```

---

## 6. 启动流程源码追踪

### 6.1 完整调用链

```
wWinMain()
  └─> runner(isProcessElevated, openSettings, settingsWindow, ...)
        ├─> DPIAware::EnableDPIAwarenessForThisProcess()
        ├─> Trace::RegisterProvider()
        ├─> get_general_settings()  // → general_settings.cpp
        │     └─> load_general_settings()
        │           └─> PTSettingsHelper::load_general_settings()
        │                 └─> 读取 %LOCALAPPDATA%/Kit/settings.json
        ├─> start_tray_icon(isElevated, showThemeAdaptiveTrayIcon)  // → tray_icon.cpp
        ├─> PeriodicUpdateWorker()  // → UpdateUtils.cpp，后台线程
        ├─> update_quick_access_hotkey(enabled, shortcut)  // → quick_access_host.cpp
        │     └─> 仅注册 Win+Space 热键，不启动进程
        ├─> set_tray_icon_visible(showSystemTrayIcon)
        ├─> CentralizedKeyboardHook::Start()  // → centralized_kb_hook.cpp
        │     └─> SetWindowsHookEx(WH_KEYBOARD_LL, ...)
        ├─> chdir_current_executable()
        ├─> [if elevated] clean_video_conference_once()
        ├─> for (moduleSubdir : KitKnownModules)
        │     └─> load_powertoy(moduleSubdir)  // → powertoy_module.cpp
        │           ├─> LoadLibraryW()
        │           ├─> GetProcAddress("powertoy_create")
        │           ├─> create()  // 返回 PowertoyModuleIface*
        │           └─> PowertoyModule 构造（缓存元数据 + 注册热键）
        ├─> start_enabled_powertoys(startupGeneralSettings)  // → general_settings.cpp
        │     └─> for (auto& [key, module] : modules())
        │           ├─> 检查 GPO 策略
        │           ├─> 检查用户配置
        │           └─> module.enable()  // → 模块 enable() 实现
        ├─> Trace::EventLaunch(product_version, isElevated)
        ├─> [if openSettings] open_settings_window(window)  // → settings_window.cpp
        │     └─> ShellExecute("Kit.Settings.exe", ...)
        └─> run_message_loop()
              └─> GetMessage / DispatchMessage 循环
```

### 6.2 Settings 窗口启动

**settings_window.cpp**:
```cpp
void open_settings_window(std::optional<std::wstring> settings_window)
{
    // 生成 UUID 用于管道名
    GUID guid_runner, guid_settings;
    CoCreateGuid(&guid_runner);
    CoCreateGuid(&guid_settings);
    
    std::wstring pipe_to_runner = L"powertoys_runner_" + guid_to_string(guid_runner);
    std::wstring pipe_from_runner = L"powertoys_settings_" + guid_settings);
    
    // 构造命令行
    std::wstring commandLine = L"\"Kit.Settings.exe\" ";
    commandLine += pipe_from_runner + L" ";
    commandLine += pipe_to_runner + L" ";
    commandLine += std::to_wstring(GetCurrentProcessId()) + L" ";
    if (settings_window.has_value())
        commandLine += settings_window.value();
    
    // 启动进程
    STARTUPINFOW si = {};
    PROCESS_INFORMATION pi = {};
    CreateProcessW(nullptr, commandLine.data(), ..., &si, &pi);
    
    // 建立 IPC
    current_settings_ipc = new TwoWayPipeMessageIPC(pipe_to_runner, pipe_from_runner, ...);
    current_settings_ipc->start(runner_to_settings_callback);
}
```

---

## 7. 热键与键盘钩子

### 7.1 centralized_kb_hook.cpp

```cpp
namespace CentralizedKeyboardHook
{
    // 全局钩子句柄
    static HHOOK hook_handle = nullptr;
    
    // 模块热键映射：key → (Hotkey → callback)
    static std::map<std::wstring, std::map<Hotkey, std::function<bool()>>> moduleHotkeys;
    
    // 底层键盘钩子回调
    LRESULT CALLBACK KeyboardHookProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode == HC_ACTION)
        {
            auto kb = reinterpret_cast<KBDLLHOOKSTRUCT*>(lParam);
            
            // 跳过 PowerToys 自身生成的输入（防循环）
            if (kb->dwExtraInfo == CENTRALIZED_KEYBOARD_HOOK_DONT_TRIGGER_FLAG)
                return CallNextHookEx(nullptr, nCode, wParam, lParam);
            
            // 检查热键匹配
            for (auto& [moduleKey, hotkeys] : moduleHotkeys)
            {
                for (auto& [hotkey, callback] : hotkeys)
                {
                    if (IsHotkeyMatch(hotkey, kb->vkCode, wParam))
                    {
                        if (callback())  // 返回 true = 吞掉按键
                            return 1;
                    }
                }
            }
        }
        return CallNextHookEx(nullptr, nCode, wParam, lParam);
    }
    
    void Start()
    {
        hook_handle = SetWindowsHookExW(WH_KEYBOARD_LL, KeyboardHookProc, ...);
    }
    
    void SetHotkeyAction(std::wstring moduleKey, Hotkey hotkey, std::function<bool()> action)
    {
        moduleHotkeys[moduleKey][hotkey] = action;
    }
}
```

### 7.2 PowertoyModule 热键注册

**powertoy_module.cpp**:
```cpp
void PowertoyModule::update_hotkeys()
{
    // 清除旧热键
    CentralizedKeyboardHook::ClearModuleHotkeys(pt_module->get_key());
    
    // 获取模块热键列表
    size_t hotkeyCount = pt_module->get_hotkeys(nullptr, 0);
    std::vector<PowertoyModuleIface::Hotkey> hotkeys(hotkeyCount);
    pt_module->get_hotkeys(hotkeys.data(), hotkeyCount);
    
    auto modulePtr = pt_module.get();
    
    // 注册每个热键
    for (size_t i = 0; i < hotkeyCount; i++)
    {
        if (hotkeys[i].isShown)  // 仅注册显示的热键
        {
            // 1. 添加到冲突检测器
            hkmng.AddHotkey(hotkeys[i], pt_module->get_key(), static_cast<int>(i), pt_module->is_enabled());
            
            // 2. 注册到键盘钩子
            CentralizedKeyboardHook::SetHotkeyAction(
                pt_module->get_key(),
                hotkeys[i],
                [modulePtr, i] {
                    Logger::trace(L"{} hotkey is invoked", modulePtr->get_key());
                    return modulePtr->on_hotkey(i);  // 调用模块回调
                }
            );
        }
    }
}
```

---

## 8. IPC 消息流

### 8.1 TwoWayPipeMessageIPC

**two_way_pipe_message_ipc.cpp**:
```cpp
class TwoWayPipeMessageIPC
{
public:
    TwoWayPipeMessageIPC(std::wstring inputPipeName,
                         std::wstring outputPipeName,
                         callback_function callback)
        : _inputPipeName(inputPipeName)
        , _outputPipeName(outputPipeName)
        , _callback(callback)
    {
    }
    
    void start()
    {
        // 创建服务端管道（等待连接）
        _inputPipe = CreateNamedPipeW(
            _inputPipeName.c_str(),
            PIPE_ACCESS_INBOUND | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            1, 0, 0, 0, nullptr
        );
        
        // 连接客户端管道
        _outputPipe = CreateFileW(
            _outputPipeName.c_str(),
            GENERIC_WRITE,
            0, nullptr,
            OPEN_EXISTING,
            0, nullptr
        );
        
        // 启动读取线程
        _readThread = std::thread(&TwoWayPipeMessageIPC::ReadThread, this);
    }
    
    void send(const std::wstring& message)
    {
        DWORD bytesWritten;
        WriteFile(_outputPipe, message.c_str(), message.size() * sizeof(wchar_t), &bytesWritten, nullptr);
    }
    
private:
    void ReadThread()
    {
        while (true)
        {
            wchar_t buffer[4096];
            DWORD bytesRead;
            if (ReadFile(_inputPipe, buffer, sizeof(buffer), &bytesRead, nullptr))
            {
                std::wstring message(buffer, bytesRead / sizeof(wchar_t));
                _callback(message);  // 调用回调处理消息
            }
        }
    }
    
    HANDLE _inputPipe;
    HANDLE _outputPipe;
    callback_function _callback;
    std::thread _readThread;
};
```

### 8.2 消息格式示例

**Runner → Settings** (get_all_settings 响应):
```json
{
  "general": {
    "startup": true,
    "enabled": {
      "Awake": true,
      "LightSwitch": false
    },
    "theme": "system",
    "show_tray_icon": true
  },
  "powertoys": {
    "Awake": {
      "name": "Awake",
      "version": "0.1",
      "properties": {
        "mode": {
          "value": "indefinite"
        }
      }
    },
    "LightSwitch": { ... }
  }
}
```

**Settings → Runner** (模块配置变更):
```json
{
  "action": "module_config_update",
  "powertoy": "Awake",
  "config": {
    "name": "Awake",
    "properties": {
      "mode": { "value": "timed" },
      "hours": { "value": 2 }
    }
  }
}
```

### 8.3 Runner 消息分发

**settings_window.cpp**:
```cpp
void runner_to_settings_callback(const std::wstring& message)
{
    auto json = json::JsonObject::Parse(message);
    auto action = json.GetNamedString(L"action");
    
    if (action == L"apply_module_status_update")
    {
        apply_module_status_update(json);  // → general_settings.cpp
    }
    else if (action == L"dispatch_json_config_to_modules")
    {
        dispatch_json_config_to_modules(json);  // → 调用模块 set_config
    }
    else if (action == L"dispatch_json_action_to_module")
    {
        dispatch_json_action_to_module(json);  // → 调用模块 call_custom_action
    }
}
```

---

## 9. 设置存储与读写

### 9.1 存储布局

```
%LOCALAPPDATA%/Kit/
  ├── settings.json              # 全局设置（general_settings）
  ├── Awake/
  │   └── settings.json          # Awake 模块设置
  ├── LightSwitch/
  │   └── settings.json          # LightSwitch 模块设置
  └── RunnerLogs/
      └── runner-log_<date>.txt  # 日志文件
```

### 9.2 全局设置读写

**general_settings.cpp**:
```cpp
json::JsonObject load_general_settings()
{
    auto loaded = PTSettingsHelper::load_general_settings();
    
    // 缓存到全局变量
    settings_theme = loaded.GetNamedString(L"theme", L"system");
    show_tray_icon = loaded.GetNamedBoolean(L"show_tray_icon", true);
    enable_quick_access = loaded.GetNamedBoolean(L"enable_quick_access", false);
    // ... 更多字段
    
    return loaded;
}

void save_general_settings(const GeneralSettings& settings)
{
    auto json = settings.to_json();
    PTSettingsHelper::save_general_settings(json.Stringify());
}
```

**PTSettingsHelper** (common/SettingsAPI/settings_helpers.h):
```cpp
json::JsonObject load_general_settings()
{
    std::wstring path = get_settings_path() + L"\\settings.json";
    if (!std::filesystem::exists(path))
        return create_default_general_settings();
    
    std::wifstream file(path);
    std::wstring content((std::istreambuf_iterator<wchar_t>(file)),
                         std::istreambuf_iterator<wchar_t>());
    return json::JsonObject::Parse(content);
}

void save_general_settings(const std::wstring& json_string)
{
    std::wstring path = get_settings_path() + L"\\settings.json";
    std::wofstream file(path);
    file << json_string;
}
```

### 9.3 模块设置读写

**settings_objects.h** (C++ 侧):
```cpp
namespace PowerToysSettings
{
    class PowerToyValues
    {
    public:
        static PowerToyValues from_json_string(const std::wstring& json, const std::wstring& key);
        static PowerToyValues load_from_settings_file(const std::wstring& key);
        
        void save_to_settings_file();
        
        bool get_bool_value(const std::wstring& property_name) const;
        int get_int_value(const std::wstring& property_name) const;
        std::wstring get_string_value(const std::wstring& property_name) const;
        
    private:
        json::JsonObject _values;
        std::wstring _module_key;
    };
}
```

使用示例（Awake 模块）:
```cpp
void set_config(const wchar_t* config) override
{
    PowerToysSettings::PowerToyValues values =
        PowerToysSettings::PowerToyValues::from_json_string(config, get_key());
    
    // 读取配置值
    auto mode = values.get_string_value(L"mode");
    auto hours = values.get_int_value(L"hours");
    
    // 持久化到文件
    values.save_to_settings_file();  // → %LOCALAPPDATA%/Kit/Awake/settings.json
}
```

---

## 10. Quick Access 机制

### 10.1 延迟启动优化

**main.cpp**:
```cpp
// OPTIMIZATION: Defer Quick Access launch until first use (Win+Space)
// Saves 200-400ms on startup by avoiding WinUI3 process spawn
// Quick Access will be lazily initialized on first hotkey press
update_quick_access_hotkey(settings.enableQuickAccess, settings.quickAccessShortcut);
log_timing("Quick Access Hotkey");
```

### 10.2 热键注册与延迟启动

**quick_access_host.cpp**:
```cpp
namespace QuickAccessHost
{
    static bool _isEnabled = false;
    static PowerToysSettings::HotkeyObject _hotkey;
    static HANDLE _processHandle = nullptr;
    
    void update_hotkey(bool enabled, PowerToysSettings::HotkeyObject hotkey)
    {
        _isEnabled = enabled;
        _hotkey = hotkey;
        
        if (enabled)
        {
            // 仅注册热键，不启动进程
            CentralizedHotkeys::RegisterHotkey(
                _hotkey.get_vk(),
                _hotkey.get_modifiers_repeat(),
                []() {
                    LaunchQuickAccessIfNeeded();  // 首次按下时才启动
                }
            );
        }
        else
        {
            CentralizedHotkeys::UnregisterHotkey(_hotkey.get_vk(), _hotkey.get_modifiers_repeat());
            stop();  // 停止已运行的进程
        }
    }
    
    void LaunchQuickAccessIfNeeded()
    {
        if (_processHandle == nullptr)
        {
            // 首次启动：ShellExecute Kit.QuickAccess.exe
            SHELLEXECUTEINFOW sei = {};
            sei.lpFile = L"Kit.QuickAccess.exe";
            sei.lpParameters = L"<pipe_names>";
            sei.nShow = SW_SHOWNORMAL;
            ShellExecuteExW(&sei);
            
            _processHandle = sei.hProcess;
        }
        else
        {
            // 已启动：发送 IPC 消息显示窗口
            SendShowMessage();
        }
    }
    
    void stop()
    {
        if (_processHandle)
        {
            TerminateProcess(_processHandle, 0);
            CloseHandle(_processHandle);
            _processHandle = nullptr;
        }
    }
}
```

---

## 附录：关键数据结构

### A.1 GeneralSettings

```cpp
struct GeneralSettings
{
    bool isStartupEnabled;
    std::wstring startupDisabledReason;
    std::map<std::wstring, bool> isModulesEnabledMap;  // key → enabled
    bool showSystemTrayIcon;
    bool showThemeAdaptiveTrayIcon;
    bool isElevated;
    bool isRunElevated;
    bool showNewUpdatesToastNotification;
    bool downloadUpdatesAutomatically;
    bool showWhatsNewAfterUpdates;
    bool enableExperimentation;
    DashboardSortOrder dashboardSortOrder;
    bool isAdmin;
    bool enableWarningsElevatedApps;
    bool enableQuickAccess;
    PowerToysSettings::HotkeyObject quickAccessShortcut;
    std::wstring theme;  // "system" | "dark" | "light"
    std::wstring systemTheme;
    std::wstring powerToysVersion;
    json::JsonObject ignoredConflictProperties;
};
```

### A.2 PowertoyModuleIface (简化)

```cpp
struct PowertoyModuleIface
{
    struct Hotkey
    {
        bool win;
        bool ctrl;
        bool shift;
        bool alt;
        WORD key;
        size_t id;
        bool isShown;
    };
    
    // 纯虚方法
    virtual const wchar_t* get_name() = 0;
    virtual const wchar_t* get_key() = 0;
    virtual bool get_config(wchar_t* buffer, int* buffer_size) = 0;
    virtual void set_config(const wchar_t* config) = 0;
    virtual void enable() = 0;
    virtual void disable() = 0;
    virtual bool is_enabled() = 0;
    virtual void destroy() = 0;
    
    // 可选方法
    virtual void call_custom_action(const wchar_t* action) {}
    virtual size_t get_hotkeys(Hotkey* buffer, size_t buffer_size) { return 0; }
    virtual bool on_hotkey(size_t hotkeyId) { return false; }
    virtual bool is_enabled_by_default() { return true; }
    virtual gpo_rule_configured_t gpo_policy_enabled_configuration() { return gpo_rule_configured_not_configured; }
};

extern "C" __declspec(dllexport) PowertoyModuleIface* __cdecl powertoy_create();
```

---

## 参考文档

- [kit-architecture.md](kit-architecture.md) - 架构设计与插件方向
- [powertoys-architecture.md](powertoys-architecture.md) - 上游 PowerToys 完整架构
- [startup-optimization-analysis.md](startup-optimization-analysis.md) - 启动优化专项分析
- [architecture-comparison.md](architecture-comparison.md) - Kit 与 PowerToys 对比

---

**文档版本**: v1.0  
**最后更新**: 2026-09-13  
**基于代码快照**: Kit main branch (commit a84ccca)
