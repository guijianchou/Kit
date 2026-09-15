# Kit 原生模块模板

该模板用于在 Kit 仓库内创建实现 `KitModuleIface` 的 C++ DLL。模块的完整接入要求、数据隔离、图标和 WinUI 3 页面规范见根目录 [PLUGIN_DEVELOPMENT.md](../../PLUGIN_DEVELOPMENT.md)。

## 安装与创建项目

1. 把本目录的 `ModuleTemplate.zip` 复制到 Visual Studio 的 **User project templates location**。实际位置以 `Tools > Options > Projects and Solutions` 中的设置为准；默认位置通常是 `%USERPROFILE%\Documents\Visual Studio 18\Templates\ProjectTemplates\`（VS 2026）或对应 VS 2022 目录。
2. 在 Visual Studio 的 C++ 项目模板中选择 **Kit Module Template**。生成的工程使用 Kit 仓库的接口和构建配置。
3. 在 Kit 的 `src\modules\` 下创建项目，例如项目名 `Sample`、位置 `src\modules\sample\`，得到 `src\modules\sample\Sample\Sample.vcxproj`。使用以字母开头的 ASCII 字母/数字名称，并确认模块 Key 不与现有模块或保留目录重名。
4. 将生成的工程加入 `Kit.slnx`，按根规范完成 Runner、设置模型、导航、语言资源及资产接入。

模板依赖祖先目录中的 `Directory.Build.props` 和 `Cpp.Build.props` 提供 `RepoRoot`、Windows SDK、平台和工具链。当前配置为 x64、SDK 10.0.26100.0，VS 2026 使用 v145；模板本身不固定旧工具链版本。脱离 Kit 仓库的独立项目需要自行提供这些构建条件。

## 参数、产物与注册

Visual Studio 会完成以下替换：

| 参数 | 作用 |
| --- | --- |
| `$projectname$` | 工程名、资源文件名、DLL 默认名称及初始显示名称 |
| `$safeprojectname$` | C++ 类名、命名空间和初始模块 Key |
| `$guid1$` | 新项目 GUID，避免与模板验证项目及其他插件冲突 |

`MyTemplate.vstemplate` 已为工程、`dllmain.cpp` 和资源文件启用参数替换，并包含 `packages.config`。手工复制源码时也必须完成这三类替换、重命名资源文件和工程文件；不能直接把带占位符的 `ModuleTemplate.vcxproj` 加入生产构建。

生成的 DLL 输出到 `$(RepoRoot)$(Platform)\$(Configuration)\`，与 Runner 同目录。例如 `Sample` 项目的 Debug 产物是 `x64\Debug\Sample.dll`。如果按根规范设置 `TargetName` 为 `Kit.SampleModuleInterface`，同步更新 `.rc` 的 `OriginalFilename`，并在 [main.cpp](../../src/runner/main.cpp) 的 `KitKnownModules` 中填写真实 DLL 文件名。

加载清单和设置界面均需显式接入，复制 DLL 不会自动注册。原生 `get_config()` 的 JSON 也不会自动生成 WinUI 3 页面；继续按根规范补齐 Settings 的模型、序列化、Catalog、导航和资源。

`MODULE_KEY` 在首次发布后必须保持不变；展示名称可以本地化。配置通过 Kit 的 `SettingsAPI` 保存到 `%LOCALAPPDATA%\Kit\<ModuleKey>\`。`destroy()` 先调用 `disable()`，插件实现应保证停用可重复执行并释放自身资源。模板中的 `trace.*` 保留空实现，不注册遥测 Provider。

## 编译验证

从仓库根目录执行：

```powershell
.\tools\build\build.ps1 -Path .\tools\project_template -Platform x64 -Configuration Debug
```

该入口还原并构建 [PowerToyTemplate.sln](PowerToyTemplate.sln) 中的 `ModuleTemplateCompileTest.vcxproj`，输出 `x64\Debug\tests\ModuleTemplate\ModuleTemplateCompileTest.dll`。验证项目也已列入 `Kit.slnx`，它共享模板的 C++/资源源码和 SettingsAPI 依赖，产物不加入 Runner 加载清单。

这里的 `-Path` 指向 `tools\project_template`，不是它下面同时包含两个 `.vcxproj` 的 `ModuleTemplate` 目录。后者会让构建脚本同时尝试编译尚未替换参数的模板工程。

维护模板时，还要通过 Visual Studio 创建一个实际项目，确认 GUID、文件名和 C++ 参数均已替换，构建生成的 DLL，再按根规范验证加载和生命周期。共享源码的 CompileTest 不能覆盖 Visual Studio 的模板实例化过程。

## 维护模板

源码位于 [ModuleTemplate](ModuleTemplate/)，打包元数据位于 [MyTemplate.vstemplate](ModuleTemplate/MyTemplate.vstemplate)。修改源码后同步重生成本目录的 ZIP，文件清单及核对要求见 [维护说明](ModuleTemplate/README.md)。不要以旧 ZIP 内的副本覆盖当前源码。
