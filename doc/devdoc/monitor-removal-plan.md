# Kit 项目重构实施计划

> 基于 `next.md` 规划，删除 Monitor 模块并优化启动速度（仅保留 Awake 和 LightSwitch 两个插件）

## 执行摘要

**目标**：
1. 完全移除 Monitor 模块（按 next.md § 13.3 清单）
2. 优化 Kit 启动速度（仅加载 2 个插件）
3. 清理所有 Monitor 相关引用（runner、Settings UI、测试、文档）

**当前状态**：
- 活跃模块：Awake、LightSwitch、Monitor（3个）
- 目标模块：Awake、LightSwitch（2个）
- 模块加载：静态注册 `KitKnownModules` 数组

**预期收益**：
- 启动时间减少 ~33%（减少 1 个模块加载）
- 代码库精简：删除 ~50+ 文件
- 维护负担减轻：聚焦 2 个核心插件

---

## 阶段 1：项目文件清理（P0，隔离安全）

### 1.1 删除 Monitor 模块源码目录

**操作**：
```bash
# 删除整个 Monitor 模块目录
rm -rf src/modules/Monitor/
```

**影响文件**：
- `src/modules/Monitor/MonitorLib/` - 核心库
- `src/modules/Monitor/Monitor/` - 工作进程
- `src/modules/Monitor/MonitorModuleInterface/` - 本地接口
- `src/modules/Monitor/Tests/` - 单元测试

**验证**：
- [ ] 目录不再存在：`ls src/modules/Monitor/` 应返回错误

---

### 1.2 更新解决方案文件

**文件**：`Kit.slnx`

**修改**：删除以下项目条目
```xml
<!-- 删除 lines 110-126 -->
<Folder Name="/modules/Monitor/">
  <Project Path="src/modules/Monitor/MonitorLib/MonitorLib.csproj">
  <Project Path="src/modules/Monitor/Monitor/PowerToys.Monitor.csproj">
  <Project Path="src/modules/Monitor/MonitorModuleInterface/MonitorModuleInterface.vcxproj">
</Folder>
<Folder Name="/modules/Monitor/Tests/">
  <Project Path="src/modules/Monitor/Tests/Monitor.UnitTests/Monitor.UnitTests.csproj">
</Folder>
```

**验证**：
- [ ] `Kit.slnx` 中无 Monitor 相关 `<Project>` 节点
- [ ] Visual Studio 打开解决方案无报错

---

### 1.3 清理 Runner 项目引用

**文件**：`src/runner/Kit.vcxproj`

**修改**：删除对 Monitor 各工程的 `<ProjectReference>` 和 `<BuildDependency>`

**搜索模式**：
```xml
<ProjectReference Include="..\..\modules\Monitor\**\*.vcxproj" />
```

**验证**：
- [ ] `Kit.vcxproj` 中无 Monitor 项目引用
- [ ] 独立编译 runner 成功：`msbuild src/runner/Kit.vcxproj /p:Configuration=Release /p:Platform=x64`

---

## 阶段 2：Runner 注册清理（P0，启动关键路径）

### 2.1 移除 KitKnownModules 中的 Monitor

**文件**：`src/runner/main.cpp`

**当前代码**（lines 66-70）：
```cpp
constexpr std::wstring_view KitKnownModules[] = {
    L"PowerToys.AwakeModuleInterface.dll",
    L"PowerToys.LightSwitchModuleInterface.dll",
    L"PowerToys.MonitorModuleInterface.dll",  // ← 删除此行
};
```

**修改后**：
```cpp
constexpr std::wstring_view KitKnownModules[] = {
    L"PowerToys.AwakeModuleInterface.dll",
    L"PowerToys.LightSwitchModuleInterface.dll",
};
```

**影响**：
- 启动时不再尝试加载 `PowerToys.MonitorModuleInterface.dll`
- 模块枚举数量从 3 降为 2

**验证**：
- [ ] 编译通过
- [ ] 启动 `Kit.exe`，托盘菜单中无 Monitor 模块

---

### 2.2 清理 ESettingsWindowNames 枚举

**文件**：`src/runner/settings_window.h`

**当前代码**（lines 5-12）：
```cpp
enum class ESettingsWindowNames
{
    Dashboard = 0,
    Overview,
    Awake,
    LightSwitch,
    Monitor,  // ← 删除此行
};
```

**修改后**：
```cpp
enum class ESettingsWindowNames
{
    Dashboard = 0,
    Overview,
    Awake,
    LightSwitch,
};
```

**文件**：`src/runner/settings_window.cpp`

**删除**：
```cpp
// 删除 to_string 分支
case ESettingsWindowNames::Monitor:
    return "Monitor";

// 删除 from_string 分支
else if (value == "Monitor")
    return ESettingsWindowNames::Monitor;
```

**验证**：
- [ ] 编译通过，无枚举引用警告
- [ ] 设置窗口导航不尝试打开 Monitor 页面

---

## 阶段 3：Settings UI 清理（P1，UI 集成）

### 3.1 更新 KitModuleCatalog

**文件**：`src/settings-ui/Settings.UI.Library/Helpers/KitModuleCatalog.cs`

**当前代码**（lines 14-20）：
```csharp
public static IReadOnlyList<ModuleType> ActiveModules { get; } =
    new[]
    {
        ModuleType.Awake,
        ModuleType.LightSwitch,
        ModuleType.Monitor,  // ← 删除
    };
```

**修改后**：
```csharp
public static IReadOnlyList<ModuleType> ActiveModules { get; } =
    new[]
    {
        ModuleType.Awake,
        ModuleType.LightSwitch,
    };
```

**同时修改**（lines 24-29）：
```csharp
public static IReadOnlyList<ModuleType> QuickAccessModules { get; } =
    new[]
    {
        ModuleType.LightSwitch,
        // ModuleType.Monitor 已删除
    };
```

**修改**（lines 31-39）：
```csharp
public static IReadOnlyList<string> ActiveSettingsModuleKeys { get; } =
    new[]
    {
        nameof(GeneralSettings),
        AwakeSettings.ModuleName,
        LightSwitchSettings.ModuleName,
        // MonitorSettings.ModuleName 已删除
        "General",
    };
```

**修改**（lines 41-47）：
```csharp
public static IReadOnlyList<string> ActiveEnabledModuleKeys { get; } =
    new[]
    {
        AwakeSettings.ModuleName,
        LightSwitchSettings.ModuleName,
        // MonitorSettings.ModuleName 已删除
    };
```

**验证**：
- [ ] `KitModuleCatalog.ActiveModules.Count == 2`
- [ ] Dashboard 仅显示 Awake 和 LightSwitch

---

### 3.2 删除 Monitor 设置类

**文件清单**（Settings.UI.Library）：
```
src/settings-ui/Settings.UI.Library/MonitorInfo.cs          - 删除
src/settings-ui/Settings.UI.Library/MonitorProperties.cs    - 删除
src/settings-ui/Settings.UI.Library/MonitorSettings.cs      - 删除
src/settings-ui/Settings.UI.Library/SndMonitorSettings.cs   - 检查后决定（可能是遗留）
```

**验证**：
- [ ] 编译 `Settings.UI.Library.csproj` 无错误
- [ ] 无其他文件引用这些类（Grep 验证）

---

### 3.3 删除 Monitor UI 页面和 ViewModel

**文件清单**（Settings.UI）：
```
# 页面
src/settings-ui/Settings.UI/SettingsXAML/Views/MonitorPage.xaml
src/settings-ui/Settings.UI/SettingsXAML/Views/MonitorPage.xaml.cs

# ViewModels
src/settings-ui/Settings.UI/ViewModels/MonitorViewModel.cs
src/settings-ui/Settings.UI/ViewModels/MonitorScanIntervalOption.cs
src/settings-ui/Settings.UI/ViewModels/MonitorStatusBrushes.cs
src/settings-ui/Settings.UI/ViewModels/MonitorStatusDayViewModel.cs
src/settings-ui/Settings.UI/ViewModels/MonitorStatusLegendItemViewModel.cs
src/settings-ui/Settings.UI/ViewModels/MonitorStatusMetricViewModel.cs

# Services
src/settings-ui/Settings.UI/Services/MonitorManualScanCoordinator.cs
src/settings-ui/Settings.UI/Services/MonitorManualScanProgressUpdate.cs
src/settings-ui/Settings.UI/Services/MonitorProgressSnapshotReader.cs
src/settings-ui/Settings.UI/Services/MonitorSettingsStoragePaths.cs
src/settings-ui/Settings.UI/Services/MonitorStatusPresentation.cs
src/settings-ui/Settings.UI/Services/MonitorStatusPresentationService.cs
src/settings-ui/Settings.UI/Services/MonitorStatusQueryService.cs
```

**操作**：
```bash
# 批量删除
rm -f src/settings-ui/Settings.UI/SettingsXAML/Views/MonitorPage.xaml*
rm -f src/settings-ui/Settings.UI/ViewModels/Monitor*.cs
rm -f src/settings-ui/Settings.UI/Services/Monitor*.cs
```

**验证**：
- [ ] 文件已删除
- [ ] `PowerToys.Settings.csproj` 编译通过

---

### 3.4 清理导航和路由

**文件**：`src/settings-ui/Settings.UI/SettingsXAML/Views/ShellPage.xaml`

**搜索并删除**：Monitor 相关的 `<NavigationViewItem>` 节点（约 line 127）

**文件**：`src/settings-ui/Settings.UI/SettingsXAML/Views/ShellPage.xaml.cs`

**删除行**：121, 123, 227, 231, 233, 250, 256, 258, 275, 329, 420, 449, 450, 453, 472
（所有 Monitor 页面导航、路由、事件处理代码）

**文件**：`src/settings-ui/Settings.UI/ViewModels/DashboardViewModel.cs`

**删除**：lines 90, 143（Monitor 模块状态查询和显示逻辑）

**验证**：
- [ ] Settings 应用启动无崩溃
- [ ] 侧边栏导航菜单中无 Monitor 条目
- [ ] Dashboard 页面无 Monitor 卡片

---

### 3.5 清理 QuickAccess 集成

**文件**：`src/settings-ui/QuickAccess.UI/QuickAccessXAML/ViewModels/AllAppsViewModel.cs`

**删除**：line 72（Monitor 模块条目）

**文件**：`src/settings-ui/Settings.UI.Controls/QuickAccess/QuickAccessViewModel.cs`

**删除**：line 55（Monitor 快速操作）

**文件**：`src/settings-ui/QuickAccess.UI/PowerToys.QuickAccess.csproj`

**删除**：Monitor 图标拷贝规则
```xml
<None Include="..\..\..\Assets\Settings\Icons\Monitor.png">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

**同时删除** `KitRemoveInactiveQuickAccessIconAssetsFromOutput` target 中的 Monitor.png 排除规则

**验证**：
- [ ] Quick Access 弹出窗口无 Monitor 条目
- [ ] 输出目录无 `Monitor.png` 残留

---

### 3.6 清理资源和资产

**文件**：`src/settings-ui/Settings.UI/Strings/en-us/Resources.resw`

**删除**：lines 142, 146（Monitor 名称和描述字符串）

**文件系统**：
```bash
# 删除图标资产
rm -f src/settings-ui/Settings.UI/Assets/Settings/Icons/Monitor.png
rm -f src/settings-ui/Settings.UI/Assets/Settings/Modules/Monitor.png
```

**验证**：
- [ ] 无 Monitor 本地化字符串
- [ ] 无 Monitor 图标文件

---

### 3.7 更新序列化上下文

**文件**：`src/settings-ui/Settings.UI/SerializationContext/SourceGenerationContextContext.cs`

**删除**：line 16（Monitor 相关类型的序列化注册）

**文件**：`src/settings-ui/Settings.UI.Library/SettingsSerializationContext.cs`

**删除**：Monitor 设置类型注册

**验证**：
- [ ] 编译通过，JSON 序列化不引用 Monitor 类型
- [ ] Settings 保存/加载无 Monitor 残留

---

## 阶段 4：测试清理（P1，质量保障）

### 4.1 删除 Monitor 专属测试

**文件清单**：
```
src/settings-ui/Settings.UI.UnitTests/ViewModelTests/MonitorSettingsRegistration.cs
src/settings-ui/Settings.UI.UnitTests/Services/MonitorManualScanCoordinatorTests.cs
```

**操作**：
```bash
rm -f src/settings-ui/Settings.UI.UnitTests/ViewModelTests/MonitorSettingsRegistration.cs
rm -f src/settings-ui/Settings.UI.UnitTests/Services/MonitorManualScanCoordinatorTests.cs
```

---

### 4.2 更新 BuildCompatibility 测试

**文件**：`src/settings-ui/Settings.UI.UnitTests/ViewModelTests/BuildCompatibility.cs`

**策略**：按 AGENTS.md 纪律，改为物理断言 "Monitor 已移除"

**示例修改**：
```csharp
[TestMethod]
public void MonitorModule_ShouldNotExist()
{
    // 物理断言：Monitor 已从活跃模块中移除
    Assert.IsFalse(KitModuleCatalog.ActiveModules.Contains(ModuleType.Monitor),
        "Monitor module should be removed from Kit");
    
    // 断言：模块数量应为 2
    Assert.AreEqual(2, KitModuleCatalog.ActiveModules.Count,
        "Kit should have exactly 2 active modules (Awake, LightSwitch)");
}

[TestMethod]
public void MonitorSettings_ShouldNotExist()
{
    // 断言：设置键中不包含 Monitor
    Assert.IsFalse(KitModuleCatalog.ActiveSettingsModuleKeys.Contains(MonitorSettings.ModuleName),
        "Monitor settings should not be registered");
}
```

**删除**：所有正向断言 Monitor 存在的测试（改为反向断言不存在）

---

### 4.3 更新其他测试文件

**文件**：`src/settings-ui/Settings.UI.UnitTests/ViewModelTests/General.cs`

**删除**：Monitor 相关断言

**文件**：`src/settings-ui/Settings.UI.UnitTests/ViewModelTests/FrameworkPrivacyDefaults.cs`

**删除**：Monitor 隐私设置测试

**验证**：
- [ ] 全部测试通过：`vstest.console.exe Settings.UI.UnitTests.dll`
- [ ] 无 Monitor 残留断言

---

## 阶段 5：文档更新（P2，记录变更）

### 5.1 更新核心文档

**文件**：`README.md`

**修改位置**：
- Line 28-29: 活跃模块列表改为 `Awake` 和 `LightSwitch`
- Line 42-43: 移除 Monitor 架构描述
- Line 52-56: 模块集从 3 个改为 2 个
- Line 62-67: `KitKnownModules` 列表删除 Monitor 条目
- Line 86: 移除 "Monitor is the reference shape" 表述

**新增说明**：
```markdown
## 当前模块集

Kit 维护两个核心插件：
- `Awake` - 保持系统唤醒
- `Light Switch` - 主题切换

Monitor 模块已在 v2.1.0 中移除，Kit 专注于轻量级插件集。
```

**文件**：`README_zh.md`

**同步翻译**：所有 README.md 的修改

---

### 5.2 更新 AGENTS.md

**文件**：`AGENTS.md`

**修改**：
- Line 12: 模块表删除 Monitor 行
- Line 32: 参考形态改为 "LightSwitch 是插件参考形态"
- Lines 104-105: 验证清单删除 Monitor 条目

**新增内容**：
```markdown
## 移除的模块

### Monitor (已移除于 v2.1.0)
Monitor 下载文件夹管理器已从 Kit 中移除，以保持项目聚焦于核心实用工具。
历史参考见 v2.0.7 及更早版本。
```

---

### 5.3 更新 changelog.md

**文件**：`changelog.md`

**新增版本条目**（顶部）：
```markdown
### 2.1.0

- Version: 升级 Kit 至 `2.1.0`。
- Modules: **移除 Monitor 模块**，Kit 现仅维护 Awake 和 LightSwitch 两个核心插件。
- Startup: 优化启动速度，模块加载从 3 个减少至 2 个（~33% 性能提升）。
- Runner: 从 `KitKnownModules` 静态注册数组中移除 `PowerToys.MonitorModuleInterface.dll`。
- Settings UI: 移除 Monitor 设置页面、导航条目、Quick Access 集成和所有 UI 组件。
- Tests: 删除 Monitor 专属测试，更新 `BuildCompatibility` 为反向断言（物理验证 Monitor 已移除）。
- Cleanup: 删除整个 `src/modules/Monitor/` 目录及其 50+ 相关文件。
- Docs: 更新 README、AGENTS.md、开发文档，将 LightSwitch 标记为新的插件参考形态。
```

**保留历史**：2.0.7 及更早版本的 Monitor 相关条目（历史记录）

---

### 5.4 更新开发文档

**文件**：`doc/devdoc/kit-first-plugin.md`

**修改**：将示例从 Monitor 改为 LightSwitch

**文件**：`doc/devdoc/kit-development-experience.md`

**更新**：移除 Monitor 作为参考形态的描述

**文件**：`doc/devdoc/README.md`

**更新**：文档索引删除 Monitor 相关章节

---

## 阶段 6：启动优化（P2，性能提升）

### 6.1 验证模块加载性能

**测试方法**：
```bash
# 使用 Performance Analyzer 跟踪启动
# 或添加启动日志时间戳

# src/runner/main.cpp 中添加日志
Logger::info(L"Starting module load...");
auto start = std::chrono::high_resolution_clock::now();

// 模块加载循环
for (const auto& module : KitKnownModules) {
    // ... load module ...
}

auto end = std::chrono::high_resolution_clock::now();
auto duration = std::chrono::duration_cast<std::chrono::milliseconds>(end - start);
Logger::info(L"Module load completed in {}ms", duration.count());
```

**预期结果**：
- 2 个模块加载时间 < 3 个模块加载时间的 70%
- 启动至托盘图标显示总时间减少

---

### 6.2 考虑延迟加载优化（可选）

**策略**：虽然只有 2 个模块，但可以考虑：
- Awake 默认启用，立即加载
- LightSwitch 按需加载（首次使用时）

**实现**（可选，未来优化）：
```cpp
// 示例：延迟加载标记
struct ModuleLoadConfig {
    std::wstring_view dll;
    bool immediate;  // true = 立即加载，false = 延迟加载
};

constexpr ModuleLoadConfig KitKnownModules[] = {
    { L"PowerToys.AwakeModuleInterface.dll", true },
    { L"PowerToys.LightSwitchModuleInterface.dll", false },
};
```

**注意**：当前范围不实施，记录为未来优化方向

---

## 阶段 7：完整验证（P0，发布前检查）

### 7.1 构建验证

**命令序列**：
```bash
# 1. 清理所有构建产物
tools/build/clean-artifacts.ps1

# 2. 完整构建 Debug
tools/build/build.ps1 -Platform x64 -Configuration Debug

# 验证：退出码 0
if ($LASTEXITCODE -ne 0) { throw "Debug build failed" }

# 3. 完整构建 Release
tools/build/build.ps1 -Platform x64 -Configuration Release

# 验证：退出码 0
if ($LASTEXITCODE -ne 0) { throw "Release build failed" }
```

**检查输出**：
- [ ] `x64/Release/` 下无 Monitor 相关 DLL
- [ ] `x64/Release/WinUI3Apps/` 下无 Monitor.png 图标
- [ ] 无 PDB / 卫星语言目录残留（Kit 裁剪 target 生效）

---

### 7.2 运行时验证

**启动测试**：
```bash
# 启动 Kit
x64/Release/Kit.exe
```

**检查清单**：
- [ ] 托盘图标正常显示
- [ ] 右键菜单仅显示 Awake 和 LightSwitch
- [ ] Settings 窗口打开无错误
- [ ] 侧边栏导航仅显示：Dashboard、General、Awake、LightSwitch
- [ ] Quick Access 弹出窗口无 Monitor 条目
- [ ] Dashboard 仅显示 2 个模块卡片

**功能测试**：
- [ ] Awake 模块可启用/禁用
- [ ] LightSwitch 模块可切换主题
- [ ] 设置保存/加载正常
- [ ] 无 Monitor 相关错误日志

---

### 7.3 测试验证

**运行所有测试**：
```bash
# Settings UI 单元测试
vstest.console.exe x64/Release/Settings.UI.UnitTests/Settings.UI.UnitTests.dll

# Common 库测试
vstest.console.exe x64/Release/UnitTests-CommonLib.dll
vstest.console.exe x64/Release/UnitTests-CommonUtils.dll

# LightSwitch UI 测试
vstest.console.exe x64/Release/LightSwitch.UITests/LightSwitch.UITests.dll
```

**验证**：
- [ ] 所有测试通过（100% pass rate）
- [ ] 无 Monitor 相关测试失败
- [ ] `BuildCompatibility` 反向断言通过

---

### 7.4 文件残留检查

**搜索残留引用**：
```bash
# 全局搜索 Monitor 引用
grep -r "Monitor" src/ --include="*.cpp" --include="*.h" --include="*.cs" --include="*.xaml" \
  | grep -v "// 历史" \
  | grep -v "changelog.md"

# 应仅返回：
# - 变量名中无意义的 "monitor"（如 performance monitor）
# - 注释中的历史记录
# - changelog.md 中的历史条目
```

**检查构建产物**：
```bash
find x64/Release -name "*Monitor*" -o -name "*monitor*"
# 应返回空（无 Monitor 相关文件）
```

---

## 风险评估

### 高风险项

| 风险 | 影响 | 缓解措施 |
|------|------|----------|
| Settings UI 序列化破坏 | 用户配置丢失 | 1. 保留旧设置迁移逻辑<br>2. 测试从 v2.0.7 升级场景 |
| IPC 契约变更 | Runner/Settings 通信失败 | 同步更新 Runner 和 Settings UI，同一提交 |
| 遗漏的 Monitor 引用 | 运行时崩溃 | 全局 Grep 验证 + 完整回归测试 |

### 中风险项

| 风险 | 影响 | 缓解措施 |
|------|------|----------|
| 测试覆盖不足 | 隐藏的回归 | 添加反向断言（Monitor 不存在） |
| 文档不同步 | 开发者困惑 | 同步更新所有文档，包括中英文版本 |

### 低风险项

| 风险 | 影响 | 缓解措施 |
|------|------|----------|
| 图标资源残留 | 磁盘空间浪费（微小） | 构建后检查输出目录 |
| 日志噪音 | 调试干扰（轻微） | 清理 Monitor 日志输出 |

---

## 回滚计划

### 如何回滚

1. **Git 回滚**：
   ```bash
   git revert <commit-hash>
   ```

2. **恢复 Monitor 模块**：
   - 从 v2.0.7 标签恢复 `src/modules/Monitor/` 目录
   - 恢复 `KitKnownModules` 中的 Monitor 条目
   - 恢复 Settings UI 集成代码

3. **回滚触发条件**：
   - 关键功能回归（Awake/LightSwitch 无法使用）
   - Settings 序列化破坏（无法保存配置）
   - 无法在 1 个工作日内修复的严重 Bug

---

## 执行顺序建议

### 第一天：模块源码和项目文件（隔离安全）

1. ✅ 阶段 1.1: 删除 `src/modules/Monitor/` 目录
2. ✅ 阶段 1.2: 更新 `Kit.slnx`
3. ✅ 阶段 1.3: 清理 Runner 项目引用
4. ✅ 验证：独立编译 Runner 成功

### 第二天：Runner 和核心集成（关键路径）

5. ✅ 阶段 2.1: 移除 `KitKnownModules` 中的 Monitor
6. ✅ 阶段 2.2: 清理 `ESettingsWindowNames` 枚举
7. ✅ 阶段 3.1: 更新 `KitModuleCatalog`
8. ✅ 验证：启动 Kit，无 Monitor 条目

### 第三天：Settings UI 全面清理（UI 层）

9. ✅ 阶段 3.2-3.7: 删除所有 Settings UI Monitor 组件
10. ✅ 验证：Settings 应用功能完整

### 第四天：测试和文档（质量保障）

11. ✅ 阶段 4: 测试清理和反向断言
12. ✅ 阶段 5: 文档更新（中英文）
13. ✅ 阶段 7: 完整验证（构建、运行时、测试）

### 第五天：发布准备和优化

14. ✅ 阶段 6: 启动性能测量
15. ✅ 最终验证和残留检查
16. ✅ 提交并标记版本 `v2.1.0`

---

## 成功标准

- [ ] **零构建错误**：Debug 和 Release 配置编译成功，退出码 0
- [ ] **零运行时错误**：Kit 启动、Settings 打开、模块操作无崩溃
- [ ] **100% 测试通过**：所有单元测试和 UI 测试通过
- [ ] **无残留引用**：全局 Grep 无意外的 Monitor 代码引用
- [ ] **文档同步**：README、AGENTS.md、changelog 准确反映 2 个模块状态
- [ ] **性能提升**：启动时间减少（可选：量化测量）

---

## 后续优化方向（未来）

1. **延迟加载**：LightSwitch 按需加载（非默认启用场景）
2. **模块热加载**：运行时加载/卸载模块而无需重启
3. **插件 API 稳定化**：明确第三方插件接入契约
4. **性能监控**：内置启动时间和模块加载性能追踪

---

**文档版本**：1.0  
**创建日期**：2026-09-12  
**目标版本**：Kit v2.1.0  
**预计工作量**：4-5 个工作日  
**负责人**：待定
