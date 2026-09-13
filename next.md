# Kit 框架同步清单（对齐上游 PowerToys）

> 用途：列出本项目框架层需要与上游 PowerToys 参考源码同步（或保持刻意差异）的具体项，供后续逐项执行。
> 对比对象：本项目（工作区根，下称 Kit）与参考源码 `Source/PowerToys`（下称上游）。
> 参考目录为只读，已加入 `.gitignore`（`/Source/`）；所有修改只落在 Kit 自身。

## 0. 结论速览

- 两边骨架同源且高度一致：解决方案、props/targets 体系、tools/build 脚本、模块形态、Settings UI 结构、MSTest / Microsoft.Testing.Platform 测试基建。
- 上游整体更新：**WinAppSDK 2.2、.NET runtime 包 10.0.11、vcpkg 管 spdlog、Common.Dotnet.props 抽取、VS2026 支持、长路径检查**；Kit 是较早快照 + 本地裁剪（内置 spdlog、无 CI、无安装器、遥测/更新器 inert）。
- 同步策略：**框架基础设施跟上游走，产品功能面保持 Kit 裁剪**。

| 领域 | 结论 | 优先级 |
|---|---|---|
| C++ 编译配置（VS2026 兼容） | 需同步（/utf-8、协程告警静默） | P0 |
| Directory.Build.targets 增量 | 需同步（长路径检查、WebView2 WPF 引用修复） | P0 |
| FuzzTest 目标框架 | 需升级 net8 到 net10 | P0 |
| 依赖版本（.NET runtime 包 / WinAppSDK / WebView2） | 需同步升级 | P1 |
| .NET 公共 props（Common.Dotnet.props） | 需抽取（消除 csproj 硬编码 TFM） | P2 |
| 版本方案（Version.props / versionSetting.ps1） | 需对齐上游机制 | P2 |
| 构建脚本（build-common.ps1 等） | 需对齐（VS2026 / DevShell 探测） | P3 |
| C++ 依赖管理（vcpkg vs 内置） | **刻意差异，保持 Kit 内置方案** | 保留 |
| CI / 安装器 / 遥测 / AI 相关 | **刻意不同步** | 保留 |

---

## 1. 依赖版本 —— Directory.Packages.props

### 1.1 需同步（版本落后）

| 包 | Kit（当前） | 上游（目标） |
|---|---|---|
| Microsoft.WindowsAppSDK | 2.0.1 | **2.2.0** |
| Microsoft.WindowsAppSDK.Foundation | 2.0.20 | **2.1.0** |
| Microsoft.WindowsAppSDK.Runtime | 2.0.1 | **2.2.0** |
| Microsoft.Web.WebView2 | 1.0.3719.77 | **1.0.4022.49** |
| Microsoft.Data.Sqlite | 10.0.8 | 10.0.11（见下） |
| Microsoft.Extensions.*（Caching / DI / Logging / Hosting 等 6 个） | 10.0.8 | 10.0.11 |
| Microsoft.Win32.SystemEvents / Microsoft.Windows.Compatibility | 10.0.8 | 10.0.11 |
| System.*（CodeDom、ComponentModel.Composition、Configuration、Data.OleDb、Diagnostics.*、Drawing、Management、Runtime.Caching、ServiceProcess、Text.*、Text.Json 等） | 10.0.8 | 10.0.11 |

**动作**：仿照上游引入属性，替换硬编码版本：

```xml
<!-- 上游：.NET SDK 10.0.303 内置 runtime 10.0.11，与 CI exactVersion 对齐 -->
<DotNetRuntimePackageVersion>10.0.11</DotNetRuntimePackageVersion>
```

把上述 10.0.8 硬编码改为 `$(DotNetRuntimePackageVersion)` 引用。Kit 无 CI 对齐要求，但保持同一属性便于日后整批升级。

### 1.2 已一致（无需动）

MSTest 3.8.3、boost 1.87.0、CppWinRT 2.0.250303.1、CsWin32 0.3.269、CsWinRT 2.2.0、ImplementationLibrary 1.0.250325.1、Windows.SDK.BuildTools 10.0.26100.6901、NetAnalyzers 10.0.102、StyleCop 1.2.0-beta.556、MessagePack 3.1.7、StreamJsonRpc 2.21.69、Polly.Core 8.6.5、Newtonsoft.Json 13.0.4、WinUIEx 2.8.0、WixToolset.* 5.0.2、CommunityToolkit.*（8.4.0 / 8.2.251219 / Labs 同版本）。

### 1.3 上游独有、Kit 刻意不引入

SemanticKernel / OpenAI / Microsoft.Extensions.AI / CommandPalette.Extensions / FoundryLocal / NLog / Monaco 相关 / AdvancedPaste / Workspaces / CmdPal 等模块依赖 —— 与 Kit 模块集无关，**不同步**。

---

## 2. C++ 编译配置 —— Cpp.Build.props / Cpp.Build.targets

**P0。** Kit 的 `Cpp.Build.props` 比上游少两块（其余 ClCompile / Link 配置一致）：

1. **/utf-8**：上游 `<AdditionalOptions>/utf-8 %(AdditionalOptions)</AdditionalOptions>`，保证源码按 UTF-8 编译。
2. **VS2026（MSVC 14.51+）兼容**：上游在 PreprocessorDefinitions 追加 `_SILENCE_EXPERIMENTAL_COROUTINE_DEPRECATION_WARNINGS` —— STL 把 `<experimental/coroutine>` 变成硬错误（STL1011），而 C++/WinRT 的 base.h 仍会回退引用它。**Kit 的 AGENTS.md 声明支持 VS2026，此条必须同步**，否则 VS2026 下 C++ 工程直接编译失败。
3. **Cpp.Build.targets（vcpkg 钩子）**：上游有该文件（`ForceImportAfterCppTargets` 引入 vcpkg.targets）。Kit **没有**该文件且 `VcpkgEnabled=false`（走内置 deps）——这是刻意差异，见 §6，**不要**照搬 vcpkg 钩子；但应评估 VS2026 下内置 spdlog 是否需要上游 `msvc-14.51-stdext-checked-array-iterator.patch`。

其余（PlatformToolset v143/v145、LanguageStandard stdcpplatest、Windows SDK 10.0.26100.0、CFG、SDLCheck、TreatWarningAsError）两边一致。

---

## 3. MSBuild 公共配置 —— Directory.Build.props / Directory.Build.targets / src/Common.Dotnet.*

### 3.1 Directory.Build.props

| 差异 | 动作 |
|---|---|
| 上游新增 `src/PhiSilicaLaf.props` import | **不引入**（AI 功能 LAF 凭据，Kit 无 AI 功能） |
| 上游 `VersionBuildSuffix` 正则（仅 3 段版本追加 .0） | 对齐；Kit 现在是无条件 .0 追加 |
| Kit 的 `DevEnvironment` AssemblyMetadata 块引用 `$(DevEnvironment)`，但 Kit 的 `src/Version.props` 未定义该属性（上游在 Version.props 定义）→ **该特性实际永不生效** | 二选一：在 Version.props 补 `<DevEnvironment>Local</DevEnvironment>`（配合 versionSetting.ps1 写入），或删掉该块 |
| Kit 保留 `PowerToysSkipRunVSTestSdk` 条件（上游已移除） | 保留（Kit 本地脚本依赖它提速） |
| Kit 保留 `SatelliteResourceLanguages=en-US` + Release DebugType none（上游移到 Common.Dotnet.props 的 pdbonly） | 与 3.3 抽取合并处理 |
| 上游 RunVSTest SDK ImportGroup、Test target、ARM64 workaround | Kit 已有等价结构，无需动 |

### 3.2 Directory.Build.targets —— P0 两处新增

1. **EnsureLongPathsEnabled**（BeforeTargets=PrepareForBuild）：检测 HKLM 长路径开关，未开启时在构建前给出明确错误（Kit 与上游一样有深层路径，远超 MAX_PATH）。Kit 缺失 → **同步**（跳过开关 `/p:SkipLongPathsCheck=true`，配合 `setup-dev-environment.ps1`）。
2. **RemoveUnusedWebView2WpfReference**（BeforeTargets=ResolveAssemblyReferences）：消除非 WPF 工程引用 WebView2.Wpf 导致的 MSB3277 冲突。Kit 用了 WebView2 → **同步**。
3. CopyOnWrite SDK：Kit 用带条件的 `<Import Sdk="Microsoft.Build.CopyOnWrite" ...>` + `PowerToysSkipCopyOnWriteSdk` 开关；上游用无条件 `<Sdk>` 元素。**保留 Kit 写法**（本地可跳过）。
4. Kit 独有的 `KitRemove*` 输出清理 target（Release PDB、非英文卫星语言目录、.exp / .lib.lastcodeanalysissucceeded、ModelProvider / Foundry 残留、遥测 DLL、VC 运行时 DLL）——上游没有，是 Kit 刻意裁剪，**保留**。

### 3.3 src/Common.Dotnet.props（上游新增，Kit 缺失）—— P2

上游把公共 .NET 设置从 `Common.Dotnet.CsWinRT.props` 抽到独立 `Common.Dotnet.props`：

- `CoreTargetFramework=net10.0`、`TargetFramework=net10.0-windows10.0.26100.0`、`TargetPlatformMinVersion=10.0.19041.0`、`RuntimeIdentifiers=win-x64;win-arm64`
- `WarningLevel=4`、`TreatWarningsAsErrors`、`WarningsNotAsErrors=CA1824;CA1416;CA1720;CA1859;CA2263;CA2022;MVVMTK0045;MVVMTK0049`
- Debug（portable）/ Release（pdbonly）配置

**动作**：Kit 新建同名文件，`Common.Dotnet.CsWinRT.props` 改为 import 它；并把 csproj 中硬编码的 `net10.0-windows10.0.26100.0`（目前 `Settings.UI.Controls`、`QuickAccess.UI`、`Common.UI.Controls`、`UITestAutomation` 等 5 处）改为继承属性。CsWinRT.props 中 Kit 的 `KitInvalidateStaleCsWinRTProjection` workaround 先保留，升级 CsWinRT 后评估是否可删。

### 3.4 src/Common.Dotnet.FuzzTest.props —— P0

- Kit：`net8.0-windows10.0.26100.0`（旧注释：OneFuzz 不支持 .NET 9）
- 上游：`net10.0-windows10.0.26100.0`（OneFuzz 已 runtime-agnostic，按构建产物目录取）
- **动作**：直接对齐为 net10。

---

## 4. 版本方案 —— src/Version.props + tools/build/versionSetting.ps1

| 项 | Kit | 上游 |
|---|---|---|
| Version.props | 静态 `2.0.7` | `0.0.1`（CI 写入）+ `VersionChannel` + `ReleaseTrainVersion` / `ReleaseTrainEpoch` + `SourceCommit` + `DevEnvironment` |
| versionSetting.ps1 | 只有 3 段版本归一化 | 新增 `-Channel`(stable/preview/private)、`-SourceCommit`、`-BuildNumber`、preview 后缀处理、UInt16 范围校验；并回写 VersionChannel / SourceCommit |

**动作（P2）**：把 versionSetting.ps1 同步为上游实现（Kit 固定 `-Channel stable`、沿用自身版本号 2.x），Version.props 增加 `VersionChannel` / `SourceCommit` / `DevEnvironment` 节点；同时解决 3.1 的 DevEnvironment 悬空问题。`src/common/version`（version.vcxproj / version_gen.h）两边一致，无需动。

---

## 5. 构建脚本 —— tools/build

### 5.1 漂移文件（需逐一对齐，P3）

| 文件 | 差异要点 |
|---|---|
| **build-common.ps1**（改动最大） | 上游：VS2026 / VS18 安装路径探测、`-prerelease`、按能力（`-requires VC.Tools.x86.x64`）限定产品（Community / Professional / Enterprise / BuildTools）、新增 `Test-VsHasNativeTools` 校验、VsDevCmd 退出码检查；移除 MSBuildExe 缓存、PATH 归一化、`/nodeReuse:false`。**同步后 Kit 在 VS2026 下能自动进入可用 DevShell** |
| build.ps1 | 上游把 ExtraArgs 改为字符串拼接；Kit 额外注入 `PowerToysSkipCopyOnWriteSdk` / `PowerToysSkipRunVSTestSdk`。对齐时**保留 Kit 的 flag 注入**（见 3.1 / 3.2） |
| build-essentials.ps1 | 上游只构建 runner + Settings（无 QuickAccess，runner 名 `runner.vcxproj`）；Kit 构建 `Kit.vcxproj` + Settings + QuickAccess。**保留 Kit 项目清单**，同步其余脚本结构 |
| versionSetting.ps1 | 见 §4 |
| setup-dev-environment.ps1 | 上游 10 行差异（长路径 / VS2026 相关，与 EnsureLongPathsEnabled 配套）→ 同步 |
| cert-management.ps1 / cert-sign-package.ps1 / self-sign.ps1 | 上游 40~72 行漂移；Kit 本地无签名发布需求 → **可选**，建议仅在有需要时同步 |
| clean-artifacts.ps1、BUILD-GUIDELINES.md | 小幅漂移 → 同步 |
| Worktree 系列（New-/Delete-Worktree*.ps1/.cmd、WorktreeLib.ps1） | 目录 diff 未报漂移 |

### 5.2 仅上游有（Kit 刻意不引入）

`build-installer.ps1`、`generate-dsc-manifests.ps1` —— 对应上游 `installer/` WiX 安装器，Kit 用 `src/PackageIdentity` 替代，**不同步**。

### 5.3 仅 Kit 有（保留）

`clean-stale-versions.ps1`、`verify-runtime-artifacts.ps1` —— Kit 本地发布清理 / 校验脚本。

---

## 6. C++ 依赖管理 —— vcpkg vs 内置 deps（刻意差异，保留）

- 上游：`vcpkg.json`（libwebp + spdlog，manifest 模式）+ `deps/vcpkg-overlays/spdlog`（含 `msvc-14.51-stdext-checked-array-iterator.patch`，VS2026 修复）+ `Cpp.Build.targets` 自动 include/link。
- Kit：`Cpp.Build.props` 中 `VcpkgEnabled=false`，`ExternalIncludePath` 指向内置 `deps/expected-lite`、`deps/spdlog`（完整源码）。
- **建议**：保持内置方案（本地自用、离线友好、避免 vcpkg 首次下载）。**唯一要做的**：确认内置 spdlog 在 VS2026 下的兼容性，必要时移植上游 overlay 补丁到 Kit 的内置 spdlog 源码；`deps/spdlog.props` 两边都存在，顺手比对内容是否一致。

---

## 7. 模块框架

| 项 | 状态 |
|---|---|
| 模块接口 `src/modules/interface/powertoy_module_interface.h` | 上游新增 `WIN_KEY_HOLD_HOTKEY_ID` 与 `keep_track_of_pressed_win_key()` / `milliseconds_win_key_must_be_pressed()`（Win 键按住触发快捷指南的遗留行为，注释明确"新模块不要用"）+ 若干注释更新，共 14+/3-。**低优先**，Kit 无模块依赖该行为时可暂缓 |
| 模块注册 | Kit 用 `KitKnownModules`（`src/runner/main.cpp`，Awake / LightSwitch / Monitor 三个 interface DLL）——Kit 特有，按 AGENTS.md 维护即可；上游注册机制不同，不照搬 |
| 模块形态 | Kit 以 Monitor 为参考形态（core lib + worker/进程 + native interface + settings 模型/页面 + Home 元数据 + 静态注册测试），与上游新模块（如 PowerDisplay）要求一致 |
| 上游新模块 | PowerDisplay（对应 Kit Monitor）、NewPlus、Workspaces、AI 相关 —— 仅把与 Kit 三个模块相关的共性框架件（如 Monitor 与 PowerDisplay）纳入同步评估 |

---

## 8. Settings UI 框架

- 项目集合：Kit 与上游核心集一致（Settings.UI、Settings.UI.Controls、Settings.UI.Library、Settings.UI.XamlIndexBuilder、QuickAccess.UI、Settings.UI.UnitTests）。上游另有 `Settings.UITests`、`UITest-Settings`（WinAppDriver UI 测试工程）——Kit 按需再引入。
- **与 WinAppSDK 2.2 升级联动**（§1.1）：Settings.UI 涉及 `TitleBar.WASDK.cs`、OOBE/SCOOBE、`SearchIndexService` 等框架件，升级后需回归验证；上游 2.2 引入的 UI 框架增量（如 FoundryLocal 模型选择器、Workspaces 页面等）**不引入**。
- Kit 保留自身的模块导航路由 / Home 元数据（AGENTS.md 中的 Kit 清单），保持裁剪。

---

## 9. 测试基础设施

- 两边均已启用 Microsoft.Testing.Platform（`EnableMSTestRunner`、`TestingPlatform*`、`--report-trx`、ARM64 禁用）一致；`msbuild /t:Test` + RunVSTest SDK（C++）结构一致。
- 待办：FuzzTest.props net8 到 net10（§3.4）。
- Kit 测试面（Settings.UI.UnitTests、Monitor.UnitTests、LightSwitch.UITests、UnitTests-CommonLib / CommonUtils）与上游结构一致。

---

## 10. 刻意不同步项（明确保留差异）

| 上游资产 | 说明 |
|---|---|
| .pipelines/、.github/ | 上游 CI/CD；Kit 为本地自用项目，无 CI |
| installer/（WiX）+ build-installer.ps1 + generate-dsc-manifests.ps1 | Kit 用 src/PackageIdentity 简化部署 |
| 遥测 / 更新器 | Kit 保持 check-only、不自动下载安装（AGENTS.md 边界） |
| PhiSilicaLaf.props / LanguageModelProvider / FoundryLocal / SemanticKernel / OpenAI / CommandPalette / Monaco / CmdPal 等 | AI 与命令面板全家桶，与 Kit 模块集无关（详见 §14 AI 功能栈审查） |
| nuget.config 上游仅私有 feed | Kit 用 nuget.org 为主 + PowerToysPublicDependencies（仅 CommunityToolkit.Labs.*）→ Kit 方案更稳，**保留** |

---

## 11. 建议执行顺序与验证

1. **P0（一次性、低风险）**：Cpp.Build.props 的 `/utf-8` + `_SILENCE_EXPERIMENTAL_COROUTINE_DEPRECATION_WARNINGS`；Directory.Build.targets 加 `EnsureLongPathsEnabled`、`RemoveUnusedWebView2WpfReference`；FuzzTest.props 改 net10。
2. **P1（依赖升级，需全量回归）**：`DotNetRuntimePackageVersion=10.0.11` 重构 + WebView2 升级；随后单独评估 WinAppSDK 2.0.1 到 2.2.0（改动最大，涉及 Settings UI / QuickAccess / CsWinRT 投影，建议一次一个提交）。
3. **P2（结构整理）**：抽取 `Common.Dotnet.props`、清 csproj 硬编码 TFM；Version.props + versionSetting.ps1 对齐（补 VersionChannel / SourceCommit / DevEnvironment）。
4. **P3（脚本对齐）**：build-common.ps1（VS2026 / DevShell 能力探测）、setup-dev-environment.ps1；build.ps1 / build-essentials.ps1 保留 Kit flag 与项目清单的对齐。
5. **验证**：`tools/build/build.cmd`（x64 Debug + Release）退出码 0；关键测试项目（Settings.UI.UnitTests、Monitor.UnitTests、UnitTests-CommonLib / CommonUtils）用 VS Test Explorer / vstest.console 通过；Release 输出无 PDB / 卫星目录残留（Kit 裁剪 target 仍生效）。

## 12. 第二轮补充：共享库与框架件漂移（深审）

> 本轮对第一轮未覆盖的公共库、runner 框架件、IPC 框架、顶层配置做了逐文件 diff，补充如下。

### 12.1 已确认完全一致（无需同步）

Display、Themes、Common.Search、ManagedCsWin32、PowerToys.ModuleContracts、common/logging、src/logging、CppRuleSet.ruleset、.config/dotnet-tools.json、src/Settings.XamlStyler、Common.Dotnet.AotCompatibility.props、Common.SelfContained.props、Common.Dotnet.PrepareGeneratedFolder.targets、codeAnalysis（Rules.ruleset / StyleCop.json / format_sources.ps1）、src/.editorconfig、src/.clang-format。

### 12.2 安全 / IPC 框架（重点，P1）

上游新增 src/common/interop/pipe_caller_auth.{h,cpp}：runner 特权命名管道（Settings / Quick Access 控制通道）的逐连接客户端认证（fail-closed）。同用户攻击者共享 SID、完整性级别与登录会话，管道 DACL 无法区分合法子进程与攻击者，只能认证连接进程的二进制身份。CallerPolicy 支持：允许的 basename 列表、期望目录（runner 相对路径）、文件版本匹配（防降级）、可选 Authenticode 签名（Debug 编译排除）、PID pin、每进程缓存与 reject 日志回调。

配套改动（均在上游 two_way_pipe_message_ipc 内）：
- 头文件新增 include pipe_caller_auth.h，客户端打开标志改为 FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION（客户端绝不让渡可模拟令牌）；
- 新增 start(HANDLE, const interop_auth::CallerPolicy&) 认证重载，逐连接认证通过后才分发消息（fail-closed）；
- 实现文件整体 +900 行：线程生命周期重构、故障注入钩子（TWO_WAY_PIPE_MESSAGE_IPC_TESTS）、认证接入。Kit 的拷贝明显落后。

动作（P1，安全加固）：移植 pipe_caller_auth + 新 start 重载 + runner 侧接线（src/runner/Kit.vcxproj 增加 pipe_caller_auth.cpp，PCH NotUsing）；必须 runner 与 settings-ui / quick access 两端同步验证（IPC 契约，AGENTS.md 重点区域）。若暂缓，请在本文件显式记录该风险。

### 12.3 runner 框架件

| 文件 | 差异 | 动作 |
|---|---|---|
| Kit.vcxproj vs runner.vcxproj | 上游将 updater 独立为 updating.vcxproj 项目引用、新增 EtwTrace 引用、bug_report / settings_telemetry / ai_detection；Kit 保留 deps/expected.props import、TargetName=Kit | 仅同步 pipe_caller_auth 与通用构建属性；其余保持 Kit 裁剪 |
| powertoy_module.cpp | 上游 UpdateHotkeyEx 新增 ClearPressedKeyActions + Win 键按住动作注册（依赖模块接口 keep_track_of_pressed_win_key，见第一轮 §7） | 采用 Win 键行为则随 §7 一起同步，否则跳过 |
| centralized_kb_hook.cpp / .h | 上游 +169 行（Win 键按住跟踪） | 同上 |
| 新增 src/runner/UnitTests/UnitTests-Runner.vcxproj（HotkeyConflictTests） | 上游新增 runner 层测试工程 | 建议引入（补齐测试面，符合 AGENTS.md 测试纪律） |
| main.cpp / settings_window.cpp / tray_icon.cpp / trace.cpp | 大幅漂移（含 bug_report、telemetry、AI 相关） | 保持 Kit 裁剪；仅按需摘取框架改进 |

### 12.4 共享库漂移

| 库 | 差异 | 动作 |
|---|---|---|
| common/logger/logger_settings.h | 上游新增大量模块日志名/路径常量（纯增量，无 ABI 破坏） | 按 Kit 模块集增量同步（awake / light-switch / monitor 等），无紧迫性 |
| common/SettingsAPI/settings_helpers | 上游 +95 行（新模块设置辅助） | 按需同步 |
| common/version/version.h | 上游引入 VERSION_BUILD（4 段版本）、VERSION_CHANNEL、VERSION_SOURCE_COMMIT 暴露，并去掉 static 缓存 | 与第一轮 §4 版本方案联动；PRODUCT_NAME 保持 Kit |
| ManagedCommon | RunnerHelper.cs / PowerToysPathResolver.cs 上游重构（-75/+125）；新增 HotkeySettingsControlHook.cs、NativeKeyboardHelper.cs | 建议同步（settings-ui 与模块共享，注意 ABI 敏感） |
| Common.UI | ThemeManager.cs（+206，WinAppSDK 2.2 主题体系）、NativeEventWaiter.cs、SettingsDeepLink.cs（+95） | 随 WinAppSDK 升级联动 |
| Common.UI.Controls | TransientSurface、TransparentWindow、AlwaysActiveDesktopAcrylicBackdrop、TitleBarHelper 等新控件 | 按需引入（无对应模块可暂缓） |
| GPOWrapper | 上游 +280 行为新模块 GPO 规则 | Kit 模块集不需要，保持 |
| Telemetry（EtwTrace / TraceBase / TelemetryBase.cs） | 上游演进 | Kit 保持 inert，不引入（AGENTS.md 边界） |
| UITestAutomation | SessionHelper / ModuleInfo 漂移；KitProcessCleanup.cs 是 Kit 特有（上游已删） | 仅在有 UI 测试需求时同步 |
| common/updating | 上游新增 configBackup.h、updateLifecycle.h、UnitTests | Kit 更新器 inert，按需评估 |

### 12.5 顶层配置

| 文件 | 差异 | 动作 |
|---|---|---|
| .vsconfig | 上游改用 Windows11SDK.22621/26100 + Vcpkg + WindowsAppSdkSupport.CSharp/Cpp；Kit 用 Windows10SDK.* + WindowsAppSDK.Cs | 不走 vcpkg，保留 Kit 版；可顺手移除 19041/20348 SDK 组件 |
| .gitattributes | 上游 text=auto + *.cs eol=crlf；Kit 强制 *.cpp/*.h/*.md 为 LF、*.sln 为 CRLF | Kit 对 C++ 更严格（干净 diff），保留；混编时注意换行一致性 |
| deps/spdlog.props | 上游移除 AdditionalIncludeDirectories 与 /utf-8（vcpkg 与全局 Cpp.Build.props 承担）；Kit 仍内联 | 完成第一轮 P0（Cpp.Build.props 全局 /utf-8）后，收敛为仅保留 SPDLOG_* 定义，与上游一致 |
| src/README.md | 上游 13+/5- 文档更新 | 顺手同步 |
| .gitignore | 两边 265/116 漂移（Kit 本地项 + 上游新项） | 合并时按 Kit 策略保留 |

### 12.6 settings-ui 框架件

- PowerToys.Settings.slnf：Kit 引用 Kit.slnx + Kit 项目集，保持 Kit 版本，仅随 Kit.slnx 变化维护（上游新增 LanguageModelProvider / ManagedTelemetry / MouseJump / ZoomIt / PowerDisplay 等条目，Kit 不需要）。
- QuickAccess.UI 图标：上游改通配符全量拷贝；Kit 用白名单 + KitRemoveInactiveQuickAccessIconAssetsFromOutput 裁剪，属刻意差异，新增模块时同步维护白名单。
- QuickAccess.UI 上游新增 ModuleGpoHelper.cs，按需引入。

### 12.7 模块级（非框架，仅记录）

- Awake：上游新增 AwakeStateCalculator / SessionStateController / UnitTests（Awake.UnitTests、Awake.ModuleServices.UnitTests），按模块逐个评估同步（含测试面）。
- Monitor 与上游 PowerDisplay：Kit 参考形态；上游 PowerDisplay.Lib 已进入 settings slnf，注意其演进（核心库 + 进程 + 接口 + 设置页的形态没有变化）。

### 12.8 本轮新增优先级汇总

| 项 | 优先级 |
|---|---|
| pipe_caller_auth IPC 认证（安全加固） | P1 |
| runner UnitTests-Runner 引入 | P2 |
| logger_settings 增量、SettingsAPI、version.h（随版本方案） | P2 |
| ManagedCommon / Common.UI 同步 | P2（随 WinAppSDK 升级） |
| .vsconfig / spdlog.props / src/README.md 微调 | P3 |
| 刻意不同步：GPOWrapper 新规则、Telemetry、Update、bug_report / ai_detection / settings_telemetry、UITestAutomation 大改 | 保留 |

## 13. 第三轮：LightSwitch / Awake 复制可行性 + Monitor 剔除计划

> **最终结论见 13.4**：按"Kit 完全兼容 PowerToys 插件规范、插件源码不改"原则，两个保留插件均可原样复制，改动全部落在 Kit 侧。

> 对 LightSwitch、Awake 与上游逐文件比对（含 settings-ui 集成面），评估能否直接复制编译；并列 Monitor 移除的完整引用清单。

### 13.1 LightSwitch —— 可以直接复制（需 4 点适配）

**比对结论**：
- 两边文件集完全一致（无新增/删除文件）；LightSwitchLib（ThemeHelper 等核心库）零漂移；
- 漂移集中在 11 个文件：LightSwitchModuleInterface/dllmain.cpp(222 行)、trace.cpp(37)/trace.h(19)、LightSwitchService.cpp(28)、LightSwitchStateManager.cpp(54)/.h(5)、LightSwitchService/trace.cpp(42)/trace.h(20)、两个 vcxproj(2-4 行)、LightSwitchSettings.cpp(1 行)；
- 上游新增能力：forceLight / forceDark 自定义动作（设置页按钮）、夜间照明相关逻辑、NotifyPowerDisplayThemeChanged 跨模块通知（LightSwitch 与 PowerDisplay 联动）。

**复制时需处理的 4 点**：
1. 保留 Kit 定制：事件名 Kit 用 Local\KitLightSwitchManualOverrideEvent-55af6d42... 与 Local\KitLightSwitchServiceStopEvent-09b983c3...（为避开与官方 PowerToys 冲突而改），上游用 POWERTOYS_LIGHTSWITCH_*；overview link Kit 指向 Kit 仓库。复制后把这两处改回 Kit 命名（dllmain.cpp + LightSwitchService.cpp 成对修改）。
2. 编译阻塞点：上游 LightSwitchStateManager.cpp 新增 include common/interop/shared_constants.h 并调用 NotifyPowerDisplayThemeChanged()；Kit 的 shared_constants.h 是旧版。因 Monitor（PowerDisplay 对应物）计划移除，**直接删除这些调用**即可，无需同步 shared_constants.h。
3. settings-ui 联动：LightSwitchSettings.cs(+6)、LightSwitchProperties.cs(+38)、LightSwitchPage.xaml(+69)、LightSwitchViewModel.cs(+283) 上游漂移较大；其中 LightSwitchViewModel.cs 引用了 PowerDisplay.Models 并调用 CheckPowerDisplayEnabled() —— **必须剥离 PowerDisplay 引用**（与 Monitor 移除联动）。已确认上游 LightSwitch 设置面无遥测。
4. trace.cpp / trace.h 增量使用 EtwTrace（common/Telemetry），Kit 已有 EtwTrace 工程，随文件复制即可。

**结论**：可复制，工作量约半天。覆盖 11 个模块文件 + 4 个 settings-ui 文件 + 3 处适配（事件名、overview link、剥离 PowerDisplay / NotifyPowerDisplay 引用）。

### 13.2 Awake —— 不能直接复制（需裁剪遥测 + 补日志常量）

**比对结论**：
- 上游新增 14 个文件：核心 3 个（AwakeStateCalculator.cs、SessionStateDetector.cs、SessionStateController.cs）、遥测 5 个（Awake/Telemetry/*.cs）、测试工程 2 组（Awake.UnitTests、Awake.ModuleServices.UnitTests，共 6 个文件）；
- 漂移 9 个文件：Awake.csproj(+10，新增 AllowUnsafeBlocks 与 InternalsVisibleTo)、Core/Manager.cs(+146，会话切换/锁屏/状态机)、Program.cs(+26)、Awake.ModuleServices.csproj(+6)、AwakeModuleInterface/dllmain.cpp(97)、trace.cpp(20)/trace.h(13)、README.md(4)；
- 核心新特性：SessionSwitch 会话监听、锁屏检测、SessionStateController/SessionStateDetector、状态计算器拆分（可测性增强）。

**不能直接复制的阻塞点**：
1. 托管遥测（编译阻塞）：上游 Awake 强依赖 Microsoft.PowerToys.Telemetry（ManagedTelemetry 工程，Kit 没有且按边界不引入）。涉及：Awake/Telemetry/ 5 个文件、Core/Manager.cs（using + 4 处 WriteEvent）、Program.cs（using + LogCLITelemetry）。**必须裁剪**：删 5 个遥测文件 + 剥离 2 个文件内调用。C++ 侧 EtwTrace 不受影响（Kit 已有）。
2. 日志常量：上游 AwakeModuleInterface/dllmain.cpp 改用 LogSettings::launcherLoggerName（launcher），Kit 的 logger_settings.h 只有 awakeLoggerName → 需同步 logger_settings.h（见 12.4）或改回 awakeLoggerName。建议随复制一并同步。
3. 接口关闭逻辑：上游简化（去掉 terminate / wait 进程兜底）；Kit 现有版本带超时终止保护。可接受丢失或手工保留（建议保留）。
4. AllowUnsafeBlocks / InternalsVisibleTo 随 csproj 复制（SessionStateDetector 用 P/Invoke + unsafe）。

**settings-ui 集成**：AwakeSettings.cs、AwakeProperties.cs、AwakeViewModel.cs 与上游完全一致；AwakePage.xaml 仅 7/7 小漂移。

**结论**：不能整目录覆盖；需裁剪遥测后复制。工作量约 1-2 天（含编译验证）：覆盖/新增 20+ 文件 + 删遥测 5 文件并剥离 2 处调用 + 补 logger 常量 + 新增两个测试工程入 Kit.slnx。

### 13.3 Monitor 剔除计划（检查清单）

目标：移除 Monitor 模块（对应上游 PowerDisplay）。以下为模块外全部引用点（已枚举验证）：

**A. 工程与解决方案**
- Kit.slnx：删除 /modules/Monitor/ 与 /modules/Monitor/Tests/ 文件夹及其工程（MonitorLib、Monitor、MonitorModuleInterface、Monitor.UnitTests）；
- src/runner/Kit.vcxproj：删除对 Monitor 各工程的 ProjectReference / BuildDependency；
- 删除整个 src/modules/Monitor/ 目录。

**B. runner**
- src/runner/main.cpp：KitKnownModules 删除 L"PowerToys.MonitorModuleInterface.dll" 条目；
- src/runner/settings_window.cpp / settings_window.h：删除 ESettingsWindowNames::Monitor 枚举及 to_string / from_string 分支；
- 其余 runner 文件无 Monitor 引用（已核查）。

**C. settings-ui**
- Settings.UI.Library：MonitorInfo.cs、MonitorProperties.cs、MonitorSettings.cs（SndMonitorSettings.cs 需先确认来源再决定）；
- Settings.UI 页面/服务：MonitorPage.xaml(.cs)、MonitorViewModel.cs、Services/MonitorManualScanCoordinator.cs、MonitorManualScanProgressUpdate.cs、MonitorProgressSnapshotReader.cs、MonitorSettingsStoragePaths.cs、MonitorStatusPresentation.cs、MonitorStatusPresentationService.cs、MonitorStatusQueryService.cs、ViewModels/MonitorScanIntervalOption.cs、MonitorStatusBrushes.cs、MonitorStatusDayViewModel.cs、MonitorStatusLegendItemViewModel.cs、MonitorStatusMetricViewModel.cs；
- 导航/注册：NavigationService.cs(60-66)、Helpers/NavHelper.cs(20)、ViewModels/ShellViewModel.cs(33,81-89)、SettingsXAML/Views/ShellPage.xaml(127)+.cs(121,123,227,231,233,250,256,258,275,329,420,449,450,453,472)、ViewModels/DashboardViewModel.cs(90,143)、Settings.UI.Library/Helpers/KitModuleCatalog.cs(12)、Utilities/CommandLineUtils.cs(17,19)、SettingsXAML/Views/SearchResultsPage.xaml.cs(77-79)；
- QuickAccess.UI：AllAppsViewModel.cs(72)、QuickAccessXAML/Flyout/ShellPage.xaml.cs(14)、Settings.UI.Controls/QuickAccess/QuickAccessViewModel.cs(55)、PowerToys.QuickAccess.csproj 图标白名单 Monitor.png 与 KitRemoveInactiveQuickAccessIconAssetsFromOutput 的 Exclude；
- 资源/资产：Strings/en-us/Resources.resw(142,146) Monitor 名称与描述；Assets/Settings/Icons/Monitor.png、Assets/Settings/Modules/Monitor.png；
- 序列化：Settings.UI/SerializationContext/SourceGenerationContextContext.cs(16) 移除 Monitor 相关类型。

**D. 测试（需同步改）**
- Settings.UI.UnitTests：删除 MonitorSettingsRegistration.cs、Services/MonitorManualScanCoordinatorTests.cs；
- BuildCompatibility.cs 大量 Monitor 断言：按 AGENTS.md 纪律改为物理断言"Monitor 已移除"；
- General.cs、LightSwitch.cs、FrameworkPrivacyDefaults.cs 中 Monitor 相关断言清理；
- src/modules/Monitor/Tests 随工程删除。

**E. 文档 / 清单**
- AGENTS.md：模块表 Monitor 行、Monitor is the reference shape 表述（改为 LightSwitch 或 Awake 作参考形态）、Validation Checklist 中 Monitor 条目；
- README.md（约 36 处）、changelog.md（约 72 处，历史版本条目保留、补一条移除说明）、src/README.md(8)、doc/devdoc/kit-first-plugin.md（以 Monitor 为示例，需替换）、doc/devdoc/kit-development-experience.md、doc/devdoc/README.md；
- KitKnownModules / KitModuleCatalog 静态注册测试（若存在）同步。

**F. 联动影响**
- 上游 LightSwitch 设置 UI 引用了 PowerDisplay.Models / CheckPowerDisplayEnabled / NotifyPowerDisplayThemeChanged —— Monitor 移除后，LightSwitch 复制方案（13.1）剥离这些引用即无来源冲突；
- QuickAccess 图标白名单与 Settings 首页图标裁剪 target 同步移除 Monitor.png；
- Monitor 作为新模块参考形态的替代说明需写进 AGENTS.md 与 devdoc。

**G. 执行顺序建议**
1. 先复制 LightSwitch（剥离 PowerDisplay 引用）与 Awake（裁剪遥测），完成编译；
2. 再剔除 Monitor（此时 PowerDisplay 引用无来源，干净）；
3. 全量 build x64 Debug + Release，退出码 0；
4. vstest 关键测试（BuildCompatibility、Settings.UI.UnitTests、Monitor.UnitTests 删除后其余），确认无残留；
5. 更新文档与 AGENTS.md，提交。

### 13.4 设计原则裁定：插件原样复制，Kit 侧适配清单（最终结论）

**裁定**：Awake 与 LightSwitch **均可原样复制编译**（插件代码与 csproj/vcxproj 不动），所需改动全部落在 Kit 侧：common 库补齐、托管遥测程序集、settings-ui（Kit 主界面）、runner 注册、解决方案。

**已验证的原样复制前提（插件侧无需改）**：
1. 插件 DLL 输出名与 Kit runner 加载清单完全一致：PowerToys.AwakeModuleInterface.dll、PowerToys.LightSwitchModuleInterface.dll（上游 vcxproj TargetName 已确认）→ runner 零改动加载；
2. 插件协议 ABI 兼容：powertoy_module_interface.h 仅 14 行差异，新增虚函数均带默认实现，双向兼容（新插件配旧 runner、旧插件配新 runner 均可）；
3. C++ 侧 ETW 宏齐全：上游 trace.cpp 依赖的 TraceLoggingWriteWrapper / TraceLoggingOptionProjectTelemetry / ProjectTelemetryPrivacyDataTag / PROJECT_KEYWORD_MEASURE 在 Kit 的 common/Telemetry（TraceBase.h、TraceLoggingDefines.h）已存在；插件 vcxproj 相对 Kit 只多一个 src\common\Telemetry 包含路径（随插件文件原样复制即生效）；
4. LightSwitch 的 NotifyPowerDisplayThemeChanged 只是 shared_constants.h 的事件名常量加内联通知函数，不依赖 PowerDisplay 模块本体 → Kit 同步 shared_constants.h 即可，Monitor 移除后事件无人监听、无副作用。

**Kit 侧适配清单（插件不动）**：

A. common 库补齐（编译依赖）：
1. logger_settings.h：补上游 launcherLoggerName / launcherLogPath 等常量（Awake 接口 dllmain 用到）；
2. src/common/interop/shared_constants.h：同步上游（LightSwitch 的 NotifyPowerDisplayThemeChanged 依赖；PowerDisplay 事件常量保留无碍）；
3. src/common/Telemetry/TelemetryBase.cs：从上游复制（ManagedTelemetry 工程编译依赖，Kit 目前缺失）；
4. Directory.Packages.props：补 Microsoft.Diagnostics.Tracing.TraceEvent 3.1.16（上游 pin）。

B. 托管遥测程序集（Awake 编译依赖）：
5. 新增 src/common/ManagedTelemetry/Telemetry/（7 个文件，MIT）并加入 Kit.slnx，为 Awake 提供 Microsoft.PowerToys.Telemetry 程序集，使插件原样编译；
6. **必须处理**：Kit 的 Directory.Build.targets 中 KitRemoveInactiveManagedTelemetryArtifactsFromOutput 会删除 OutDir 下的 PowerToys.ManagedTelemetry.* 及 TraceEvent 依赖 DLL（Dia2Lib.dll、TraceReloggerLib.dll 等）→ 引入 ManagedTelemetry 后需移除或改造该 target，否则 Awake 运行期缺 DLL 无法启动；
7. 边界说明：这只是编译期/程序集支持；Kit 不接遥测 consent 与上传管线，ETW 事件无收集器，行为保持 inert。若坚持零遥测代码，替代方案是裁掉 Awake 内 2 处调用（属于改插件，与"原样"原则冲突，不推荐）。

C. settings-ui（Kit 主界面）：
8. Awake：AwakeSettings.cs / AwakeProperties.cs / AwakeViewModel.cs 与上游已完全一致，AwakePage.xaml 仅 7 行小漂移，可选同步；
9. LightSwitch：同步上游 LightSwitchSettings.cs(+6)、LightSwitchProperties.cs(+38)、LightSwitchPage.xaml(+69)、LightSwitchViewModel.cs(+283)；因 Kit 移除 Monitor（无 PowerDisplay），在 **Kit 侧 ViewModel** 剥离 PowerDisplay.Models 与 CheckPowerDisplayEnabled 调用（这是主界面代码，不属于插件）；
10. forceLight / forceDark 设置按钮随 settings 框架自动渲染，无需额外开发。

D. runner（Kit 主界面）：
11. KitKnownModules 无需改动（DLL 名一致）；Monitor 条目按 13.3 剔除；ESettingsWindowNames 相应清理。

E. 解决方案 / 构建：
12. Kit.slnx：新增 ManagedTelemetry.csproj 与（可选）Awake.UnitTests、Awake.ModuleServices.UnitTests；删除 Monitor 工程（13.3）；
13. 包版本核对：插件 vcxproj 硬编码的 ImplementationLibrary（AwakeModuleInterface.vcxproj 校验 1.0.260126.7）等 Error 检查路径需与 Kit 实际恢复的包版本一致，否则构建报错；
14. 若 Kit 未来引入第三方 PowerToys 插件，ManagedTelemetry 与 shared_constants.h 的同步将是一般性前提（本方案即为插件兼容基建）。

F. 决策点（二选一，涉及是否动插件）：
15. 事件名：上游 POWERTOYS_LIGHTSWITCH_*；Kit 现行改名 Local\KitLightSwitch*（防与官方 PowerToys 同机会话冲突）。原样复制 = 用上游名（插件规范优先）；保留隔离 = 需改插件 2 处（违背原样原则）；
16. overview link：上游 aka.ms/powertoys。原样 = 设置页链接指向官方页面（仅外观）；如需指向 Kit 仓库则需改插件 1 行。

G. 验证：
17. build x64 Debug + Release 退出码 0；Awake / LightSwitch 可启用、设置可读写、forceLight / forceDark 生效；Settings.UI.UnitTests 等 vstest 通过；确认 OutDir 无 PowerToys.ManagedTelemetry 缺失问题。

## 14. 第四轮补充：AI 功能栈审查（AdvancedPaste / LanguageModelProvider）

> 结合本机 Windows sandbox 实测（Advanced Paste AI 配置极简、默认模型偏旧）对上游 AI 栈做的源码级核对，补全 §1.3 与 §10 中"AI 全家桶"的具体内容。

### 14.1 AI 相关包清单（Directory.Packages.props）

| 包 | 版本 | 用途 |
|---|---|---|
| OpenAI | 2.7.0 | 官方 OpenAI .NET SDK，OpenAI / Azure OpenAI chat 通道 |
| Microsoft.SemanticKernel | 1.71.0 | AI 编排层（prompt 组装、动作调度） |
| Microsoft.SemanticKernel.Connectors.OpenAI | 1.71.0 | OpenAI 连接器 |
| Microsoft.SemanticKernel.Connectors.AzureAIInference | 1.71.0-beta | Azure AI Inference 连接器 |
| Microsoft.SemanticKernel.Connectors.Google | 1.71.0-alpha | Gemini 连接器 |
| Microsoft.SemanticKernel.Connectors.MistralAI | 1.71.0-alpha | Mistral 连接器 |
| Microsoft.SemanticKernel.Connectors.Ollama | 1.71.0-alpha | 本地 Ollama 连接器 |
| Microsoft.Extensions.AI | 10.2.0 | .NET 统一 AI 抽象层 |
| Microsoft.Extensions.AI.OpenAI | 10.0.1-preview.1.25571.5 | 该抽象层的 OpenAI 实现 |
| Microsoft.AI.Foundry.Local | 0.3.0 | 本地模型（Phi Silica / Foundry Local） |
| Microsoft.Bcl.AsyncInterfaces | 10.0.11 | 为 SemanticKernel 强制版本 |

### 14.2 消费工程与职责

- src/common/LanguageModelProvider（公共库）：ILanguageModelProvider 抽象 + FoundryLocalModelProvider / FoundryClient / FoundryCatalogModel / FoundryCachedModel（本地模型目录与缓存）+ ModelSettings / ModelDetails / PromptTemplate / Runtime / HardwareAccelerator。引用 Microsoft.Extensions.AI、Microsoft.Extensions.AI.OpenAI、Microsoft.AI.Foundry.Local、OpenAI；仅被 AdvancedPaste 与 Settings.UI 引用。
- src/modules/AdvancedPaste/AdvancedPaste（唯一产品模块）：AI 智能粘贴（FixSpellingAndGrammar、翻译、总结、解释、生成代码、自定义动作）。核心文件：Services/AdvancedAIKernelService.cs、Services/CustomActions/SemanticKernelPasteProvider.cs、PasteAIProviderFactory.cs、AdvancedAIProviderResolver.cs、PromptModerationService.cs（内容安全审核）、EnhancedVaultCredentialsProvider.cs（API Key 存 Windows Credential Manager）。
- Settings.UI：引用 LanguageModelProvider，提供模型选择页与 FoundryLocalModelPicker 控件；AdvancedPastePage.xaml 内嵌 AI provider 配置对话框。

### 14.3 AdvancedPaste AI 配置面（实测印证：极简）

PasteAIConfig 字段：ProviderType、Model、ApiKey、Endpoint、DeploymentName、LocalModelPath、ModelPath、SystemPrompt、ModerationEnabled。

设置对话框（AdvancedPastePage.xaml 的 PasteAIProviderConfigurationDialog）暴露：
- Provider 下拉（AIServiceType 枚举：OpenAI / AzureOpenAI / Onnx / ML / FoundryLocal / Mistral / Google / AzureAIInference / Ollama / PhiSilica）；
- 模型名：纯文本框，占位 gpt-4o（无目录、无自动发现）；
- Endpoint URL、API Key、API Version（默认折叠，占位 2024-10-01）、Deployment Name、System Prompt（多行）；
- 本地模型（FoundryLocal / PhiSilica）切换为 FoundryLocalModelPicker（带本地模型目录）。

无 temperature / top_p / max_tokens 等采样参数、无连接测试按钮。

### 14.4 默认模型分析（确认偏旧）

- Settings.UI.Library/PasteAIProviderDefaults.cs：OpenAI 与 AzureOpenAI 默认 gpt-4o，AzureAIInference 默认 gpt-4o-mini；
- Services/AdvancedAIKernelService.cs:95 兜底同样 gpt-4o；
- gpt-4o 为 2024-05 发布的模型；本快照整体较新（.NET 10.0.11 / WinAppSDK 2.2 / WebView2 1.0.4022.49），默认模型确实偏旧；
- 缓解点：模型名为自由文本，可手填 gpt-4.1 / o3 / o4-mini / gpt-5 等新模型直接使用（OpenAI SDK 2.7.0 与 SemanticKernel 1.71 均支持）→ 属默认值与 UX 问题，而非硬限制。

### 14.5 改进建议（若未来把 Advanced Paste 或本地 AI 纳入 Kit）

1. 默认模型更新：PasteAIProviderDefaults.cs 换新模型（gpt-4.1 或 o 系列）；
2. 模型目录下拉：复用 FoundryLocalModelPicker 模式，为云 provider 加可用模型目录；
3. 按需暴露常用采样参数（temperature 等）；
4. 本地优先方案：只引入 LanguageModelProvider + Microsoft.AI.Foundry.Local（Phi Silica），不接云端 OpenAI，符合 Kit 本地/离线倾向。

### 14.6 与 Kit 的关联结论

- 该 AI 栈属于刻意不同步项（§1.3 / §10），不参与 Awake / LightSwitch 插件兼容方案（两插件不依赖任何 AI 包，已在 §13.4 验证）；
- 若 Kit 未来做本地 AI 能力，最小引入面：LanguageModelProvider + Microsoft.AI.Foundry.Local（Phi Silica）+ settings 模型选择页 + FoundryLocalModelPicker，需配套 AOT / CsWinRT 兼容性验证；
- OpenAI / SemanticKernel / AdvancedPaste 云端通道保持不引入（AGENTS.md 边界：不引入 AI 全家桶、遥测保持 inert）。
