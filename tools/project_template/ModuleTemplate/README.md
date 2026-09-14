# Kit 模块模板维护

开发者使用入口见 [上一级 README](../README.md)，完整插件规范见 [PLUGIN_DEVELOPMENT.md](../../../PLUGIN_DEVELOPMENT.md)。

## 源码与验证工程

- `ModuleTemplate.vcxproj` 是交给 Visual Studio 实例化的参数化工程，保留 `$projectname$`、`$safeprojectname$` 和 `$guid1$`。
- `ModuleTemplateCompileTest.vcxproj` 使用固定验证 GUID，共享本目录的 C++ 与资源源码；包和项目引用与模板一致，输出到 `tests\ModuleTemplate`。
- `MyTemplate.vstemplate` 是 ZIP 的元数据源。保留模板类型、默认名称、图标等元数据；涉及占位符的工程、C++ 源码和资源文件必须启用 `ReplaceParameters`。
- `trace.h` / `trace.cpp` 是空实现兼容入口，修改或重新导出模板时不得重新引入遥测 Provider。

两个工程通过 `RepoRoot` 引用现有 `SettingsAPI.vcxproj` 和 CppWinRT 包，不依赖已删除的 `common.vcxproj`，也不假定生成项目固定嵌套几层。工具链和 SDK 继承仓库根配置。新增功能前先修改共享源码，再用上一级 README 的命令构建验证工程。

## ZIP 文件清单

ZIP 根目录必须只包含以下文件，不额外嵌套 `ModuleTemplate` 目录：

| ZIP 条目 | 源文件 |
| --- | --- |
| `ModuleTemplate.vcxproj` | 本目录同名文件 |
| `dllmain.cpp` | 本目录同名文件 |
| `pch.cpp`、`pch.h` | 本目录同名文件 |
| `trace.cpp`、`trace.h` | 本目录同名文件 |
| `resource.h` | 本目录同名文件 |
| `$projectname$.rc` | 本目录同名文件，保留占位符文件名 |
| `packages.config` | 本目录同名文件 |
| `MyTemplate.vstemplate` | 本目录同名文件 |
| `__TemplateIcon.ico` | 上一级 `TemplateIcon.ico`，打包时使用左侧条目名 |

不打包 CompileTest 工程、`.filters`、solution、构建产物或 README。当前模板工程没有 `.vcxproj.filters`，元数据中不得引用不存在的文件。

## 更新与验收

1. 从上述明确的源码清单创建临时 ZIP，保留 `MyTemplate.vstemplate` 和图标映射；核对完成后替换上一级 `ModuleTemplate.zip`。
2. 检查 ZIP 每个源码条目与对应文件的 SHA-256 一致，并核对元数据中所有 `File` / `ProjectItem` 和图标条目都存在。
3. 检查工程、`dllmain.cpp`、`.rc` 的 `ReplaceParameters="true"`；其他不含模板参数的内容保持 `false`。资源输出名与生成工程中的 `ResourceCompile` 必须一致。
4. 用上一级 README 的命令编译共享源码；再用 Visual Studio 从 ZIP 新建一个 `Sample` 项目，确认生成独立 GUID、`Sample.vcxproj`、`Sample.rc`，且源码中没有未替换的模板参数。
5. 按根规范验证生成项目的编译、实际 DLL 文件名、Runner 加载、设置持久化以及重复启停/退出行为。记录实际验证的配置和日志，不把源码 CompileTest 的通过等同于插件已完成接入。

如果使用 Visual Studio 的 Export Template 功能生成中间包，导出后仍需逐项核对以上清单及参数替换设置。最终 ZIP 应以本目录已审核的源码和元数据为准。
