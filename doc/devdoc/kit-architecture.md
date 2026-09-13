# Kit 架构参考（轻量插件宿主方向）

> 本文按当前 Kit 源码核对（src/runner/main.cpp、general_settings.cpp、settings_window.cpp、src/common/SettingsAPI、src/settings-ui），并按核心需求更新：
> **主框架轻量、启动快、按插件开发逻辑兼容官方 PowerToys 插件与第三方自定插件。**

## 1. 核心目标与设计原则

- 主框架最小化：runner + Settings UI + 公共库不内置任何模块业务逻辑，模块一律以插件承载；
- 启动快：只加载已启用插件；Quick Access 延迟到首次使用；关键路径全程可计时（STARTUP_TIMING）；
- 插件兼容：插件契约 = PowertoyModuleIface + powertoy_create()，与上游 PowerToys 完全一致，官方模块复制即用、第三方按契约开发；
- 第一方/第三方分层：第一方模块走编译期清单（深度集成：Home、Quick Access、设置路由、测试），第三方插件走运行时清单（轻量接入：目录扫描 + manifest + 通用设置页）。

## 2. 进程模型

- Kit.exe（runner，src/runner）：托盘、插件加载与生命周期、全局热键、设置窗口协调、更新检查（check-only）；
- Kit.Settings.exe（WinUI3，src/settings-ui/Settings.UI）：Settings v2，MVVM；
- Kit.QuickAccess.exe（WinUI3，src/settings-ui/QuickAccess.UI）：快速访问，首次 Win+Space 才启动；
- 插件接口 DLL（实现契约）与可选附属进程（如 Awake 的 PowerToys.Awake.exe）。

进程间通信沿用上游：TwoWayPipeMessageIPC 命名管道（powertoys_runner_<uuid> / powertoys_settings_<uuid>），消息为 JSON。

## 3. 插件系统（本文核心）

### 3.1 契约（与上游一致，不变）

- 接口头：src/modules/interface/powertoy_module_interface.h；
- 工厂：extern "C" __declspec(dllexport) PowertoyModuleIface* __cdecl powertoy_create()；
- 必选：get_name / get_key / get_config / set_config / enable / disable / is_enabled / destroy；
- 可选：call_custom_action、get_hotkeys/on_hotkey、GetHotkeyEx/OnHotkeyEx、gpo_policy_enabled_configuration 等；
- 设置 UI 描述：get_config() 输出 PowerToysSettings::Settings 序列化 JSON（properties / links / custom_actions），见 powertoys-architecture.md §5.3。

### 3.2 现状：第一方硬编码加载

- KitKnownModules（src/runner/main.cpp）：constexpr 数组，仅两项
    L"PowerToys.AwakeModuleInterface.dll"
    L"PowerToys.LightSwitchModuleInterface.dll"
- is_known_module_registered()：白名单校验，非白名单模块在注册时被拒；
- 加载循环：for 每个 known module → load_powertoy() → modules().emplace(get_key())；
- 未启用模块也会被加载，启停由 start_enabled_powertoys() 在加载后过滤；
- 缺点：新增插件必须改源码并重编译 runner；未启用插件仍付出加载开销。

### 3.3 目标：插件宿主模型

- plugins/ 目录（与 runner 同级）承载第三方插件，每个插件一个子目录：接口 DLL + manifest.json；
- manifest.json 字段：key、name、dll、version、author、enabledByDefault；
- 加载规则：仅加载"已启用"的插件（enabled 集合来自 general settings + GPO 过滤）；第一方模块仍走编译期清单；
- 校验：manifest 字段完整、key 不冲突、DLL 导出 powertoy_create；失败插件跳过并记录，不影响启动；
- 枚举：Settings UI 经 IPC get_all_settings() 拿到 { general, powertoys: { key: json_config } }，第三方插件页面由通用 PluginSettingsPage 渲染（见 §6）；
- 白名单语义扩展：is_known_module_registered() 变为"编译期清单 ∪ plugins/ 清单"。

## 4. Runner 启动流程（实测阶段与计时）

src/runner/main.cpp 全程 log_timing，阶段如下：

1. DPI Awareness；
2. Trace Provider 注册；
3. Load Settings（读 general settings）；
4. Tray Icon（托盘窗口创建）；
5. Update Worker（PeriodicUpdateWorker，check-only 线程）；
6. Quick Access Hotkey（仅注册 Win+Space 热键，进程延迟到首次使用）；
7. Tray Icon Visible；
8. Keyboard Hook（CentralizedKeyboardHook::Start）；
9. Chdir（切到可执行目录）；
10. Video Conference Cleanup（仅提权且首次，注册表标记一次性）；
11. 模块加载（按 KitKnownModules，失败 Debug 日志 / Release 弹窗）；
12. Modules Enabled（start_enabled_powertoys，GPO + 用户配置过滤）；
13. Event Launch；
14. 总时长日志。

已落地的启动优化：

- Quick Access 延迟启动：首次 Win+Space 才拉起 WinUI3 进程，省 200-400ms（main.cpp 注释明确）；
- clean_video_conference_once：注册表标记只清理一次，省 10-20ms 后续提权启动；
- STARTUP_TIMING 全阶段计时日志，作为优化基线。

## 5. 设置与存储

- 根目录：%LOCALAPPDATA%/Kit/（CommonSharedConstants::APPDATA_PATH = L"Kit"，与官方 PowerToys 完全隔离）；
- 全局：根下 settings.json；每模块：%LOCALAPPDATA%/Kit/<模块 key>/settings.json；
- Settings v2：src/settings-ui/Settings.UI（页面/视图模型）+ Settings.UI.Library（SettingsRepository<T>、SettingsUtils、每模块模型）；
- 配置流：Settings 改 → SettingsRepository 写盘 → IPC → runner dispatch_json_config_to_modules → 插件 set_config；
- 反向：runner get_power_toys_settings() 收集各插件 json_config() → get_all_settings() 发给 Settings UI。

## 6. 设置 UI 结构（第三方插件接入点）

- 编译期页面：ShellPage、DashboardPage、GeneralPage、AwakePage、LightSwitchPage、SearchResultsPage；
- 导航：NavigationService + ShellViewModel（模块 → 页面类型映射为编译期）；
- 第三方插件接入：新增通用 PluginSettingsPage，运行时渲染 get_config JSON 描述的控件（bool_toggle / int_spinner / string / multiline_string / color_picker / hotkey / choice_group / dropdown / custom_action / header_szLarge）；
- 路由：ShellViewModel 把 plugins/ 清单中的 key 映射到 PluginSettingsPage（参数为 key），避免为每个插件写 XAML；
- 热键冲突检测沿用上游约定：插件 get_hotkeys() 顺序 = IHotkeyConfig.GetAllHotkeyAccessors() 顺序 = ViewModel.GetAllHotkeySettings() 顺序。

## 7. 启动瓶颈与优化路线

现状：第一方仅 2 个模块，启动已较快；目标是在插件数量增长时保持轻快。

- 只加载已启用插件：未启用插件不再加载（当前实现会加载后过滤）；
- 并行加载：插件多时 LoadLibrary 阶段并行（enable() 视模块情况异步化）；
- 设置 UI 进程按需启动：Settings 窗口打开时才拉起（深链接除外）；
- 清理：clean_video_conference_once 可在确认无历史 VCM 注册后彻底移除；
- 度量：以 STARTUP_TIMING 总时长为基线，纳入 CI 冒烟。

## 8. 官方插件兼容的前提（框架级缺口，P0）

复制官方模块到 Kit 运行，除契约外还依赖以下框架件（当前 Kit 缺/漂移）：

- ManagedTelemetry（Microsoft.PowerToys.Telemetry 程序集 + Directory.Build.targets 的 KitRemoveInactiveManagedTelemetryArtifactsFromOutput target）：Awake 等官方托管侧代码需要；
- logger_settings.h：launcherLoggerName 等常量与上游最新版有漂移；
- EtwTrace / TraceBase / TraceLoggingDefines：版本落后上游；
- shared_constants.h：APPDATA_PATH 已改为 Kit（官方模块编译进来会自然继承，属优点）；但 UpdateUtils 等硬编码 PowerToys 路径/仓库的模块需兼容层或列入例外；
- 依赖排除清单：AI 全家桶（OpenAI/SemanticKernel/LanguageModelProvider）、AdvancedPaste 等维持不引入（见 next.md §14 与 fix.md）。

## 9. 与其它文档的关系

- **主框架代码结构详解**：[kit-framework-structure.md](kit-framework-structure.md) — 源码层级、启动流程追踪、IPC 消息流、设置读写机制；
- **插件契约与上游框架细节**：[powertoys-architecture.md](powertoys-architecture.md) — PowerToys 完整架构参考；
- **架构对比分析**：[architecture-comparison.md](architecture-comparison.md) — Kit 与 PowerToys 的差异点；
- **启动优化专项**：[startup-optimization-analysis.md](startup-optimization-analysis.md) — 性能分析与优化方案；
- **同步状态与缺口**：[kit-sync-status.md](kit-sync-status.md) — 已同步/待同步模块清单；
- **上游差异与修正清单**：fix.md（主目录）；
- **行动方案**：fix.plan（主目录）— 分阶段任务、验收标准。