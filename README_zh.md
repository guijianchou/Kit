# Kit

**Language / 语言:** [English](README.md) | 中文

---

Kit 是一个基于 Microsoft PowerToys 的本地自用 Windows 实用工具工作区。它的存在使得选定的 PowerToys 实用工具可以被修改、隔离，并与同一台机器上安装的官方 PowerToys 构建进行比较。

## 项目目标

Kit 目前是一个稳定性优先的 PowerToys 衍生工作区，而不是一个完整的产品重新品牌化。主要设计选择是保持上游运行器、模块接口、设置和仪表板模式的可识别性，以便复制的 PowerToys 模块可以用最少的适配器代码进行验证。

Kit 特定的更改应保持小而有意：品牌、设置存储、可见导航、主页内容、备份和恢复默认值，以及删除不属于本地工作区的产品服务。

## 当前版本

当前 Kit 版本：`1.1.4`。

## 更新日志

### 1.1.4

- 更新：强制 GitHub release 检查绕过 HTTP 缓存，避免断网后的手动检查复用旧缓存并误报“已是最新”。
- 设置：手动检查会保持 “Checking for updates” 状态，直到拿到新的检查结果或超时，避免缓存状态覆盖正在进行的检查。
- 设置：检查期间禁用重复点击 Check for updates；仅在发现新版本时显示 release 链接。
- 测试：新增 no-cache release 检查、缓存状态竞态保护，以及 `1.1.4` README/版本/开发日志元数据回归覆盖。

### 1.1.3

- 通用：在 About 中添加 GitHub 仓库链接和手动检查更新入口，并与版本文本左对齐。
- 更新：新增仅检查的 GitHub release 检查，目标为 `https://github.com/guijianchou/Kit/releases`，后台每日检查一次，仅在有新版本时弹出 toast。
- 更新：保持 Kit 的更新边界为仅检查，不会自动下载、安装或启动更新程序。
- 设置：将 About 中的版本号和仓库文本从 caption 字号提升到 body 字号。
- 测试：为 Kit release 检查 IPC 路径、About 反馈状态和 `1.1.3` README/版本元数据添加回归覆盖。

### 1.1.2

- 启动：通过重用已加载的通用设置对象进行初始模块启用，而不是读取设置两次，减少了启动和首帧工作。
- 启动：从 Kit 运行器启动中删除了非活动的 OOBE/SCOOBE 版本状态读取和写入。
- 托盘：在托盘初始化期间停止读取 `UpdateState.json`，同时保持更新徽章 API 可用于任何未来的显式更新程序状态集成。
- 设置：将通用页面诊断清理、备份试运行刷新和搜索索引构建推迟到首帧之后。
- 主页：从主页快捷方式卡中隐藏了 Monitor 的仅状态激活行，因此 Monitor 不再显示为仅快捷方式模块，同时它仍然在模块列表、设置页面和快速访问设置回退中可用。
- 测试：为启动/加载优化边界、Monitor 主页快捷方式过滤以及 `1.1.2` 的更新版本元数据检查添加了回归覆盖。

### 1.1.1

- 构建：将 Kit 设置/通用 UI 构建层与本地 PowerToys-main .NET 10 基线对齐，包括共享的 CsWinRT 目标框架、快速访问、设置 UI 控件、通用 UI 控件、UITestAutomation 和中央包固定。
- 构建脚本和开发者文档现在引用 .NET 10 目标框架用于设置发布和 PowerToys Run 插件检查清单指导。
- 设置：添加了回归覆盖，以便 .NET 10 构建层、README 版本元数据和 Kit 的禁用更新程序/遥测边界不会悄悄漂移。
- 更新程序边界：Kit 保留系统托盘更新徽章渲染用于现有的 Kit 更新状态，但自动更新检查、下载、更新启动和遥测保持禁用。

### 1.1.0

- 将 PowerDisplay 导入到活动 Kit 模块集中，包括运行器加载、解决方案构建条目、设置导航、仪表板元数据、快速访问操作、序列化和 LightSwitch 配置文件路由。
- 设置：跨不同实用工具的多个 UI 和可用性改进。
- 通用：简化了默认模块状态，以便新安装以更轻的初始体验开始。
- 系统托盘图标：更新了单色 PowerToys 系统托盘图标，并保留了现有 Kit 更新状态的更新徽章渲染；自动更新检查和下载保持禁用。
- PowerDisplay 现在使用 Kit 应用数据路径和 Kit 前缀的运行时事件，因此它不与已安装的官方 PowerToys 构建共享状态或命名事件。

### 1.0.4

- Monitor 立即扫描现在遵循来自 `%LOCALAPPDATA%\Kit\Monitor\scan-progress.json` 的工作器报告进度和命名的扫描完成事件，而不是依赖于设置本地进度计时器。
- Monitor 在每次立即扫描请求之前清除陈旧的手动扫描进度，以便设置页面无法重用旧的已完成或临时进度状态。
- Monitor 工作器从扫描管道写入进度快照，包括阶段、已处理/总文件计数、完成时间和最终记录计数。
- Monitor 模块接口现在从模块输出目录解析工作器，并在调试输出没有 apphost `PowerToys.Monitor.exe` 时回退到 `dotnet.exe "PowerToys.Monitor.dll"`。
- 为 Monitor 进度文件报告、设置进度消费和模块接口工作器启动回退添加了回归覆盖。

### 1.0.3

- 发布构建在 `Kit.exe` 构建后从运行时输出中修剪本机链接工件（`*.lib`、`*.exp` 和静态库分析标记）。
- 发布构建从活动 Kit 输出中删除非英语运行时卫星文件夹和非活动 AI 模型提供程序工件，与托管卫星修剪匹配。
- 添加了 `tools\build\clean-stale-versions.ps1` 用于显式清理旧版本输出文件夹，同时保留活动版本、`Debug` 和 `Release`。
- 添加了 `tools\build\verify-runtime-artifacts.ps1` 以检查版本化或 `Release` 输出中的链接工件、PDB、Foundry 资产和非英语区域设置文件夹。
- 从 `Common.UI` 中删除了未使用的 WPF/WinForms 依赖项，以便设置和快速访问不会通过该共享库拉取 WPF 运行时程序集。
- 删除了非活动的设置模块源/XAML 文件，而不是将它们隐藏在 `Compile Remove` 和 `Page Remove` 规则后面。
- 从 `Kit.slnx` 中修剪了非活动的通用、DSC 和未使用的 Awake 服务项目，同时保留 `Common.Search`，因为设置搜索仍在使用它。
- 快速访问现在在可见磁贴没有直接启动器操作时打开模块的设置页面，包括 Monitor。

## 第一阶段收尾

第一阶段现在实际上是一个工作的 Kit 外壳加上一个新编写的模块。该框架可以加载显式的 PowerToys 风格模块，在设置和主页中显示它们，保持 Kit 品牌存储与官方 PowerToys 分离，并通过现有的运行器/模块接口/设置路径运行 Monitor 的下载工作流程。

当前稳定的交接点是：

- 保持 `Awake`、`Light Switch`、`Monitor` 和 `PowerDisplay` 作为活动模块集。
- 通过维护的列表和测试保持模块发现显式。暂时不要添加文件系统探测。
- 保持通用和主页使用英语 Kit 措辞，删除自动更新和遥测界面。
- 保持 Monitor 的工作器无头。用户操作和进度应通过设置/主页显示，而不是工作器窗口。
- 保持设置扫描进度与工作器进度/完成状态绑定。避免独立于工作器推进的扫描完成 UI。
- 在交接前清理构建工件，以便下一次 Visual Studio 构建从源状态开始。
- 工作区可以在稳定交接后减少回源大小。本地 `Debug`、`Release`、`x64`、`bin`、`obj`、`TestResults`、`.vs` 和恢复的 `packages` 目录是可丢弃的构建状态。

## 当前模块集

活动 Kit 模块集故意很小：

- `Awake`
- `Light Switch`
- `Monitor`
- `PowerDisplay`

Kit 不会自动公开源树中复制的每个上游 PowerToys 实用工具。模块仅在注册到运行器、设置导航、主页和测试的维护 Kit 列表后才启用。

## PowerToys 兼容性模型

Kit 遵循 PowerToys 模块加载模型，而不是发明新的插件协议。运行器通过 `src/runner/main.cpp` 中维护的 `KitKnownModules` 列表加载已知的模块接口 DLL，当前：

- `PowerToys.AwakeModuleInterface.dll`
- `PowerToys.LightSwitchModuleInterface.dll`
- `PowerToys.MonitorModuleInterface.dll`
- `PowerToys.PowerDisplayModuleInterface.dll`

这个固定列表是有意的。它避免了不稳定的目录探测，并使每个导入的模块成为显式的兼容性决策。当另一个 PowerToys 模块被引入 Kit 时，它应该一起添加到运行器、解决方案、设置路由、主页仪表板元数据和测试中。

## 架构

- `src/runner` 启动 Kit，加载模块接口 DLL，拥有模块生命周期，并与设置应用协调设置 IPC。可执行文件已经足够分离，可以作为 `Kit.exe` 启动，而许多面向构建的项目名称仍然保留上游 PowerToys 名称。在运行时，运行器从 `Kit.exe` 旁边的 `WinUI3Apps` 打开设置和快速访问应用，因此运行器构建目标必须保持对两个 UI 可执行项目的显式依赖。
- `src/modules` 包含活动实用工具。`Awake` 从上游 PowerToys 复制，包括 `Awake.ModuleServices`、`Awake` 和 `AwakeModuleInterface`；`LightSwitch` 是当前的 Kit 实用工具模块；`Monitor` 是从早期 Python 下载监视器创建的第一个 Kit 编写模块；`PowerDisplay` 从 PowerToys 风格模块形状导入，包括其设置页面、配置文件对话框、模型库、WinUI 应用和模块接口。
- `src/settings-ui/Settings.UI` 包含 WinUI 设置应用，包括主页、通用、模块页面、导航和页面级视图模型。
- `src/settings-ui/Settings.UI.Controls` 包含共享 UI 控件，如快速访问。
- `src/settings-ui/Settings.UI.Library` 包含设置模型、设置序列化、模块设置存储库、备份和恢复助手、GPO 助手和共享设置基础设施。
- `src/common` 保留运行器、模块和设置使用的共享本机和托管 PowerToys 基础设施。

运行时设置存储在 Kit 特定的应用程序数据下，例如 `%LOCALAPPDATA%\Kit\settings.json`，而不是官方 PowerToys 设置目录。备份和恢复默认值也使用 Kit 品牌，包括 `Documents\Kit\Backup`、`HKCU\Software\Microsoft\Kit` 和 `Kit_settings_*` 临时备份文件夹。

## 最近的 Awake 和主页实现

最新的主页工作保持 PowerToys 行为，但将其范围限定为 Kit 的活动模块：

- `DashboardViewModel` 使用 `KitModuleCatalog.DashboardModules`，当前为 `Awake`、`LightSwitch`、`Monitor` 和 `PowerDisplay`，因此主页实用工具列表是固定和可预测的。
- `QuickAccessViewModel` 仍然支持可操作的快速访问项，但主页传递仪表板模块列表，以便启用的 Kit 模块一致显示。
- 快速访问首先尝试正常启动器。如果模块没有直接快速操作，主页会回退到打开该模块的设置页面。这让 `Awake` 和 `Monitor` 在不创建虚假快捷方式操作的情况下有用地运行，而 `LightSwitch` 和 `PowerDisplay` 保持直接切换操作。
- `Awake` 贡献一个 `DashboardModuleActivationItem`，在主页快捷方式卡中显示当前 Awake 模式，使用现有的 PowerToys 仪表板项模板。
- 快速访问空状态现在使用可见项的计数，而不是原始项集合计数，因此禁用或 GPO 隐藏的模块不会留下可见的空卡。

## Monitor 实现

Monitor 是第一个直接针对 PowerToys 模块形状开发的 Kit 模块。它保持了早期 Python 监视器的核心行为，同时将实现移动到可构建、可测试的 Kit 项目中：

- `src/modules/Monitor/MonitorLib` 包含用于下载扫描、扩展和智能规则分类、SHA1 哈希、Python 兼容 CSV 持久化、重复分组、文件组织、安装程序清理原语和扫描进度快照的托管核心库。
- `src/modules/Monitor/Monitor` 在存在 apphost 时将 Monitor 工作器构建为 `PowerToys.Monitor.exe`，在无 apphost 的调试输出中构建为 `PowerToys.Monitor.dll`。它支持 `--scan-once` 用于一次性扫描，将进度写入 `%LOCALAPPDATA%\Kit\Monitor\scan-progress.json`，发出命名的扫描完成事件，并支持 `--pid` 用于运行器管理的生命周期。
- `src/modules/Monitor/MonitorModuleInterface` 构建 `PowerToys.MonitorModuleInterface.dll`。它遵循 Awake/LightSwitch 接口模式：`powertoy_create`，键 `Monitor`，显式启用/禁用，从模块输出文件夹启动工作器，当工作器 apphost 缺失时 `dotnet` 回退，退出事件信号，用于扫描/组织/清理请求的基本自定义操作，以及没有文件系统模块探测。
- `src/settings-ui/Settings.UI.Library` 拥有 `MonitorSettings`、`MonitorProperties`、序列化、启用状态和模块助手映射。
- `src/settings-ui/Settings.UI` 拥有 `MonitorPage`、`MonitorViewModel`、Shell 导航、主页仪表板元数据、英语资源和设置路由。
- `src/settings-ui/Settings.UI.Controls` 在 Kit 快速访问模块列表中包含 Monitor，以便主页在启用时可以一致地公开它。

当前 Monitor 对等目标是 Python 实现的基线功能：扫描下载，维护 `results.csv`，对文件进行分类，保留重复行用于分析，按类别组织文件，并为安装程序清理提供试运行/删除原语。基于注册表的真实已安装软件发现和更丰富的 UI 操作是未来的改进。

当前设置界面包括手动扫描卡、`OrganizeDownloads` 和 `CleanInstallers` 切换、`Run in background` 切换、默认下载文件夹选择器、哈希算法下拉菜单（默认为 SHA1），以及放置在手动扫描内容和扫描按钮之间的同行进度条/百分比。Monitor 模块切换控制模块和设置操作是否可用。`Run in background` 单独控制运行器是否在启用时启动持久工作器；当它关闭时，立即扫描仍然启动一次性扫描。一次性扫描始终刷新类别文件夹和 CSV 状态，然后应用当前操作切换：`OrganizeDownloads` 默认开启，`CleanInstallers` 默认关闭，`Run in background` 默认关闭。进度显示现在读取工作器进度快照和完成状态，而不是从仅 UI 的计时器推进。

## 最近的 Monitor 工作器进度稳定化

最新的 Monitor 传递修复了可能使设置卡在 `Waiting for worker progress` 的手动立即扫描路径：

- 运行器 IPC 已经正确调度 `scanNow` 操作；失败在于模块接口工作器启动路径。
- 模块接口过去只搜索 `PowerToys.Monitor.exe`。调试构建可以在没有准备好的 apphost 的情况下生成 `PowerToys.Monitor.dll`，因此接口现在首选同文件夹 exe 并回退到 `dotnet.exe "PowerToys.Monitor.dll"`。
- 设置在启动手动扫描之前清除陈旧的 `scan-progress.json`，重置扫描完成事件，然后轮询工作器写入的快照。
- 工作器通过 `MonitorScanProgressFileReporter` 报告真实扫描阶段；完成的快照包括最终记录计数。
- 手动工作器烟雾路径使用临时下载目录进行验证，因此在验证期间不会触及本地用户下载内容。

## 最近的 Monitor 和 Light Switch 稳定化

最新的设置传递使活动模块行为更接近上游 PowerToys，同时保留 Kit 的修剪模块表面：

- Monitor 的立即扫描操作发送 `scanNow` 自定义操作，工作器使用 `--use-configured-actions` 运行一次传递。这使手动扫描、类别文件夹创建、组织、安装程序清理和 CSV 写入保持在一个代码路径上，同时让 `OrganizeDownloads` 和 `CleanInstallers` 决定允许哪些副作用。
- Monitor 的模块启用路径在启动工作器之前读取 `runInBackground`。模块可以保持启用以进行设置/主页/手动操作，而无需启动持久工作器。
- Monitor 的设置页面现在将 `OrganizeDownloads`、`CleanInstallers` 和 `Run in background` 放置在手动扫描正下方，与设置的控制流匹配。
- Light Switch 保持上游 `Apply monitor settings to` 形状，现在将 PowerDisplay 配置文件选择路由到导入的 PowerDisplay 设置页面。控件从 `GeneralSettings.Enabled.PowerDisplay` 启用，配置文件名称在该文件存在时从 `%LOCALAPPDATA%\Kit\PowerDisplay\profiles.json` 的 Kit 存储加载。加载器对缺失或格式错误的配置文件数据保持容忍。
- `Settings.UI.UnitTests` 现在具有 Monitor 设置顺序和 Light Switch 的 PowerDisplay 启用/配置文件加载路径的静态回归覆盖。

## 通用和主页 UI 范围

通用保持有用的 PowerToys 设置结构，但删除自动更新和遥测控件。关于部分有意减少到底部的小版本文本。主页使用 PowerToys 风格的介绍、模块列表、快速访问和快捷方式布局，但仅用于 Kit 模块。

可见 UI 应使用英语 Kit 文本。仅在构建面向命名空间、程序集名称、模块接口名称、上游兼容性或来源归属仍然需要时保留 `PowerToys`。

## 稳定性方向

近期工作应优化可预测的构建和低风险的 PowerToys 兼容性：

- 优先选择上游 PowerToys 模式和小增量，而不是新的本地抽象。
- 保持模块注册显式，直到当前运行器/设置/模块兼容性无聊地稳定。
- 仅在现有列表被测试覆盖后才减少需要手动模块列表更新的地方。
- 在扩大到整个解决方案构建之前，保持设置、运行器、模块接口项目、快速访问和复制的模块项目可独立构建。
- 保持运行器构建依赖项与运行时启动的 UI 应用对齐。`Kit.exe` 可以启动并显示托盘图标，即使 `WinUI3Apps\PowerToys.Settings.exe` 缺失；调试输出可以用陈旧文件隐藏该问题，因此干净的发布验证必须确认设置和快速访问可执行文件都已重新生成。
- 为复制的模块保持 PowerToys CsWinRT 元数据稳定。`PowerToys.Interop.winmd` 和 `PowerToys.GPOWrapper.winmd` 由本机项目发布到 `$(RepoRoot)$(Platform)\$(Configuration)`，`Common.Dotnet.CsWinRT.props` 在先前失败或清理的构建没有留下生成的投影源时使陈旧的 `cswinrt.rsp` 文件无效。这防止导入的模块（如 `Awake` 和快速访问）在其 `PowerToys.*` 投影重新生成之前编译。
- 仅当缺失的生产表面被有意删除时才修复或排除陈旧的上游测试。`Settings.UI.UnitTests` 现在排除不属于活动 Kit 设置表面的 PowerToys 模块的 ViewModel 测试。
- 保持 UI 状态从真实设置和模块状态派生。主页应一致显示启用的模块，每个快速访问命令应执行真实操作或导航到模块设置页面。
- 保持 Kit 存储、备份、窗口标题和可见文本与已安装的官方 PowerToys 应用分离。
- 不要在 Kit 中重新启用自动更新或遥测行为。
- 保持更新程序入口点和设置遥测惰性。运行器更新程序回调是无操作的，更新提示/菜单操作返回而不启动更新程序，旧的设置遥测源保持未启动，除非未来的更改故意用仅本地路径替换它。
- 保持新模块拆分为可测试的核心库、工作器进程、本机模块接口、设置模型、设置页面、主页元数据和静态注册测试。
- 当项目共享本机输出（如 `Version.pdb` 和 `PowerToys.Interop` 跟踪日志）时，按顺序或通过解决方案调度程序运行 C++ 模块接口验证。独立的并行 MSBuild 调用可能会竞争这些共享文件并报告错误的构建失败。
- 在每次稳定化传递后保持文档接近实现。模块注册列表是有意手动的，因此陈旧的文档是真正的集成风险。

## 最近的发布构建回归

干净的发布 x64 构建暴露了围绕 CsWinRT 和本机 WinMD 输出的 PowerToys 兼容性问题。可见的错误是缺少 `PowerToys.GPOWrapper`、缺少 `GpoRuleConfigured` 以及 `x64\Release` 下缺少 `PowerToys.Interop.winmd` 或 `PowerToys.GPOWrapper.winmd`。

调查发现了两种相关的失败模式：

- 本机 WinMD 生产者项目可以在不可靠地将其合并的 WinMD 发布到复制的 PowerToys 模块期望的共享配置输出的情况下完成。
- 一些托管项目可以在失败或清理的构建后保留陈旧的 `Generated Files\CsWinRT\cswinrt.rsp` 文件，而生成的投影 `.cs` 文件已消失。然后 CsWinRT 跳过重新生成，后来的 C# 编译失败，因为 `PowerToys.*` 命名空间不存在。

兼容性修复保持上游 PowerToys 依赖形状完整：

- `PowerToys.Interop.vcxproj` 和 `GPOWrapper.vcxproj` 现在将其 WinMD 输出复制到 `$(RepoRoot)$(Platform)\$(Configuration)`。
- `Common.Dotnet.CsWinRT.props` 在不存在生成的投影源时删除陈旧的 CsWinRT 响应文件，强制投影重新生成。
- `Settings.UI.UnitTests` 具有陈旧投影保护和共享 WinMD 发布规则的 `BuildCompatibility` 回归检查。

在同一传递期间处理了两个额外的完整解决方案发布清理项：DSC 模块列表不再宣传已删除的 `MouseJump` 设置表面，`UnitTests-CommonUtils` 现在使用 `/utf-8` 构建，以便一致接受上游 `spdlog/fmt` Unicode 支持。

## 工件清理

在框架达到可用状态后，本地工作区从构建输出大小清理回源大小。大目录是生成的工件，而不是必需的源：

- `src\kit\x64`
- `src\kit\Release`
- `src\kit\.vs`
- 根 `TestResults`
- `src\kit\src` 和 `src\kit\tools` 下的项目本地 `bin`、`obj`、`x64`、`Debug`、`Release` 和 `TestResults` 目录
- `src\kit\packages`

第一次清理传递删除了约 28.71 GB 的编译器和测试输出。后来的完整清理删除了约 39 GB 的重新生成的调试/发布输出。`src\kit\packages` 是 NuGet 恢复缓存，而不是源；它已经通过 `**/[Pp]ackages/*` 被 `src\kit\.gitignore` 覆盖，因此不应上传到 GitHub。删除 `packages` 对于源状态是安全的，但下一次 Visual Studio 或 MSBuild 编译必须再次恢复 NuGet 包，并且可能在第一次运行时花费更长时间。

推荐的清理策略：

- 在 GitHub 上传或存档之前，删除 `src\kit\x64`、`src\kit\Debug`、`src\kit\Release`、`.vs`、`TestResults`、项目 `bin`/`obj` 文件夹和 `src\kit\packages`。
- 在本地迭代开发期间，如果磁盘空间允许，保留 `src\kit\packages`。它可以防止由于缺少包（如 WIL 和 C++/WinRT）而导致的冷构建失败和缓慢恢复。
- 如果 `packages` 被删除，在判断缺少头文件或 WinMD 投影的编译错误之前，运行 Visual Studio `Restore NuGet Packages` 或执行完整解决方案构建。
- 发布构建仅保留 `en-US` 卫星资源，从运行时输出中删除生成的调试符号和本机链接工件，从设置发布中排除非活动 OOBE 和 AI 模型资产，并且不再为活动 Kit 模块集构建仅 AdvancedPaste 的 `LanguageModelProvider` 依赖项。
- WindowsAppSDK 1.8 仍然通过 `Microsoft.WindowsAppSDK` 元包贡献其自己的 Windows AI/Onnx 运行时文件。删除这些将需要用细粒度的 WindowsAppSDK 包引用替换元包，因此推迟到可以更广泛地验证设置兼容性。

在源大小清理后，`src\kit` 应该看起来接近仅源大小：源和文档保留，而 `x64`、`Release`、`.vs`、`packages` 和项目 `bin`/`obj` 目录应该不存在，直到下一次恢复/构建。

## Git 工作树清理

Git 工作树仅在需要隔离分支工作区时使用。在 2026-04-29，`git worktree prune` 删除了陈旧的 `C:\Users\Zen\Repo\Codings\Kit\.worktrees\kit-phase1-host` 记录。当前 `git worktree list --porcelain` 基线应仅显示 `C:\Users\Zen\Repo\Codes\Kit`，除非故意创建了新的隔离工作树。

对于 Git 已经标记为可修剪的陈旧记录，使用 `git worktree prune`。在检查其分支状态和未提交文件之前，不要删除活动工作树目录。

## 第一个插件方向

Kit 还没有活动的第三方插件主机。实际的第一步是 PowerToys 风格的模块导入或使用现有运行器/设置/模块接口契约的小型 Kit 模块。Monitor 是遵循此路线的第一个模块。

使用 `doc/devdoc` 下复制的插件文档作为参考材料，而不是作为活动契约，直到 PowerToys Run 或命令面板被有意导入。如果第一个插件必须是 PowerToys Run 插件，首先导入并稳定 Run 主机；否则，将第一个功能构建为显式 Kit 模块，并通过 `Awake` 和 `LightSwitch` 使用的相同维护列表连接它。

有关第一个模块检查清单和验证基线，请参阅 `doc/devdoc/kit-first-plugin.md`。有关第一阶段经验教训和下一个稳定化检查清单，请参阅 `doc/devdoc/kit-development-experience.md`。

## 添加另一个 PowerToys 模块

导入另一个上游模块时使用此检查清单：

1. 复制模块源并尽可能保持其上游项目形状完整。
2. 将模块项目和所需的构建依赖项添加到 `Kit.slnx`。
3. 将模块接口 DLL 添加到运行器 `KitKnownModules` 列表。
4. 仅为导入的模块添加设置导航、路由映射、页面/视图模型包含和 GPO 页面映射。
5. 当模块使用 `PowerToys.Interop` 或 `PowerToys.GPOWrapper` 时，保持上游 CsWinRT 引用完整；从干净的发布树构建模块一次以确认 WinMD 投影重新生成。
6. 仅当模块应出现在主页上时才添加主页仪表板元数据。
7. 仅当存在真实快速操作时才添加快速访问行为；否则使用设置页面导航作为回退。
8. 为运行器列表、导航路由、仪表板列表、快速访问行为以及任何添加的 WinMD/GPO 依赖项添加集中的静态或单元覆盖。
9. 在更广泛的解决方案构建之前验证目标构建。

## 验证快照

2026-04-25 的本地验证使用 Visual Studio 18 MSBuild 和 VSTest。以下目标调试 x64 构建通过，0 个警告和 0 个错误：

- `Monitor.UnitTests.csproj` Debug x64
- `PowerToys.Monitor.csproj` Debug x64
- `MonitorModuleInterface.vcxproj` Debug x64
- `PowerToys.Settings.csproj` Debug x64
- `PowerToys.QuickAccess.csproj` Debug x64
- `Kit.vcxproj` Debug x64
- `Awake.csproj` Debug x64
- `AwakeModuleInterface.vcxproj` Debug x64
- `LightSwitchModuleInterface.vcxproj` Debug x64
- `LightSwitchService.vcxproj` Debug x64

`Settings.UI.UnitTests.csproj` 现在在将测试项目与 Kit 的修剪模块集和 Kit 设置路径对齐后干净构建。`vstest.console.exe` 通过 `Settings.UI.UnitTests.dll`，59/59 测试通过。`vstest.console.exe` 也通过 `Monitor.UnitTests.dll`，13/13 测试通过，包括 Monitor 运行器/解决方案注册和工作器生命周期处理的静态覆盖。

在发布运行器构建依赖项修复后，目标 `Kit.slnx /t:Kit` 发布 x64 构建通过并从干净树生成了预期的运行时三元组：

- `x64\Release\Kit.exe`
- `x64\Release\WinUI3Apps\PowerToys.Settings.exe`
- `x64\Release\WinUI3Apps\PowerToys.QuickAccess.exe`

在 PowerToys CsWinRT/WinMD 兼容性修复后，完整的 `Kit.slnx` 发布 x64 构建也在本地通过，并生成了 Awake、快速访问、设置、DSC 和其他 PowerToys 派生表面期望的复制模块元数据：

- `x64\Release\PowerToys.Interop.winmd`
- `x64\Release\PowerToys.GPOWrapper.winmd`
- 重新生成的 CsWinRT 投影，如消费项目 `obj` 目录中的 `PowerToys.GPOWrapper.cs`

2026-04-29 的本地验证涵盖了最新的 Monitor/Light Switch 设置传递：

- `Settings.UI.UnitTests.csproj` Debug x64 使用 Visual Studio 18 MSBuild 构建。
- `dotnet test Settings.UI.UnitTests.csproj -p:Platform=x64 -p:Configuration=Debug --no-build --filter "LightSwitchPowerDisplayIntegrationShouldFollowOriginalModuleContract|MonitorRunInBackgroundShouldBeImmediatelyAfterManualScan"` 运行设置 UI 测试程序集，77/77 测试通过。
- `PowerToys.Settings.csproj` Release x64 成功构建并重新生成 `x64\Release\WinUI3Apps\PowerToys.Settings.dll`。
- `git worktree prune` 删除了陈旧的外部工作树元数据，`git worktree list --porcelain` 现在仅报告当前 `C:\Users\Zen\Repo\Codes\Kit` 工作树。

在将干净树交给 Visual Studio 之前，可以删除本地构建输出和恢复缓存。下一次编译应该一起重新创建运行时输出目录、`WinUI3Apps` 子项、共享 WinMD 文件、CsWinRT 投影和包恢复缓存。
