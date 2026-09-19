# AI Hub (AIHub) 模块与插件开发文档

[English](README.md) · [简体中文](README.md)

本文档记录了将 **Locals (Essential Toolbox)** 移植为 **Kit** 原生插件模块（`AIHub`）的系统架构、关键设计决策、安全边界策略、踩坑教训与工程实践，供后续功能演进与长期维护参考。

---

## 1. 模块基本信息与架构定位

`AIHub` 是 Kit 面向 Windows 11 的智能桌面分析与系统维护中枢。它整合了**系统空间与下载整理优化（Optimization）**与**系统事件安全合规审计（Security Audit）**两大核心模块，并与 Kit 主框架的 AI 服务（AI Services）深度互联，支撑基于本地 LLM / 外部 Agent（如 Codex / Pi CLI）的深度诊断、安全合规与智能修复决策链路。

| 属性 | 配置 / 路径 | 说明 |
| --- | --- | --- |
| **模块名称** | `AIHub` | 统一规范标识符，保持与模块接口大小写一致 |
| **模块键名 (Key)** | `AIHub` | 用于设置序列化、GPO 策略匹配及路由注册 |
| **原生接口 DLL** | `Kit.AIHubModuleInterface.dll` | C++ 原生 DLL，导出 `kit_create()`，实现 `KitModuleIface` |
| **托管业务库** | `Kit.AIHubLib.dll` | 基于 **.NET 10** (`net10.0-windows10.0.26100.0`)，提供日志审计、空间扫描与回收站操作 |
| **设置与交互界面** | `AIHubPage.xaml` / `AIHubPageViewModel.cs` | WinUI 3 + Mica Alt 界面体系，内嵌于 Kit Settings 进程中 |
| **设置与 DTO** | `AIHubSettings.cs` / `AIHubProperties.cs` | 注册至 `KitModuleCatalog`、`EnabledModules` 与 `SettingsSerializationContext` |
| **持久化数据目录** | `%LOCALAPPDATA%\Kit\AiHub\` | `State\audit_history.json`（审计历史存档，最多保留 200 条 + 30 天轮转）与 `chains\`（任务策略与 Agent 上下文） |
| **路由与深度链接** | `kit://settings/AIHub` | 注册于 `Common.UI/SettingsDeepLink.cs`，支持外部或全局搜索直接跳转 |

---

## 2. 架构拓扑与执行链路

### 2.1 分层架构
```
┌────────────────────────────────────────────────────────────────────────┐
│                        Kit Settings UI (WinUI 3)                       │
│  - AIHubPage.xaml (Optimization 选项卡 & Security Audit 选项卡)         │
│  - AIHubPageViewModel (双模式扫描、响应式状态绑定、防重入调度)              │
│  - FindingDetailsDialog.xaml (审计事件详情与建议交互弹窗)                │
│  - Settings -> AI Services -> Essential Policy 策略配置面板            │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │ Managed Ref / In-process
┌───────────────────────────────────▼────────────────────────────────────┐
│                  Kit.AIHubLib (.NET 10 Managed Library)                │
│  - EventLogService: System/Application/Setup/Forwarded 聚合；Full 模式另读 Security/防火墙 │
│  - HealthScoreCalculator: 0-100 加权健康评分算法与风险严重等级归纳         │
│  - CacheCleanupService: 安全白名单临时缓存扫描与生命周期分析               │
│  - DownloadOrganizerService: Downloads 根目录分类扫描与单层归档整理      │
│  - RecycleBinHelper: Windows COM IFileOperation 撤销保障封装           │
│  - AuditHistoryStorage: 历史审计 JSON 结构化存储与版本轮转管理           │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │ IPC / Lifecycle Sync
┌───────────────────────────────────▼────────────────────────────────────┐
│                    Kit.exe Runner (C++ Win32 Core)                     │
│  - 动态加载 Kit.AIHubModuleInterface.dll                                │
│  - 模块生命周期（Enable/Disable/Init/Destroy）与全局快捷键调度             │
└────────────────────────────────────────────────────────────────────────┘
```

### 2.2 Essential Policy 决策闭环链路

AI Hub 设立了统一的 AI 驱动策略链路，将系统规则策略、智能体分析与桌面操作安全缝合：

$$\text{Codex / Pi CLI} \longrightarrow \text{Essential Policy} + \text{对应 AGENTS.md} \longrightarrow \text{Analysis Summary} \longrightarrow \text{Suggested Action}$$

1. **Essential Policy 全局策略配置**：
   位于 Kit Settings &rarr; AI Services，通过 `Essential Policy` Expander 折叠组暴露，包含：
   - **Security Audit Policy** (`chains/security-audit/AGENTS.md`)
   - **System Optimization Policy** (`chains/system-optimization/AGENTS.md`)
2. **策略沙箱与隔离加载 (`SecurityPolicyService`)**：
   - 外部扩展模块只能从自身专属目录 (`modules/<pluginId>/Chains/<taskId>/`) 加载策略，严格禁止跨模块覆盖或回退至用户目录。
   - 内建 `aihub` 模块优先读取 `%LOCALAPPDATA%\Kit\AiHub\chains\` 中的自定义策略；若文件缺失则安全降级使用 `TaskPolicyDefaults.cs` 中的内置基准策略，杜绝因外部文件损坏造成服务瘫痪。
3. **结构化产出**：
   Agent 在策略约束下分析 EventLog 异常指标或临时空间沉冗，生成结构化摘要与操作建议，提交给用户确认后方可进入执行流。

---

## 3. 核心功能与安全边界设计

### 3.1 空间优化 (System Optimization)

针对磁盘空间清理与下载归档，界面与交互严格对齐原型标准（`media_1789645291159.png`）：

1. **工作流卡片 (Workflow Card)**：
   - 扫帚图标与清晰操作指引，支持“快速扫描 (Fast scan)”与“深度扫描 (Deep scan)”。
   - 具备实时进度环与动态取消机制 (`CancellationTokenSource`)；执行阶段显示 `(n/N)` 进度。**扫描耗时统计尚未实现**（README 此前声称具备，属超前描述）。
2. **三组核心度量磁贴 (Metric Tiles)**：
   - `Candidates`：扫描发现的可优化文件总数与总体积。
   - `Selected items`：当前勾选待处理的文件项数与体积（支持全部全选/按分类多选）。
   - `Safety boundary`：醒目标注 `Read-only` / `Recycle Bin` 安全保护边界。
3. **两大受控扫描区域 (Scan Areas)**：
   - **Downloads 根目录归档**：严格仅针对 Windows Downloads 根目录下的孤立文件按扩展名进行单层分类（`Documents`, `Archives`, `Images`, `Installers`, `Videos`, `Audio`, `Code`），**绝不递归扫描或移动子文件夹**。
   - **临时文件白名单清理**：仅针对系统用户 `AppData\Local\Temp`、浏览器安全缓存目录进行白名单匹配。
4. **绝对安全三边界 (Triple Safety Boundaries)**：
   - **严格排除系统敏感路径**：绝对禁止扫描或触碰 `C:\Windows`、`Program Files`、`Program Files (x86)`、`System32` 等关键目录。
   - **COM `IFileOperation` 撤销保障**：所有清理删除操作通过底层 Windows Shell 接口执行，设置 `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT` 标志。**所有文件一律送入 Windows 回收站，绝不调用永久物理删除，支持用户在资源管理器中随时 Ctrl+Z 还原**。
   - **只读建议模式**：扫描结果默认仅供查看，必须经用户主动勾选与二次确认才能触发整理或入站操作。

### 3.2 安全审计 (Security Audit)

针对企业和高级开发者的合规审计需求，提供高效的事件日志提取与诊断能力：

1. **多日志源实时采集**：
   通过 `EventLogService` 检索 `System`、`Application`、`Setup`、`ForwardedEvents` 四通道（拓展模式，标准权限）；**完整模式**（需提权）额外读取 `Security` 通道与 Windows 防火墙事件，覆盖登录失败（Event 4625）、提权操作（Event 4672）、服务异常挂起（Event 7031）等高危事件。Security 通道仅检索审计白名单事件 ID（4624/4625/4672/4688/4697/1102/4719 等），避免全量扫描。
2. **0-100 安全健康评分 (`HealthScoreCalculator`)**：
   - 基准分 100 分。
   - 按严重等级加权扣分：高危事件 (High) 每项 15 分（封顶 60），中危事件 (Medium) 每项 6 分（封顶 25），低危警告 (Low) 每项 1.5 分向上取整即 2 分（封顶 15）。总扣分封顶后结果钳制在 0~100。
   - 动态更新健康度评级（Good: 80~100，Warning: 60~79，Critical: <60）。
3. **交互式审计结果网格与详情弹窗 (`FindingDetailsDialog`)**：
   - 结构化呈现事件发生时间、来源通道、事件 ID、风险等级及一句话摘要。
   - 点击“查看详情”弹窗展示原始事件详情、规则生成的建议操作（`PriorityActionText`）与事件证据，支持一键复制诊断信息。
- **AI 深度分析**：审计页签的“AI 深度分析”按钮将事件提交给 security-audit 策略链，返回的 issues 显示在独立 AI 报告面板；优化批量执行会列出失败文件与原因。
4. **审计历史归档 (`AuditHistoryStorage`)**：
   - 每次审计结果写入 `%LOCALAPPDATA%\Kit\AiHub\State\audit_history.json`（源生成 JSON 序列化 + `.tmp` 原子替换），支持 30 天轮转并额外限制最多保留 200 条，保证文件与整表重写开销有界。

---

## 4. 关键踩坑教训与解决方案

### 4.1 Windows SDK `makepri.exe` 资源键大小写碰撞 (PRI277 故障)
- **痛点**：
  在 `Resources.resw` 中，旧资源可能存在 `AiHub_` 前缀，而新标准使用 `AIHub_`。尽管在 .NET 编译器中通过，但 Windows App SDK 的资源打包工具 `makepri.exe` 是**大小写不敏感 (Case-insensitive)** 的。两组同名不同大小写的键名共存会触发致命编译报错：
  `PRI277: 0xdef00052 - Conflicting values for resource ...`
- **经验与规范**：
  所有新模块的资源键名统一遵循全大写缩写规范（如 `AIHub_EnableSettingsCard`），严格删除所有旧的混淆大小写键（`AiHub_`），并在修改后运行 `msbuild /t:GeneratePri` 验证无重复。

### 4.2 WinUI 3 XAML 属性与子内容冲突 (WMC0035 错误)
- **痛点**：
  在 XAML 中定义按钮时，若同时编写了属性 `Content="Fast scan"` 且在标签内包含了 `<StackPanel>` 子元素，WinUI 3 XAML 编译器将报错 `WMC0035: The property 'Content' is set more than once`。
- **经验**：
  富文本/图标按钮必须移除属性级 `Content`，仅使用内嵌子元素定义其内容结构。

### 4.3 XAML `x:Bind` 作用域与静态方法约束 (CS0176 错误)
- **痛点**：
  在页面代码后置（Code-behind）中，若将 Tab 状态转换方法（如 `IsAuditTab(int index)`）声明为 `static`，而在 XAML 中使用 `{x:Bind IsAuditTab(ViewModel.SelectedTabIndex)}` 时，生成的 C# 绑定代码会尝试通过页面实例引用该静态方法，导致报出 `CS0176: Member cannot be accessed with an instance reference`。
- **经验**：
  页面专用的 `x:Bind` 转换辅助方法应统一声明为普通的**实例方法**（Instance methods），使编译器生成安全无歧义的实例绑定调用。

### 4.4 C++/WinRT 与 .NET 10 代码生成器冲突 (CsWinRT1028 与 SA 规则)
- **痛点**：
  当类实现了 `INotifyPropertyChanged` 并跨 WinRT ABI 时，CsWinRT 要求类型及其父类必须标记为 `partial`，否则抛出警告 `CsWinRT1028`；同时 StyleCop (`SA1316`, `SA1509`) 对元组命名和代码排版有极其严苛的格式检查。
- **经验**：
  所有数据模型（如 `TempFileInfo`）显式标记为 `public sealed partial class`；返回元组元素一律使用 PascalCase（如 `(int Succeeded, int Failed)`）；JSON 序列化器使用静态缓存实例避免热路径反复分配。

---

## 5. 项目目录结构

```
src/modules/AIHub/
├── AIHubLib/                                   # .NET 10 托管业务库
│   ├── Kit.AIHubLib.csproj                     # 目标 net10.0-windows10.0.26100.0
│   ├── Models/
│   │   ├── AuditIssueEnhanced.cs               # 增强安全审计事件模型
│   │   ├── AuditResult.cs                      # 完整审计报告快照
│   │   └── TempFileInfo.cs                     # 待优化临时文件与下载文件实体
│   ├── Services/
│   │   ├── CacheCleanupService.cs              # 临时缓存白名单扫描与受控回收
│   │   ├── DownloadOrganizerService.cs         # 下载目录单层扩展名智能分类
│   │   ├── EventLogService.cs                  # Windows 4大事件通道日志聚合检索
│   │   ├── HealthScoreCalculator.cs            # 0-100 健康评分扣分加权算法
│   │   └── RecycleBinHelper.cs                 # COM IFileOperation 撤销式安全删除
│   └── Storage/
│       └── AuditHistoryStorage.cs              # 历史审计持久化与轮转存储
│
├── AIHubModuleInterface/                       # C++ 原生模块接口 DLL
│   ├── Kit.AIHubModuleInterface.vcxproj        # C++/WinRT 原生工程
│   ├── dllmain.cpp                             # DLL 入口点
│   ├── AIHubConstants.h                        # 模块 GUID 与静态常量定义
│   └── trace.h / trace.cpp                     # ETW 事件跟踪支持
│
└── README.md                                   # 本开发文档
```

---

## 6. 测试与验证清单

每次针对 AI Hub 进行修改或功能迭代后，必须执行以下三层全量验证：

1. **AiHub 专用测试套件**：
   ```powershell
   dotnet test src\common\AiHub.UnitTests\Kit.AiHub.UnitTests.csproj -c Debug /p:Platform=x64 --nologo
   ```
   - 验证项：全部通过（含严格策略隔离、Agent 任务降级、CLI 运行器与审计核心计算，以及新增的 HealthScoreCalculator/DownloadOrganizer/AuditHistoryStatistics/AuditHistoryStorage/EventLogService/AuditSchedule/OptimizationOutcome 测试）。截至 fix 分支为 151 通过 / 1 跳过。

2. **Settings UI 单元测试**：
   ```powershell
   dotnet test Debug\x64\tests\SettingsTests\net10.0-windows10.0.26100.0\Settings.UI.UnitTests.dll --no-build --nologo
   ```
   - 验证项：全部通过（验证模块目录注册、设置 JSON 序列化、仪表板激活状态及版本对齐）。截至 fix 分支为 199 项。

3. **全工程构建验证**：
   ```powershell
   & 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe' 'Kit.slnx' /t:Build /p:Configuration=Debug /p:Platform=x64 /m /nr:false /nologo
   ```
   - 验证项：全解决方案 0 Errors 成功编译，输出产物就绪于 `x64\Debug\`。
