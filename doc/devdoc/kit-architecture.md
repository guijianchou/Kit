# Kit Architecture Reference

> 基于当前 Kit 项目的架构分析

## Overview

Kit 是 PowerToys 的简化版本，保留核心架构模式但将模块数量从 35+ 减少到 2 个（Awake、LightSwitch）。移除了企业功能（GPO、遥测、OOBE），专注于个人使用场景。

## Core Components

### 1. Runner (Kit.exe)

**源文件**: `src/runner/main.cpp` (458 行)

**职责**:
- 单实例强制（互斥量检查）
- 模块生命周期管理
- 托盘图标和菜单
- Quick Access 主机
- 设置管道通信

**关键差异与 PowerToys**:
- 移除了 OOBE 窗口
- 移除了 GPO 检查（代码保留但未使用）
- 移除了 PeriodicUpdateWorker（更新检查禁用）
- 移除了 AI 能力检测

### 2. Module System

**当前模块** (2个):

#### Awake
- **路径**: `src/modules/awake/Awake/`
- **功能**: 阻止系统休眠
- **实现**: 生成独立的 `Kit.Awake.exe` 进程
- **配置**: 
  - `mode`: "keep_awake" / "timed" / "off"
  - `keep_display_on`: bool

#### LightSwitch  
- **路径**: `src/modules/lightswitch/LightSwitch/`
- **功能**: 明暗主题快速切换 + 定时切换
- **实现**: 
  - 后台线程监控主题状态
  - 定时器触发自动切换
  - Win+Alt+T 热键手动切换
- **配置**:
  - `auto_switch_enabled`: bool
  - `light_theme_start`: "HH:MM"
  - `dark_theme_start`: "HH:MM"

### 3. Module Registration

**静态数组** (`main.cpp:66-69`):

```cpp
constexpr std::array KitKnownModules{
    L"modules\\Awake\\PowerToys.Awake.dll",
    L"modules\\LightSwitch\\PowerToys.LightSwitch.dll",
};
```

**对比 PowerToys**:
- PowerToys: `std::vector<std::wstring_view>` 33+ 项
- Kit: `constexpr std::array` 2 项
- 相同模式，不同规模

### 4. Settings UI

**架构**: 独立的 WinUI3 进程

**统计**:
- 709 个文件
- 主要技术栈：
  - WinUI 3 (Windows App SDK 2.0)
  - MVVM 模式
  - C# .NET 10

**关键文件**:
- `src/settings-ui/Settings.UI/MainWindow.xaml.cs` - 主窗口
- `src/settings-ui/Settings.UI/ViewModels/` - MVVM 视图模型
- `src/settings-ui/Settings.UI/Views/` - XAML 页面
  - `GeneralPage.xaml` - 通用设置
  - `AwakePage.xaml` - Awake 配置
  - `LightSwitchPage.xaml` - LightSwitch 配置

**通信**: TwoWayPipeMessageIPC 与 Runner

### 5. Settings Persistence

**文件**: `%LocalAppData%\Microsoft\PowerToys\settings\general_settings.json`

**示例结构**:
```json
{
  "startup": true,
  "enabled": {
    "Awake": true,
    "LightSwitch": true
  },
  "Awake": {
    "properties": {
      "awake_mode": "keep_awake",
      "keep_display_on": true
    }
  },
  "LightSwitch": {
    "properties": {
      "auto_switch_enabled": true,
      "light_theme_start": "07:00",
      "dark_theme_start": "19:00"
    }
  }
}
```

**管理器**: `src/runner/general_settings.cpp` (571 行)

## Initialization Flow

### 启动序列 (简化版)

```
1. WinMain Entry
   └─ CreateMutexW("Local\\PowerToys_Runner_Instance")
      └─ 如果已存在 → 退出（单实例）

2. Load Settings
   └─ load_general_settings()
      └─ 读取 general_settings.json (~50-100ms)

3. Start Tray Icon
   └─ tray_icon.init()
      └─ 创建窗口 + 系统托盘注册 (~100-200ms)

4. Start Quick Access (可选)
   └─ if (settings["quick_access"]["enabled"])
      └─ CreateProcessW for WinUI3 进程 (~200-400ms) ← 瓶颈

5. Initialize Keyboard Hook
   └─ CentralizedKeyboardHook::Start()
      └─ SetWindowsHookEx(WH_KEYBOARD_LL)

6. Change to Executable Directory
   └─ SetCurrentDirectory(exe_directory)

7. Load Modules (循环 2 次)
   └─ for module_path in KitKnownModules:
      ├─ LoadLibraryW(module_path) (~20-50ms/模块)
      ├─ GetProcAddress("powertoy_create")
      ├─ factory() → PowertoyModuleIface*
      ├─ PowertoyModule wrapper
      │  └─ 立即注册热键
      └─ modules()[key] = module
   
   总计: ~40-100ms (仅 2 个模块)

8. Enable Configured Modules
   └─ start_enabled_powertoys(settings)
      ├─ Awake: 生成 Kit.Awake.exe 进程
      └─ LightSwitch: 启动调度器 + 切换线程
   
   总计: ~50-150ms

9. Enter Message Loop
   └─ run_message_loop()
      └─ GetMessage/DispatchMessage

总启动时间: ~500-1000ms
```

### 关键路径分析

```
Settings Load (50-100ms)
  ↓
Tray Icon (100-200ms)
  ↓
Quick Access Spawn (200-400ms) ← 最大瓶颈
  ↓
Module Loading (40-100ms)
  ↓
Module Enable (50-150ms)
  ↓
Ready
```

## Identified Bottlenecks

### 1. Quick Access Process Spawn (HIGH)

**影响**: 200-400ms

**原因**: 
- 阻塞式 `CreateProcessW` 等待 WinUI3 进程初始化
- WinUI3 加载运行时 + XAML 解析 + 窗口创建

**优化策略**:
- 延迟启动直到首次 Win+Space 按下
- 异步进程生成 + 后台初始化

### 2. Tray Icon Creation (MEDIUM)

**影响**: 100-200ms

**原因**:
- 窗口类注册
- 系统托盘 API 调用
- 发生在模块加载之前

**优化策略**:
- 后台线程创建
- 与模块加载并行

### 3. Settings JSON Parsing (MEDIUM)

**影响**: 50-100ms

**原因**:
- 同步磁盘 I/O
- JSON 反序列化

**优化策略**:
- 内存映射文件
- 缓存解析的设置 + FileSystemWatcher

### 4. Module Enable Calls (MEDIUM)

**影响**: 50-150ms

**原因**:
- Awake 生成独立进程
- LightSwitch 创建线程 + 启动定时器

**优化策略**:
- 延迟到消息循环后的空闲时间
- 异步初始化

### 5. Module DLL Loading (LOW)

**影响**: 40-100ms (2 个模块)

**原因**:
- 顺序同步 LoadLibrary
- Windows 加载器解析导入

**优化策略**:
- 并行加载（`std::async`）
- 但收益有限（仅 2 个模块）

## Module Details

### Awake Module

**接口实现**: `src/modules/awake/Awake/dllmain.cpp`

**关键方法**:
```cpp
bool AwakeModule::enable() {
    // 生成独立的 Kit.Awake.exe 进程
    std::wstring command = get_awake_exe_path();
    CreateProcessW(command, ...);
    return true;
}

void AwakeModule::disable() {
    // 终止 Awake 进程
    terminate_awake_process();
}

void AwakeModule::set_config(const wchar_t* config) {
    // 解析 JSON 配置
    // 向 Awake 进程发送 IPC 消息
    send_config_to_awake_process(config);
}
```

**进程通信**:
- 命名管道：`\\.\pipe\powertoys_awake_<pid>`
- JSON 消息格式

### LightSwitch Module

**接口实现**: `src/modules/lightswitch/LightSwitch/dllmain.cpp`

**关键方法**:
```cpp
bool LightSwitchModule::enable() {
    // 启动后台线程监控主题
    m_scheduler_thread = std::thread([this]() {
        scheduler_loop();
    });
    
    // 启动定时器线程
    m_timer_thread = std::thread([this]() {
        timer_loop();
    });
    
    return true;
}

void LightSwitchModule::disable() {
    // 停止线程
    m_scheduler_running = false;
    m_scheduler_thread.join();
    m_timer_thread.join();
}

bool LightSwitchModule::on_hotkey(size_t hotkeyId) {
    // Win+Alt+T 切换主题
    toggle_theme();
    return true;
}
```

**主题切换实现**:
```cpp
void toggle_theme() {
    // 读取当前主题
    auto current = get_current_theme();
    
    // 切换到相反主题
    auto target = (current == Theme::Light) ? Theme::Dark : Theme::Light;
    
    // 应用新主题
    set_theme(target);
}

void set_theme(Theme theme) {
    // 修改注册表
    // HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
    RegSetValueEx(
        key,
        L"AppsUseLightTheme",
        0,
        REG_DWORD,
        (theme == Theme::Light) ? 1 : 0
    );
}
```

## Architecture Simplifications

### 从 PowerToys 移除的功能

| 功能 | PowerToys | Kit | 原因 |
|------|-----------|-----|------|
| **GPO 支持** | ✓ | ✗ (代码保留) | 个人使用，非企业 |
| **ETW 遥测** | ✓ | ✗ | 隐私优先 |
| **OOBE 窗口** | ✓ | ✗ | 简化首次运行 |
| **更新检查** | ✓ | ✗ (禁用) | 手动更新 |
| **AI 检测** | ✓ | N/A | 无 AI 模块 |
| **33+ 模块** | ✓ | 仅 2 个 | 专注核心功能 |

### 保留的核心系统

| 系统 | 状态 | 备注 |
|------|------|------|
| **模块接口** | ✓ | 完全兼容 PowerToys |
| **Settings UI** | ✓ | WinUI3 完整保留 |
| **TwoWayPipeIPC** | ✓ | Runner ↔ Settings 通信 |
| **CentralizedKeyboardHook** | ✓ | 热键系统 |
| **JSON 设置** | ✓ | 持久化机制 |
| **Quick Access** | ✓ | Win+Space 启动器 |

## Code Organization

### 目录结构

```
Kit/
├── src/
│   ├── runner/                      # Runner 可执行文件
│   │   ├── main.cpp                 # 入口点 (458 行)
│   │   ├── general_settings.cpp     # 设置管理 (571 行)
│   │   ├── powertoy_module.cpp      # 模块包装器
│   │   └── ...
│   │
│   ├── modules/                     # 模块 DLL
│   │   ├── awake/                   # Awake 模块
│   │   │   ├── Awake/               # DLL 实现
│   │   │   └── AwakeApp/            # 独立进程
│   │   │
│   │   └── lightswitch/             # LightSwitch 模块
│   │       └── LightSwitch/         # DLL 实现
│   │
│   ├── settings-ui/                 # Settings UI (WinUI3)
│   │   ├── Settings.UI/             # 主项目 (709 文件)
│   │   │   ├── Views/               # XAML 页面
│   │   │   ├── ViewModels/          # MVVM 视图模型
│   │   │   └── MainWindow.xaml.cs
│   │   │
│   │   └── Settings.UI.Library/     # 共享库
│   │
│   └── common/                      # 共享代码
│       ├── interop/                 # C++ ↔ C# 互操作
│       ├── logger/                  # 日志系统
│       └── ...
│
├── doc/devdoc/                      # 架构文档
│   ├── architecture-comparison.md   # 对比分析
│   ├── powertoys-architecture.md    # PowerToys 参考
│   └── kit-architecture.md          # 本文档
│
├── Cpp.Build.props                  # C++ 构建配置
├── Directory.Build.targets          # MSBuild 目标
└── Version.props                    # 版本号 (2.0.8)
```

## Optimization Roadmap

### 快速优化（Phase 1）

**目标**: 250-550ms 改进

1. **延迟 Quick Access 启动**
   - 当前: 启动时同步生成进程
   - 优化: 首次 Win+Space 按下时启动
   - 收益: 200-400ms

2. **延迟模块 enable()**
   - 当前: 启动时立即调用 enable()
   - 优化: 消息循环后空闲时间调用
   - 收益: 50-150ms

### 中期优化（Phase 2）

**目标**: 120-250ms 额外改进

1. **异步托盘图标创建**
   - 后台线程创建
   - 与模块加载并行
   - 收益: 100-200ms

2. **并行模块加载**
   - std::async 并发加载 2 个 DLL
   - 收益: 20-50ms（有限）

### 长期优化（Phase 3）

**目标**: 20-50ms 额外改进

1. **设置缓存**
   - 内存映射文件或内存缓存
   - FileSystemWatcher 监控更改
   - 收益: 20-50ms

## Performance Targets

| 场景 | 当前 | 目标 | 方法 |
|------|------|------|------|
| **温启动** | 800-1000ms | <500ms | Phase 1+2 |
| **冷启动** | 1000-1200ms | <800ms | Phase 1+2 |
| **最小配置** | 600-800ms | <400ms | 所有模块禁用 |

## Testing Strategy

### 测量工具

```cpp
// 内联计时器
class ScopedTimer {
    LARGE_INTEGER start;
public:
    ScopedTimer(const char* name) : name(name) {
        QueryPerformanceCounter(&start);
    }
    ~ScopedTimer() {
        LARGE_INTEGER end, freq;
        QueryPerformanceCounter(&end);
        QueryPerformanceFrequency(&freq);
        double ms = (end.QuadPart - start.QuadPart) * 1000.0 / freq.QuadPart;
        Logger::info("{}: {:.2f}ms", name, ms);
    }
};

// 使用示例
void WinMain() {
    ScopedTimer timer("Startup");
    
    {
        ScopedTimer t("Settings Load");
        load_general_settings();
    }
    
    {
        ScopedTimer t("Module Loading");
        load_powertoys();
    }
    
    // ...
}
```

### 测试场景

1. **基线测量**: 当前启动时间分布
2. **冷/温启动**: 重启后 vs 后续启动
3. **Quick Access 禁用**: 跳过 WinUI3 进程
4. **模块禁用**: 隔离 Runner 开销
5. **压力测试**: 高 CPU/磁盘负载下启动

## Future Considerations

### 模块扩展

如果添加更多模块（>5）：
- 重新评估并行加载收益
- 考虑模块加载优先级队列
- 实现动态模块发现（扫描目录）

### 性能监控

- 添加 ETW 标记（可选）
- 构建启动时间仪表板
- 回归检测自动化

## References

- **PowerToys 架构**: `doc/devdoc/powertoys-architecture.md`
- **对比分析**: `doc/devdoc/architecture-comparison.md`
- **源码**: `src/runner/`, `src/modules/`
- **设置 UI**: `src/settings-ui/Settings.UI/`
