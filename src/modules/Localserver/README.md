# Localserver 模块与插件移植经验

[English](README.md) · [简体中文](README.md)

本文档记录了将 **LocalServerHub**（原版独立 WinUI 3 应用）移植为 **Kit** 原生插件模块（`Localserver`）的架构演进、关键设计决策、踩坑教训与最佳工程实践，供后续模块开发和长期维护参考。

---

## 1. 模块基本信息与架构定位

`Localserver` 是 Kit 内部面向 Windows 11 的本地开发服务、微服务与环境监控中心。它负责注册、启动、监控和停止本地开发服务，并提供实时硬件状态（CPU/RAM/GPU）、环境自检、服务会话指标与端口管理能力。

| 属性 | 配置 / 路径 | 说明 |
| --- | --- | --- |
| **模块名称** | `Localserver` | 统一标识符，大小写固定 |
| **模块键名 (Key)** | `Localserver` | 不可包含空格、路径分隔符或版本后缀 |
| **原生接口 DLL** | `Kit.LocalserverModuleInterface.dll` | 导出 `kit_create()`，实现 `KitModuleIface` |
| **核心业务库** | `LocalserverLib.dll` | 包含服务运行引擎、Windows 进程/Job、硬件检测与配置存储 |
| **设置与管理界面** | `LocalserverPage.xaml` / `LocalserverViewModel.cs` | 运行在 Kit Settings 进程中（WinUI 3 + Mica Alt 体系） |
| **设置与 DTO** | `LocalserverSettings.cs` / `LocalserverProperties.cs` | 注册至 `KitModuleCatalog` 与 `SettingsSerializationContext` |
| **持久化数据目录** | `%LOCALAPPDATA%\Kit\Localserver` | 独立于原版 LocalServerHub 的私有配置与运行时状态目录 |
| **日志接入** | `LocalserverLogSink.cs` | 异步批量转发至 Kit 统一的 `ManagedCommon.Logger` |

---

## 2. 原版 (LocalServerHub) 与插件版架构差异

| 维度 | 原版 LocalServerHub | Kit 插件版 Localserver | 移植收益与架构决策 |
| --- | --- | --- | --- |
| **宿主形态** | 独立的 WinUI 3 进程 (`LocalServerHub.exe`)，拥有独立托盘图标与主窗口 | 嵌入 Kit 主框架：Runner 动态加载 C++ 接口 DLL，UI 嵌入 Settings 主窗口 | 消除多余后台常驻进程，与 Kit 系统托盘、导航与 Mica 视觉统一 |
| **生命周期** | 独立主窗口关闭即退出，需自实现托盘保活与退出管理 | 依附 Kit 统一的 Runner 进程与 Settings 页面导航生命周期 | 页面使用 `NavigationCacheMode.Required`，离页暂停采样省电，关闭 Settings 不杀外部服务 |
| **配置存储** | 固定单文件 `data/services.json`，无加密 | 统一接入 Kit 仓储，支持 `services.d` 多源合并与 DPAPI 敏感参数加密 | 提升微服务团队协同能力与 API Key/密码存储安全性 |
| **日志输出** | 独立重型 UI 日志列表控件，逐行同步解析与刷新 | 移除重型 UI 面板；内存保留轻量环形缓冲，落盘由后台 Channel 批量聚合 | 杜绝高吞吐日志时的 UI 假死与磁盘 I/O 尖刺 |
| **冗余功能** | 包含单行“开机自启”、主界面显示过滤、“最后错误”堆叠等 | 彻底剔除冗余项，开机自启由 Kit 统一调度管理，状态聚焦于当前健康 | 界面极度精炼，符合 Kit 插件规范第 12.5 节与单元测试约束 |
| **进程并存** | 静态 Job Object 名称，双开易发生所有权抢占 | 引入动态随机 Owner Tag 与严格六重归属校验 | Kit 插件版与原版 LocalServerHub 完全可以安全共存，互不干扰 |

---

## 3. 核心移植经验与踩坑教训

### 3.1 进程所有权与 Windows Job Objects 并存隔离

- **背景与痛点**：
  原版使用固定的命名 Job Object（如 `Local\LocalServerHub.Service.<hash>`）。若用户同时打开原版应用与 Kit，两个进程会尝试打开或复用同一个命名 Job，导致子进程句柄冲突或服务归属混乱。
- **移植解决方案**：
  1. **动态 Owner Tag**：每次启动生成唯一的随机实例标记，Job Object 命名融合服务 ID 与随机 Tag，杜绝两套系统间的命名碰撞。
  2. **严格六重归属核验 (`ServiceOwnershipStore`)**：
     在尝试停止、检查或接管服务前，必须同时比对：
     - 进程 PID
     - 进程启动时间戳 (`StartTimeUtc`)
     - 进程镜像完整路径 (`ExecutablePath`)
     - 配置指定的服务可执行文件
     - 工作目录 (`WorkingDirectory`)
     - 实例 Owner Tag
     六项指标只要有一项不符，坚决视为外部独立进程，**绝不强杀、绝不接管**。
  3. **端口释放竞态防范 (`PortInspector.ReleaseAsync`)**：
     当用户点击“强制释放端口”时，严禁仅凭 PID 直接调用 `Process.Kill()`。必须二次核实端口实际监听者的 PID 与创建时间，防止短命进程退出后 PID 被系统分配给无关应用而造成误杀。

### 3.2 页面生命周期与按需采样优化

- **背景与痛点**：
  原版作为独立桌面应用，主界面的 CPU/GPU 资源采样定时器（1~2 秒轮询）始终常驻运行。但作为 Kit 的 Settings 子页面，用户可能在大部分时间处于 General、Awake 或 LightSwitch 页面。若后台持续进行 WMI 查询与进程枚举，将带来 1%~3% 的无谓 CPU 占用与电池损耗。
- **移植解决方案**：
  1. **导航生命周期感知**：
     设置页面启用 `NavigationCacheMode.Required` 以保留页面和滚动状态。在 `OnNavigatedTo` 中激活采样定时器，在 `OnNavigatedFrom` 中**立即停止定时器**。
  2. **首帧主动刷新与防重入**：
     切回页面时立即触发一次异步首帧采样，同时对 `RefreshTelemetryAsync()` 和 `RefreshEnvironmentAsync()` 加设原子防重入标志或信号量，防止多次快速点击引发并发竞争。
  3. **总开关与运行态解耦**：
     Kit 顶部模块开关控制“是否允许启动和管理新服务”。如果用户关闭了 Localserver 总开关，正在运行的后台服务**不应被强制终止**，避免开发调试现场丢失；但所有编辑、启动动作将被安全置灰。

### 3.3 界面排版、文字渲染与 WinUI 3 视觉对齐

在将原版 XAML 迁移到 Kit 设置框架时，暴露了大量由于“固定尺寸假设”和“原始系统字符串”导致的排版缺陷：

1. **动态自适应列宽（根除 "HOST" 标签折行）**：
   - *缺陷*：原版使用 `Grid ColumnDefinitions="30,*"`，但 Windows 11 下英文 "HOST" 文本实际渲染宽度约为 34px，导致在 30px 列宽下折行为两行：`HOS\nT`。
   - *经验*：对所有标签列坚决采用 `ColumnDefinitions="Auto,*"`，配合 `Margin="0,0,12,0"`，彻底消除非中文语言环境下的折行截断。
2. **硬件与系统信息的商业友好化清洗**：
   - *缺陷*：原版直接读取环境变量和 WMI 原生字段，导致界面显示出机器底层的内部调试字符串：
     - CPU：`AMD64 Family 25 Model 80 Stepping 0, AuthenticAMD · 12 logical cores`
     - OS：`Microsoft Windows NT 10.0.26100.0`
     - GPU：冗长的 WMI 驱动名称（如 `NVIDIA GeForce RTX 3050 Ti Laptop GPU`），占满整行。
   - *经验*：在 `LocalserverViewModel` 中增加清洗方法：
     - 读取注册表 `ProcessorNameString` 并精简为商业名称：`Ryzen 5 5600H · 12 logical cores`；
     - 读取注册表 `ProductName`、`DisplayVersion`、`UBR`，双行提炼为 `Windows 11 LTSC` 与 `Build 26100.9168`；
     - GPU 清洗为 `RTX 3050 Ti`，驱动信息格式化为 `Driver 566.36 · 1 device`，并具备 `ACTIVE` / `IDLE` 状态徽标。
3. **文本高对比度语义化绑定**：
   - *缺陷*：原版自检结论文本（"All environment checks passed"）绑定了服务的运行状态颜色。由于默认处于停止状态，该文本被渲染为极浅的灰色，在暗色/浅色主题下几乎不可见。
   - *经验*：解耦自检结果与运行状态，新增 `EnvSummaryBrush`，自检全部通过时强制赋予高对比度的 `SystemFillColorSuccessBrush`（清晰绿色加粗），失败时赋予危险红。
4. **健康环（Health Ring）视觉减负**：
   - *缺陷*：原版在 88x88 环形进度条内塞入了对勾图标、状态文字和静态 "HEALTH" 标签三重元素，视觉拥挤凌乱。
   - *经验*：移除非必要的装饰性图标与静态文本，环心居中只展示清晰大号的状态字（`STOPPED` / `RUNNING` / `IDLE`，14px SemiBold），质感与留白大幅提升。
5. **等宽指标与字号层级规范**：
   - 严格遵循 Kit 规范定义通用资源样式：
     - `MetricPrimaryStyle`：20px SemiBold 等宽数字（用于 PORT、PID、RUNTIMES）
     - `MetricCompactStyle`：18px SemiBold 等宽数字（用于 REGISTERED、RUNNING、FAULTED）
     - `MetaLabelStyle`：10px SemiBold，60 tracking，大写 Secondary 标签
   - 所有命令行与路径显示显式添加 `TextWrapping="NoWrap"` 与 `TextTrimming="CharacterEllipsis"`，防止长路径破坏卡片栅格平衡。

### 3.4 高吞吐日志子系统的异步化与有界化

- **痛点分析**：
  本地微服务（尤其是 Node.js、Spring Boot、Python 训练脚本）在冷启动或构建时可能产生每秒数千行的 stdout/stderr 输出。原版采用 UI 控件逐行追加，导致：
  1. UI 线程消息队列被海量 Dispatcher 消息淹没，导致窗口卡死；
  2. 磁盘 I/O 频繁单行写入，导致严重的磁盘抖动；
  3. 内存无限增长引发 OOM。
- **移植架构 (`LocalserverLogSink`)**：
  1. **UI 与落盘完全分离**：UI 界面不再展示实时流式日志面板，仅在内存维护一个轻量 `LogRingBuffer`（默认保留最近 1000 行），专门用于异常健康诊断正则匹配。
  2. **有界背压管道**：使用 `System.Threading.Channels.Channel<QueuedLine>` 构建最大容量 512 的有界队列，超出容量时采用 `DropOldest` 丢弃老旧日志并记录丢弃计数，确保主进程内存绝对安全。
  3. **单行与服务 ID 截断**：单行正文限制 8,192 字符，ServiceId 限制 256 字符，超长附加 `... [truncated]` 标记，防范单行超大 JSON 搞崩日志解析器。
  4. **定时定量批处理刷盘**：后台专用消费线程以 100ms 为间隔批量拉取日志，单批次最多合并 128 行或 65,536 字符，调用 Kit 统一的 `ManagedCommon.Logger` 进行单次写入。
  5. **进程退出优雅收尾**：注册 `AppDomain.ProcessExit`，退出时给予最多 500ms 排空排队日志，随后立即关闭。

### 3.5 国际化与业务状态彻底解耦

- **反模式警告**：
  严禁根据界面的本地化文本判断业务逻辑（如 `if (statusText == "已停止")`）。一旦切换语言或修改文案，状态机将瞬间崩溃。
- **最佳实践**：
  - 核心状态一律由强类型枚举表达（`ServiceStatus`、`HealthState`、`EnvironmentStatus`）；
  - XAML 元素全部打上 `x:Uid`，文案从 `Strings/en-us/Resources.resw` 与 `Strings/zh-CN/Resources.resw` 加载；
  - ViewModel 动态拼接字符串统一调用 `ResourceLoader.GetLocalized()`；
  - 本模块中英文资源文件各保留 212 个本地化键，保持 100% 结构对称。

### 3.6 凭据安全与敏感数据保护

- 在微服务配置中常常包含数据库密码、Token 或 API Key（如 AI 诊断服务）。
- 移植时废弃了原版明文写盘的做法，引入 `System.Security.Cryptography.ProtectedData` (Windows DPAPI)。
- 使用当前用户上下文 (`DataProtectionScope.CurrentUser`) 加密敏感字段后再写入配置文件，即便配置文件被意外分享或提交到代码库，也不会造成跨机器凭据泄露。

---

## 4. 关键源码与工程对应关系

```text
src/
├── modules/Localserver/
│   ├── LocalserverModuleInterface/    # 原生 C++ 接口 (KitModuleIface)
│   │   ├── dllmain.cpp                # 导出 kit_create()，响应 enable/disable
│   │   └── LocalserverConstants.h     # 模块 Key 常量定义
│   ├── LocalserverLib/                # 核心托管业务类库 (netstandard / net10.0)
│   │   ├── Engine/                    # ServiceRunner, JobObject, 进程归属控制
│   │   ├── Configuration/             # CatalogStore, SecretStore, JSON 转换
│   │   ├── Monitors/                  # GpuInspector, EnvironmentInspector, PortInspector
│   │   └── Logging/                   # LogRingBuffer (健康诊断环形缓冲)
│   └── README.md                      # 本文档
│
├── settings-ui/
│   ├── Settings.UI/
│   │   ├── SettingsXAML/Views/
│   │   │   ├── LocalserverPage.xaml   # 模块主界面 (WinUI 3 + Mica Alt)
│   │   │   └── LocalserverPage.xaml.cs# 导航生命周期感知 (暂停/恢复采样)
│   │   ├── ViewModels/
│   │   │   └── LocalserverViewModel.cs# 数据绑定、硬件精简算法、状态控制
│   │   └── Helpers/
│   │       └── LocalserverLogSink.cs  # 有界后台 Channel 日志聚合管道
│   │
│   ├── Settings.UI.Library/
│   │   ├── LocalserverSettings.cs     # 模块设置模型 DTO
│   │   ├── LocalserverProperties.cs   # 模块属性配置
│   │   └── SndLocalserverSettings.cs  # IPC 发送模型
│   │
│   └── Settings.UI.UnitTests/
│       └── ViewModelTests/
│           └── Localserver.cs         # 移植边界、死锁防范与裁剪合规性单元测试
```

---

## 5. 质量验证与回归规范

在进行 Localserver 相关的修改后，必须通过以下多维验证标准：

1. **单元测试回归**：
   运行 `Settings.UI.UnitTests.dll` 确保 100% 通过（重点关注 `Localserver.cs` 中的删除项断言、路径隔离校验及序列化往返测试）：
   ```powershell
   & "vstest.console.exe" Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.dll /Platform:x64
   ```
2. **构建一致性校验**：
   全量解决方案编译需保持 0 Error，`LocalserverLib.dll` 需正确输出至测试和运行目录。
3. **视觉与布局验收**：
   - 验证英文环境（`en-us`）下 "HOST"、"CPU"、"STATE" 标签无折行；
   - 验证自检文本 "All environment checks passed" 为清晰鲜艳的高对比度绿色；
   - 验证离开设置页时采样定时器彻底暂停，不产生后台 CPU 唤醒。
