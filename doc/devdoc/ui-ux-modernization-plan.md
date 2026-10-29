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
- 单测：`Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.exe`（现 234 项；改动会触发源码断言，随批同步）。
- 烟雾（先关闭全部 Kit 进程）：`tools\tests\SettingsUI\Invoke-ModulePageSmoke.ps1 -Configuration Debug`；`Invoke-DashboardPageSmoke.ps1 -Configuration Debug -Language zh-CN`；`tools\tests\NetMap\Invoke-NetMapPageSmoke.ps1`。
- 视觉手工过检矩阵：浅色 / 深色 / 高对比 × 每页 × 键盘导航；系统动画关闭时无动效。

## 4. i18n 现状数据

- zh-CN 缺 289 键；仍被引用 22 键（19 个 `General_SettingsBackupAndRestore_*` + 3 个更新标题；`No/Yes/alias/assembly` 为扫描误报）。其余约 267 键为 en-only 且无引用（上游残留）：删除或注记。
- QuickAccess 经 `Kit.QuickAccess.pri` 链接 `Settings.UI\Strings\**`（`Kit.QuickAccess.csproj:55-57`），与 Settings 共用同一套双语资源。
- `Settings.UI.Controls\Strings\en-us\Resources.resw` 的 6 个真实键（Dashboard_SortBy* / NewInfoBadge / GPOWarning）已全部由 Settings.UI resw 双语覆盖（含 QA 排序三键，zh:148-154；en:1173-1179），其余 4 键为 resx 模板残留 → 该文件整体删除（B3）。
- runner 托盘：`runner.base.rc` 单语言英文；`tools\build\convert-resx-to-rc.ps1:49-50` 已含 zh-Hans / zh-CN 映射，新增 `src\runner\Resources.zh-Hans.resx` 并接入构建语言循环即可。

## 5. 前代遗留项吸收（v9）

- a11y 收尾 → §1.3 规则 6，落各页批次；HealthStyles 归并 → 规则 4（随 B6 / B9）；死键清理 → B3；手工过检 → B10；托盘本地化 → B3。
- `UsageProgressBar` 类别色依赖 `AutomationProperties.Name` 字符串匹配（Dashboard / NetMap / Localserver 共用）→ 改为显式附加属性（如 `kit:UsageCategory`），随首次触达该控件的批次执行。
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
8. **两套标题栏收敛**：ShortcutConflictWindow 原生 TitleBar 与壳层自绘 TitleBar 策略统一（新窗口一律原生；壳层评估迁移，本轮先对齐视觉与拖拽语义）。**B1 复核**：原生 TitleBar 默认语义已足够，无需改动；视觉一致性随 B10 复检。
9. **测试锚点同步**：BuildCompatibility.cs:3127-3148 / :2792 / :2998 / :3190-3194；General.cs:127-164 / 184-191 / 207-211 / 482-494 / 696-699 / 733 / 746 / 761；FrameworkPrivacyDefaults.cs:264-269；UDPtest.cs:112；Localserver.cs:147。**✅ B1 已执行**：构建绿 + Settings 单测 234/234 通过（未需补锚点）；烟雾结果见下方执行记录。

## 附录 B：Awake / LightSwitch / QuickAccess 修改清单（主会话审计）

**Awake（AwakePage.xaml，113 行）**

- 交互控件补 AutomationId：启用 ToggleSwitch、模式 ComboBox、两个 NumberBox、显示 ToggleSwitch（沿用 `*_Awake` 命名）。
- 品牌决策：SecondaryLink（`:110`）指向 awake.den.dev（上游作者）；保留署名或改 Kit 仓库链接。
- 其余保持（已是 Fluent SettingsCard 范式，含 `SettingsCardsAnimations`）。

**LightSwitch（LightSwitchPage.xaml，358 行）**

- `:28` StackPanel 补 `ChildrenTransitions="{StaticResource SettingsCardsAnimations}"`（与 Awake 对齐）。
- 死码清理：`LightSwitchPage.xaml.cs:367-394`（无对应 XAML 的 AutoSuggestBox 处理器 + 注释块）、`LightSwitchPage.xaml:104` 注释行；`SearchLocations` / `SelectedCity` 关联残留评估。
- Dialog a11y：`SyncLoader` / `LocationErrorText` / `LocationResultPanel` 补 Name / LiveSetting；夜间模式 HyperlinkButton（`:162-166`）统一到 TextButtonStyle。
- `TimelineStyles.xaml` 从 `App.xaml:24` 全局合并收回页级资源；Timeline 三态配色走语义令牌核验。
- 既有 AutomationId（Toggle_LightSwitch、ModeSelector 等）保持不动。

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

**NetMap**

- 状态色强类型化：24 个页内样式 + `NetMapHealthStyleConverter` 字符串拼 key（N:20-34、V2:29-36、NC:251 / 441）→ 仿 AC:48-61 的 Style 映射函数。
- 地图绘制令牌化：线宽统一（NC:398 的 3 vs N:64 的 2.5）；选中标签 28px（NC:331-336）字体令牌化；MapBody 440 列（N:400）与拟合常量（NC:372）评估收敛。
- a11y / 本地化：Start / Stop / AI 检测（N:294-333）与 Details（N:159-206）硬编码英文 AutoName / Tip → 改 resw（键已存在）；HopList（N:441）、EnableSwitch（N:242）、Browse ×2（N:612 / 623）、应用设置（N:673）补 Name；30 处英文 `Text=` 兜底清理。
- 测试锚点：BuildCompatibility.cs:2858 / 2884 / 1603-1604 / 1812-1813 / 1956-1957 / 2624-2625；NetMapAiServiceTests.cs:25-85；Localserver.cs:112-114（UsageProgressBar 契约；该控件 AIHub/NetMap 两页均未使用）。

## 附录 C2：Localserver / UDPtest 修改清单（子代理审计 2026-10-29）

图例：LS=`LocalserverPage.xaml`（779 行）、LSVM=`LocalserverViewModel.cs`、U=`UDPtestPage.xaml`（618 行）、UVM=`UDPtestViewModel.cs`、URow=`UDPtestLineRowViewModel.cs`。

**Localserver（骨架已现代：Kit 样式 / 全 resw / 响应式 VSM；欠账为局部收敛）**

- 尺寸 / 圆角：按钮五档（22 / 26 / 28 / 30 / 32）收敛为 28 / 32；删除 `LS.xaml:699-702` 对 KitCompactIconButtonStyle 的 30×30 覆写；ComboBox 圆角 r6（LS.xaml:509）对齐 U 页 r4；hero 卡硬编码 `CornerRadius=8`（LS.xaml:53）改 `OverlayCornerRadius`；表头 Padding（LS.xaml:202）与 U 页对齐。
- 卡片：3 处内联 Border（LS.xaml:53 / 196 / 690）→ `KitHeroSurfaceStyle` / `KitCardSurfaceStyle`。
- a11y / 文案：行展开 / 更多 / 日志折叠等图标按钮（LS.xaml:293 / 300 / 699）补 AP.Name；"Fix"（LS.xaml:597）等英文硬编码入 resw；22 处 ToolTip + 9 处 AP.Name 英文改 resw 键。
- 颜色：`UsageProgressBar.cs:100-111` 硬编码 ARGB → 主题令牌；`ThemeBrushHelper.cs:13-17` 评估令牌化。
- 合并：与 UDP 的"微条形"实现（LSVM:112-137 六档 vs RecentProbeBarViewModel 三档）合并为共享一套。
- 测试锚点：Localserver.cs:74-133（钉 KitCardSurfaceStyle / KitStatusPillStyle / UsageProgressBar / `Localserver_*` 键；禁止 LastError、EnvTrendLinePath 等）。

**UDPtest（视觉骨架已复刻 LS；差 resw 化、响应式、卡面、交互语义）**

- resw 全量接入（最高优先）：UVM 57 处 + URow 11 处 IsChinese 内联、U.xaml 27 处 ToolTip + 12 处 AP.Name + Placeholder（:185 / :196）英文硬编码 → 按 Localserver 样板平移（`UDPtest_*` 骨架 51 键 en/zh 已对齐）。
- 响应式：复制 `LS.xaml:20-41` 的 ControlSizeTrigger（760px）模式；表格 MinWidth 合计约 750-780px 需窄窗兜底。
- 卡片：6 张内联卡（U.xaml:250 / 354 / 403 / 488 / 528 / 571）→ `KitCardSurfaceStyle` / `KitHeroSurfaceStyle`。
- 交互语义：周期按钮改 ToggleButton / RadioButtons（现为代码换 Style，U.cs:100-124、U.xaml:19-23），对齐 `KitFilterPillToggleStyle`；失败刻度 `Stroke="{StaticResource TcpBrush}"`（U.xaml:478）改 `SystemFillColorCriticalBrush`。
- 尺寸：选线路 ComboBox（U.xaml:439）与 LS 统一；表格列宽三套重复（U.xaml:49 / 111 / 539 / 582）收敛为共享定义。
- 行控件：行 ToggleSwitch 40×36 ×2（U.xaml:72 / 138）与 LS 同型；`RightAlignedCompactToggleSwitchStyle`（Button.xaml:79-648，全仓零使用）二选一：启用替换四处内联，或删除该模板克隆。**B1 暂不删**，B6 / B7 一并裁定。
- a11y：展开按钮（U.xaml:139）、周期按钮（:469-472）等补 AP.Name。
- 测试锚点：UDPtest.cs:72-98（钉 TcpLineTemplate / UdpLineTemplate / UdpExpandButton；禁止 TcpExpandButton、"Restore Default"）。

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
