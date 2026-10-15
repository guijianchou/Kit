# Kit UI/UX 优化方案（v3）— 遗留收尾 + Mica / WinUI 3 深化

> 状态：**下一步执行稿** ｜ 2026-10-14 ｜ 基线：`main@782c626`（v2.3.11 + 清理轮，已合入并通过验证）
> 说明：v1 逐按钮/逐折叠/逐下拉规格与 v2 as-built 对照已随 git 历史存档（取回：`git show 782c626:doc/devdoc/ui-ux-refactor-plan.md`）。本稿只列【未完成项】与【新增深化项】，供下一轮直接执行。

## 0. 现状基线

- 验证（2026-10-14）：构建 exit 0；4 项烟雾（ModulePage / Dashboard en+zh / NetMap）PASS；单测 196/212（16 项历史遗留 → §2.1）。
- 设计基础：`Themes/Colors.xaml`（唯一色彩定义处）、`Styles/Button|TextBlock|Surface.xaml` 样式族、页级 `SettingsCardsAnimations`（13 处引用）。
- 材质：主窗 `MicaBackdrop Kind="BaseAlt"`；快捷方式冲突窗 `MicaBackdrop`；QuickAccess `DesktopAcrylicBackdrop`；全透明内容链（审计见 §3.1）。
- **已定决策**（不再改动，除非用户重新拍板）：
  - 动画一律系统原生、禁自定义 Storyboard（唯二保留：QA `FlyoutMenuButton` 图标缩放、`UsageProgressBar` 数值动画）。
  - General 分类 chips 保留令牌化彩色（Section* 体系 + HC）；QuickAccess `App.xaml` 3 处 hex（`LayerOnAcrylicFillColorDefaultBrush`）为有意例外。
  - `SettingsPageControl` 不挂入场动画（2.3.11 白屏性能修复）；AIHub 保持单文件（不拆 UserControl）。
  - 动态键族必须保留：NetMap `Text(shortKey)`、Localserver `$"Localserver_State{suffix}"`；MRT 大小写不敏感（PRI277）。

## 1. 执行纪律（每批次必守）

- 只动 UI/UX 层；x:Name / AutomationId / x:Uid 契约零改动；文案改动 en-us + zh-CN 同步。
- 新增面板/容器一律走半透明材质体系（`CardBackground*` / `LayerFill*` / `SubtleFill*`），禁新增不透明面与字面 hex。
- 验证链：build.ps1（exit 0）→ 烟雾（pwsh 7，先关 Kit）→ 单测（MSTest 直接运行器）；命令见 §5。
- 核心零改动守卫：`git diff --name-only` 仅允许 `src/settings-ui/**`、`tools/tests/**`、`doc/**`；越界项（如 §2.1 的 sparse 清单）须单列提交并说明。

## 2. 遗留收尾

### 2.1 单测 16 项修复（最高优先：恢复契约绿）

> 对照实验结论（2026-10-14）：16 项失败在重构基线 `4da08bd` 即存在，与批次 0–6 及清理轮无关；详见下表的族与建议。

| 族 | 失败测试（文件:行） | 原因 | 建议动作 |
|---|---|---|---|
| 实验特性移除遗留 ×6 | BuildCompatibility.cs:1104 / 1721 / 3093；General.cs:131 / 458；FrameworkPrivacyDefaults.cs:331 | b108d90（09-25）移除实验特性后，测试与 runner `general_settings.cpp` 未同步 | 按"已移除"改写断言；核对 `enable_experimentation` 残留是否有意保留，死代码则另单删除 |
| 版本与元数据 ×2 | BuildCompatibility.cs:1342；General.cs:748 | `src/PackageIdentity/AppxManifest.xml` 版本停留 2.3.7；README 措辞已改为「Current source version」 | sparse 版本改为随 `Version.props` 递增（**越界项，单列提交**）；README 断言按现措辞更新 |
| GeneralPage 结构 ×2 | General.cs:158 / 707 | Startup & permissions 与 Appearance & behavior 顺序、VersionAndUpdate 段 Substring 边界 | 按现布局更新断言 |
| 品牌与文案 ×3 | General.cs:469 / 486 / 514 | Intro 文案、`PT.png` 引用（2.3.7 起替换）、`Kit\Backup` 字面量 | 按现文案/资产更新断言 |
| 模块目录 ×2 | General.cs:539 / 589 | NetMap 激活后目录与 enabled-states 序列化为 6 项，断言仍 5 项 | 断言更新为含 NetMap 的 6 项 |
| 运行器启动清理 ×1 | BuildCompatibility.cs:1218 | `settings_window.cpp` LExit 块含 `WM_CLOSE`，断言要求不含 | 先核对 61d6ed4 意图（很可能是有意行为）→ 更新断言 |

### 2.2 字符串迁移收尾

- UDPtest：`UDPtestViewModel` 57 处 + `UDPtestLineRowViewModel` 11 处 IsChinese 三元 → resw（`UDPtest_*` 前缀）。
- AIHub：`AIHubPageViewModel` 232 处；`AiHubViewModel` 余 13 处（`AiHub_`/`AIHub_` 前缀逐一核对 PRI277）。
- 验收：中英切换无硬编码英文残留；en/zh 键数一致。

### 2.3 HealthStyles 共享化

- 现状：`DashboardEgressStyles`（DashboardPage.xaml:19）与 `NetMapHealthStyles`（NetMapPage.xaml:20；code-behind :244/:434 引用）两份重复。
- 目标：提升为共享资源（`Themes/` 或 `Styles/Surface.xaml`）；键名（`UnknownText/GoodText/ErrorText`、`*Dot/*Box`）保持不变（烟雾断言）。
- 风险：先核对 NetMapPageSmoke 对资源键的查找方式（页面级 vs 全局链）再移动。

### 2.4 死键与残留

- NetMap 死键 ~18：`Text(shortKey)` 使静态不可证死 → 为 `Text()` 增加显式映射/白名单后清理（批次 10）。
- `AiHub_PipelineCard` 2 键：生产代码零引用（仅 AiHubSettingsTests.cs:80 反向断言字符串）→ 随该测试更新一并清理。

### 2.5 可访问性收尾（WinUI 3 原生属性）

- `AutomationProperties.LiveSetting="Polite"`：除 NetMap（3 处）外全缺 → Localserver / UDPtest 状态区、AIHub 工作流状态补。
- `AccessibilityView="Raw"`：AIHub / Localserver / UDPtest 装饰元素为 0 处 → 补。
- `HeadingLevel`：General 0 处（按分组语义补 Level2）；核对其余页卡头齐全（现 AIHub 3 / Localserver 4 / UDPtest 6 / NetMap 4）。

### 2.6 手工过检（批次 12）

浅色/深色/高对比 × 全页；Mica 透出观感与文字对比；键盘导航（Tab 顺序、焦点视觉）；系统"动画效果"关闭时全静默。

## 3. Mica + WinUI 3 深化

### 3.1 材质现状审计（2026-10-14，结论：透出链完整、无阻断）

| 层 | 现状 | 结论 |
|---|---|---|
| 主窗 | `MicaBackdrop Kind="BaseAlt"`（MainWindow.xaml:13） | 保持（带自定义标题栏窗口的推荐值） |
| 快捷方式冲突窗 | `MicaBackdrop`（默认 Base，对话框场景） | 保持 |
| QuickAccess | `DesktopAcrylicBackdrop`（MainWindow.xaml:20-22） | 保持；测试钉死 `<Window.SystemBackdrop>` + `<DesktopAcrylicBackdrop`（BuildCompatibility.cs:3097-3107），勿换类型 |
| 透明链 | App.xaml 页面底色 `Transparent`（:68）；ShellPage 根 Grid `Transparent`（:85）；`NavigationViewContentBackground` 透明覆盖（:151-152）；内容层 Border `LayerFillColorDefaultBrush`（:244） | 保持；Mica 经内容层透出 |
| 卡片/面板 | `CardBackgroundFillColorDefaultBrush` ×22、`SubtleFillColorSecondaryBrush` ×60、`LayerFillColorDefaultBrush` ×3，全半透明 | 保持 |
| 实色面 | 仅 `ShortcutDialogContentControl.xaml:215`（对话框内容，系统设计） | 保留 |
| 字面 hex 背景 | Views/Controls 0 处 | ✓ |

### 3.2 深化项（批次 8 执行）

- **T1 标题栏高对比回退**：`TitleBar.WASDK.cs` 的 `UpdateCaptionButtons` 硬编码 `ButtonForegroundColor=White/Black`、`ButtonInactiveForegroundColor=DarkGray`，HC 下不可读风险 → 检测 `AccessibilitySettings.HighContrast` 时跳过覆写并 `ResetToDefault()`。验收：HC 主题下标题栏按钮可读、悬停正常。
- **T2 动态列表增删过渡**：以下 `ItemsControl` 现仅 Reposition（`SettingsCardsAnimations`），行增删为瞬现 → App.xaml 新增共享 `TransitionCollection`（`AddDeleteThemeTransition` + `RepositionThemeTransition`，建议命名 `KitDynamicListAnimations`）替换用于：
  - Localserver 服务表（LocalserverPage.xaml:233-238）
  - UDPtest TCP 表 / UDP 表（UDPtestPage.xaml:563 / 606）
  - NetMap 服务列表（NetMapPage.xaml:515）；`HopList`（ListView，:412）系统默认已带容器过渡，核对未被覆盖即可。
  - 约束：不改 x:Name/ItemsSource；日志流式容器（高频增行）先不加，观察后再定。
- **T3 AIHub 过渡补齐**：AIHubPage.xaml 现无任何 ChildrenTransitions → 为非虚拟化 `ItemsControl` 的 ItemsPanel 补 `SettingsCardsAnimations`（需要处加 AddDelete）：:820（PriorityFindings）、:825（FindingSections）、:849（嵌套 Issues）、:923（AiReportFindings）、:1101（DownloadCategories）、:1147（UserDirectorySummaries）；`x:Load`（:105/:166）与 `MaxHeight` 虚拟化参数保持不变。
- **T4 评估结论（暂不做）**：ConnectedAnimation（Dashboard 卡 → 模块页）——Win11 原生设置不使用此模式，与"对齐原生"方向冲突；如用户点名再评估。

### 3.3 已核对、保持不动

- Mica 种类与窗口圆角/阴影（DWM 自动）；省电/远程桌面下材质由系统自动回退（无代码）。
- ContentDialog / MenuFlyout / NavigationView 面板：系统材质，不自定义。
- 不引入 ThemeShadow 重投影、不新增自定义 Acrylic 面板（拼凑感风险）。

### 3.4 验收

- T1/T2/T3：构建 + ModulePageSmoke + DashboardPageSmoke + NetMapPageSmoke 回归；动画须尊重系统"动画效果"开关。
- Mica 与 HC 观感并入 §2.6 手工过检。

## 4. 下一步批次（8–12，每批原子提交）

| 批次 | 内容 | 验收 |
|---|---|---|
| 8 | Mica / WinUI 3 深化（§3.2 T1–T3） | 构建 + 3 烟雾 + 手动（HC 标题栏、列表增删、动画开关） |
| 9 | 单测 16 项修复（§2.1） | 212/212（或记录仍无法修复项与原因） |
| 10 | 字符串迁移（§2.2）+ 死键清理（§2.4） | 构建 + 单测源码扫描（AiHubSettingsTests / Localserver / UDPtest） |
| 11 | HealthStyles 共享化（§2.3）+ a11y 收尾（§2.5） | 构建 + NetMapPageSmoke + 单测 |
| 12 | 手工过检（§2.6）+ 文档/changelog 收尾 | 全量回归 |

## 5. 验证与回退

```bash
# 构建（exit 0 = 成功；失败先读 src/settings-ui/build.debug.x64.errors.log）
pwsh tools\build\build.ps1 -Platform x64 -Configuration Debug -Path "src\settings-ui"
```

```powershell
# 烟雾（pwsh 7；必须先关闭 Kit 全部进程）
pwsh -File tools\tests\SettingsUI\Invoke-ModulePageSmoke.ps1 -Configuration Debug
pwsh -File tools\tests\SettingsUI\Invoke-DashboardPageSmoke.ps1 -Configuration Debug -Language zh-CN
pwsh -File tools\tests\NetMap\Invoke-NetMapPageSmoke.ps1
```

- 单元测试：`Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.exe`（本机 vstest.console testhost 崩溃，用 MSTest 直接运行器；禁 `dotnet test`）。
- 回退点：`782c626`（本稿基线；后续批次提交后以最新提交为准）。
