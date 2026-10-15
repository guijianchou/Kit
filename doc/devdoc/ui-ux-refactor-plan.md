# Kit UI/UX 重构方案与实施记录 — 原生 Fluent 规范化 + Mica（v2 · as-built）

> 状态：**批次 0–6 已实施并直接合入 `main`（2026-10-12/13）；2026-10-14 复审清理轮完成（验证证据见 E）** ｜ 基线：`main@4da08bd`（origin 已备份，随时可回退）
> 范围：主框架（Shell / TitleBar / Dashboard / General / QuickAccess / 托盘）+ 全部 6 个模块页（Awake、LightSwitch、Localserver、UDPtest、NetMap、AIHub）
> 原则：**只动 UI/UX 层**，不碰底层核心；观感目标：**对齐 Windows 11 原生设置**，充分利用 WinUI 3 + Mica；修复重点：**样式不统一、拼凑感**。
> 阅读指引：以下 as-built 记录反映实际落地状态；§1 起为 v1 原始设计与逐按钮/逐动画规格，凡未在 as-built 中标注偏差的条目均已按稿实施。

## 实施结果（as-built）

### A. 批次 → 提交映射（直接提交 `main`）

| 阶段 | 提交 | 日期 | 内容 |
|---|---|---|---|
| 计划入库 | `ae5d5c7` | 10-12 | 本方案 + DOCUMENT_INDEX 注册 |
| 批次 0 | `30b4f77` | 10-12 | 共享设计令牌与样式族（Colors alias-first / Button / TextBlock / Surface） |
| 批次 1 | `ad5d21d` | 10-12 | Shell / TitleBar / Dashboard / General / QuickAccess |
| 批次 2 | `8b57daf` | 10-12 | Awake / LightSwitch |
| 批次 3 | `87714a9` | 10-12 | Localserver |
| 批次 4 | `da64a82` | 10-12 | UDPtest |
| 批次 5 | `20929f5` | 10-13 | NetMap |
| 批次 6 | `535a6ce` | 10-13 | AIHub |
| 定版 | `ca9b7fb` + `a86af9f` | 10-13 | v2.3.9 定版与 README/changelog |
| 补丁 | `6e6bac9` | 10-13 | v2.3.10 AIHub kernel 暂存目录重试 |
| 补丁 | `4ab8ff4` | 10-13 | v2.3.11 NetMap UX/延迟优化、Settings 导航配色、白屏加载修复 |
| 清理轮 | 本次提交 | 10-14 | 死代码/死键/令牌残留清理 + 全量验证（见 C/E） |

### B. 与 v1 计划的偏差（as-built）

| v1 计划 | 实际落地 | 说明 |
|---|---|---|
| §4.6A AIHub 拆分 5 文件 | **未拆分**：保持单文件（AIHubPage.xaml 1297 行、`x:Load` 惰性 5 处） | 规避 FindName/虚拟化契约风险；就地 Fluent 对齐，烟雾与源码扫描契约天然保绿 |
| §2.5 #2 入场动画 | 页级 `SettingsCardsAnimations`（App.xaml 定义、13 处引用）；**SettingsPageControl 不再挂载**（2.3.11 白屏修复）；AIHub 页无入场过渡 | 加载性能优先；内联 Entrance/Reposition 已全清除（0 残留） |
| §3.3 / §4.5 HealthStyles 共享 | **未提升**：`DashboardEgressStyles` 在 DashboardPage.xaml:19、`NetMapHealthStyles` 在 NetMapPage.xaml:20（code-behind :244/:434 引用） | 键名契约优先；两处重复登记 backlog（D-4） |
| §3.4 chips 中性化 | **令牌化彩色**：6 chips 用 `KitSectionChipButtonStyle` + `Section*Foreground/TintBrush`（12 键 ×3 主题含 HC） | 保留分类色识别；颜色全部收敛到 Colors.xaml 并补 HC |
| §2.1 页面 hex 清零 | General 36 处、UDPtest 2 处清零；QuickAccess App.xaml 保留 3 处 hex（`LayerOnAcrylicFillColorDefaultBrush`，Opacity 0.7/0.6 + HC 变体） | 自定义 acrylic 瓦片底色为有意例外 |
| §2.7 UDPtest IsChinese 全量迁移 | **未执行**：UDPtestViewModel 57 + UDPtestLineRowViewModel 11 处仍在 | backlog（D-1） |
| §2.7 Optimization 段迁移 | **已完成**：OptimizationCategory/GroupViewModel 走 `AIHub_*` 键 | — |
| §2.7 AIHubPageViewModel ~226 处 | 232 处未迁移（v1 已声明延后） | backlog（D-2） |
| §2.7 AiHubViewModel 英文状态 | 大部分已迁，余 13 处 | backlog（D-3） |
| §4.3 Localserver 死键 ~50 | 实删 39 个基键（含 MoveUp/MoveDown 菜单键）；死 handler 已删 | 按引用证据精确化 |
| §4.5 NetMap 死键 19 | 仅删 `NetMap_HttpResponded` | `Text(shortKey)` 前缀助手（NetMapViewModel.cs:324）使其余键静态不可证死（D-5） |
| §5 批次 7 清理 | 由清理轮执行（C） | — |
| §7 分支策略 | **直接提交 `main`**（原计划分支 `uiux/fluent-refactor` 未采用） | 用户已确认 GitHub 备份可回退；提交仍按批次原子化 |
| 只动 UI 层 | 例外：`4ab8ff4` 含 NetMapLib 延迟优化（独立提交，可单独回退） | 用户验收通过 |

### C. 清理轮记录（2026-10-14）

- **死代码**：删 `Converters/ModuleItemTemplateSelector.cs`、`ViewModels/DashboardModuleItem.cs`（4 个类、全项目 0 引用）；`ShellViewModel.ShowCloseMenu`（只写不读）及其在 `GeneralPage` 的 Toggled handler；`DashboardListItem.DashboardModuleItems` 空集合链。
- **令牌残留**：`Colors.xaml` 删 8 个 alias 空壳族（KitContentLayer / KitSurface / SolidBackground / MetricCard* / PillBadge*）；页面归一到系统刷（PillBadge → `SubtleFillColorSecondaryBrush` / `CardStrokeColorDefaultBrush`，AIHub 32 + Localserver 5 + UDPtest 2 处）；`MonoFontFamily → KitMonoFontFamily`；`MetaLabel / MicroLabel / Metric*` 本地样式 → Kit 共享样式；ShortcutConflictWindow 本地渐变上收 `KitWindowsLogoGradientBrush`；CheckUpdate 金/绿渐变 → 顶层 Color 令牌；InfoBadge `#F09A2A` → `KitUpdateBadgeBrush`。
- **死 resw 键**：en/zh 各删 **168** 键（1248→1080 / 961→793；146 个基键 = Localserver 39 + AIHub 旧布局 106 + NetMap 1）。保留 `AiHub_Segment_SecurityAudit/Optimization`（MRT 大小写不敏感命中）、`Localserver_State*`（插值 `$"Localserver_State{suffix}"`）与全部 `NetMap_*`（`Text(shortKey)` 助手）。首轮大小写敏感守卫曾误删 Segment 键，已回滚并改用大小写不敏感复核（PRI277 教训）。

### D. 剩余 backlog

1. UDPtest 字符串迁移：UDPtestViewModel 57 + UDPtestLineRowViewModel 11 处 IsChinese → resw。
2. AIHubPageViewModel 232 处 IsChinese → resw。
3. AiHubViewModel 余 13 处英文状态 → resw。
4. HealthStyles 共享化（DashboardEgressStyles 与 NetMapHealthStyles 去重）。
5. NetMap 死键 ~18：待 `Text(shortKey)` 助手改造或白名单机制后清理。
6. 手工过检：浅色/深色/高对比 × 每页（自动化未覆盖的视觉抽查）。
7. **单测遗留 16 项失败**（基线 `4da08bd` 即红，非重构/清理轮引入；建议逐族更新断言或修复源码）：
   - 实验特性移除遗留 ×6：GpoPolicySurface / GpoPolicyAssets / SettingsXamlNamedElements / GeneralPage 两处 Experimentation 断言 / UpdateSettingsDefaultOff —— b108d90（09-25）移除实验特性后，测试与 runner `general_settings.cpp` 未同步；
   - 版本与文档元数据 ×2：`src/PackageIdentity/AppxManifest.xml` 版本停留 2.3.7（未随 2.3.8–2.3.11 递增）；README 措辞由「Current Kit version」改为「Current source version」；
   - GeneralPage 结构断言 ×2：Startup & permissions 与 Appearance & behavior 顺序、VersionAndUpdate 段 Substring 边界；
   - 品牌与文案 ×3：Intro 文案、Dashboard PT.png 资产引用（2.3.7 起替换）、`Kit\Backup` 字面量；
   - 模块目录 ×2：NetMap 激活后 `KitModuleCatalog.ActiveModules/DashboardModules` 与 enabled-states 序列化均为 6 项，断言仍为 5 项；
   - 运行器启动清理 ×1：`settings_window.cpp` LExit 清理块含 `WM_CLOSE`，断言要求不含。
8. `AiHub_PipelineCard` 残留 2 键：生产代码零引用（仅单测反向断言字符串命中），可随测试改造一并清理。

### E. 验证证据（2026-10-14 清理轮）

- 工具备注：烟雾脚本必须用 **pwsh 7** 运行（Windows PowerShell 5.1 无法加载 .NET 10 SDK 的 HostWriter 类型）；单测用 MSTest 直接运行器 `Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.exe`（本机 vstest.console 18.11.0 的 testhost 启动即崩溃：OpenProcess 父进程 Access Denied）。
- 构建：`pwsh tools\build\build.ps1 -Platform x64 -Configuration Debug -Path "src\settings-ui"` → **exit 0**（修复 3 处 StyleCop 空白错误后）。
- 烟雾：ModulePageSmoke、DashboardPageSmoke（en-US、zh-CN）、NetMapPageSmoke → **全部 PASS**。
- 单元测试：212 项 **196 通过 / 16 失败**；对照实验（同一测试 DLL 分别在清理轮工作树、HEAD、基线 `4da08bd` 三态运行）证明 16 项失败在基线即已存在，与批次 0–6 及清理轮无关（分族见 D-7）。

---

## 0. 背景与目标

- 上一轮 AI（gemini/gpt）产出的 UI 存在明显拼凑感：硬编码尺寸/颜色、各页风格割裂、共享样式缺失。
- 本方案做三件事：**① 建立统一设计基础（令牌+样式族）→ ② 全框架/全页面替换到位 → ③ 用既有烟雾测试与单测守住行为契约**。
- 交付标准：任一页面在浅色/深色/高对比下观感一致；按钮/圆角/字号/间距/动画全部来自共享定义；无新增硬编码色值；中英双语文案齐备。

## 1. 范围与硬约束

### 1.1 范围（确认版）

| 层 | 对象 |
|---|---|
| 主框架 | `SettingsXAML`（App/MainWindow/ShellPage/TitleBar/Controls/Styles/Themes）、Dashboard、General、QuickAccess.UI、Search 结果模板 |
| 模块页 | Awake、LightSwitch（官方插件，一并纳入）、Localserver、UDPtest、NetMap、AIHub（含 **Optimization** 专项） |
| 轻量项 | Runner 托盘菜单的 UI 层清理（可选，最后做） |

### 1.2 路径澄清（重要）

- **实时编译树 = 仓库根 `src/`**（`Kit.*` 命名；`Kit.slnx` 引用 `src/...`；近期提交全部落在 `src/`；烟雾测试编译产物在根 `x64\<Config>\WinUI3Apps`）。
- `Source\PowerToys\` 是**上游 PowerToys 完整参考快照**：被 `.gitignore:234 (/Source/)` 忽略、无 git 历史、0 个跟踪文件。**只作设计参考，绝不修改**。
- Awake/LightSwitch 的实时页面位于 `src\settings-ui\Settings.UI\SettingsXAML\Views\AwakePage.xaml`、`LightSwitchPage.xaml`；模块实现在 `src\modules\awake\`、`src\modules\LightSwitch\`。重构全部落在实时树。

### 1.3 不可动清单（核心边界）

- IPC / 命名管道：`src\common\interop\*`、`AiHubIpcBridge`、`ShellPage.SendDefaultIPCMessage` 调用点；`src\runner\ai_hub_ipc.*`。
- 设置模型与序列化：`src\settings-ui\Settings.UI.Library\*Settings.cs / *Properties.cs / Snd*.cs / SettingsSerializationContext`。
- 共享引擎与服务：`src\common\AiHub\*`、`src\modules\*\*Lib\*`（含 `CacheCleanupService`、`OptimizationAnalysisService`、`NetMapLib`、`LocalserverLib`、`UDPtestLib`、`Awake.ModuleServices`、`LightSwitchLib`）、Worker/ModuleInterface。
- 契约清单：`KitKnownModules`（`src\runner\main.cpp`）、`KitModuleCatalog`、`App.GetPage`、`ShellPage` 导航注册、`SettingsDeepLink`。
- ViewModel 允许改动范围：**仅显示文案（迁 resw）、可见性属性、纯展示元数据**；不改任何业务行为、命令逻辑、计时器、IPC 发送。

### 1.4 测试契约（重构必须保绿）

- `tools\tests\SettingsUI\ModulePageSmoke.cs`（真实 WinUI 导航）：Awake/LightSwitch/Localserver/UDPtest/AIHub 五页；钉死元素名与 AutomationId（例：`AuditWorkflowProgress`、`OptimizationWorkflowProgress`、`AuditScoreCard`、`AiReadinessIcon`、`AIHub_RecheckAiButton`、`AIHub_ScanDownloads`、`AIHub_CleanAll`、`AiTaskPolicyStatus`…）、Optimization 指标文本 `"7"/"3"/"14 B"`、家族顺序 `development,packages,editors,browsers,system`、虚拟化上限（<30 realized rows / <100 file rows / ≤2 ListViews）、主题调色稳定性与对比度（文字 ≥4.5、状态 ≥3）、960/760 折行阈值、`FindingDetailsDialog` 的 `SeverityBadge/SeverityTextBlock`。
- `Settings.UI.UnitTests\ViewModelTests\AiHubSettingsTests.cs`：**以文本方式扫描** `AIHubPage.xaml`，断言 `x:Uid="AIHub_EnableSettingsCard"`、`AutomationId="Toggle_AIHub"`、`AiReadinessGlyph`、`x:Name="AiTaskPolicySegmented"`、`x:Name="AiTaskPolicyContentBox"`、`x:Uid="AiHub_SavePolicyButton"` 等必须存在；`GeneralPage.xaml` 反向断言（kernel/endpoint/`AiPolicyRulesExpander` 必须在 General 页）。同类源码扫描：`Localserver.cs`、`UDPtest.cs`。
- `tools\tests\SettingsUI\DashboardPageSmoke.cs`：Dashboard + General AI 面板控件名（`Toggle_AiHub`、`AiKernelSettingsExpander`、effort 组合恰为 low/high/max）、NetMap egress 文案与侧栏图标对比度、QuickAccess 图标。
- `tools\tests\NetMap\NetMapPageSmoke.cs`：**现状全量断言已破损**（3c57836 重写后按钮内容变 StackPanel、`DetectionCard` 已移除、64-DIP 工具条断言失效，仅 `-LifecycleOnly` 在跑）——NetMap 批次必须同步修复该测试。
- `LightSwitch.UITests`：钉 `Toggle_LightSwitch` AccessibilityId。
- **规则：一切 x:Name / AutomationId / x:Uid 键在重构中保持不变**；确需移动元素（AIHub 拆分）时同步增强测试查找器（见 §4.6），不改断言语义。
- 内嵌搜索索引 `Assets/Settings/search.index.json` 依赖页面头部/卡片 x:Uid（如 `NetMap_EnableCard`）；保持键名即保索引有效；若确需改键，必须同步重建（`Settings.UI.XamlIndexBuilder`）。

---

## 2. 设计基础（Design Foundation）— 批次 0 交付物

### 2.1 材质与色彩（Mica）

**窗口**：`MainWindow.xaml` 保持 `winuiex:WindowEx` + `MicaBackdrop Kind="BaseAlt"`；QuickAccess 浮窗保持 `DesktopAcrylicBackdrop`（原生 flyout 材质）。不新增自定义材质。

**令牌收敛**（`SettingsXAML/Themes/Colors.xaml`，全项目唯一色彩定义处）：

| 旧令牌 | 现状值 | 目标 |
|---|---|---|
| `KitContentLayerBrush` | LayerFillColorDefault @35% 不透明 hack | 删除，直接用系统 `LayerFillColorDefaultBrush` |
| `KitSurfaceBrush` | = CardBackgroundFillColorDefault | 删除，直接用系统 `CardBackgroundFillColorDefaultBrush` |
| `MetricCardBackgroundBrush` | #0EFFFFFF / #70FFFFFF | 删除 → 卡片容器用 `CardBackgroundFillColorDefaultBrush`；内嵌行用 `SubtleFillColorSecondaryBrush` |
| `MetricCardStrokeBrush` | #1CFFFFFF / #18000000 | 删除 → `CardStrokeColorDefaultBrush` |
| `MetricCardHoverBackgroundBrush` | #18FFFFFF | 删除 → hover 用 `ControlFillColorSecondaryBrush` |
| `PillBadgeBackgroundBrush` | #16FFFFFF / #55FFFFFF | 删除 → `SubtleFillColorSecondaryBrush` |
| `PillBadgeBorderBrush` | #28FFFFFF / #1E000000 | 删除 → `CardStrokeColorDefaultBrush` |
| `BadgeSuccess*/BadgeWarning*/BadgeInfo*` | 硬编码 hex（已有 HC 变体） | 保留键名，值改由系统语义色派生：Success→`SystemFillColorSuccess*`、Warning→`SystemFillColorCaution*`、Info→`SystemFillColorAttention*`；对比度以烟雾断言为准，不达标时保留微调值但仅在此一处定义 |
| `InfoBarInformationalSeverity*` | 硬编码 hex override | 删除 override，用系统 InfoBar 默认；LightSwitch 两处「InfoBar 卡片」hack 同步改结构（§4.2） |
| `SolidBackgroundBrush` | Black/White | 查引用后替换为系统 `SolidBackgroundFillColorBaseBrush` 或删除 |
| **新增** `KitDataSeriesTcpBrush` / `KitDataSeriesUdpBrush` | —（UDPtest 页面内 hex） | Dark/Light/HC 三套；HC 用 `SystemColorWindowTextColor` |
| **新增** `KitMonoFontFamily` | 4 处各写一份 | `Cascadia Code, Cascadia Mono, Consolas, Courier New`（统一值，UDPtest 反序写法废弃） |

**迁移策略（alias-first）**：批次 0 先在 `Colors.xaml` 内把旧令牌的**值**替换为系统刷（保留键名，页面零改动即获新观感、烟雾可先行验证）；各页面批次再改为直接引用系统刷；批次 7 删除空壳令牌。

**页面内硬编码色清零**（当前非 obj XAML 共 83 处 hex）：
- `GeneralPage.xaml` 36 处（6 个分类 chips 的 18 色 ×3 主题）→ 删除整个 `Section*Fill/Hover/PressedColor` 字典；chips 改中性风格（§3.4）。
- `UDPtestPage.xaml` 2 处（`TcpBrush #FFFF745A` / `UdpBrush #FF20C9A6`）→ 新语义令牌。
- 其余（Styles/Controls 内）逐处映射到系统刷。

### 2.2 排版与字阶

统一到 WinUI 标准字阶 + 3 个 Kit 专用样式（`Styles/TextBlock.xaml`）：

| 用途 | 样式 | 规格 |
|---|---|---|
| 页面标题 | `TitleTextBlockStyle`（系统） | 28 SemiBold（保持） |
| 分组标题 | `BodyStrongTextBlockStyle`（系统）+ `HeadingLevel=Level2` | 14 SemiBold |
| 卡片标题 | SettingsCard 自带 | 14 |
| 正文 | `BodyTextBlockStyle` | 14 |
| 次级/说明 | `CaptionTextBlockStyle` 或 `SecondaryTextStyle`（已有，12） | 12 |
| **新增** `KitMicroLabelTextStyle` | 微标签（合并 Localserver `MetaLabelStyle` / UDPtest `MicroLabelStyle` / NetMap `MicroLabelStyle` 三份重复） | 10 SemiBold，CharacterSpacing 60，Secondary 前景 |
| **新增** `KitMetricHeroTextStyle` | Hero 数字（健康分 26、活动统计 22 等） | Mono 24 Bold，Tabular |
| **新增** `KitMetricTextStyle` | 卡片指标（IP、端口、大小） | Mono 20 SemiBold，Tabular |
| **新增** `KitMetricCompactTextStyle` | 行内数字 | Mono 14 SemiBold，Tabular |

- 删除页面内全部 FontSize 字面量（现散布 9/10/11/13/15/16/18/22/26），映射：9/10→10（micro）、11→12（caption）、13→14（body）、15/16→16（卡片头）、18→20 或 Subtitle 样式、22/26→KitMetricHero。**例外**：胶囊徽章内 10-11 保留（徽章字阶独立）。
- 数字型文本统一 `Typography.NumeralAlignment="Tabular"`。

### 2.3 间距与圆角

- 圆角规范：**控件 = `ControlCornerRadius`(4)**；**卡片/容器 = `OverlayCornerRadius`(8)**；**细条 = 2**（高 4 进度条）；**胶囊徽章 = 高度一半**（28 高 → 14）；**圆形图标底 = 宽高一半**（28/40/48/52 圆的 13/14/24/26 属此类，保留语义）。
  收敛表：3→4；6→4（控件）或 8（容器）；10→8；11/12/13→按语义归入上两类；17 个 `CornerRadius="2"` 保留。
- 间距规范：卡片内 Padding 统一 **16,14**（紧凑瓦片 12,10；表格行 16,10）；区块间距 16；页面 Section 间距 24（General 现状保留）；`SettingsCardSpacing=2` 保持。
- InfoBar 删除一切 `CornerRadius="8"` 覆盖（系统默认 4）——General 4 处、Localserver/UDPtest/AIHub 各处。

### 2.4 按钮规范（样式族，`Styles/Button.xaml` 新增）

| 样式 | 规格 | 用途 |
|---|---|---|
| `KitActionButtonStyle` | BasedOn DefaultButtonStyle；MinHeight=32、Padding=12,4、CornerRadius=ControlCornerRadius | 全项目默认操作按钮（替代散落的 `MinHeight=32 CornerRadius=6 Padding=14,5`，当前 AIHub 19 处、General 17 处等） |
| `KitPrimaryActionButtonStyle` | BasedOn AccentButtonStyle；同尺寸 | **每个可见区块（工具条 / Tab / 卡片组）至多一个 Accent 主操作**。保留 Accent 清单：General（Save endpoints、Save policy、Check for updates、Apply kernel）；Localserver（Add service、行内 Save）；UDPtest（Start、行内 Save）；NetMap（Start、UpdateAsn、Apply）；AIHub（FullScan、CleanAll、Save policy） |
| `KitIconButtonStyle` | 32×32、Padding=0、CornerRadius=4 | 图标按钮（必须 ToolTip + `AutomationProperties.Name`，双语 resw） |
| `KitSubtleIconButtonStyle` | BasedOn SubtleButtonStyle；32×32 | 行内次要图标按钮（配置展开、更多选项） |
| `KitCompactToolbarButtonStyle` | 28 高（图标或带字） | 抽屉/工具条（日志抽屉、hero 刷新条） |
| `KitFilterPillToggleStyle` | ToggleButton；Height=28、CornerRadius=14、Padding=12,0 | AIHub 滤镜胶囊（补 `AutomationProperties.Name`） |
| `KitSectionChipButtonStyle` | DefaultButtonStyle；MinHeight=36、MinWidth=96、Padding=12,4、CornerRadius=4、SemiBold、BorderBrush=CardStroke | General 分类导航 chips（替代彩色块） |

- 危险操作不新建样式：默认按钮 + `Foreground=SystemFillColorCriticalBrush`（保留现状语义，Force Kill / Delete / Stop all / Clear）。
- 按钮状态（hover/pressed/disabled）全部由系统样式提供；**删除页面上所有 `Button.Resources` 的 `ButtonBackground*` 覆盖**。
- 图标按钮的图形规范：操作类 FontIcon 12-13px；装饰图标 `AccessibilityView="Raw"`。

### 2.5 动画与交互反馈规范（逐按钮 / 逐折叠 / 逐下拉）

**A. 总原则**
- 一律使用 WinUI 系统过渡与状态动画；**禁止自定义 Storyboard**。唯二保留的自定义动画：`FlyoutMenuButton` 图标缩放（QA 瓦片）与 `UsageProgressBar` 数值动画（450ms CubicEase）。
- 所有过渡尊重系统「动画效果」开关（`UISettings.AnimationsEnabled`）；时长/缓动不自定义，一律用系统值。
- 下拉/浮出层（Flyout、MenuFlyout、ComboBox 弹出、ContentDialog）**全部保持系统开合动画**（锚点展开 + 淡入 / 淡出收回）：不设置任何自定义 `FlyoutPresenterStyle`/`Transition`，确保主题与 HighContrast 由系统接管。

**B. 按钮状态反馈（逐类）**

| 按钮类 | 状态反馈（系统行为） | 目标 |
|---|---|---|
| 操作按钮（KitAction / KitPrimary） | hover=ControlFillColor 换色；press=深一档；disabled=禁用前景 | 删除页面级 `Button.Resources` 覆盖后由系统统一；消除"个别按钮 hover 无反馈/颜色不一致" |
| 图标/次级按钮（KitIcon / KitSubtleIcon） | hover=SubtleFillColorSecondary；press=Tertiary | 同上 |
| 危险按钮（Delete / Force Kill / Stop all） | 默认按钮状态 + `SystemFillColorCriticalBrush` 前景 | 保留语义；确认 hover/press 下前景仍为 critical |
| ToggleSwitch（行内 40×36） | 滑块位移（系统动画） | 保留；仅统一尺寸（40×40→40×36） |
| 滤镜胶囊 ToggleButton（KitFilterPill） | 选中/未选中换色（系统） | 统一 28 高 / 圆角 14；补 `AutomationProperties.Name` |
| Segmented（AIHub 分页、策略链） | 选中指示条系统动画 | 保留 |
| AnimatedIcon（TitleBar 后退/汉堡、General 导航项） | Normal/PointerOver/Pressed 矢量动画 | 保留 |
| HyperlinkButton | 系统颜色态 | 保留 |
| FlyoutMenuButton（QA 瓦片） | hover 图标 scale→1.06；press→0.92；背景 83ms BrushTransition；disabled opacity 0.4 | 保留（已是原生级），尺寸令牌化 |
| 周期按钮（KitPeriodButton，1m/5m/30m/1h） | 系统状态 | 新增样式统一（宽 42、高 26） |
| Section 导航 chips（General） | 系统状态（删除彩色覆盖后） | 中性化后自动获得一致的 hover/press |

**C. 折叠 / 展开清单（逐处）**（机制：设置类折叠 → `SettingsExpander` 即时展开、邻居位移由 `RepositionThemeTransition` 承担；数据类分组 → `Expander`；AIHub 保持 `x:Load` 惰性展开——虚拟化契约）：

| # | 位置 | 现状 | 目标 |
|---|---|---|---|
| 1 | Shell 页面切换（Frame） | `NavigationThemeTransition` | 保留 |
| 2 | 全部页面卡片入场 | `SettingsCardsAnimations`（Entrance FromVerticalOffset=20 阶梯 + Reposition） | **唯一入场动画**；删除 Dashboard 内联 ×2（行 105-111、181-186）与 AIHub 内联 ×8（三个 tab 体、AuditWorkflowCard、三处空态、AuditAiReport 等，含自定义 16px 偏移版）→ 全部改引用共享资源 |
| 3 | Localserver/UDPtest 表格行入场 | 内联 Reposition+Entrance（12px 偏移，3 处） | 改引用 `SettingsCardsAnimations` |
| 4 | General 9 个 SettingsExpander（ShowSystemTrayIcon、EnableQuickAccess、AdminMode、AiKernel、AiMainEndpoint、AiFallback、AiPolicyRules、Diagnostics、BackupAndRestore） | 即时展开 + Reposition | 保留（仅统一卡内控件样式） |
| 5 | LightSwitch 2 个 Expander（Mode、ApplyDarkMode，均 IsExpanded=True 常开） | 即时 | 保留 |
| 6 | Awake Expiration Expander（IsExpanded=True） | 即时 | 保留 |
| 7 | NetMap 设置 Expander + 嵌套 DataUpdate Expander | 即时 | 保留 |
| 8 | AIHub 数据 Expander（FindingSection、AuditActivity、UserDirectory、CacheGroups、Category/Project 模板） | 即时 + x:Load 惰性 | 保留机制；统一头/内容样式 |
| 9 | Localserver/UDPtest 行内配置面板（Border Visibility 切换，非 Expander） | 即时 | 保留（表格行语义），邻居动画由行 StackPanel 的 Reposition 承担 |
| 10 | LightSwitch VSM 模式卡显隐（Off/Manual/Sun/FollowNightLight） | 即时 | 保留 |
| 11 | QuickAccess `FlyoutMenuButton` hover/press（图标 scale 1.06/0.92 + 83ms 背景过渡） | 已原生级 | 保留，尺寸令牌化（116×84、CornerRadius=OverlayCornerRadius） |
| 12 | `UsageProgressBar` 数值动画（450ms CubicEase） | 已实现 | 保留 |
| 13 | 进度条 indeterminate（审计/优化扫描） | 系统默认 | 保留 |
| 14 | 禁用态透明度（`BoolToDisabledOpacityConverter`） | 即时 | 保留（与系统设置一致） |
| 15 | AIHub Segmented tab 切换 | 即时（原生） | 保留；tab 内容入场统一走 #2 |
| 16 | LightSwitch Timeline | 静态绘制 | 保留 |

**D. 下拉 / 浮出层清单（逐处：打开与收回）**

| # | 位置 | 控件 | 打开/收回（现状） | 目标 |
|---|---|---|---|---|
| 1 | Shell 搜索框 | AutoSuggestBox 建议下拉 | 系统淡入/收起 | 保留；仅改结果行样式 |
| 2 | Dashboard / AppsList 排序按钮 | MenuFlyout（2 项） | 系统锚点展开/淡出 | 保留；placement 统一 `BottomEdgeAlignedRight` |
| 3 | AIHub 扫描选项按钮 | MenuFlyout（重新分析 + 全量模式 ToggleMenuFlyoutItem） | 系统 | 保留；Toggle 状态与 AutomationId 保留 |
| 4 | Localserver 行「更多选项」 | MenuFlyout（Restart / Force Kill） | 系统 | 保留；Force Kill 图标 critical 色保留 |
| 5 | Localserver 环境 chips 修复按钮 | MenuFlyout（复制修复命令 / 释放端口） | 系统 | 保留 |
| 6 | Localserver / UDPtest 行内删除 | Flyout（内嵌确认按钮） | 系统 | 保留（轻量确认，不升级为 ContentDialog） |
| 7 | UDPtest 采样间隔按钮 | Flyout（内嵌 ComboBox） | 系统 | 保留；嵌套 ComboBox 下拉沿用系统 |
| 8 | NetMap egress「Details」 | Flyout（详情面板） | 系统 | 保留；标签文案迁 resw |
| 9 | 全部 ComboBox 下拉（主题/语言/kernel/端点/日志级别/服务选择/协议 Kind/周期/代理模式… ~15 处） | ComboBox 弹出层 | 系统 | 保留；仅关闭态圆角 6→4 |
| 10 | Awake DatePicker/TimePicker、LightSwitch TimePicker ×2、AIHub CalendarDatePicker | 日历/时间浮出 | 系统 | 保留 |
| 11 | LightSwitch 定位对话框、AIHub 发现详情 | ContentDialog | 系统缩放淡入/淡出 | 保留；按钮/字号按规范 |
| 12 | 各页 InfoBar 关闭 | InfoBar 收起 | 系统 | 保留（IsClosable 绑定不动） |
| 13 | NumberBox 微调、PasswordBox 显隐、TextBox 清除 | 系统 | 系统 | 保留 |
| 14 | QuickAccess 浮窗整体显隐 | Runner 宿主显示/隐藏 + DWM cloak + 鼠标钩子自动收起 | 保留（跨进程宿主行为，不属本次 UI 层） | 不动 |
| 15 | 托盘右键菜单 | 原生 Win32 菜单 | 保留原生 | 不动 |

**E. 页面与窗口级过渡**
- 页面切换：`NavigationThemeTransition` 保留；卡片入场唯一入口为 `SettingsCardsAnimations`；禁用态 opacity 即时切换（与系统设置一致）；QuickAccess 固定 400×516 无尺寸过渡。

### 2.6 徽章 / 状态 / 进度规范

- 状态点（Ellipse 6-8px）颜色只允许：`SystemFillColorSuccess/Caution/Critical/AttentionBrush` + `TextFillColorTertiaryBrush`（未知）。
- 状态胶囊（Border + dot + 文本）：统一 28 高、CornerRadius=14、Padding=10,4、`SubtleFillColorSecondaryBrush` 背景（`KitStatusPillStyle`，NetMap 手写 ~10 处、Localserver/UDPtest hero 条同步）。
- 进度条：高 4、CornerRadius=2、`AccentFillColorDefaultBrush` 前景（统一；NetMap 更新条 6/3 改 4/2）。
- 健康环（88×88 双 Ellipse / ProgressRing）：保留几何，颜色走语义色样式（AIHub `HealthRing*Style`、Localserver/UDPtest 内联 `HealthRingBrush`）。
- 徽章字阶独立（10-11px SemiBold）不受 §2.2 字阶约束。

### 2.7 文案与本地化规范

- **新增/修改文案一律走 resw（en-us + zh-CN 同步）**；本重构触碰区域内的硬编码英文全部迁入：
  - General AI 面板约 20 处（`Header="Kernel updates"`、`Content="Test connection"` 等）、`AiHubViewModel` 未翻译英文状态（"Save failed." 等）；
  - Localserver 约 10 处 tooltip、UDPtest 约 15 处 tooltip/空态、NetMap 4 处 flyout 标签 + 8 处 tooltip + 3 处 automation name + ":" 分隔符、TitleBar "Back"/"Toggle menu" 2 处。
- `IsChinese ? ... : ...` 三元迁移：AIHubPageViewModel ~226 处、UDPtest ~57+11 处、OptimizationCategory/GroupViewModel 全量。**分批**：本重构触碰的 Optimization 相关与 UDPtest 全量在本方案批次内完成；其余 AIHub 文案列为独立后续批次（避免一次性巨大 diff）。
- 死键清理（确认无引用后 en/zh 同步删）：Localserver ~50、NetMap 19（含 3c57836 "dead on arrival" 3 键）、AIHub 旧布局遗留键（`AiHub_PipelineCard` 等）。
- **PRI277 规则**：`AiHub_` 与 `AIHub_` 前缀不得定义同一逻辑键；新增键沿用所在区域既有前缀，迁移重命名时逐一核对（MRT 大小写折叠）。

### 2.8 可访问性规范

- 一切 icon-only 按钮：`ToolTipService.ToolTip` + `AutomationProperties.Name`（resw）。
- 手写分组标题补 `AutomationProperties.HeadingLevel="Level2"`：Localserver（Environment/System/Session 卡头）、UDPtest（全页缺）、NetMap（全页缺）、AIHub（findings 区已部分有）。
- 装饰元素补 `AccessibilityView="Raw"`（当前多处缺失）；图表（HealthChartCanvas、CPU trend、Map）补 `AutomationProperties.Name/HelpText` 摘要。
- 动态状态文本补 `AutomationProperties.LiveSetting="Polite"`（NetMap 已有 3 处，推广到 Localserver/UDPtest 状态区）。

---

## 3. 主框架规格

### 3.1 TitleBar（`Controls/TitleBar/TitleBar.xaml` + `.cs`）

- 删除 `PART_BackButton.Visibility` 重复 Setter（xaml 行 122-123）。
- ToolTip "Back"/"Toggle menu" → 在 `TitleBar.cs` 从 resw 读取赋给 PART_BackButton/PART_PaneButton（模板无法直接 x:Uid）。
- 后退/汉堡按钮已用 AnimatedIcon + 系统刷（保留）；显示模式/窗口激活状态机保留。
- 尺寸键（32/48）保留。

### 3.2 ShellPage

- 搜索框：`MinHeight 34→32`、`CornerRadius 8→4`（对齐 Win11 设置）；下拉模板 FontSize 13→14、11→12；结果行 Padding 12,6、CornerRadius 6→4；ShowAll 模板保留。
- 内容层 Border：`KitContentLayerBrush → LayerFillColorDefaultBrush`（令牌删除后）。
- NavigationView 结构、导航项（ImageIcon 显式写法）、Footer AnimatedIcon、OpenPaneLength 等全部保留。
- 死代码清理（确认无引用后删）：`ModuleItemTemplateSelector`、`ShellViewModel.ShowCloseMenu`、`DashboardModuleItems` 空集合、`KitTrayIconWindowClass` 测试专用常量保持（测试引用）。

### 3.3 Dashboard

- 内联入场动画 ×2 → `SettingsCardsAnimations`（§2.5 #2）。
- `OverviewSurfaceStyle`：KitSurfaceBrush → `CardBackgroundFillColorDefaultBrush`；Padding 20→16,14。
- **`DashboardEgressStyles`（行 26-37）删除**：把 `NetMapHealthStyles` 与 `NetMapHealthStyleConverter` 从 NetMapPage 页面资源提升为共享资源（`Themes/HealthStyles.xaml`，App.xaml 合并），Dashboard 与 NetMap 共用；键名 `UnknownText/GoodText/ErrorText` 等保持不变（烟雾断言）。
- `DashboardDetailTemplate`：FontSize 13→14（label 12）；UsageProgressBar Height=4 CornerRadius=2 保留。
- 排序按钮 32×32 → `KitSubtleIconButtonStyle`；`DirectIpText/ProxyIpText` FontSize=20 → `KitMetricTextStyle`。
- 双列 VSM（<600 / <1200 堆叠）保留。

### 3.4 General（逐按钮）

**分类导航 chips ×6（Appearance/Startup/AI/Diagnostics/Backup/Updates）**：
- 删除 18×3 个 `Section*Color` hex 资源与每按钮 3 个 `Button.Resources` 背景覆盖；
- 改 `KitSectionChipButtonStyle`（中性背景 + `AccentTextFillColorPrimaryBrush` 图标）；`AutomationProperties.Name`/ToolTip 绑定保留；
- 删除页面级 `SettingsCardBackground`/`ExpanderContentBackground` 主题覆盖（行 41-42、63-64、85-86）→ SettingsCard 回到系统默认卡面。

**逐按钮映射**（现状全部为内联 `MinHeight=32 CornerRadius=6 Padding=…`）：

| 按钮 | 目标样式 | 备注 |
|---|---|---|
| LanguageRestartInfoButton | KitActionButtonStyle | InfoBar 内 |
| RestartAsAdmin | KitActionButtonStyle | 保留命令绑定 |
| AI: Apply（kernel） | KitPrimaryActionButtonStyle | Accent 保留 |
| AI: Check for updates / Download or update | KitActionButtonStyle ×2 | 图标+文字结构保留 |
| AI: Test connection ×2 | KitActionButtonStyle | 主/备端点 |
| AI: Clear fallback | KitActionButtonStyle | |
| AI: Discard / Save endpoints | KitActionButtonStyle / **KitPrimaryActionButtonStyle** | Save 为 Accent |
| AI: Reset / Save policy | KitActionButtonStyle / **KitPrimaryActionButtonStyle** | |
| Open logs folder | KitActionButtonStyle | |
| Backup / Restore | KitActionButtonStyle ×2 | |
| Select folder（34×34 图标按钮） | KitIconButtonStyle（32×32） | 保留 ToolTip |
| Check for updates（版本卡） | **KitPrimaryActionButtonStyle** | Accent 保留 |

**其他**：
- 输入控件 `CornerRadius=6 → 4`；policy TextBox `FontFamily="Consolas, Cascadia Code" → KitMonoFontFamily`。
- AI 面板全部硬编码 `Header=`/`Content=`/`PlaceholderText` 英文字面量迁 resw（§2.7）；`AiHubViewModel` 英文状态迁 resw 双语。
- 修复 `" </tkcontrols:SettingsCard>"` 格式瑕疵（~行 541）及同类空格残留。
- `QuickAccessShortcut` 隐形卡 HACK 保留（CommunityToolkit 缺陷 workaround），补充注释。

### 3.5 QuickAccess.UI

- `LaunchPage`：MoreBtn（默认样式）→ `KitActionButtonStyle`；Docs/Settings 按钮（`FlyoutButtonStyle`）→ `KitSubtleIconButtonStyle`；标题区留白保留。
- `AppsListPage`：排序/返回按钮统一到共享样式。
- `MainWindow`：400×516 固定、Acrylic 底、无标题栏——**保留**（产品设计如此）。
- `FlyoutMenuButton` 主题（`Settings.UI.Controls/Themes/Generic.xaml`）：`CornerRadius=8 → OverlayCornerRadius`；hover/press 动画与禁用透明度保留。
- **待定（默认不动）**：Dashboard QA 卡（全 6 模块）与浮窗 QA（仅 LightSwitch）的语义差异——属信息架构决策，默认保持现状。

### 3.6 Runner 托盘（轻量、可选，最后做）

- `tray_icon.cpp`：删除 `ID_ABOUT_MENU_COMMAND` 死处理器（.rc 中 About 已注释）；托盘图标"命名反转"（PowerToysWhite 用于深色主题）加注释说明，不重命名文件。
- `Resources.resx` 英文串保持机制不动；不改 `quick_access_host`、`settings_window`、`general_settings` 内部逻辑。

---

## 4. 模块页规格（逐按钮 / 逐折叠）

### 4.0 通用骨架（6 页统一）

1. **页头**：`SettingsPageControl`（`ModuleTitle` + `ModuleDescription` + `ModuleImageSource` 64×64 + `PrimaryLinks`=LearnMore）。补全缺口：Localserver/UDPtest/NetMap 补 `ModuleImageSource`（资产已存在：`Assets/Settings/Modules/<Name>.png`）；NetMap 补描述；AIHub 若无 Modules 资产则保持不设。核对每页 `x:Uid` 与 resw `<Name>.Title/.Description` 键的对应关系，补齐或移除无用 x:Uid。
2. **启用卡**：`GPOInfoControl`（NetMap 例外保持无 GPO 包裹）+ SettingsCard + ToggleSwitch（`AutomationId="Toggle_<Name>"` 全部保留）。AIHub/Localserver/UDPtest/NetMap 的 hero 结构（启用卡 + 遥测子条同卡）保留结构，子条背景 `MetricCardBackgroundBrush → SubtleFillColorSecondaryBrush`。
3. **卡片容器**：新建 `KitCardSurfaceStyle`（Border：CardBackgroundFillColorDefaultBrush + CardStrokeColorDefaultBrush + CornerRadius=OverlayCornerRadius + Padding=16,14），替换 4 个模块页手写重复的卡 Border（每页 5-10 处）。
4. **状态 InfoBar**：统一为系统默认外观（删 CornerRadius 覆盖）。
5. 按钮/字号/圆角/间距/动画全部按 §2 令牌与样式族替换。

### 4.1 Awake（baseline 校验页，改动最小）

已是最标准的 SettingsCard 页。执行项：ComboBox `MinWidth=SettingActionControlMinWidth` 保留；NumberBox Width=96 保留；无按钮改动；唯一动作 = 跑烟雾确认批次 0 令牌替换无回归。作为"参考实现"写入文档。

### 4.2 LightSwitch

- **删除注释掉的 `AutoSuggestBox` 死块**（行 226-252）。
- 按钮：SetLocationButton / FindLocation(SyncButton) → `KitActionButtonStyle`；NightLightSettingsButton（Hyperlink）保留；LocationDialog 主按钮 Accent 保留；Latitude/Longitude NumberBox 124 宽保留；SyncLoader ProgressRing 40 保留。
- **InfoBar 卡片 hack 改造**（`NoScheduleCard`、`FollowNightLightCard`：`Padding=0 + Background=InfoBarInformationalSeverityBackgroundBrush + InfoBar Background=Transparent BorderThickness=0`）→ 直接以 InfoBar 为 SettingsCard 内容，删除背景 hack；`LocationWarningBar` 保留。
- VSM 模式显隐（§2.5 #10）保留；`ModeSelector_SelectionChanged` 逻辑不动。
- 清理 `LightSwitch_FindLocationAutomation` 双 x:Uid（Button 与内嵌 TextBlock 二选一）。
- `Toggle_LightSwitch` AutomationId 契约保留。

### 4.3 Localserver

**按钮**：

| 按钮 | 目标 |
|---|---|
| Add service | **KitPrimaryActionButtonStyle**（Accent，本屏主操作） |
| Start all / Stop all | KitActionButtonStyle（success/critical 图标保留） |
| Refresh（hero 28 高） | KitCompactToolbarButtonStyle |
| Refresh（工具条 32×32 图标） | KitIconButtonStyle |
| 行内配置展开/更多选项（32×32 Subtle） | KitSubtleIconButtonStyle（Flyout 保留） |
| Force Kill 菜单项图标 | critical 色保留 |
| Browse（可执行/目录） | KitActionButtonStyle |
| Delete（含 Flyout 确认） | 危险样式（默认+critical fg）保留 |
| Save（Accent） | **KitPrimaryActionButtonStyle** |
| 日志抽屉：toggle 30×30、follow ToggleButton 28、open folder 28×28、clear 28×28 | KitCompactToolbarButtonStyle 家族；ToolTip 全部迁 resw |
| 环境 chips 修复按钮 22×22 | 保留微尺寸 + 补 AutomationProperties.Name |

**其他**：行内 ToggleSwitch 统一 40×36；配置面板 Border `CornerRadius 6→4`、内嵌输入 6→4；表格头 Padding 17,10→16,10；Tag/ADOPTED 徽章 3→4；ComboBox 180×32 CornerRadius 6→4；CPU trend 卡（Height=50）保留 + a11y；空态卡保留。
**文案/清理**：~10 处硬编码 tooltip 迁 resw；VM 内英文（StatusChartRangeText、GPU 回退文案、状态动态键）迁 resw；死 resw 键 ~50 清理；死 handler（ReloadCatalog/MoveUp/MoveDown）删除。
**a11y**：Environment/System/Session 卡头补 Level2；图表补摘要文本。

### 4.4 UDPtest

**按钮**：

| 按钮 | 目标 |
|---|---|
| Start（Accent） | **KitPrimaryActionButtonStyle** |
| Stop | KitActionButtonStyle（critical 图标保留） |
| Clear history / Interval（32×32） | KitIconButtonStyle（Flyout 保留） |
| Add UDP Link | KitActionButtonStyle |
| 周期按钮 1m/5m/30m/1h（36/42×26） | 新 `KitPeriodButtonStyle`：宽统一 42、高 26、CornerRadius 4（消除 36/42 混用） |
| UdpExpandButton（x:Name 契约保留） | KitSubtleIconButtonStyle |
| Delete / Save | 同 Localserver 规范 |

**其他**：`TcpBrush/UdpBrush → KitDataSeriesTcpBrush/UdpBrush`（含 HC）；MonoFontFamily 统一；`CornerRadius=6→4` 全页输入控件；ToggleSwitch 40×40→40×36；ComboBox 140×32 补 `AutomationProperties.Name`；表格动作列宽以烟雾为准微调统一（80 vs 60 收敛）。
**文案**：VM `IsChinese` ~57+11 处全量迁 resw（本批次完成）；code-behind 空态文案（"No samples in this period"）迁 resw；~15 处硬编码 tooltip/automation 迁 resw。
**a11y**：全页补 HeadingLevel；周期按钮/组合框补 Name。

### 4.5 NetMap

- **移除外层 `ScrollViewer`**（行 216）→ 消除嵌套滚动与重复 `PageScrollViewer` AutomationId（SettingsPageControl 内部已有）。执行后跑 NetMapPageSmoke 验证 `page.Content` 强转路径。
- **Message banner**（行 322 自定义 Border）→ 统一 `InfoBar`（`IsClosable=False`、Informational；`LiveSetting` 保留）。
- **按钮**：Start（Accent）→ **KitPrimaryActionButtonStyle**；Stop → KitActionButtonStyle；Details（28 高→32）→ KitActionButtonStyle；Browse ×2 → KitActionButtonStyle；UpdateAsn（Accent）→ **KitPrimaryActionButtonStyle**；Cancel download → KitActionButtonStyle；Apply settings（34→32，Accent）→ **KitPrimaryActionButtonStyle**。
- **状态胶囊统一**（~10 处手写 pill → `KitStatusPillStyle`）：egress 卡状态 pill、route target pill、latency pill、hop TTL 圆、服务延迟 pill、timestamp pill 等；**保留** `TargetStatusLight` 的 `GoodDot` 样式引用与 `NetMapHealthStyles` 资源键（烟雾断言）。
- **`NetMapHealthStyles` 提升共享**（与 Dashboard 共用，§3.3）。
- 设置区：ProxyMode ComboBox 6→4；数据库 Border 6→4/8 统一；更新 ProgressBar 6/3→4/2；`DataUpdateExpander` 保留。
- **文案**：flyout 标签 4 处（"Source Endpoint:" 等）+ "Local Geolocation Databases" + 8 tooltip + 3 automation name + ":" 分隔符 → resw；死 resw 19 键清理；VM `RouteStatus` 死表面清理。
- **NetMapPageSmoke 修复**（必做）：按钮内容断言改按 `AutomationProperties.Name` 或文本查找；删除 `DetectionCard` 断言（改 hero 条断言）；64-DIP 工具条断言改新布局；保留语言/几何/生命周期断言。
- 建议（可选）：把 NetMap 纳入 `ModulePageSmoke` 覆盖范围。

### 4.6 AIHub（最大目标，含 Optimization 专项）

**A. XAML 拆分（1302 行 → 页面壳 + 3 个 Tab 视图 + 模板资源）**：

```
SettingsXAML/Views/AIHubPage.xaml                （壳：hero + 状态 InfoBar + Segmented + 3 个视图）
SettingsXAML/Controls/AIHub/AIHubAuditTab.xaml   （Tab1：workflow、command header、健康 hero、findings、空态、AI 报告）
SettingsXAML/Controls/AIHub/AIHubOptimizationTab.xaml（Tab2：见下）
SettingsXAML/Controls/AIHub/AIHubTaskPoliciesTab.xaml（Tab3：状态条、编辑器）
SettingsXAML/Controls/AIHub/AIHubTemplates.xaml  （FindingPreview/OptimizationCategory/OptimizationProject 模板 + 页级样式外置）
```

- 每个 Tab UserControl 暴露 `ViewModel` 属性（code-behind 注入），x:Bind 作用域随迁。
- **契约保持**：所有 x:Name / AutomationId 原样保留（`AuditWorkflowProgress`、`OptimizationWorkflowProgress`、`AuditScoreCard`、`AuditScoreText`、`AuditHealthIndicator`、`AiReadinessIcon`、`AuditAiReport`、`AiTaskPolicySegmented`、`AiTaskPolicyContentBox`、`AiTaskPolicyStatus`、`AIHub_*` 系列…）。
- **风险与对策**：`FindName` 不跨 UserControl 边界 → 同步增强 `ModulePageSmoke.cs` 的元素查找为**视觉树递归查找（按名字/ID）**，断言内容不变。同时保留 `AiHubSettingsTests` 源码扫描字符串（键名不动即天然保绿）。

**B. Optimization 专项（用户点名不可漏）**：

| 项 | 现状 | 目标 |
|---|---|---|
| OptimizationWorkflowCard | 与 Audit 工作流卡结构不同（无 glyph、无双行状态） | 与 Audit 版同构化：共享 `KitWorkflowCardStyle`（8/18,14/accent 边）+ 行结构（glyph、标题、Mono tabular 百分比、4px 进度条、状态 caption）；x:Name 保留 |
| 指标瓦片 ×3（Scanned/Approved/Cleanable） | 三段完全相同的 inline Border | 共享 `KitMetricTileBorderStyle` + `KitMetricTileValueStyle`；**保持 inline（不模板化）**以保 `x:Name` FindName 契约 |
| Command header（Rescan/CleanAll/Cancel + 18px ProgressRing） | 内联尺寸 | Rescan/Cancel → KitActionButtonStyle；CleanAll → **KitPrimaryActionButtonStyle**（本 Tab 主操作）；ProgressRing 保留 |
| Downloads 区（Scan/Organize 按钮、路径徽章、分类 ItemsControl） | 内联 | 按钮 → KitActionButtonStyle；路径徽章 → 胶囊规范；分类模板样式收敛 |
| Caches 区（Scan 按钮、UserDirectory Expander、CacheGroups 大 Expander 列表） | 内联 | 同上；分组行内 CleanLabel 按钮 MinHeight 30→32 → KitActionButtonStyle；`OptimizationProjectList`/`OptimizationDirectCategoryList` 的 x:Load/MaxHeight 虚拟化参数**保持**（<30 realized rows 断言） |
| Category/Project 模板（~80% 重复） | 两份独立模板 | 共享子样式（标题/pill/mono 路径行），**模板结构保留**（降低 FindName/虚拟化风险） |
| 展开/收起 | x:Load HasBeenExpanded 惰性 | 保留（契约）；头/详情样式统一 |
| 文案 | OptimizationCategory/GroupViewModel + AIHubPageViewModel Optimization 段 IsChinese 三元 | 迁移 resw（`AIHub_` 前缀），本批次完成 |
| 死管道 | `AuditProgressState` 侧栏进度无绑定 | **默认删除死绑定**；可选增强（侧栏显示审计进度徽章）留待用户拍板 |

**C. 审计 Tab 与通用**：
- 滤镜胶囊（severity ×4、source ×6）→ `KitFilterPillToggleStyle` + 补 `AutomationProperties.Name`（a11y 缺口修复）。
- 搜索框 Height=34→32、CornerRadius 6→4；视图切换/重置按钮统一 32 高。
- `FindingPreviewTemplate`：三段 severity pill 收敛为共享样式（结构保留）；Mica 注释文案清理。
- 空态 ×3、AI 报告卡：内联动画删除（§2.5 #2），样式收敛。
- 健康 hero：`MetricCard* → CardBackgroundFillColorDefaultBrush`（分数卡）/`SubtleFillColorSecondaryBrush`（内嵌）；优先级卡 accent 边保留；HealthScore 环形与指示点保留（对比度断言）。
- 页级样式（Readiness*/Health*/Metric*/Safety*）→ 外置到 `AIHubTemplates.xaml`；`MonoFontFamily → KitMonoFontFamily`。
- `FindingDetailsDialog`：severity badge 样式与 `SeverityBadge/SeverityTextBlock` 契约保留；FontSize 11→12；结构不动。

**D. 文案**：AiHubViewModel 硬编码英文（"Save failed." 等 ~13 处）→ resw 双语；AIHub 死键清理；PRI 前缀冲突核对。

---

## 5. 实施批次（每批原子提交，构建+测试+烟雾全绿再进下一批）

| 批次 | 内容 | 验证 |
|---|---|---|
| **0** | 设计基础：`Styles/Button.xaml` 样式族、`Styles/TextBlock.xaml` 字阶/mono/metric、新增 `Styles/Surface.xaml`（卡面/状态胶囊/指标瓦片）、`Colors.xaml` 令牌 alias-first 收敛（旧键保留、值指向系统刷）+ 新语义令牌、App.xaml 合并 | 构建 + ModulePageSmoke（全页零回归） |
| **1** | Shell + TitleBar + Dashboard + General + QuickAccess | 构建 + 单测 + DashboardPageSmoke + ModulePageSmoke |
| **2** | Awake + LightSwitch（小页先行） | ModulePageSmoke（Awake/LightSwitch 段） |
| **3** | Localserver | ModulePageSmoke + Localserver.cs 单测 |
| **4** | UDPtest（含 IsChinese 全量迁移子提交） | ModulePageSmoke + UDPtest.cs 单测 |
| **5** | NetMap（含 NetMapPageSmoke 修复） | NetMapPageSmoke（全量模式） |
| **6** | AIHub：6a 模板/样式外置 → 6b Tab UserControl 拆分 + smoke 查找器增强 → 6c Optimization 专项 → 6d 文案迁移 | ModulePageSmoke + AiHubSettingsTests + Kit.AiHub.UnitTests |
| **7** | 死代码/死键全量清理 + runner 托盘轻量项 + 文档（DOCUMENT_INDEX/changelog） | 全量回归 |

> `Themes/HealthStyles.xaml`（共享健康样式）不在批次 0 执行，随批次 1（Dashboard）与批次 5（NetMap）进行——移动前先核对 NetMapPageSmoke 对 `NetMapHealthStyles` 资源键的查找方式（页面级 vs 全局链），确保断言不变。

## 6. 验证

```bash
# 构建（exit 0 才算成功；首次或缺包先跑 build-essentials.cmd）
pwsh tools\build\build.ps1 -Platform x64 -Configuration Debug -Path "src\settings-ui"
```

```powershell
# 烟雾（必须先关闭 Kit 进程）
pwsh -File tools\tests\SettingsUI\Invoke-ModulePageSmoke.ps1 -Configuration Debug
pwsh -File tools\tests\SettingsUI\Invoke-DashboardPageSmoke.ps1 -Configuration Debug -Language zh-CN
pwsh -File tools\tests\NetMap\Invoke-NetMapPageSmoke.ps1   # 批次 5 修复后
```

- 单元测试：VS Test Explorer 或 `vstest.console.exe`（**禁止 `dotnet test`**）。
- 手工过检：浅色/深色/高对比 × 每页；键盘导航（Tab 顺序、焦点视觉）；对比度抽查。
- **核心零改动守卫**：`git diff --name-only` 仅允许 `src/settings-ui/**`、`tools/tests/**`、`doc/**`；出现 `src/common/**`、`src/modules/**/…Lib/**`、`src/runner/**`（托盘 UI 文件除外）即回退该提交。

## 7. 回退策略

- 实际直接提交 `main`（原计划分支 `uiux/fluent-refactor` 未采用；用户已确认 GitHub 备份可回退）；逐批次原子提交，`main@4da08bd` 为回退点。
- 每批次提交信息注明批次号与范围，便于单批 revert。

## 8. 风险与对策

| 风险 | 对策 |
|---|---|
| AIHub 拆分导致 FindName 失效 | smoke 查找器改视觉树递归；元素名/ID 零改动 |
| 令牌替换破坏对比度断言 | 批次 0 后先跑全量烟雾；不达标仅回退单个令牌值 |
| PRI277 大小写冲突 | 新键核对前缀；迁移键逐一 grep 双前缀 |
| SettingsExpander 行为差异 | 只改样式不改结构；不改 IsExpanded 默认值 |
| NetMapPageSmoke 既已破损 | 属批次 5 必修项，不作为"新增回归" |
| 构建/测试纪律 | 一次一个终端；错误先读 `build.<config>.<platform>.errors.log` |

## 附录 A：量化基线（重构前）

- 非 obj XAML 硬编码 hex：**83 处**（GeneralPage 36、UDPtest 2、其余 Styles/Controls）。
- CornerRadius 直方图：6×108、8×44、4×34、2×17、14×14、3×7、10×5、0,0,8,8×5、其余个位数。
- `MinHeight="32"` 内联：AIHub 19、General 17（另 Localserver 28 处 6/8 圆角、NetMap 19、UDPtest 18 处内联尺寸）。
- 按钮样式族：`Styles/Button.xaml` 仅有 HyperlinkButtonStyle / TextButtonStyle / RightAlignedCompactToggleSwitchStyle —— **无任何操作按钮共享样式**。
- AIHubPage.xaml 1302 行、AIHubPageViewModel ~3156 行；IsChinese 三元 ~226 处。

## 附录 B：既有缺陷清单（本方案顺带修复）

1. TitleBar 重复 Setter；TitleBar 硬编码英文 tooltip。
2. GeneralPage stray-space 格式瑕疵（f92f2cd 引入）。
3. UDPtest MonoFontFamily 反序；两页 ToggleSwitch 尺寸不一（40×40 vs 40×36）。
4. NetMap 嵌套 ScrollViewer + 重复 PageScrollViewer ID；`xmlns:ui` 未使用；`NetMapLogo.png` 与 `Modules\NetMap.png` 死资产。
5. Localserver 死 resw ~50 键、死 handler ×3、孤儿注释。
6. AIHub `AuditProgressState` 死管道；旧布局死 resw 键；`AiHub_`/`AIHub_` 双前缀混乱。
7. NetMapPageSmoke 全量断言破损（仅 LifecycleOnly 在跑）。
8. Runner：About 死处理器；托盘图标命名反转陷阱。
