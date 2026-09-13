# PowerToys Architecture Reference

> 基于 `Source/PowerToys` 源码的架构分析

## Overview

PowerToys 使用 DLL 插件架构，中心 Runner 进程加载并协调独立的模块 DLL。每个模块实现 `PowertoyModuleIface` 接口并导出 `powertoy_create()` 工厂函数。

## Core Components

### 1. Runner (PowerToys.exe)

**职责**:
- 进程生命周期管理
- 托盘图标和菜单
- 模块发现和加载
- 集中式键盘钩子
- 设置窗口协调
- 消息循环

**关键文件**:
- `src/runner/main.cpp` - WinMain 入口点
- `src/runner/powertoy_module.cpp` - 模块 RAII 包装器
- `src/runner/general_settings.cpp` - 设置持久化
- `src/runner/centralized_hotkeys.cpp` - 热键管理

### 2. PowertoyModuleIface

**接口契约**:

```cpp
class PowertoyModuleIface {
public:
    virtual const wchar_t* get_name() = 0;
    virtual const wchar_t* get_key() = 0;
    
    virtual bool enable() = 0;
    virtual void disable() = 0;
    virtual bool is_enabled() = 0;
    
    virtual intptr_t get_config(wchar_t* buffer, int* buffer_size) = 0;
    virtual void set_config(const wchar_t* config) = 0;
    
    virtual size_t get_hotkeys(Hotkey* buffer, size_t buffer_size) = 0;
    virtual bool on_hotkey(size_t hotkeyId) = 0;
    
    virtual void destroy() = 0;
};
```

**工厂函数**:
```cpp
extern "C" __declspec(dllexport) PowertoyModuleIface* __cdecl powertoy_create()
```

### 3. Module DLLs

**33+ 模块包括**:
- FancyZones - 窗口布局管理
- PowerRename - 批量重命名
- MouseUtils - 鼠标增强
- AlwaysOnTop - 窗口置顶
- Awake - 保持唤醒
- ColorPicker - 颜色选择器
- FileLocksmith - 文件解锁
- ImageResizer - 图片批量调整
- KeyboardManager - 键盘映射
- PowerToys Run - 启动器
- QuickAccent - 快速重音符号
- ...等

**模块结构**:
```
modules/
├── ModuleName/
│   ├── dllmain.cpp           # DLL 入口 + powertoy_create()
│   ├── ModuleInterface.cpp   # PowertoyModuleIface 实现
│   ├── trace.cpp             # ETW 遥测
│   └── ...功能实现
```

### 4. CentralizedKeyboardHook

**机制**: `WH_KEYBOARD_LL` 低级键盘钩子

**工作流程**:
1. Runner 调用 `SetWindowsHookEx(WH_KEYBOARD_LL, ...)`
2. 模块通过 `SetHotkeyAction()` 注册回调
3. 钩子过程匹配热键并调用模块回调
4. 使用 `dwExtraInfo` 标志过滤生成的按键

**冲突检测**:
- `HotkeyConflictDetector` 跟踪所有注册的热键
- `HotkeyConflictManager` 验证新分配
- 阻止重复热键分配

### 5. Settings System

**持久化**: `general_settings.json`

**结构**:
```json
{
  "startup": true,
  "enabled": {
    "Awake": true,
    "FancyZones": true,
    "PowerRename": false
  },
  "module_settings": {
    "Awake": {
      "mode": "keep_awake",
      "keep_display_on": true
    }
  }
}
```

**API**:
- `load_general_settings()` - 启动时加载
- `save_general_settings()` - 更改时保存
- `apply_module_status_update()` - 运行时启用/禁用

### 6. TwoWayPipeMessageIPC

**用途**: Runner ↔ WinUI3 进程通信

**通信模式**:
- Settings UI 发送配置更改
- Runner 发送模块状态更新
- Quick Access 请求模块操作

**管道命名**: `\\.\pipe\powertoys_<name>_<pid>`

## Initialization Flow

### 启动序列

```
1. WinMain Entry
   ├─ SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)
   ├─ CoInitializeSecurityWithAppId() - COM 安全初始化
   └─ check_install_location() - 验证安装路径

2. Load Settings
   └─ parse_settings(general_settings.json)

3. Create UI Components
   ├─ tray_icon.init() - 托盘图标 + 菜单
   └─ quick_access::start() - 可选的 Quick Access 进程

4. Initialize Input System
   └─ CentralizedKeyboardHook::Start()

5. Module Discovery & Loading
   ├─ 遍历 knownModules 向量 (33+ 路径)
   │  ├─ LoadLibraryW(module_path)
   │  ├─ GetProcAddress("powertoy_create")
   │  ├─ factory() → PowertoyModuleIface*
   │  ├─ PowertoyModule wrapper 构造
   │  │  ├─ update_hotkeys()
   │  │  └─ UpdateHotkeyEx()
   │  └─ modules()[key] = module
   │
   └─ 错误处理: Logger::warn (Release) / MessageBox (Debug)

6. AI Capability Detection (如果 ImageResizer 启用)
   └─ spawn ImageResizer.exe --detect-ai + 等待完成

7. Enable Configured Modules
   └─ start_enabled_powertoys()
      └─ for enabled modules: module.enable()

8. Create Optional Windows
   ├─ Settings Window (if requested)
   └─ OOBE Window (if first run)

9. Enter Message Loop
   └─ run_message_loop() - GetMessage/DispatchMessage

10. Shutdown
    ├─ disable all modules
    ├─ destroy module interfaces
    └─ FreeLibrary DLL handles
```

### 模块加载详解

```cpp
// main.cpp
std::vector<std::wstring_view> knownModules = {
    L"modules\\PowerToys.Awake.dll",
    L"modules\\PowerToys.FancyZonesModuleInterface.dll",
    // ... 33+ 模块路径
};

void load_powertoys() {
    for (const auto& module_path : knownModules) {
        try {
            HMODULE handle = LoadLibraryW(module_path);
            auto create_fn = GetProcAddress(handle, "powertoy_create");
            PowertoyModuleIface* instance = create_fn();
            
            // RAII 包装器管理生命周期
            PowertoyModule module(handle, instance);
            modules()[module.get_key()] = std::move(module);
        }
        catch (...) {
            Logger::warn(L"Failed to load {}", module_path);
        }
    }
}
```

## Startup Bottlenecks

### 识别的瓶颈

1. **Sequential Module Loading** (~300-800ms)
   - 33+ DLL 逐个同步加载
   - 每个 LoadLibrary 触发依赖解析

2. **AI Detection Subprocess** (~200-500ms)
   - 阻塞式生成 ImageResizer.exe --detect-ai
   - 等待进程完成

3. **Settings File I/O** (~50-100ms)
   - 启动时同步读取 general_settings.json

4. **Module Enable Calls** (~200-1000ms)
   - start_enabled_powertoys() 顺序调用 enable()
   - 某些模块生成窗口/线程/资源

5. **Hotkey Registration** (~50-100ms)
   - 每个模块构造时注册热键
   - 需要遍历热键数组 + 映射插入

## Communication Patterns

### Runner → Module

**直接接口调用**:
```cpp
// 启用模块
modules()[L"Awake"].enable();

// 配置更新
modules()[L"Awake"].set_config(json_config);

// 热键触发
modules()[L"Awake"].on_hotkey(hotkey_id);
```

### Module → Runner

**通过回调**:
```cpp
// 模块在构造时注册
SetHotkeyAction(hotkey_id, []() {
    // 热键按下时由 Runner 调用
});
```

### Runner ↔ Settings UI

**TwoWayPipeMessageIPC**:

```cpp
// Runner 发送状态更新
send_json_config_to_settings_ui({
    "module": "Awake",
    "enabled": true
});

// Settings UI 发送配置
receive_json_config_from_settings_ui({
    "module": "Awake",
    "properties": { "mode": "keep_awake" }
});
```

## Group Policy Objects (GPO)

**企业管理**:
- 注册表策略强制禁用模块
- `HKLM\Software\Policies\PowerToys`
- Runner 在 `start_enabled_powertoys()` 时检查策略

## Telemetry (ETW)

**事件记录**:
```cpp
TraceLoggingWrite(
    g_hProvider,
    "Awake_Enable",
    TraceLoggingKeyword(PROJECT_KEYWORD_MEASURE)
);
```

**提供者**: `Microsoft.PowerToys`

## Best Practices Observed

### 1. RAII Resource Management

```cpp
class PowertoyModule {
    HMODULE m_handle;
    PowertoyModuleIface* m_module;
    
    ~PowertoyModule() {
        if (m_module) m_module->destroy();
        if (m_handle) FreeLibrary(m_handle);
    }
};
```

### 2. Settings Synchronization

- 单一真相来源 (general_settings.json)
- 写时原子化 (写临时文件 + 移动)
- 模块通过 `set_config()` 接收更新

### 3. Error Resilience

- 模块加载失败不会使 Runner 崩溃
- 每个模块在隔离的 DLL 中
- try-catch 围绕模块操作

### 4. Hotkey Conflict Prevention

- 中心化冲突检测器
- 注册前验证
- UI 在设置中显示冲突

## Optimization Opportunities

### 从源码识别

1. **Parallel Module Loading**
   - 模块加载无相互依赖
   - 可使用线程池并发

2. **Lazy Module Init**
   - 延迟 enable() 直到首次使用
   - 按需激活通过托盘菜单/热键

3. **Async AI Detection**
   - 后台运行 ImageResizer 检测
   - 完成时更新能力标志

4. **Settings Caching**
   - 内存中保留解析的设置
   - 文件监视器检测更改

5. **Hotkey Batching**
   - 收集所有模块热键
   - 单次批量注册

## Module Development Template

```cpp
// dllmain.cpp
#include <interface/powertoy_module_interface.h>

class MyModule : public PowertoyModuleIface {
public:
    const wchar_t* get_name() override { return L"MyModule"; }
    const wchar_t* get_key() override { return L"MyModule"; }
    
    bool enable() override {
        // 启动模块功能
        return true;
    }
    
    void disable() override {
        // 清理资源
    }
    
    bool is_enabled() override {
        return m_enabled;
    }
    
    // ... 其他接口方法实现
    
private:
    bool m_enabled = false;
};

extern "C" __declspec(dllexport) 
PowertoyModuleIface* __cdecl powertoy_create() {
    return new MyModule();
}
```

## References

- **源码位置**: `Source/PowerToys/src/runner/`
- **接口定义**: `Source/PowerToys/src/common/interface/powertoy_module_interface.h`
- **模块示例**: `Source/PowerToys/src/modules/*/`
- **设置系统**: `Source/PowerToys/src/runner/general_settings.cpp`
