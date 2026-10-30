# Kit UI/UX 现代化方案（2026-10 轮）- 壳层 + 6 插件 + QuickAccess + 托盘

**指令来源**：用户 2026-10-29："开始优化 ui/ux。包括 kit 和所有 6 个插件，具体到每一个按钮，充分利用 mica+winui3。"
**基线**：`0960041`（插件缺陷批次提交后）。
**审计方式**：壳层、AIHub / NetMap、Localserver / UDPtest 由子代理只读审计（2026-10-29）；Awake / LightSwitch / QuickAccess 由主会话逐文件审计。全部 8 个界面盘点完毕。
**前身**：v9《Kit UI/UX 优化方案》已随 `cd1c0ba` 删除；其遗留项已并入本稿（§5）。

## 1. 现状盘点

### 1.1 已具备的现代化基础（沿用，不推倒）

- **窗口 Mica**：`SettingsXAML\MainWindow.xaml:13-16` 用 `MicaBackdrop Kind="Base"`；`MainWindow.xaml.cs:183-195` 仅在不支持时回退 DesktopAcrylic。QuickAccess 用 `DesktopAcrylicBackdrop`（`QuickAccess.UI\QuickAccessXAML\MainWindow.xaml:20-22`，被 `BuildCompatibility.cs:3097-3107` 钉死，**勿换类型**）。
- **内容分层链**：透明根（`ShellPage.xaml:92`）→ NavigationView 原生内容层（LayerFill + 发丝线）→ 卡片 `CardBackgroundFillColorDefaultBrush`。
- **设计体系（批次 0 产物，已相当完整）**：
  - `Styles\Button.xaml`：Kit 按钮族 9 种（Action / PrimaryAction / Icon / SubtleIcon / CompactToolbar / CompactIcon / FilterPill / Period / SectionChip，`:656-731`）；另有遗留 `TextButtonStyle`（`:5-77`）与**全仓未使用**的 `RightAlignedCompactToggleSwitchStyle`（`:79-648`）。
  - `Styles\Surface.xaml`：卡面 / 工作流卡 / 状态胶囊 / 指标瓦片 / 进度条 / 图标徽章 / Hero 等 11 个表面样式。
  - `Styles\TextBlock.xaml`：Mono 字体 + 四个排版令牌；`Themes\Colors.xaml`：语义 / 品牌 / 导航色三主题字典；`SettingsXAML\App.xaml`：转换器、`SettingsCardsAnimations`、`DynamicListTransitions`。
- **页面基类** `Helpers\NavigablePage.cs`：透明背景、导航高亮动效（Accent 光晕 1850ms）、焦点接管。
- **测试防护**：Settings.UI.UnitTests 共 797 条源码字符串断言（全量清单在会话 `/tmp/kit-string-asserts.txt`）；真页导航烟雾 `tools\tests\SettingsUI\Invoke-ModulePageSmoke.ps1`（5 模块页 × 25 断言）、Dashboard 烟雾（zh-CN）与 NetMap 烟雾。

### 1.2 主要缺口（按面）

- **壳层**（逐条修改清单见附录 A）：标题栏语义未对齐（未设 Tall、模板 32px 占 48 行、断点三套）；壳层几乎未用 Kit 按钮族；卡片两套（ctCard vs KitCardSurfaceStyle）、状态色三套、Hero 双拷贝；导航 6 个 PNG 图标不随主题 / 高对比；6 处死代码；TitleBar 模板与 GeneralPage AiHub 区约 20 处硬编码英文兜底。
- **Awake**：结构干净（SettingsCard 范式 + 全 x:Uid）。缺口：交互控件无 AutomationId；SecondaryLink 指向上游作者站点（品牌决策）；其余保持。
- **LightSwitch**：页面 StackPanel 缺 `ChildrenTransitions`；代码死余（无对应 XAML 的 AutoSuggestBox 处理器与被注释块）；LocationDialog 部分控件缺 a11y 标注；`TimelineStyles.xaml` 全局合并但仅本页使用（收回页级）。
- **QuickAccess**：质量较好（x:Uid 双语齐全、AnimatedIcon 已用、ToolTip 齐）。小项：LaunchPage 底部 Grid.Row 越界寻址；`FlyoutButtonStyle` 无使用；排序菜单键已核查双语覆盖（§4）。
- **AIHub**：文案双轨制（VM 约 232 处 IsChinese vs resw 体系；`AIHub_Segment_*` 键缺失导致硬编码显示）；6 处主卡为内联 Border、徽章尺寸 40 / 36 / 34 / 28 混用；筛选 ToggleButton 高度 32 覆盖 Kit 的 28；响应式靠 SizeChanged 硬阈值（无 AdaptiveTrigger）；健康环为叠加 hack；结果列表无虚拟化；多处缺 a11y Name / LiveSetting。
- **NetMap**：状态着色按字符串拼 key（24 个页内样式 + converter）；地图代码绘制硬编码（线宽 3 vs 样式 2.5、28px 选中标签、440 固定列）；Start / Stop / AI 按钮与 Details 按钮硬编码英文 AutoName / ToolTip（resw 键已存在）；HopList / EnableSwitch 缺 Name；30 处英文 Text= 兜底。
- **Localserver**：已具备现代骨架（Kit 样式 / 全 resw / 响应式 VSM）；欠账为局部收敛：按钮五档高度、ComboBox 圆角 r6、`UsageProgressBar` 硬编码调色、图标按钮 AP.Name 与英文 ToolTip（22 + 9 处）、"微条形"双实现待合并。
- **UDPtest**：视觉骨架已复刻 Localserver，但差在整页英文硬编码（UVM 57 + URow 11 处双语内联、XAML 27 ToolTip + 12 AP.Name）、页面级 0 响应式、6 张内联卡、周期按钮靠代码换 Style、失败刻度色语义误用（U.xaml:478 用 TcpBrush）。
- **i18n**：见 §4。

### 1.3 设计基调（本轮统一规则，全部批次遵循）

1. **表面**：Mica 直通透不做实色遮盖；内容一律 `KitCardSurfaceStyle` / `ctSettingsCard`；禁止页内私有卡面副本（Hero 抽共享控件）；圆角走系统令牌（ControlCornerRadius / OverlayCornerRadius）。
2. **按钮**：一律 Kit 按钮族；每可见区块至多一个主按钮；图标按钮必须 `ToolTip` + `AutomationProperties.Name`。
3. **图标**：Segoe Fluent `FontIcon` / `AnimatedIcon`；导航与页头弃 PNG（不随主题 / 高对比着色）。
4. **状态色**：收敛到 `Themes\Colors.xaml` 语义令牌 + `KitStatusPillStyle` / `KitIconBadge*`；删除页内私有状态样式（DashboardEgressStyles、NetMapHealthStyles 等归并，键名保持）。
5. **动效**：列表用 `DynamicListTransitions`、卡片组用 `SettingsCardsAnimations`；尊重系统"动画效果"开关（关闭时静默）。
6. **无障碍**：全部交互控件 `AutomationProperties.Name` + `AutomationId`（沿用 `*_LightSwitch` 命名法）；装饰元素 `AccessibilityView="Raw"`；标题补 `HeadingLevel`；动态状态区补 `LiveSetting`。
7. **本地化**：新文案一律 resw（x:Uid）；禁止新增内联双语；XAML 硬编码仅作设计期兜底。
8. **布局**：间距走 SettingsCard 体系；禁止负 Margin 找平（现存例外随批清理）；断点收敛为 NavigationView 的 640 / 1008 一套。

## 2. 批次划分（每批原子提交 + 构建 + 单测；批次完成即汇报）

| 批次 | 内容 | 主要文件 |
|---|---|---|
| B1 | 共享层与壳层：TitleBar 语义（Tall / 断点 / 两套实现收敛）、6 处死代码清理、壳层按钮 / 卡片 / 状态色 / 图标归一、Hero 抽共享控件、GeneralPage 约 20 处英文兜底补 x:Uid + resw | ShellPage、DashboardPage、GeneralPage、TitleBar、Styles/* |
| B2 | QuickAccess + 壳层小项：LaunchPage 结构修正、死样式清理、SearchResultsPage a11y | QuickAccessXAML/*、SearchResultsPage.xaml |
| B3 | i18n 收尾：22 键翻译（19 备份状态 + 3 更新标题）、托盘 `Resources.zh-Hans.resx`、`Settings.UI.Controls` resw 去留、死键清单（约 267）处理 | Strings/*、src\runner |
| B4 | Awake 精修：AutomationId、页脚链接决策 | AwakePage.xaml |
| B5 | LightSwitch 精修：过渡、死码、Dialog a11y、TimelineStyles 收拢 | LightSwitchPage.*、Timeline* |
| B6 | Localserver：按钮五档收敛 28 / 32、ComboBox 圆角统一、`UsageProgressBar` 调色令牌化、微条形合并（与 UDP 共用）、图标按钮 Name 与英文 ToolTip 入 resw | LocalserverPage.*、LocalserverViewModel、UsageProgressBar.cs、ThemeBrushHelper.cs |
| B7 | UDPtest：全量 resw（VM 68 + XAML 39 处）、补响应式 VSM、6 张卡 → Kit 卡面、周期按钮改 ToggleButton、失败刻度改 critical 色、列宽/表头对齐 | UDPtestPage.*、UDPtestViewModel、UDPtestLineRowViewModel |
| B8 | AIHub：Segment / FindingDialog / 新控件 resw 补键、6 处内联卡面与徽章归共享样式、筛选高度统一 28、a11y 补齐（Name / Header / LiveSetting）、健康环与结果列表语义 | AIHubPage.*、AIHubPageViewModel、FindingDetailsDialog.*、Strings/* |
| B9 | NetMap：状态色强类型化、地图绘制令牌化、按钮 AutoName / ToolTip 走 resw、HopList / EnableSwitch 补 Name、英文兜底清理 | NetMapPage.*、NetMapViewModel、NetMapAiService |
| B10 | 收尾：手工过检（浅 / 深 / HC × 键盘 × 动画关闭）、三套烟雾、全量单测、README / changelog 同步 | — |

## 3. 验证协议

- 构建：`pwsh -ExecutionPolicy Bypass -File tools/build/build.ps1 -Platform x64 -Configuration Debug -Path "src/settings-ui"`（成功标志：exit 0 且含 `[BUILD] Local projects built`）。
- 单测：`Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.exe`（现 241 项，B3 增 en/zh 键集奇偶校验 1 项、B4 增 Awake AutomationId 钉 1 项、B5 增 LightSwitch/Timeline 钉 1 项、B6 增 Localserver 收敛钉 1 项、B7 增 UDPtest 全量 resw 钉 1 项、B8 增 AIHub 收敛钉 1 项、B9 增 NetMap 收敛钉 1 项；改动会触发源码断言，随批同步）。
- 烟雾（先关闭全部 Kit 进程）：`tools\tests\SettingsUI\Invoke-ModulePageSmoke.ps1 -Configuration Debug`；`Invoke-DashboardPageSmoke.ps1 -Configuration Debug -Language zh-CN`；`tools\tests\NetMap\Invoke-NetMapPageSmoke.ps1`。
- 视觉手工过检矩阵：浅色 / 深色 / 高对比 × 每页 × 键盘导航；系统动画关闭时无动效。
- 模块套件（B10 起实测的补充口径，非闸门）：六个模块单测套件可单独构建直跑——`pwsh -ExecutionPolicy Bypass -File tools/build/build.ps1 -Platform x64 -Configuration Debug -Path "src/modules/NetMap/NetMap.UnitTests"`（同法换目录：`src/modules/Localserver/Localserver.UnitTests`、`src/modules/UDPtest/UDPtest.UnitTests`、`src/modules/awake/Awake.UnitTests`、`src/modules/awake/Awake.ModuleServices.UnitTests`、`src/common/AiHub.UnitTests`），产物落 `x64\Debug\tests\<项目名>\`，直接运行 exe（B10 实测全绿 102 / 29 / 28 / 8 / 3 / 355）。`src/common/interop/interop-tests` 产物在项目自身 `bin\x64\Debug\Microsoft.Interop.Tests.exe`（程序集名 ≠ 项目名，1 项）。`src/modules/LightSwitch/Tests/LightSwitch.UITests` 为 UI 自动化工程（`RunVSTest=false`、引用 `common/UITestAutomation`、带 `Package.appxmanifest`），**不参与无头运行**。搜产物须排除 `.claude\worktrees\**`（其他会话 worktree 的副本会先被 `find` 命中，B10 实测踩过一次）。

## 4. i18n 现状数据

- zh-CN 缺 289 键；仍被引用 22 键（19 个 `General_SettingsBackupAndRestore_*` + 3 个更新标题；`No/Yes/alias/assembly` 为扫描误报）。其余为 en-only。**✅ B3 已执行**（附录 F）：XML 口径修正为 en-only 287（22 键补中文、237 键删除、28 键白名单保留），并新增 en/zh 奇偶校验单测钉住。
- QuickAccess 经 `Kit.QuickAccess.pri` 链接 `Settings.UI\Strings\**`（`Kit.QuickAccess.csproj:55-57`），与 Settings 共用同一套双语资源。
- `Settings.UI.Controls\Strings\en-us\Resources.resw` 的 6 个真实键（Dashboard_SortBy* / NewInfoBadge / GPOWarning）已全部由 Settings.UI resw 双语覆盖（含 QA 排序三键，zh:148-154；en:1173-1179），其余 4 键为 resx 模板残留。**✅ B3 已执行**：该文件与空目录已删除，csproj 与全仓无引用，构建、单测、烟雾全绿。
- runner 托盘：`runner.base.rc` 单语言英文；`tools\build\convert-resx-to-rc.ps1:49-50` 已含 zh-Hans / zh-CN 映射。**✅ B3 已执行**：新增 `src\runner\Resources.zh-Hans.resx`（24 条，UTF-8 BOM，`\t` 分隔符逐字节对齐英文），转换脚本自动发现并生成 `LANGUAGE LANG_CHINESE, SUBLANG_NEUTRAL` 块；rc 双语言块与重链 Kit.exe 资源段均已核验。

## 5. 前代遗留项吸收（v9）

- a11y 收尾 → §1.3 规则 6，落各页批次；HealthStyles 归并 → 规则 4（随 B6 / B9）；死键清理 → B3；手工过检 → B10；托盘本地化 → B3。
- `UsageProgressBar` 类别色依赖 `AutomationProperties.Name` 字符串匹配（Dashboard / NetMap / Localserver 共用）→ 改为显式附加属性（如 `kit:UsageCategory`），随首次触达该控件的批次执行。**✅ B6 已执行**：显式附加属性 `UsageProgressBar.UsageCategory`（枚举 Default / Cpu / Memory / Disk / Gpu / Temperature）落地，全部调用点（Dashboard 系统 / 存储 / GPU 行 + Localserver 两条）改显式赋值，字符串启发式删除；调色落 Colors.xaml 三主题字典 8 令牌（数值逐位同旧 ARGB）；NetMap 实际未使用该控件（附录 C1 已核）。
- T5 首屏骨架（未定）：本轮不主动做；B1 后复测首帧再决策。
- L3 / L4 / L8 性能项（ShellPage XAML 膨胀、GeneralPage x:Load、合并字典审计）：不属于本 UI 轮，维持挂起；如 B1 顺带低风险可并入并注明。

## 附录 A：壳层修改清单（来源：壳层只读审计 2026-10-29）

1. **TitleBar 语义**：ShellPage 给 Content（搜索框）但未设 `DisplayMode=Tall`（`TitleBar.Properties.cs:111` 依据）；模板实高 32 占 48 行（`TitleBar.xaml:17` vs `ShellPage.xaml:94`）；`PreferredHeightOption` 随 Tall 复核（`TitleBar.WASDK.cs:69-78`）；断点 850 / 900 合并。**✅ B1 已执行**：ShellPage 设 `DisplayMode="Tall"`（48px 填满预留行）；断点收敛为控件默认值 900（删除 ShellPage 覆写）；模板 ToolTip 兜底移除（code-behind 以 resw 覆盖）。
2. **死代码（先删再改，缩小迁移面）**：`CheckUpdateControl`（无消费者，General.cs:492 禁止 Dashboard 引用）、`ShortcutConflictControl`、`ShortcutConflictWindow.xaml:139-156 EmptyStatePanel`、GeneralPage 双空 HACK 卡（GeneralPage.xaml:196-204）**评估后保留**：其为 ShortcutControl 在 Expander 中作首尾项失效的上游 bug 载荷件，删除即复现。`RightAlignedCompactToggleSwitchStyle`（Button.xaml:79-648）处置推迟到 B6 / B7（Localserver/UDPtest 审计建议在行模板启用，见附录 C2）；TitleBar BackButton 路径随标题栏重构一并处理。**✅ B1 已执行**：CheckUpdateControl / ShortcutConflictControl / EmptyStatePanel 已删除，csproj、隐私测试名单（FrameworkPrivacyDefaults.cs）、品牌色（Colors.xaml:125-129）同步清理；HACK 卡如上保留；BackButton 与 RightAlignedCompact 两项按计划推迟。
3. **按钮归一**：Dashboard 排序按钮补 AutomationProperties.Name；壳层按钮全部改挂 Kit 族（现仅此一处使用）。**✅ B1 已执行**：排序按钮 Name 已由 `Dashboard_SortBy` x:Uid 双语覆盖，另补 `AutomationId="DashboardSortByButton"`。
4. **表面归一**：Dashboard 内 ctCard（:126 / :217）与 KitCardSurfaceStyle 并存二选一；Hero 抽共享控件（`DashboardPage.xaml:95-123` 与 `GeneralPage.xaml:31-118` 目前双拷贝）。**✅ B1 已执行**：Hero 抽取为 `KitHeroPanelStyle`（Surface.xaml 模板化 ContentControl），Dashboard / General 共用；`controls:Card` 与 KitCardSurfaceStyle 经核对同为 CardBackgroundFill / CardStroke / OverlayCornerRadius 令牌，视觉本就一致，无需二选一。
5. **状态色归一**：`DashboardEgressStyles`（DashboardPage.xaml:19-29）归并到 Colors.xaml / KitStatusPill；网络卡内联 FontSize19 改令牌。**✅ B1 已执行**：归并为 Surface.xaml 共享 `KitEgressStatusStyles`（NetMap 于 B9 接入）；IP 字号 19→20 对齐 20px 度量刻度（该 TextBlock 的 Style 槽被健康着色转换器占用，暂保持内联）。
6. **图标**：6 个导航 PNG（ShellPage.xaml:178-225）改 FontIcon；General 的 AnimatedIcon 保留。**✅ B1 已执行**：6 项换 FontIcon（EA80 / E706 / E790 / E968 / E774 / E945，均经 System.Drawing 渲染校验）；PNG 资产保留（ModuleHelper.cs:31-36 与 search.index.json 仍在引用）。
7. **文案兜底**：TitleBar 模板硬编码 "Back" / "Toggle menu"（TitleBar.xaml:96 / :100）改 resw；GeneralPage AiHub 区约 20 处 Content / PlaceholderText（:292-482，含 4 个无 x:Uid 的 ComboBoxItem）补 x:Uid + resw。**✅ B1 已执行**：模板兜底移除；AiHub 区 7 处无 x:Uid 控件补齐（EndpointUrlBox / ModelBox / ApiModeResponses / ApiModeChat / EffortLow / EffortHigh / EffortMax）并新增 en+zh 键各 7 条；其余约 20 处属"有键兜底"，按设计期用途保留。
8. **两套标题栏收敛**：ShortcutConflictWindow 原生 TitleBar 与壳层自绘 TitleBar 策略统一（新窗口一律原生；壳层评估迁移，本轮先对齐视觉与拖拽语义）。**B1 复核**：原生 TitleBar 默认语义已足够，无需改动；视觉一致性随 B10 复检。**✅ B10 复检**：ShortcutConflictWindow 以原生 `TitleBar` 控件承接（`ExtendsContentIntoTitleBar` + `SetTitleBar(titleBar)` 满足拖拽语义、`IconSource` 图标、resw 本地化标题、`ThemeService` 主题跟随），与壳层自绘 TitleBar 的视觉与拖拽约定一致；**无需改动**。
9. **测试锚点同步**：BuildCompatibility.cs:3127-3148 / :2792 / :2998 / :3190-3194；General.cs:127-164 / 184-191 / 207-211 / 482-494 / 696-699 / 733 / 746 / 761；FrameworkPrivacyDefaults.cs:264-269；UDPtest.cs:112；Localserver.cs:147。**✅ B1 已执行**：构建绿 + Settings 单测 234/234 通过（未需补锚点）；烟雾结果见下方执行记录。

## 附录 B：Awake / LightSwitch / QuickAccess 修改清单（主会话审计）

**Awake（AwakePage.xaml，113 行）**

- 交互控件补 AutomationId：启用 ToggleSwitch、模式 ComboBox、两个 NumberBox、显示 ToggleSwitch（沿用 `*_Awake` 命名）。**✅ B4 已执行**：实补 7 个（计划 5 个之外追加到期 DatePicker / TimePicker，按 §1.3 规则 6 覆盖全部交互控件）；`BuildCompatibility.cs` 新增断言钉住 7 个 ID。
- 品牌决策：SecondaryLink（`:110`）指向 awake.den.dev（上游作者）；保留署名或改 Kit 仓库链接。**✅ B4 已裁定：保留署名链接**——区块标题与文案本身即署名（`Attribution` / "Den Delimarsky's work on creating Awake"），指向原作者站点语义正确；Kit 自有文档链接要求已由主链接 `LearnMore_Awake`（→ Kit 仓库）满足并被测试钉住。
- 其余保持（已是 Fluent SettingsCard 范式，含 `SettingsCardsAnimations`）。**✅ B4 复核**：无内联卡面 / 硬编码文案 / 私有样式需改。

**LightSwitch（LightSwitchPage.xaml，358 行）**

- `:28` StackPanel 补 `ChildrenTransitions="{StaticResource SettingsCardsAnimations}"`（与 Awake 对齐）。
- 死码清理：`LightSwitchPage.xaml.cs:367-394`（无对应 XAML 的 AutoSuggestBox 处理器 + 注释块）、`LightSwitchPage.xaml:104` 注释行；`SearchLocations` / `SelectedCity` 关联残留评估。
- Dialog a11y：`SyncLoader` / `LocationErrorText` / `LocationResultPanel` 补 Name / LiveSetting；夜间模式 HyperlinkButton（`:162-166`）统一到 TextButtonStyle。
- `TimelineStyles.xaml` 从 `App.xaml:24` 全局合并收回页级资源；Timeline 三态配色走语义令牌核验。
- 既有 AutomationId（Toggle_LightSwitch、ModeSelector 等）保持不动。

**✅ B5 已执行**（附录 H）：全 5 项落地；`TimelineStyles` 超出计划收至 Timeline 控件自身资源（非页级），随触达面另本地化色带 / 日出日落 tooltip 与自动化名兜底（本批共 8 键对）；`SearchLocations` / `SelectedCity` 评估后保留（均有真实消费者）。

**QuickAccess**

- `LaunchPage.xaml:75` 底部条：补行定义或拆行，消除 `Grid.Row="2"` 越界寻址；InfoBar 与按钮组同格重叠需实渲染验证。
- `FlyoutButtonStyle`（`:15-23`）无使用 → 删除。
- 排序 MenuFlyout（AppsListPage.xaml:29-51）三键已核查：共享 resw 双语覆盖（en:1173-1179 / zh:148-154）✓。
- `MainWindow` Acrylic 钉死勿动；`QuickAccessList.xaml` 模板（Name + ToolTip）已达标。

## 附录 C1：AIHub / NetMap 修改清单（子代理审计 2026-10-29）

图例：A=`AIHubPage.xaml`、AC=其 code-behind、V1=`AIHubPageViewModel.cs`、N=`NetMapPage.xaml`、NC=其 code-behind、V2=`NetMapViewModel.cs`、DC=`FindingDetailsDialog.xaml.cs`。

**AIHub**

- 本地化：`AIHub_Segment_*`（A:1269-1275）resw 键缺失导致 Segment 显示硬编码 → 补 en+zh 键；FindingDetailsDialog 内联三元（DC:40 / 57 / 70-77）→ 改引用已有 resw 键；V1 约 232 处 IsChinese：本批先保证"新增 / 触达控件必须走 resw"，全量迁移列观察项。
- 卡面 / 徽章：6 处内联 Border 主卡（A:267 / 475 / 696 / 1074 / 1132 / 1250）→ `KitCardSurfaceStyle`；全部内联图标徽章（40 / 36 / 34 / 28）→ `KitIconBadgeStyle` / `KitIconBadgeSmallStyle`；指标瓦片数字改用 `KitMetricTileValueStyle`（A:1043-1071 现 FontSize24 Bold）。
- 尺寸统一：筛选 / 视图 ToggleButton（A:726 / 734，现 Height32 覆盖）与 `KitFilterPillToggleStyle`（28）对齐；卡面圆角 8 / 6 / 4 / 10 混用清理。
- a11y：`Toggle_AIHub`（A:281-283）补 Name；快速扫描 / 全量审计（A:417 / 423）补 Id + Name；CalendarDatePicker（A:500）、搜索框（A:721）、策略框（A:1281）补 Name / Header；InfoBar（A:334 / 1244）补 LiveSetting；优先处理卡按钮（A:613-627）补 Name。
- 组件：健康环叠加 hack（A:524-533）改单一 ProgressRing（Track 主题）方案；结果列表（A:820 / 830 / 1121 / 1189，MaxHeight 320-520）虚拟化列观察项。
- 响应式：AC:75 / 83 / 95 的 SizeChanged 阈值改 AdaptiveTrigger（对照 DashboardPage.xaml:325-384）。
- 测试锚点：AiHubSettingsTests.cs:62-97（钉 `Toggle_AIHub`、`AIHub_RecheckAiButton`、负断言 `AIHub_PipelineCard` 等）；General.cs:100-123。
- 核实项：A:22 注释所称 theme/contrast 烟雾断言在单测无命中，需确认或移除注释。

**✅ B8 已执行**（附录 K）：全 8 项落地（结果列表虚拟化按审计口径维持观察项），执行面三处增量——(1) AI 报告卡顺手收编为第 6 处 `KitCardSurfaceStyle` 表面；(2) `AIHub_Segment_*` 补键时发现并删除两对历史 casing 错配死键（`AiHub_Segment_*.Content`，全仓零消费方，B3 死键清扫漏网）；(3) 复制报告（AC 内 10 处中文字面量）随 FindingDialog 同批迁 resw，AIHub 页 code-behind 与触达 VM 标签 `IsChinese` 清零；V1 全量迁移维持观察项。健康环：实测 WinUI 1.8 ProgressRing 模板为 Lottie 播放器、无内建 Track 元素，以“单 ProgressRing + 主题 Ellipse 轨道”实现语义等价（详附录 K）。响应式：三处 SizeChanged 改 4 组 `ControlSizeTrigger` VSM，阈值 640 / 760 / 960 逐位保留元素宽度语义（烟雾按元素 ActualWidth 断言；窗口宽 AdaptiveTrigger 会致断点漂移，故弃）。“核实项”经烟雾断言复核成立，注释已改准确表述。

**NetMap**

- 状态色强类型化：24 个页内样式 + `NetMapHealthStyleConverter` 字符串拼 key（N:20-34、V2:29-36、NC:251 / 441）→ 仿 AC:48-61 的 Style 映射函数。
- 地图绘制令牌化：线宽统一（NC:398 的 3 vs N:64 的 2.5）；选中标签 28px（NC:331-336）字体令牌化；MapBody 440 列（N:400）与拟合常量（NC:372）评估收敛。
- a11y / 本地化：Start / Stop / AI 检测（N:294-333）与 Details（N:159-206）硬编码英文 AutoName / Tip → 改 resw（键已存在）；HopList（N:441）、EnableSwitch（N:242）、Browse ×2（N:612 / 623）、应用设置（N:673）补 Name；30 处英文 `Text=` 兜底清理。
- 测试锚点：BuildCompatibility.cs:2858 / 2884 / 1603-1604 / 1812-1813 / 1956-1957 / 2624-2625；NetMapAiServiceTests.cs:25-85；Localserver.cs:112-114（UsageProgressBar 契约；该控件 AIHub/NetMap 两页均未使用）。

**✅ B9 已执行**（附录 L）：全 4 项落地，执行面三处增量——(1) 字符串拼 key 的转换器类由 `NetMapHealthStylesStore` 取代：模板内改由模型属性（`DotStyle` / `TextStyle`）承接、页级绑定与画布绘制改类型化映射函数，`NetMapHealthStyles` 字典键名原样保留（烟雾的样式引用断言不受影响）；转换器两处消费者齐迁后**类已删除**，Dashboard 四处绑定改 `EgressTextStyle` 类型化映射（Surface 共享字典注释同步更正）。(2) 线宽归一取样式值 2.5（删运行时 3 覆写）；选中标签 28px 并入 `NetMapMapLabelStyle`；MapBody 440 与拟合常量 800 / 340 / 1 / 4 常量化。(3) 30 处英文直量（AutoName / ToolTip / Text / Header）清零，`NetMap_HopList` 补无障碍名（新增 1 键对）；EnableSwitch 经既有 `x:Uid="NetMap_Enable"` 已具备名称，免改。Hero 卡内联 Border 不在 C1 清单内，留待 B10 复核；**✅ B10 复核**：该内联 Border 已收敛为共享 `KitHeroSurfaceStyle`（全仓最后一处手写 hero 表面清零，NetMap 收敛钉同步加"用共享样式 + 该页内联表面画刷清零"两条断言，见附录 M）。

## 附录 C2：Localserver / UDPtest 修改清单（子代理审计 2026-10-29）

图例：LS=`LocalserverPage.xaml`（779 行）、LSVM=`LocalserverViewModel.cs`、U=`UDPtestPage.xaml`（618 行）、UVM=`UDPtestViewModel.cs`、URow=`UDPtestLineRowViewModel.cs`。

**Localserver（骨架已现代：Kit 样式 / 全 resw / 响应式 VSM；欠账为局部收敛）**

- 尺寸 / 圆角：按钮五档（22 / 26 / 28 / 30 / 32）收敛为 28 / 32；删除 `LS.xaml:699-702` 对 KitCompactIconButtonStyle 的 30×30 覆写；ComboBox 圆角 r6（LS.xaml:509）对齐 U 页 r4；hero 卡硬编码 `CornerRadius=8`（LS.xaml:53）改 `OverlayCornerRadius`；表头 Padding（LS.xaml:202）与 U 页对齐。
- 卡片：3 处内联 Border（LS.xaml:53 / 196 / 690）→ `KitHeroSurfaceStyle` / `KitCardSurfaceStyle`。
- a11y / 文案：行展开 / 更多 / 日志折叠等图标按钮（LS.xaml:293 / 300 / 699）补 AP.Name；"Fix"（LS.xaml:597）等英文硬编码入 resw；22 处 ToolTip + 9 处 AP.Name 英文改 resw 键。
- 颜色：`UsageProgressBar.cs:100-111` 硬编码 ARGB → 主题令牌；`ThemeBrushHelper.cs:13-17` 评估令牌化。
- 合并：与 UDP 的"微条形"实现（LSVM:112-137 六档 vs RecentProbeBarViewModel 三档）合并为共享一套。
- 测试锚点：Localserver.cs:74-133（钉 KitCardSurfaceStyle / KitStatusPillStyle / UsageProgressBar / `Localserver_*` 键；禁止 LastError、EnvTrendLinePath 等）。

**✅ B6 已执行**（附录 I）：全 6 项落地，执行面两处扩大——空态卡（第 4 处内联 Border）一并改 `KitCardSurfaceStyle`；"微条形合并"在本批就完成 UDP 侧接线（B7 不再重复）。表头 Padding 取 U 页卡内"标题 11 / 列头 7"节奏定为 `16,11,16,7`。`ThemeBrushHelper` 评估结论：保留——其为"资源缺失兜底"路径，兜底色按定义无法再令牌化；本批起它同时是微条形唯一语义刷来源。

**UDPtest（视觉骨架已复刻 LS；差 resw 化、响应式、卡面、交互语义）**

- resw 全量接入（最高优先）：UVM 57 处 + URow 11 处 IsChinese 内联、U.xaml 27 处 ToolTip + 12 处 AP.Name + Placeholder（:185 / :196）英文硬编码 → 按 Localserver 样板平移（`UDPtest_*` 骨架 51 键 en/zh 已对齐）。
- 响应式：复制 `LS.xaml:20-41` 的 ControlSizeTrigger（760px）模式；表格 MinWidth 合计约 750-780px 需窄窗兜底。
- 卡片：6 张内联卡（U.xaml:250 / 354 / 403 / 488 / 528 / 571）→ `KitCardSurfaceStyle` / `KitHeroSurfaceStyle`。
- 交互语义：周期按钮改 ToggleButton / RadioButtons（现为代码换 Style，U.cs:100-124、U.xaml:19-23），对齐 `KitFilterPillToggleStyle`；失败刻度 `Stroke="{StaticResource TcpBrush}"`（U.xaml:478）改 `SystemFillColorCriticalBrush`。
- 尺寸：选线路 ComboBox（U.xaml:439）与 LS 统一；表格列宽三套重复（U.xaml:49 / 111 / 539 / 582）收敛为共享定义。
- 行控件：行 ToggleSwitch 40×36 ×2（U.xaml:72 / 138）与 LS 同型；`RightAlignedCompactToggleSwitchStyle`（Button.xaml:79-648，全仓零使用）二选一：启用替换四处内联，或删除该模板克隆。**B1 暂不删**，B6 / B7 一并裁定。**✅ B6 裁定：启用**——LS 行模板已在本批接入（同步删去 MinWidth=0 / Height=36 内联覆写）；UDP 两处（U.xaml:72 / 138）随 B7 接入，此后该样式为唯一行开关实现。**✅ B7 已接入**：两处行开关改挂该样式（内联 MinWidth / Height 覆写删除），行开关实现至此全仓统一。
- a11y：展开按钮（U.xaml:139）、周期按钮（:469-472）等补 AP.Name。
- 测试锚点：UDPtest.cs:72-98（钉 TcpLineTemplate / UdpLineTemplate / UdpExpandButton；禁止 TcpExpandButton、"Restore Default"）。

**✅ B7 已执行**（附录 J）：全 8 项落地，含三处执行面增量——(1) 两张表套横向 `ScrollViewer` 作窄窗兜底（列 MinWidth 合计约 786px）；(2) `KitPeriodButtonStyle` 清零引用后从共享按钮族删除，周期胶囊选中态由样式互换改为 `IsChecked` 同步；(3) `ResourceExtensions` 新增 `GetLocalizedOrFallback` / `FormatLocalized` 两个带回退的 resw 取值扩展，供 VM 全量迁移复用。两个 VM 的 `IsChinese` 与页面英文硬编码全部清零；resw 各语言 +108 键对（en 999 / zh 971，en-only 仍 28）。

## 附录 D：B1 执行记录（2026-10-29）

**范围**：共享层与壳层，附录 A 全 9 项收口。

- **TitleBar 语义**：ShellPage 设 `DisplayMode="Tall"`（48px 填满预留行）；断点收敛为单一来源 900（控件默认值，删除 ShellPage 覆写）；模板硬编码 ToolTip 移除（code-behind 以 resw `TitleBar_BackButton_ToolTip` / `TitleBar_PaneButton_ToolTip` 覆盖）。
- **死代码**：删除 CheckUpdateControl、ShortcutConflictControl（各 xaml+cs）、ShortcutConflictWindow EmptyStatePanel；同步 csproj、FrameworkPrivacyDefaults 扫描名单、Colors.xaml 品牌色 4 条。GeneralPage 双空 HACK 卡为 ShortcutControl 载荷件，评估后保留。
- **共享层**：Surface.xaml 新增 `KitHeroPanelStyle`（模板化 ContentControl）与 `KitEgressStatusStyles`；Dashboard/General 双 Hero 收纳；Dashboard 出口配色改共享字典；IP 字号 19→20 对齐度量刻度；排序按钮补 AutomationId。
- **图标**：6 个导航 PNG → FontIcon（EA80 / E706 / E790 / E968 / E774 / E945，System.Drawing 渲染校验；PNG 资产保留——ModuleHelper 与 search.index 仍引用）。
- **本地化**：GeneralPage AiHub 区补 7 处 x:Uid（EndpointUrlBox / ModelBox / ApiModeResponses / ApiModeChat / EffortLow / EffortHigh / EffortMax）+ en/zh 键各 7 条。

**验证**：
- 构建绿；Settings.UI.UnitTests 234/234（未需补锚点）。
- Dashboard 烟雾 PASS（17 张截图；`Capture` 改 System.IO 管线；侧栏断言由 PNG 对比度采样改为必须为 FontIcon——前景色随主题，对比度结构性成立，快捷访问 PNG 的对比度检查保留）。
- Module 页烟雾 PASS（29 张截图）；两处宿主写拒以 SETUP-SKIP / CHECK-SKIP 响亮跳过，在有写权限的机器上自动全量恢复。
- 真实应用启动截图核验：Tall 标题栏、7 个字形导航图标、共享 Hero、共享出口配色与度量刻度全部按预期渲染。

**宿主环境发现（非产品缺陷）**：本机安全套件（Trend Micro 在役、Defender 被动）拒绝新建的无签名测试 exe 写入 `%LOCALAPPDATA%` 全树（AiHub 存储与 Temp 均被拒；pwsh 与已签名程序不受影响）。适配：烟雾脚本把 TEMP/TMP 重定向到 `TestResults\PluginLifecycle\tmp`；RenderPage / Capture 弃用 WinRT StorageFolder 改 System.IO；AIHub 设置、任务策略保存与插件回写检查按宿主可写性门控。可选建议：给 Kit 开发目录与烟雾 exe 加 AV 白名单，跳过将自动消失。

**烟雾适配同步修复**：ModulePageSmoke 对 `_scheduleTimer` 的反射改为反向钉（"页面不得拥有审计调度器"），同步 0960041 缺陷批次的页面调度器删除；此前烟雾因该引用在此处 NRE（旧形状反射）。

## 附录 E：B2 执行记录（2026-10-29）

- **LaunchPage 结构修正**：根栅格行 `*` / 48 → `*` / `Auto`；底部条 `Grid.Row="2"`（越界行被钳制导致与按钮组重叠）改为 `Grid.Row="1"` 并补 `Auto` / `Auto` 行定义；按钮组 Margin 0,0,12,0 → 0,4,12,8。更新提示 InfoBar 与 Docs/Settings 按钮不再相互遮挡。
- **死样式清理**：删除 `FlyoutButtonStyle`（全仓零引用），移除随之变空的 Page.Resources 块。
- **SearchResultsPage a11y**：结果宿主加 `AutomationProperties.LiveSetting="Polite"`。

**验证**：构建绿（Kit.QuickAccess.exe 重编译）；Settings 单测 234/234；Dashboard 烟雾 PASS（壳层回归网）。B2 未提交。

## 附录 F：B3 执行记录（2026-10-29）

**范围**：i18n 收尾（§2 B3 行、§4 数据按实证修正）。口径修正：resw 为 XML，行级统计会被"单行多 `<data>`"欺骗（本仓 zh 有一条约 200 键的挤行，实测行级 587 vs XML 810）。本轮一律以 XML 解析取 `root.data.name` 为准：en 1097、zh 810、en-only 287（旧口径的 `alias`/`assembly` 两处为 resx 头模板残留，已剔除）。

- **22 键中文化**：zh 补 19 个 `General_SettingsBackupAndRestore_*`（备份状态 / 来源 / 时间 / 错误）+ 3 个更新标题（`General_NewVersionAvailable.Title` / `General_UpToDate.Title` / `General_CantCheck.Title`）；zh 810 → 832。三标题在 `GeneralViewModel` 以斜杠形式引用（`General_UpToDate/Title`），行级扫描曾漏检，XML 口径并入。
- **237 键删除**：en 删全仓无引用的上游残留（`Admin_Mode` / `CmdNotFound_*` / 独立 `No` `Yes` 等），en 1097 → 860。删除前按字面引用、x:Uid 基名、`GetResourceString` 三形态扫全仓核验引用面；删除后复核 en-only 恰为 28 键白名单，无悬空引用。
- **28 键白名单保留**（引用面尚存或不宜删）：`CmdNotFound` / `QuickAccent` / `TextExtractor` / `PasteAs*` / `TranscodeToMp*` 等上游模块残留键；`Default.Content` / `On` / `Off` / `Shortcut` / `File` / `Activation_Shortcut.*` 通用键；测试负断言钉住的 `General_Repository` / `LearnWhatsNew` 等。
- **奇偶校验单测**：`BuildCompatibility.cs` 新增 `KitSettingsResourcesShouldKeepChineseKeysInSyncWithEnglish`（zh ⊆ en，且 en-only 恰为 28 键白名单）；单测 234 → 235。
- **旧 resw 移除**：`Settings.UI.Controls\Strings\en-us\Resources.resw` 及空目录删除（6 个真实键已由 Settings.UI resw 双语覆盖）。
- **托盘本地化**：新增 `src\runner\Resources.zh-Hans.resx`（24 条；错误框 / 更新提示 / 托盘菜单 / 工具提示；UTF-8 BOM；`\t` 分隔符逐字节对齐英文）。转换脚本自动发现，rc 生成 `LANGUAGE LANG_CHINESE, SUBLANG_NEUTRAL` 块，IDS 定义 24 个不变，英文块保留。

**验证**：
- 构建：settings-ui exit 0；runner（Kit.vcxproj）exit 0。
- 单测：235/235 全绿。
- 生成物：`Generated Files\runner.rc` 双语言块（LANG_ENGLISH + LANG_CHINESE，48 条 IDS）；重链 `Kit.exe` 以 UTF-16 字节抽查"快速访问 / 关闭 / 管理员 / 文档 / 立即更新 / 设置"全部命中资源段。
- 烟雾：Dashboard（zh-CN）PASS；Module 页 PASS（宿主写拒项照旧 SETUP-SKIP / CHECK-SKIP）。
- 悬空引用扫查：全仓 x:Uid 与 `GetResourceString` 均可解析；`AIHub_Segment_Optimization` / `AIHub_Segment_SecurityAudit` 仍缺（B8 已知缺口，与本批无关）。

**B2 与 B3 均未提交。**

## 附录 G：B4 执行记录（2026-10-29）

**范围**：Awake 精修（附录 B Awake 段全 3 项）。

- **AutomationId 补齐（7 个）**：`Toggle_Awake`（启用）、`ModeSelection_Awake`（模式）、`ExpirationDate_Awake` / `ExpirationTime_Awake`（到期日期 / 时间；计划外追加，规则 6 覆盖全部交互控件）、`IntervalHours_Awake` / `IntervalMinutes_Awake`（间隔）、`KeepDisplayOn_Awake`（显示）。
- **署名链接裁定**：保留 `SecondaryLink_Awake` → `https://awake.den.dev`（区块语义为署名，指向原作者站点正确；Kit 自有链接要求由主链接满足）。
- **测试锚点**：`BuildCompatibility.cs` 新增 `KitAwakePageShouldExposeAutomationIdsForCoreControls` 钉住 7 个 ID；单测 235 → 236。

**验证**：构建绿（settings-ui exit 0，`[BUILD] Local projects built`）；单测 236/236；Module 页烟雾 PASS（含 Awake 页导航渲染）。

**B2、B3、B4 均未提交。**

## 附录 H：B5 执行记录（2026-10-29）

**范围**：LightSwitch 精修（附录 B LightSwitch 段全 5 项，随触达面扩展 Timeline 控件）。

- **过渡**：`:28` StackPanel 补 `ChildrenTransitions="{StaticResource SettingsCardsAnimations}"`。
- **死码清理**：删 `LightSwitchPage.xaml.cs` 的 AutoSuggestBox 处理器 + 注释块（连带失去引用的 `System.Linq` / `System.Globalization` using）与 XAML 注释行；`SearchLocations` / `SelectedCity` 评估后保留——前者由 `SearchLocationLoader` 填充并被 ViewModel 消费，后者有测试钉住。
- **Dialog a11y**：`SyncLoader` / `LocationErrorText` / `LocationResultPanel` 补 `x:Uid` 无障碍名（3 键对）与 `LiveSetting`（Assertive / Polite）；夜间模式 HyperlinkButton 挂 `TextButtonStyle`。
- **TimelineStyles 收拢（超出计划的更严范围）**：从 `App.xaml` 全局合并移除，改由 `Timeline.xaml` 的 `UserControl.Resources` 局部合并（仅该控件消费）；`Timeline.xaml.cs` 10 处样式查找 `Application.Current.Resources` → `this.Resources`。
- **Timeline 文案本地化（审计扩展面）**：色带 2 条、日出 / 日落 2 条 tooltip 与 `TimelineAutomationPeer` 名兜底共 5 键入 resw（原为硬编码英文）；本批 resw 共新增 8 键对（含 dialog 3 键）。
- **测试锚点**：`BuildCompatibility.cs` 新增 `KitLightSwitchPageShouldExposeDialogA11yAndScopedTimelineStyles`（钉过渡、dialog 键、样式收拢、tooltip 本地化与 peer 兜底）；单测 236 → 237。

**验证**（末轮修订后整树复跑）：构建 exit 0（`[BUILD] Local projects built`）；单测 237/237 全绿；Module 页烟雾 PASS；Dashboard(zh-CN) 烟雾 PASS。静态复核：改动 XAML / resw 全部 XML 良构、resw 键集无漂移（en-only 28 不变）。

**B2、B3、B4、B5 均未提交。**

## 附录 I：B6 执行记录（2026-10-29）

**范围**：Localserver 局部收敛（附录 C2 Localserver 段全 6 项）+ §5 UsageProgressBar 名称耦合 + 微条形共享合并（含 UDP 侧接线）。

- **尺寸收敛 28 / 32**：Fix 芯片按钮 22 → 28；日志折叠按钮删除 30×30 覆写（回落 `KitCompactIconButtonStyle` 28）；行内 ToggleSwitch 改用 `RightAlignedCompactToggleSwitchStyle`（本批裁定启用，B1 遗留的两选一收口）；其余按钮全部落在样式默认 28 / 32 两档。
- **圆角 / 卡面**：ComboBox 圆角 r6 → r4（对齐 U 页）；hero / 空态 / 服务表 / 日志抽屉 4 处内联 Border → `KitHeroSurfaceStyle` / `KitCardSurfaceStyle`（容器类以 `Padding="0"` 覆写保留内部分栏节奏）；表头 Padding `16,10` → `16,11,16,7`（U 页卡内标题 / 列头节奏）。
- **a11y / 文案入 resw**：页内 12 处英文 ToolTip 字面量与 8 处英文 AP.Name（含 "Fix"、"CPU" / "Memory"）全部改 x:Uid 键——新增 12 组基名 + 2 组既有基名（ComboBox / 日志跟随）补键，共 23 键对双语（en 868 → 891、zh 840 → 863，en-only 仍 28）；两条进度条 AP.Name 改双语无障碍名。
- **UsageProgressBar 令牌化 + 类别显式化**：Colors.xaml 三主题字典各新增 `KitUsage*Brush` 8 个（颜色与旧内联 ARGB 逐位一致；高对比走 WindowText）；`UsageProgressBar.cs` 重写——`UsageCategory` 附加属性、按令牌键取刷、`DetectCategory` 启发式与 `Color.FromArgb` 全部删除；Dashboard 明细行（系统 CPU / 内存、存储盘、GPU 显存 / 负载 / 温度）经 `DashboardDetail.UsageCategory` 显式分类。
- **微条形合并**：新增 `ViewModels\StatusBarViewModel.cs`（私有构造 + `FromServiceState` / `FromProbeResult` 两条语义阶梯；刷统一走 `ThemeBrushHelper`，LS 侧原裸资源查表同批收敛）；LSVM 内 `RecentBarViewModel` 删除、`RecentBars` 改共享型；UDP 侧 `UDPtestLineRowViewModel` 与两处 DataTemplate 同批接线，`RecentProbeBarViewModel.cs` 删除。
- **测试锚点**：`BuildCompatibility.cs` 新增 `KitLocalserverPageShouldConvergeSurfacesSizingA11yAndSharedUsageTelemetry`（钉卡面样式、30 / 22 覆写消失、ComboBox r4、18 条英文字面量清零、resw 键、令牌化与启发式删除、显式类别接线、共享微条形双侧接线）；`General.cs` 既有断言随重构更新（`"Temperature"` → `"UsageCategory.Temperature"`，语义不变）；单测 237 → 238。
- **修复**：`StatusBarViewModel` 首版多行 switch 直做实参触发 SA1118（本仓该规则为 error），改局部变量后通过——SA1118 与 SA1210 一并记入工具链记忆。

**验证**：构建 exit 0（`[BUILD] Local projects built`）；单测 238/238 全绿；Module 页烟雾 PASS；Dashboard(zh-CN) 烟雾 PASS（含改版明细条渲染）。静态复核：6 个改动 XML 全部良构；resw en 891 / zh 863、zh⊆en、零重复、en-only 28 不变；页内英文 ToolTip / AP.Name 字面量扫描清零。

**B2、B3、B4、B5、B6 均未提交。**

## 附录 J：B7 执行记录（2026-10-29）

**范围**：UDPtest 全量 resw 与结构收敛（附录 C2 UDPtest 段全 8 项 + B6 裁定的行开关接入收口）。

- **resw 全量接入**：UVM `IsChinese` 57 处与 URow 11 处双语内联、页面 17 处 ToolTip 字面量 + 10 处 AP.Name + 1 处无键 Placeholder 全部迁入 resw——新增 108 键对/语言（XAML 25 + UVM 60 + URow 23；en 891 → 999、zh 863 → 971，en-only 仍 28）；`ResourceExtensions` 新增 `GetLocalizedOrFallback` / `FormatLocalized`（带回退取值，替代各处 IsChinese 三元）；顺带把 `Monitoring error`、`Failed to start/stop/...` 等纯英文 ShowError 文案一并本地化，评估逻辑的字符串比较按原值保留（en/zh 值与原字面量逐一相等）。
- **响应式**：复制 LS 的 `ControlSizeTrigger`（760px）模式——窄窗时 Assessment 卡下折整行（`UdpAssessmentCard` 的 Row / Column / ColumnSpan / Margin 四 setter）；两张表套横向 `ScrollViewer` 兜底列 MinWidth（合计约 786px）。
- **卡面**：hero / 身份条 / Realtime / Assessment / TCP 表 / UDP 表 6 处内联 Border → `KitHeroSurfaceStyle` / `KitCardSurfaceStyle`（内部 Padding 按原值保留）。
- **交互语义**：周期按钮 4 枚改 `ToggleButton` + `KitFilterPillToggleStyle`，选中态改由代码统一 `IsChecked` 同步（删 AccentPeriod/AccentHealth 私有样式与样式互换逻辑）；失败刻度 `TcpBrush` → `SystemFillColorCriticalBrush`；`KitPeriodButtonStyle` 零引用删除。
- **尺寸 / 行控件**：选线路 ComboBox 宽 140 → 200 与 LS 统一；两处行 ToggleSwitch 接入 `RightAlignedCompactToggleSwitchStyle`（B6 裁定在本批收口）。
- **a11y**：展开按钮、周期胶囊、ComboBox 等补 resw 化 AP.Name / ToolTip（共 25 键）。
- **测试锚点**：`BuildCompatibility.cs` 新增 `KitUdpTestPageShouldFullyLocalizeAndConvergeSurfacesSizingAndInteraction`（钉卡面、4 胶囊 / 2 行开关计数、字面量清零、ComboBox、VSM、失败色、`IsChinese` 清零与 resw 键）；单测 238 → 239。
- **修复**：pin 测试首版 `Split(new[] { … })` 触发 CA1861（测试工程 TreatWarningsAsErrors），改 `Split(string)` 重载通过，并记入工具链记忆。

**验证**：构建 exit 0（`[BUILD] Local projects built`）；单测 239/239 全绿；Module 页烟雾 PASS（含 UDPtest 页导航与渲染）；Dashboard(zh-CN) 烟雾 PASS。静态复核：全部改动 XML 良构；resw zh⊆en、零重复、en-only 28 不变；页内 ToolTip / AP.Name 英文字面量与两个 VM 的 `IsChinese` 扫描清零。

**B2、B3、B4、B5、B6、B7 均未提交。**

## 附录 K：B8 执行记录（2026-10-29）

**范围**：AIHub 收敛（附录 C1 AIHub 段全 8 项；V1 约 232 处 `IsChinese` 全量迁移维持观察项，触达控件随批走 resw）。

- **resw 补键与本地化**：`AIHub_Segment_SecurityAudit` / `AIHub_Segment_Optimization` 两键对补齐（x:Uid 缺键导致的 Segment 硬编码显示收口）；补键时发现两对历史 casing 错配死键（`AiHub_Segment_*.Content`，全仓零消费方、B3 悬空扫查所记缺口根因），本批删除。FindingDetailsDialog 8 处内联三元（DC:40 / 57 / 70-77）迁 resw（新增 `FindingDetails_EventIdFormat` / `_AffectedNotAvailable` / `_Copy*` 6 键共 8 键对）；AC 复制报告 10 处中文字面量随同批迁 resw（`AIHub_CopyReport*`），AC `IsChinese` 清零；触达控件 VM 标签 `FastScanLabel` / `FullScanLabel` 走 resw。本批 resw 净增 23 键对 / 语言（25 增 2 删）：en 999 → 1022、zh 971 → 994，en-only 仍 28，删后零重复（补键后曾出现大小写重名，已随死键删除消解）。
- **卡面 / 徽章 / 瓦片**：6 处审计主卡全数归共享表面（hero → `KitHeroSurfaceStyle`；健康总览 / 发现项 / 下载 / 缓存 / 策略 → `KitCardSurfaceStyle` + 原 Padding），AI 报告卡顺手收编为第 6 处 `KitCardSurfaceStyle`；10 处内联图标徽章归位（40 / 36 / 34 共 8 处 → `KitIconBadgeStyle`，28 ×2 → `KitIconBadgeSmallStyle`；语义色保留在内层图标前景，Success / Caution 底随样式化统一为 Accent 洗底）；指标数字 10 处（审计高 / 中 / 低 + 动态 4 格 + 优化 3 瓦片）归 `KitMetricTileValueStyle`。
- **尺寸 / 圆角**：视图筛选 2 枚 ToggleButton 的 `Height="32"` 覆写删除、改挂 `KitFilterPillToggleStyle`（全页 12 处胶囊同款）；orphan r10 胶囊 → r6 ×2、控件 r6 → `ControlCornerRadius`(r4) ×3；r8 全部随表面样式化，页内零 orphan 圆角。
- **a11y**：模块 ToggleSwitch / 审计日期 CalendarDatePicker / 策略内容 TextBox 补 resw 化 AP.Name（3 键对）；快速 / 全量扫描按钮补 AutomationId 并绑定 Name；优先处理卡按钮绑定 `AuditPriorityTitle` 为 Name；两条 InfoBar 补 `LiveSetting="Assertive"`；搜索框既有绑定 Name 达标免改。
- **健康环**：实测 WinUI 1.8 `generic.xaml` 中 ProgressRing 模板为 Lottie `AnimatedVisualPlayer`、无内建 Track 元素（`Background` 属性不被模板消费），故“单一 ProgressRing（Track 主题）”以单值环 + 主题 Ellipse 轨道（`ControlStrokeColorDefaultBrush`、88×88、厚 6）实现——假轨道环（Value=100 + Opacity 0.25 叠放）删除；记分弧仍由 `HealthScoreRingStyle` 按档着色。椭圆厚度 6 对齐 LS 健康环视觉权重（判断值）。
- **响应式**：AC 三个 SizeChanged 处理器（640 / 760 / 960 元素宽阈值）删除，改页根 4 组 `ui:ControlSizeTrigger` VSM（readiness / overview / 两个 command header），阈值与 setter 逐一等价；Optimization header 宽态 Right 对齐由显式 setter 承接（原由处理器运行时赋值），窄态两 header 均 Row 下折 + 左对齐。未采用窗口宽 `AdaptiveTrigger`：ModulePageSmoke 直接按元素 `ActualWidth < 960 / 760` 断言行号（:1052-1073），元素宽度语义为唯一兼容解。
- **核实项收口**：A:22 注释经核实成立——ModulePageSmoke 对记分 / 就绪图标 / 详情徽章有对比度断言（:1097-1148），单测亦钉 `AiReadinessStyle(ViewModel.AiReadinessLevel)` 用法；注释改写为准确表述（就绪 / 健康样式按级绑定并受双测锁定）。
- **测试锚点**：`BuildCompatibility.cs` 新增 `KitAiHubPageShouldConvergeSurfacesLocalizationA11yAndResponsiveLayout`（钉表面样式、徽章 8+2 / 瓦片 10 / 胶囊 12 计数、r10 与假轨道环清零、`ControlSizeTrigger` 8 处、`SizeChanged` 清零、a11y 与 resw 键、双 code-behind `IsChinese` 清零）；既有锚点（AiHubSettingsTests / General.cs）全数保持通过；单测 239 → 240。

**验证**：构建 exit 0（`[BUILD] Local projects built`）；单测 240/240 全绿（含新增钉与 resw 奇偶校验）；静态复核：全部改动 XML 良构；resw en 1022 / zh 994、zh⊆en、零重复、en-only 28 不变；页内 `SizeChanged=` / `CornerRadius="10"` / `Opacity="0.25"` / `FontSize="24" FontWeight="Bold"` 扫描清零。**模块 / 仪表盘烟雾本批次未能完成**：自 10:35 起宿主安全层对新编译的未签名烟雾 harness（`ModulePageSmoke.exe` / `DashboardPageSmoke.exe`）的启动实施滞留——四次尝试（工具后台树 ×2、用户交互终端 ×1）全部停在 harness 启动一步（对比：csc 编译与应用宿生成均已完成、通用进程创建 0.2s 正常、同套烟雾今晨 09:07 尚可全绿）——判定为宿主环境态变化、非本批代码问题。AIHub 页渲染 / 响应式断言（三档窗宽 × 三 Tab）与 Dashboard 回归渲染**留待 B10 全量验证补跑**；若届时宿主仍拦截，建议为仓库目录加安全套件白名单后再跑。

- **过程修复（不计入上款）**：(1) 两处检查命令自身假象——删键校验首版 rg 模式大小写笔误、钉核对的 `rg -o` 括号被当作正则分组——均以 `-F` 定串复核澄清，无产物返工；(2) 烟雾脚本加固——两个 `Invoke-*.ps1` 的 finally 原为无参 `WaitForExit()`（超时路径下可能等待句柄继承链而永不返回），已改有界 `WaitForExit(5000)` 并在超时路径先打印 harness 日志尾再抛错（为 harness 超时类故障兜底；不能绕过上述启动拦截）；(3) 多轮强杀后的 `{Module,Dashboard}PageSmoke.*` 残留生成物已清除（属脚本自检前置）；孤儿 dash 曾写 `%LOCALAPPDATA%\Kit\language.json` 未及还原（现值 zh-CN），后续成功重跑会以“备份 / 还原”闭环自愈；(4) 首次模块运行结果随强杀不可恢复、判定作废。

**B2、B3、B4、B5、B6、B7、B8 均未提交。**

**补记（B9 轮复测，2026-10-29）**：宿主对新编译烟雾 harness 的启动滞留为瞬态——B9 轮中模块 / 仪表盘 / NetMap 三场烟雾均可正常启动与运行；模块烟雾复跑全绿（含 AIHub 三档窗宽 × 三 Tab 的 18 帧渲染、明暗主题与对比度断言），**B8 的渲染验证缺口由此闭合**，B10 的该补跑项移除。

## 附录 L：B9 执行记录（2026-10-29）

**范围**：NetMap 收敛（附录 C1 NetMap 段全 4 项），执行面两处增量——Dashboard 四处出口着色绑定连带迁移（转换器类两消费者齐迁后删除）、Surface.xaml 共享字典注释更正。Hero 卡内联 Border 不在 C1 清单内，留待 B10 复核。

- **状态色强类型化**：字符串拼 key 的 `NetMapHealthStyleConverter` 删除，改 `NetMapHealthStylesStore`（显式 switch 映射健康态→键前缀，杜绝枚举名 `ToString()` 隐式耦合）+ 三处承接：模板内由模型属性 `DotStyle` / `TextStyle`（`Update()` 同步通知）绑定，页级与画布改 `HealthDotStyle` / `HealthTextStyle` / `HealthBoxStyle` 实例函数（经私有 `HealthStyles` 直取嵌套字典）；`NetMapHealthStyles` 字典与 12 个键名原样保留（烟雾的样式引用相等断言不受影响）。Dashboard 四处绑定改 `EgressTextStyle(ProbeHealth)`（读 App 级 `KitEgressStatusStyles`；未知态回退 `UnknownText`，较旧转换器对 `Warning` 的查空更稳）。
- **地图绘制令牌化**：路线 `StrokeThickness = 3` 运行时覆写删除（统一取 `NetMapRouteLineStyle` 的 2.5）；选中标签 28px 并入 `NetMapMapLabelStyle`；MapBody 440 列改 `MapBodyTableWidth` 常量；拟合常量 800 / 340 / 1 / 4 具名化（`MapFitMaxWidth` / `MapFitMaxHeight` / `MapFitMinScale` / `MapFitMaxScale`）。
- **a11y / 本地化**：30 处英文直量（`AutomationProperties.Name` / `ToolTipService.ToolTip` / `Text` / `Header` / `ModuleTitle` / `ModuleDescription` / `Description`）清零，全部由既有 `x:Uid` 键承接；`NetMap_HopList` 补无障碍名（新增 1 键对：en 1022 → 1023、zh 994 → 995，en-only 28 不变）；EnableSwitch 经既有 `x:Uid="NetMap_Enable"` 已具名称（核验免改）；HyperlinkButton 的 `GeoLite2 · P3TERX/GeoLite.mmdb` 为产品名，保留。备注：en 值较旧内联更规范（如 "Start detection" → "Start network detection"），烟雾无字面依赖、核验通过。
- **测试锚点**：`BuildCompatibility.cs` 新增 `KitNetMapPageShouldConvergeHealthStylesAccessibilityAndMapTokens`（钉转换器清零、类型化映射、常量化、样式与旧直量清零、a11y 名与 resw 键，约 50 条直量断言）；既有锚点（`NetMapStopMustAlsoCancelRunningAiRouteCheck` / `NetMapMtrSnapshotMustOnlyBeSavedOnDemand` / 模块目录与资源钉）零改动、全绿；单测 240 → 241。
- **过程修复（不计入上款）**：(1) 首轮构建 2 条 CS0103——`NetMapServiceRow` 只补了通知行、漏补 `DotStyle` / `TextStyle` 属性，致服务行模板绑定悬空，补齐后通过（实现疏漏，非设计变更）；(2) 烟雾首跑捕获真缺陷——WinUI `ResourceDictionary` 索引器**键缺失即抛 `COMException 0x80004005`**，页级函数最初在顶层 `Resources` 查嵌套字典内的键、页面加载即崩；改为经 `HealthStyles` 直达嵌套字典后修复（该缺陷由烟雾真实拦截，记入工具链）；(3) 烟雾二跑全部断言通过、仅 `Capture` 的 WinRT `StorageFolder` 落盘撞宿主写拒——按 B1 的 Dashboard 定式移植为内存编码 + `File.WriteAllBytes`（新 harness 落盘约定，记入工具链）；(4) 清杀纪律再教训：除规避自身 pwsh 命令行外，还须规避**父 bash** 命令行（其含被启动脚本的连续路径串）——本轮与 B8 各踩一次，均无产物损害；另记 harness 日志为追加写（判读看新增段，末行 `PASS` 仍可作通过标志）。
- **测试环境**：B8 轮记录的"新编译烟雾 harness 启动滞留"为瞬态，本轮模块 / 仪表盘 / NetMap 三场烟雾均可正常启动运行；**B8 的 AIHub 渲染验证缺口由本轮模块烟雾复跑闭合**（已回填附录 K 补记）。

**验证**：构建 exit 0（`[BUILD] Local projects built`）；单测 241/241 全绿；NetMap 烟雾 PASS（含 9 帧截图与全部功能 / 样式 / a11y 断言）；模块烟雾 PASS（含 AIHub 三档窗宽 × 三 Tab 18 帧渲染与明暗主题对比度断言）；Dashboard(zh-CN) 烟雾 PASS。静态复核：改动 XML 良构；resw en 1023 / zh 995、zh⊆en、零重复、en-only 28 不变；页内转换器引用与旧英文直量扫描清零。

**B2、B3、B4、B5、B6、B7、B8、B9 均未提交。**

## 附录 M：B10 执行记录（2026-10-29）

**范围**：收尾与全量验证（§2 B10 行）：手工过检（浅 / 深 / HC × 键盘 × 动画关闭）、三套烟雾、全量单测、README / changelog 同步；另含附录 C1 留待 B10 的 NetMap hero 卡复核。

- **收尾代码项（hero 卡复核）**：NetMap hero 主卡的四处内联表面属性（`OverlayCornerRadius` + `CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush` + `BorderThickness=1`，与 `KitHeroSurfaceStyle` 的 setter 集逐项相等）收敛为共享样式引用；`Views/**` 全量普查确认此为**全仓最后一处手写 hero 表面**（AIHub / UDPtest / Localserver 早前已收敛，Dashboard 与常规页走 `KitHeroPanelStyle`），视觉零变化。`BuildCompatibility` 的 NetMap 收敛钉并入两条断言（"用共享样式" + "该页内联表面画刷清零"）；**未新增用例，单测数仍 241**。
- **§5-8 第 8 项复检（两套标题栏）**：`ShortcutConflictWindow` 以原生 `TitleBar` 控件承接（`ExtendsContentIntoTitleBar` + `SetTitleBar(titleBar)` 满足拖拽语义、`IconSource` 图标、`ShortcutConflictWindow_TitleTxt` resw 本地化标题、`ThemeService` 主题跟随），与壳层自绘 `Controls/TitleBar` 的视觉与拖拽约定一致——**无需改动**（结论已回填该条与附录 C1）。
- **手工过检口径**：可自动化的部分全部自动化。浅 / 深两态由三套烟雾覆盖（`Invoke-ModulePageSmoke.ps1` 覆盖 Awake / LightSwitch / Localserver / UDPtest / AIHub 五页 + 仪表盘 + 常规页，含明暗主题继承与文字 / 状态对比度断言；NetMap 由其专属烟雾覆盖，含 9 帧渲染）；高对比度以静态审计替代（`Colors.xaml` 三主题字典键 **Dark 32 / Light 32 / HighContrast 32、缺集为空**，且 `Views/**` 与 `Controls/**` 无任何硬编码颜色字面量）；键盘可达性由烟雾断言与各批 a11y 钉（AutomationId / Name / LiveSetting）承接。**余下三项须人眼完成，已列为用户侧清单**：① 系统切高对比度主题实机过检（令牌已就位，未见实际渲染）；② 整页键盘走查（Tab 序列与焦点可见性）；③ 关闭系统动画后确认无动效（全应用仅 `UsageProgressBar` 显式读 `UISettings.AnimationsEnabled` 门控，其余卡片过渡走框架 `SettingsCardsAnimations`，须实测）。
- **文档同步**：README / README_zh 的 UI 能力句补"无障碍元数据（键盘可达控件、屏幕阅读器名称、高对比度配色）"维度；changelog 双语各新增 `### 3.3.0` 段（六条内容 + 版本标识一条：共享 Fluent 表面体系、层级排版与语义令牌、响应式布局、无障碍、本地化收尾、状态着色强类型化）——**版本号随本批升至 3.3.0**（`src/Version.props` 与 `src/PackageIdentity/AppxManifest.xml` 同步）；§3 追加模块套件补充口径 bullet。
- **观察项归档（本批明确不改）**：AIHub V1 约 232 处 `IsChinese` 全量迁移、AIHub 结果列表虚拟化（A:820 / 830 / 1121 / 1189，MaxHeight 320-520）——维持审计口径的观察项。
- **过程与教训（不计入上款）**：(1) 两处自检口径写错但未致产物损害——测试文件中 `KitHeroSurfaceStyle` 的期望计数（该串在新增块内出现两次，另有他页钉 3 处）、`B10 复核` 子串在原句"留待 B10 复核"中本就存在（期望 1 实为 2）；两处写入均先于自检执行，复核后确认落盘正确。(2) **判据定位教训**：`find` 搜 interop 产物时先命中**其他会话 worktree**（`.claude\worktrees\relaxed-moser-7f5375\src\...\bin\...`）的副本，一度把"别人的树"当作本树证据；搜产物须排除 `.claude\worktrees\**`（已记入工具链）。(3) 记忆追加首轮失败：pwsh 内 `/tmp/...` 被解析为当前盘根（`C:\tmp\...`），且嵌套数组被展平致参数错位；改为"片段落仓库内 + 绝对 Windows 路径 + 显式函数调用"后落盘。

**验证**：构建 exit 0（`[BUILD] Local projects built`）；Settings 单测 **241/241**（failed 0、skipped 0，9s 715ms）；三套烟雾全 PASS——NetMap（9 帧渲染与全部功能 / 样式 / a11y 断言）、模块（含 AIHub 三档窗宽 × 三 Tab 18 帧渲染、明暗主题与对比度；`%LOCALAPPDATA%` 写回按 CHECK-SKIP 跳过）、Dashboard(zh-CN)；烟雾残留清理 `LEFTOVERS-GONE`。补充模块套件全绿：NetMap **102/102**、Localserver **29/29**、UDPtest **28/28**、Awake **8/8**、Awake.ModuleServices **3/3**、AiHub **355**（354 通过 / 1 跳过 / 0 失败）、interop **1/1**（本树产物）。静态复核：改动 XML 良构；resw en 1023 / zh 995 **未变**（本批未动资源键）；六个改动文件 BOM 与 HEAD 逐字节一致、`git diff` 无整文件重写。

**B2、B3、B4、B5、B6、B7、B8、B9、B10 均未提交。**

**补记（升版后复验，2026-10-29）**：版本号由 3.2.0 升至 **3.3.0**，同步面共 9 处：`src/Version.props`（唯一条 `<Version>`）、`src/PackageIdentity/AppxManifest.xml`（`Version="3.3.0.0"`）、README 双语第 7 行、changelog 双语段名（`### Unreleased` / `### 未发布（Unreleased）` → `### 3.3.0`，各补"Version Identity / 版本标识"一条）、附录 M 本段落。测试与烟雾无版本串硬编码；消费版本的三族断言（Version.props ↔ AppxManifest 身份一致性、已构建程序集 FileVersion、README 动态句）随升版复验通过。升版后全链复跑：构建 exit 0（`[BUILD] Local projects built`）、单测 **241/241**（failed 0）、NetMap / 模块 / Dashboard(zh-CN) 三套烟雾全 PASS、残留清理干净；模块套件与 interop 复跑全绿：NetMap 102 / Localserver 29 / UDPtest 28 / Awake 8 / Awake.ModuleServices 3 / AiHub 355（354 通过 / 1 跳过）/ interop 1。升版前全量日志留存 `TestResults/b10-tmp/prev32/`。残留 `3.2.0` 仅存于 changelog 历史条目（第 18 / 26 / 842 / 850 行）与 `optimization-review-2026-10-28.md` 历史基线句，均按原样保留。备注：链脚本内一行残留扫描因后台环境 PATH 无 `rg` 未执行（非闸门；该扫描已在主会话单独完成，即上行清单）。
