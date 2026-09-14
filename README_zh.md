# Kit

**Language / 语言:** [English](README.md) | 中文

---

Kit 是一个基于 Microsoft PowerToys 的本地自用 Windows 实用工具工作区。它的存在使得选定的 PowerToys 实用工具可以被修改、隔离，并与同一台机器上安装的官方 PowerToys 构建进行比较。

## 项目目标

Kit 目前是一个稳定性优先的 PowerToys 衍生工作区，而不是一个完整的产品重新品牌化。主要设计选择是保持上游运行器、模块接口、设置和仪表板模式的可识别性，以便复制的 PowerToys 模块可以用最少的适配器代码进行验证。

Kit 特定的更改应保持小而有意：品牌、设置存储、可见导航、主页内容、备份和恢复默认值，以及删除不属于本地工作区的产品服务。

## 当前版本

当前 Kit 版本：`2.0.13`。

## 文档索引

- [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) — Kit 插件与模块开发规范：PowerToys C++ 契约、注册、WinUI 3 + Mica Alt、Logo 规格、数据目录隔离、生命周期与模板。
- `doc/devdoc/kit-architecture.md` — Kit 架构参考（轻量插件宿主方向）。
- `doc/devdoc/powertoys-architecture.md` — PowerToys 主框架架构参考（按上游源码核对）。
- `doc/devdoc/architecture-comparison.md` — PowerToys 与 Kit 架构对比与启动优化分析。
- `doc/devdoc/kit-first-plugin.md` — 首个模块检查清单与验证基线。
- `doc/devdoc/kit-development-experience.md` — 第一阶段经验与后续稳定化清单。
- `doc/devdoc/startup-optimization-analysis.md` — 启动优化专项分析。
- `fix.plan` — 轻量插件宿主改造分阶段行动方案（仓库根目录）。
- `fix.md` — 上游差异与修正清单（仓库根目录）。
- `next.md` — 随上游更新的进度与同步清单（仓库根目录）。


## 构建输出

`tools/build/build.ps1` 调用 MSBuild，沿用各工程的输出路径。x64 Debug 构建会生成 `x64/Debug/`、`Debug/x64/` 及项目自身的输出目录；脚本不会自动把产物搬入 `bin/`，也不会自动删除这些目录。

| 路径 | 用途 |
| --- | --- |
| `x64/Debug/` | 主要 x64 Debug 运行目录，包含 `Kit.exe` 和 `WinUI3Apps/` |
| `Debug/x64/`、项目 `bin/` 和 `obj/` | 其他工程输出与构建中间文件 |
| `bin/debug/2.0.13/` | 人工整理的 Debug 测试交付目录，整理步骤独立于构建 |

版本源是 `src/Version.props`，`src/common/version/Generated Files/version_gen.h` 由它生成。Release 构建与 ZIP 打包需要单独执行，上述目录约定不代表已经产出新的 Release 或压缩包。

运行时设置与日志位于 `%LOCALAPPDATA%\Kit`，插件数据按 `%LOCALAPPDATA%\Kit\<ModuleKey>` 隔离，不写入 `Kit.exe` 旁的 `Kit/` 子目录；完整目录规范见 [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md)。

## 更新日志

请查看 [changelog.md](changelog.md) 获取完整的版本历史。

## 第一阶段收尾

第一阶段现在实际上是一个工作的 Kit 外壳，承载活动的 PowerToys 风格模块。该框架可以加载显式的 PowerToys 风格模块，在设置和主页中显示它们，保持 Kit 品牌存储与官方 PowerToys 分离，并通过现有的运行器/模块接口/设置路径运行各活动模块。

当前稳定的交接点是：

- 保持 `Awake` 和 `Light Switch` 作为活动模块集（`Monitor` 已在 2.0.8 移除）。
- 第一方模块通过维护的列表和测试保持显式发现；第三方插件宿主（`plugins/` + manifest）按 `fix.plan` 规划，尚未实现。
- 保持通用和主页使用英语 Kit 措辞，删除自动更新和遥测界面。
- 保持 Kit UI 自动化指向 Kit 的运行器、设置窗口、安装根目录和两个活动模块可执行文件，避免意外附着到已安装的上游 PowerToys。
- 保持 Settings 深度链接和模块设置链接只启动 `Kit.exe`。不要从 Kit UI 回退到已安装的上游 `PowerToys.exe`。
- 在交接前清理构建工件，以便下一次 Visual Studio 构建从源状态开始。
- 工作区可以在稳定交接后减少回源大小。本地 `Debug`、`Release`、`x64`、`bin`、`obj`、`TestResults`、`.vs` 和恢复的 `packages` 目录是可丢弃的构建状态。

## 架构

- `src/runner` 启动 Kit，加载模块接口 DLL，拥有模块生命周期，并与设置应用协调设置 IPC。可执行文件已经足够分离，可以作为 `Kit.exe` 启动，而许多面向构建的项目名称仍然保留上游 PowerToys 名称。在运行时，运行器从 `Kit.exe` 旁边的 `WinUI3Apps` 打开设置和快速访问应用，因此运行器构建目标必须保持对两个 UI 可执行项目的显式依赖。
- `src/modules` 包含活动实用工具。`Awake` 从上游 PowerToys 复制，包括 `Awake.ModuleServices`、`Awake` 和 `AwakeModuleInterface`；`LightSwitch` 是当前的 Kit 实用工具模块（Monitor 已在 2.0.8 移除）。
- `src/settings-ui/Settings.UI` 包含 WinUI 设置应用，包括主页、通用、模块页面、导航和页面级视图模型。
- `src/settings-ui/Settings.UI.Controls` 包含共享 UI 控件，如快速访问。
- `src/settings-ui/Settings.UI.Library` 包含设置模型、设置序列化、模块设置存储库、备份和恢复助手、GPO 助手和共享设置基础设施。
- `src/common` 保留运行器、模块和设置使用的共享本机和托管 PowerToys 基础设施。

运行时设置存储在 Kit 特定的应用程序数据下，例如 `%LOCALAPPDATA%\Kit\settings.json`，而不是官方 PowerToys 设置目录。备份和恢复默认值也使用 Kit 品牌，包括 `Documents\Kit\Backup`、`HKCU\Software\Microsoft\Kit` 和 `Kit_settings_*` 临时备份文件夹。

## 当前模块集

活动 Kit 模块集故意很小：

- `Awake`
- `Light Switch`

`Monitor` 已在 `2.0.8` 移除；移除记录见 `doc/devdoc/kit-development-experience.md` 与 [changelog.md](changelog.md)。

Kit 不会自动公开源树中复制的每个上游 PowerToys 实用工具。模块仅在注册到运行器、设置导航、主页和测试的维护 Kit 列表后才启用。

## PowerToys 兼容性模型

Kit 遵循 PowerToys 模块加载模型，而不是发明新的插件协议。运行器通过 `src/runner/main.cpp` 中维护的 `KitKnownModules` 列表加载已知的模块接口 DLL，当前：

- `PowerToys.AwakeModuleInterface.dll`
- `PowerToys.LightSwitchModuleInterface.dll`

这个固定列表对第一方模块是有意的：它避免了不稳定的目录探测，并使每个导入的模块成为显式的兼容性决策。计划的第三方插件宿主（`fix.plan` P1）会通过 `plugins/` 目录 + `manifest.json` 承载外部插件，同时保持此第一方列表不变。当另一个第一方 PowerToys 模块被引入 Kit 时，它应该一起添加到运行器、解决方案、设置路由、主页仪表板元数据和测试中。

## 添加另一个 PowerToys 模块

> **开发指南**：详细全流程教程与工程契约请参考仓库根目录的 [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md)。

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

## 插件方向

Kit 的核心方向是轻量插件宿主：主框架（runner + Settings UI + 公共库）不内置模块业务逻辑，启动快，并通过同一个 `PowertoyModuleIface` + `powertoy_create()` 契约同时承载官方 PowerToys 模块与第三方自定插件。

- 第一方模块（`Awake`、`Light Switch`）保持在编译期 `KitKnownModules` 清单内，深度集成（Home、Quick Access、设置路由、测试）。
- 第三方插件计划从 `plugins/` 目录加载（接口 DLL + `manifest.json`），只加载已启用插件，并由通用设置页渲染各插件的 `get_config` JSON。
- 导入官方 PowerToys 模块需要先核对源码兼容性，再接入 Kit 的设置、IPC 与生命周期；保持遥测禁用，按 [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md) 执行。

`Awake` 与 `Light Switch` 都源自 PowerToys，并已按 Kit 的现有契约适配。完整开发规范见 [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md)，首个模块检查清单见 `doc/devdoc/kit-first-plugin.md`，经验教训见 `doc/devdoc/kit-development-experience.md`。分阶段方案见 `fix.plan`（仓库根目录），目标架构见 `doc/devdoc/kit-architecture.md`。

## 稳定性方向

近期工作应优化可预测的构建和低风险的 PowerToys 兼容性：

- 优先选择上游 PowerToys 模式和小增量，而不是新的本地抽象。
- 保持模块注册显式，直到当前运行器/设置/模块兼容性无聊地稳定。
- 仅在现有列表被测试覆盖后才减少需要手动模块列表更新的地方。
- 在扩大到整个解决方案构建之前，保持设置、运行器、模块接口项目、快速访问和复制的模块项目可独立构建。
- 保持运行器构建依赖项与运行时启动的 UI 应用对齐。`Kit.exe` 可以启动并显示托盘图标，即使 `WinUI3Apps\PowerToys.Settings.exe` 缺失；调试输出可以用陈旧文件隐藏该问题，因此干净的发布验证必须确认设置和快速访问可执行文件都已重新生成。
- 为复制的模块保持 PowerToys CsWinRT 元数据稳定。`PowerToys.Interop.winmd` 和 `PowerToys.GPOWrapper.winmd` 由本机项目发布到 `$(RepoRoot)$(Platform)\$(Configuration)`，`Common.Dotnet.CsWinRT.props` 在先前失败或清理的构建没有留下生成的投影源时使陈旧的 `cswinrt.rsp` 文件无效。这防止导入的模块（如 `Awake` 和快速访问）在其 `PowerToys.*` 投影重新生成之前编译。
- 对于有意删除的上游测试和源码，优先直接删除，不再把它们隐藏在项目排除规则后面。`Settings.UI.UnitTests` 现在用 `BuildCompatibility` 覆盖非活动 Settings 源码、单元测试、资产、图标、控件、转换器、旧 sibling Settings 资产树以及陈旧 WinUI 输出清理。
- 保持 UI 状态从真实设置和模块状态派生。主页应一致显示启用的模块，每个快速访问命令应执行真实操作或导航到模块设置页面。
- 保持 Kit 存储、备份、窗口标题和可见文本与已安装的官方 PowerToys 应用分离。备份默认值应保持为 Kit 活动模块设置的通用规则，不要携带非活动 PowerToys 模块专用文件或恢复修正。
- 不要在 Kit 中重新启用自动下载/安装或遥测行为。
- 版本检查只保留明确的手动入口。后台检查、重试线程、更新 toast、自动下载安装与设置遥测均已移除或保持禁用。
- Runner、Settings 与 Worker 的 GPO 兼容入口统一返回 `not_configured`；导入模块时不得重新启用官方 PowerToys 策略读取。
- 不要在 Kit 中保留只供 DSC 使用的 Settings 命令行入口。当前保留的 Settings 命令行表面只有活动的 `set`/`get` 兼容路径；除非 DSC 生成重新成为活动功能，否则不要恢复 `setAdditional`。
- 在 OOBE/SCOOBE 窗口未交付时，不要在 Kit 中保留其启动和状态路径。除非完整的引导界面重新成为活动功能，否则不要恢复它们的 SettingsAPI helper、备份规则、资源或样式。
- 保持上游 BugReportTool 不进入活动 Kit 运行时。它的收集模型是广泛的 PowerToys 诊断状态，包括 Kit 不交付的非活动模块。
- 在明确导入之前，不要在 Kit 中保留非活动 Command Palette 和独立 module-loader 开发表面。孤立的 CmdPal 版本 props 和 `tools/module_loader` 应直接删除，而不是继续复制。
- 保持新模块拆分为可测试的核心库、工作器进程、本机模块接口、设置模型、设置页面、主页元数据和静态注册测试。
- 当项目共享本机输出（如 `Version.pdb` 和 `PowerToys.Interop` 跟踪日志）时，按顺序或通过解决方案调度程序运行 C++ 模块接口验证。独立的并行 MSBuild 调用可能会竞争这些共享文件并报告错误的构建失败。
- 保持本地构建脚本适合非 VS shell。MSBuild 参数应以数组转发，导入 Visual Studio 环境后缓存解析出的 MSBuild 路径，归一化 `VsDevCmd.bat` 带来的重复 `PATH`/`Path` 值，并在包源映射阻止 SDK restore 时默认跳过本地 CopyOnWrite/RunVSTest SDK resolver 导入。
- 保持已删除模块的 runner、Settings、GPO、Quick Access 和解决方案表面真实删除，而不是隐藏在项目排除规则后面。
- 保持开发签名范围明确。当前用户证书信任是默认本地路径；机器级根信任、递归包签名和非 sparse 包签名都应显式选择。
- 在每次稳定化传递后保持文档接近实现。模块注册列表是有意手动的，因此陈旧的文档是真正的集成风险。

## 最近的 Awake 和主页实现

最新的主页工作保持 PowerToys 行为，但将其范围限定为 Kit 的活动模块：

- `DashboardViewModel` 使用 `KitModuleCatalog.DashboardModules`，当前为 `Awake` 和 `LightSwitch`，因此主页实用工具列表是固定和可预测的。
- `QuickAccessViewModel` 仍然支持可操作的快速访问项，但主页传递仪表板模块列表，以便启用的 Kit 模块一致显示。
- 快速访问首先尝试正常启动器。如果模块没有直接快速操作，主页会回退到打开该模块的设置页面。这让 `Awake` 在不创建虚假快捷方式操作的情况下有用地运行，而 `LightSwitch` 保持直接切换操作。
- `Awake` 贡献一个 `DashboardModuleActivationItem`，在主页快捷方式卡中显示当前 Awake 模式，使用现有的 PowerToys 仪表板项模板。
- 快速访问空状态现在使用可见项的计数，而不是原始项集合计数，因此禁用或 GPO 隐藏的模块不会留下可见的空卡。

## 通用和主页 UI 范围

通用保持有用的 PowerToys 设置结构，但删除自动更新和遥测控件。About 部分显示 Kit 版本、GitHub 仓库和手动版本检查入口。主页使用 PowerToys 风格的介绍、模块列表、快速访问和快捷方式布局，但仅用于 Kit 模块。

可见 UI 应使用英语 Kit 文本。仅在构建面向命名空间、程序集名称、模块接口名称、上游兼容性或来源归属仍然需要时保留 `PowerToys`。

## 工件清理

在框架达到可用状态后，本地工作区从构建输出大小清理回源大小。大目录是生成的工件，而不是必需的源：

- 根 `x64`、`Debug`、`Release`、`.vs`
- 根 `TestResults`
- `src` 和 `tools` 下的项目本地 `bin`、`obj`、`x64`、`Debug`、`Release` 和 `TestResults` 目录
- 根 `packages`

第一次清理传递删除了约 28.71 GB 的编译器和测试输出。后来的完整清理删除了约 39 GB 的重新生成的调试/发布输出。`packages` 是 NuGet 恢复缓存，而不是源；它已经通过 `**/[Pp]ackages/*` 被 `.gitignore` 覆盖，因此不应上传到 GitHub。删除 `packages` 对于源状态是安全的，但下一次 Visual Studio 或 MSBuild 编译必须再次恢复 NuGet 包，并且可能在第一次运行时花费更长时间。

推荐的清理策略：

- 在 GitHub 上传或存档之前，删除根 `x64`、`Debug`、`Release`、`.vs`、`TestResults`、项目 `bin`/`obj` 文件夹和根 `packages`。
- 在本地迭代开发期间，如果磁盘空间允许，保留 `packages`。它可以防止由于缺少包（如 WIL 和 C++/WinRT）而导致的冷构建失败和缓慢恢复。
- 如果 `packages` 被删除，在判断缺少头文件或 WinMD 投影的编译错误之前，运行 Visual Studio `Restore NuGet Packages` 或执行完整解决方案构建。
- 发布构建仅保留 `en-US` 卫星资源，从运行时输出中删除生成的调试符号和本机链接工件，清理非活动 Settings 模块资产、图标、资源字符串、OOBE/模型资产以及陈旧的非活动控件 XBF 输出，并且不再为活动 Kit 模块集保留仅 AdvancedPaste 的 `LanguageModelProvider` 源码树、AI provider 包 pin、provider UI metadata/helper 或非序列化 AI enum helper。
- Shortcut Conflict 热键查找显式限定为 Quick Access 和 LightSwitch，不再扫描 PowerToys 衍生库中的所有历史 `IHotkeyConfig` settings 模型。
- WindowsAppSDK 1.8 仍然通过 `Microsoft.WindowsAppSDK` 元包贡献其自己的 Windows AI/Onnx 运行时文件。删除这些将需要用细粒度的 WindowsAppSDK 包引用替换元包，因此推迟到可以更广泛地验证设置兼容性。

在源大小清理后，仓库应该看起来接近仅源大小：源和文档保留，而 `x64`、`Release`、`.vs`、`packages` 和项目 `bin`/`obj` 目录应该不存在，直到下一次恢复/构建。

## Git 工作树清理

Git 工作树仅在需要隔离分支工作区时使用。在 2026-04-29，`git worktree prune` 删除了一个陈旧的外部工作树记录。当前 `git worktree list --porcelain` 基线应仅显示活动的 Kit 工作树，除非故意创建了新的隔离工作树。

对于 Git 已经标记为可修剪的陈旧记录，使用 `git worktree prune`。在检查其分支状态和未提交文件之前，不要删除活动工作树目录。

## 最近的 Light Switch 稳定化

最新的设置传递使活动模块行为更接近上游 PowerToys，同时保留 Kit 的修剪模块表面：

- Light Switch 保留上游 schedule、Night Light 和 toggle hotkey 形状，但不再携带已删除的 PowerDisplay profile bridge。
- `Settings.UI.UnitTests` 现在具有 Light Switch 无 PowerDisplay 边界和已移除 Monitor 模块表面的静态回归覆盖。

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

## 验证快照

2026-04-25 的本地验证使用 Visual Studio 18 MSBuild 和 VSTest。以下目标调试 x64 构建通过，0 个警告和 0 个错误：

- `PowerToys.Settings.csproj` Debug x64
- `PowerToys.QuickAccess.csproj` Debug x64
- `Kit.vcxproj` Debug x64
- `Awake.csproj` Debug x64
- `AwakeModuleInterface.vcxproj` Debug x64
- `LightSwitchModuleInterface.vcxproj` Debug x64
- `LightSwitchService.vcxproj` Debug x64

`Settings.UI.UnitTests.csproj` 现在在将测试项目与 Kit 的修剪模块集和 Kit 设置路径对齐后干净构建。`vstest.console.exe` 通过 `Settings.UI.UnitTests.dll`，59/59 测试通过。
在发布运行器构建依赖项修复后，目标 `Kit.slnx /t:Kit` 发布 x64 构建通过并从干净树生成了预期的运行时三元组：

- `x64\Release\Kit.exe`
- `x64\Release\WinUI3Apps\PowerToys.Settings.exe`
- `x64\Release\WinUI3Apps\PowerToys.QuickAccess.exe`

在 PowerToys CsWinRT/WinMD 兼容性修复后，完整的 `Kit.slnx` 发布 x64 构建也在本地通过，并生成了 Awake、快速访问、设置、DSC 和其他 PowerToys 派生表面期望的复制模块元数据：

- `x64\Release\PowerToys.Interop.winmd`
- `x64\Release\PowerToys.GPOWrapper.winmd`
- 重新生成的 CsWinRT 投影，如消费项目 `obj` 目录中的 `PowerToys.GPOWrapper.cs`

2026-04-29 的本地验证涵盖了最新的 Light Switch 设置传递：

- `Settings.UI.UnitTests.csproj` Debug x64 使用 Visual Studio 18 MSBuild 构建。
- `vstest.console.exe` 使用 `LightSwitchPowerDisplayIntegrationShouldFollowOriginalModuleContract` 过滤器运行 `Settings.UI.UnitTests.dll`，77/77 测试通过。
- `PowerToys.Settings.csproj` Release x64 成功构建并重新生成 `x64\Release\WinUI3Apps\PowerToys.Settings.dll`。
- `git worktree prune` 删除了陈旧的外部工作树元数据，`git worktree list --porcelain` 现在仅报告活动的 Kit 工作树。

在将干净树交给 Visual Studio 之前，可以删除本地构建输出和恢复缓存。下一次编译应该一起重新创建运行时输出目录、`WinUI3Apps` 子项、共享 WinMD 文件、CsWinRT 投影和包恢复缓存。
