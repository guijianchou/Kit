# Kit 随上游同步进度与逻辑修正清单

> 生成日期：本次 review。基于上游 Source/PowerToys 与 Kit 源码的逐文件比对、构建产物与 git 历史（cea7b7a、cb2c1e5）核查。
> 定位：[upstream-sync-checklist.md](upstream-sync-checklist.md) 是"要同步什么"的计划清单；本文档是"现状核对 + 哪里逻辑要修"的落地清单，两者配合使用。

## 0. 结论速览

1. **构建 P0 已全部落地**（提交 cb2c1e5）：Directory.Build.targets 的 EnsureLongPathsEnabled 与 RemoveUnusedWebView2WpfReference、FuzzTest.props 已到 net10、Cpp.Build.props 已有 /utf-8 与协程弃用静默 → [upstream-sync-checklist.md](upstream-sync-checklist.md) §1/§3 对应条目应标记完成。
2. **Monitor 模块已移除**（提交 cea7b7a，版本 2.0.8）：代码、runner、序列化、单元测试（BuildCompatibility 有 11 处负向断言）、devdoc 均已一致。**但 README.md 与 [AGENTS.md](AGENTS.md) 仍把 Monitor 当活动模块**，README 的 KitKnownModules 列表还列着已删除的 PowerToys.MonitorModuleInterface.dll —— 文档失实是当前最优先修正项。
3. **版本号三处不一致**：src/Version.props = 2.0.8；changelog.md 最新条目仍是 2.0.7（缺 2.0.8 移除记录）；doc/devdoc/kit-development-experience.md 的移除记录写 2.1.0。
4. **代码逻辑隐患 4 项（P1）**：runner 启动时执行 clean_video_conference 死代码（Kit 从未发布过 Video Conference Mute）；Directory.Build.props 无条件拼接版本号并保留微软品牌元数据；KitRemoveInactiveManagedTelemetryArtifactsFromOutput 会在引入 ManagedTelemetry 后误删运行库；version.h 硬编码第四段版本号。
5. **随上游进度**：构建基础设施约 95%（剩版本方案、Common.Dotnet.props 抽取、构建脚本）；共享库约 80%（logger_settings、shared_constants、EtwTrace/TraceBase、version.h、TelemetryBase.cs / ManagedTelemetry 落后）；LightSwitch 落后 11 个文件；Awake 落后约 23 个文件（含遥测依赖阻塞）；Settings UI 的 LightSwitch 侧落后。

## 1. 随上游更新进度（按域）

| 域 | 同步度 | 差距详情 | 关键依据 |
|---|---|---|---|
| 构建基础设施 | 约 95% | P0 已落地；缺 src/Common.Dotnet.props 抽取、版本方案（Version.props + versionSetting.ps1 未对齐上游 VersionBuildSuffix）、tools/build 脚本未对齐 VS2026/DevShell | Directory.Build.props/targets、Cpp.Build.props、src/Common.Dotnet.FuzzTest.props、tools/build |
| 依赖版本 | 落后 | WinAppSDK 2.0.1 对 2.2.0、WebView2 1.0.3719.77 对 1.0.4022.49、缺 DotNetRuntimePackageVersion 10.0.11、缺 Microsoft.Diagnostics.Tracing.TraceEvent 3.1.16 | Directory.Packages.props |
| 共享库 | 约 80% | logger_settings.h 缺 launcher 常量；shared_constants.h 旧版；EtwTrace.cpp/TraceBase.h/ProjectTelemetry.h 漂移（+81/+36）；version.h 硬编码 .0；TelemetryBase.cs 缺失；ManagedTelemetry 工程缺失 | src/common/logger、src/common/interop、src/common/Telemetry、src/common/version |
| LightSwitch 模块 | 落后 11 文件 | 缺上游 forceLight/forceDark 自定义动作、会话管理、trace 增量；LightSwitchLib 零漂移 | LightSwitchModuleInterface/dllmain.cpp(222)、LightSwitchService.cpp(28)、LightSwitchStateManager.cpp(54) 等 |
| Awake 模块 | 落后约 23 文件 | 缺上游会话状态机（AwakeStateCalculator/SessionStateController/SessionStateDetector）与 2 个测试工程；Manager.cs(+146)、Program.cs(+26)；遥测 5 文件为编译阻塞 | src/modules/awake 各文件 |
| Settings UI | LightSwitch 侧落后 | Awake 设置模型/页完全一致；LightSwitchSettings.cs(+6)/Properties(+38)/Page.xaml(+69)/ViewModel(+283) 落后，ViewModel 需在 Kit 侧剥离 PowerDisplay 引用 | src/settings-ui |
| 测试 | 基本同步 | Settings.UI.UnitTests 含 Monitor 移除断言；缺上游 Awake.UnitTests / Awake.ModuleServices.UnitTests | src/settings-ui/Settings.UI.UnitTests |
| 文档 | **失实** | README.md 全篇仍以 Monitor 为活动模块并列出已删 DLL；AGENTS.md 模块表与参考形态未更新；changelog 缺 2.0.8；版本号三处不一致 | README.md、AGENTS.md、changelog.md、doc/devdoc |

## 2. 逻辑修正清单

### P0 文档失实（应立即修，会误导后续开发与插件复制）

1. **README.md**：
   - 第 25-35 行"Phase One Closeout"整段以 Monitor 为活动模块（含 Keep Monitor 的 worker headless 等要求）；
   - 第 42 行"Monitor is the first Kit-authored module"；第 55-57 行当前模块集列表含 Monitor；
   - 第 63-67 行 **KitKnownModules 列表仍含 PowerToys.MonitorModuleInterface.dll**（实际 runner 只加载 Awake/LightSwitch）——这是硬性失实；
   - 第 119-142 行"Monitor Implementation"整节指向已删除的 src/modules/Monitor/MonitorLib、Monitor、MonitorModuleInterface 与 Settings 侧 MonitorPage/MonitorViewModel；
   - 第 182-251 行 Recent Monitor 稳定化、构建清单（Monitor.UnitTests.csproj、PowerToys.Monitor.csproj、MonitorModuleInterface.vcxproj）与本地验证记录。
   → 重写为两个活动模块（Awake、Light Switch）；Monitor 内容并入历史/移除记录或删除；KitKnownModules 列表改为实际两项。
2. **AGENTS.md**：第 12 行模块表（active module set 含 Monitor）、第 31 行"Monitor is the reference shape"（改为 Awake 或 LightSwitch）、第 74 行测试面（Monitor.UnitTests）→ 全部更新为两个活动模块；参考形态建议改用 LightSwitch（interface+service 形态更贴近新模块模板）。
3. **changelog.md**：最新版本段仍是 2.0.7，缺 2.0.8（Monitor 移除 + 版本提升）条目 → 补一条移除记录（历史 2.0.7 及更早的 Monitor 条目保留，属历史事实）。
4. **版本号三处不一致**：Version.props 2.0.8 / changelog 2.0.7 / devdoc 2.1.0 → 统一为 2.0.8（或在下次发版时一并升）。
5. **README_zh.md**（若存在）：changelog 提到 README_zh 同步维护，需一并核查 Monitor 失实。
6. **next.md 过期条目**：§13.3 Monitor 剔除计划（已完成）、§13.4 中"因 Kit 移除 Monitor"前提（已成立）、§1/§3 的 P0 项（EnsureLongPathsEnabled/WebView2/FuzzTest//utf-8 均已落地）→ 更新为完成态或标注过期，避免重复执行。

### P1 代码逻辑隐患

7. **src/runner/main.cpp:175 的 clean_video_conference() 死代码**：Kit 从未发布 Video Conference Mute，该调用只是上游为旧版用户清理摄像头驱动注册；每次提权启动执行 RegDeleteTreeW 清理 HKLM/HKCR 是无效逻辑 → 删除调用与 src/common/utils/clean_video_conference.h。
8. **Directory.Build.props 版本与品牌**：
   - `<Version>$(Version).0</Version>` 无条件拼接 → Version.props 若出现 4 段版本（如 2.0.8.1）会变 5 段；应对齐上游 VersionBuildSuffix 正则；
   - AssemblyCompany=Microsoft Corp.、Company=Microsoft Corporation、Authors=Microsoft Corporation、RepositoryUrl=https://github.com/microsoft/PowerToys → 改为 Kit 归属（github.com/guijianchou/Kit），与 AGENTS.md 品牌分离要求一致。
9. **Directory.Build.targets:59-66 KitRemoveInactiveManagedTelemetryArtifactsFromOutput**：当前无害（Kit 未构建 ManagedTelemetry）；但按 next.md §13.4 引入 ManagedTelemetry 支撑 Awake 原样复制后，该 target 会在每次构建删除 OutDir 下刚生成的 PowerToys.ManagedTelemetry.dll 及 TraceEvent 依赖（Dia2Lib.dll、TraceReloggerLib.dll 等）→ Awake 运行期缺 DLL；引入前必须移除或改造。
10. **src/common/version/version.h**：FILE_VERSION_STRING 与 get_std_product_version 硬编码第四段 .0；上游已改用 version_gen.h 的 VERSION_BUILD → 同步 version.vcxproj HeaderLines 与 versionSetting.ps1 时一并对齐（当前仅是漂移，不构成运行错误）。

### P2 插件原样复制的前置缺口（§13.4 落地依赖，非当前运行 bug）

11. src/common/logger/logger_settings.h：缺上游 launcherLoggerName / launcherLogPath → 上游 Awake 接口 dllmain.cpp 原样复制会编译失败。
12. src/common/interop/shared_constants.h：旧版 → 上游 LightSwitch StateManager 需要的 NotifyPowerDisplayThemeChanged 相关常量缺失；补齐后无 PowerDisplay 模块也可编译（事件无人监听无害）。
13. src/common/Telemetry/TelemetryBase.cs 缺失 + Directory.Packages.props 缺 TraceEvent 3.1.16 → ManagedTelemetry 工程（Awake 编译依赖）无法构建。
14. Awake：上游新增 14 文件（会话状态机 3 + 遥测 5 + 测试工程 2 组）+ 9 文件漂移；遥测调用需 Kit 侧 ManagedTelemetry 支撑（见 9、13）。
15. LightSwitch：上游 11 文件漂移（forceLight/forceDark、会话管理、trace 增量）；settings-ui 4 文件漂移需在 Kit 侧剥离 PowerDisplay.Models / CheckPowerDisplayEnabled（Monitor 已移除，引用无来源）。

### P3 小项 / 决策项

16. src/Common.Dotnet.FuzzTest.props 注释过期（仍写"OneFuzz 不支持 .NET 9、用 .NET 8 兜底"，实际 TFM 已是 net10）→ 更新注释。
17. Settings.UI.Library 保留约 30 个非活动上游模型（AdvancedPasteSettings、FancyZonesSettings 等）——与上游 Library 结构一致、当前无害；是否裁剪需决策（BuildCompatibility 已约束序列化只暴露活动模块）。SndAwakeSettings / SndLightSwitchSettings 是活动 IPC 包装（AwakePage/LightSwitchPage 使用），勿删。
18. 依赖版本滞后清单按 next.md §1.1 执行（WinAppSDK 2.2.0、WebView2 1.0.4022.49、DotNetRuntimePackageVersion 10.0.11、TraceEvent 3.1.16）。

## 3. 修正后验证

1. tools/build/build.cmd（x64 Debug + Release）退出码 0；
2. vstest.console.exe 运行 Settings.UI.UnitTests（BuildCompatibility 全绿 = Monitor 移除断言 + 文档断言通过）；
3. 输出目录无 PowerToys.ManagedTelemetry 缺失、无 PDB/卫星目录残留（Kit 裁剪 target 仍生效）；
4. README/AGENTS/devdoc/changelog 与 next.md 的 Monitor 表述全部收敛到"已移除"事实。