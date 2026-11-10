# PowerToys 上游同步流程

本流程将本地只读 `Source/PowerToys-main` 作为对照，沿 Runner、模块、服务 / worker、Settings 和 IPC 的实际调用链审查。每次同步应形成可追溯的小范围改动和验证记录，不以文件数量减少或品牌替换完成作为行为对齐的证据。

当前工作要求只构建、测试 **x64 Debug**。本流程不包含 Release、发布、提交或推送步骤。

## 1. 记录工作树和参考身份

先查看 `git status --short` 与相关 diff，识别已有用户修改，确定本轮负责的文件。保留其他工作，不通过 reset、清空包缓存或删除输出目录解决来源不明的问题。

记录 Kit HEAD、`src/Version.props`、Source 的对应版本文件和来源。如果 Source 有自己的 Git 仓库，确认 Git 顶层目录确实是 Source 根目录，再记录其 HEAD；否则记录实际可得的快照信息及内容哈希，明确 commit 不可得。当前快照 `SourceCommit` 为空，`ReleaseTrainVersion=0.103` 不能作为提交号。

只读复算差异概要：

```powershell
python tools/build/generate-common-drift-ledger.py
python tools/build/generate-common-drift-ledger.py --check
```

规则、文件范围及参考子集哈希的含义见 [common 差异台账](common-drift-ledger.md)。复算失败时先区分报告过期、输入缺失与命令执行失败，不立即重写报告掩盖变化。不要修改 Source 来让差异消失。

## 2. 沿实际主链路验证候选问题

从触发动作向后追踪，给每项候选修改写出具体调用点和状态所有者：

1. **Runner 注册与加载**：`src/runner/main.cpp` 如何消费 `kit_module.h::KitModuleCatalog`，何时调用 `kit_create()`，模块是否属于当前七个活跃模块。
2. **配置、策略与启停**：Settings 的启用键、JSON、GPO 和原生模块 `enable/disable` 是否一致；“已启用”是否真的启动 worker，或者仅表示允许使用。
3. **进程与服务生命周期**：谁创建进程、持有句柄、监听退出、停止服务、释放资源；并发启动、禁用、Dispose 和迟到回调如何处理。
4. **命令与 IPC**：Settings 发出的命令、管道名、对端身份校验、响应和错误传播是否对应；服务是否真实调用了业务引擎。未实现命令不能返回成功。
5. **UI 状态与主题**：页面绑定值来自哪个状态源，异步结果是否可能覆盖新状态，主题资源和共享页头是否一致；不要把服务失败仅当作颜色问题修饰。

对照 Source 时同时检查声明、实现和消费者。Kit 的原生 `KitModuleIface` 与 PowerToys 上游接口有产品差异，不能只复制实现文件而忽略工厂导出、配置格式、启动模型或 WinMD 消费端。

托管 UI catalog、导航、Home 和 Quick Access 不是原生 catalog 的重复实现；新增或移除模块时需核对各自职责及相同模块键。模块状态机和业务引擎也不能因命名相同就假定已打通。

## 3. 分开处理依赖、契约、生命周期和视觉变化

| 改动类别 | 合并前检查 | 验证重点 |
| --- | --- | --- |
| 构建与依赖 | `Directory.Build.props/targets`、`Cpp.Build.props/targets`、中央 PackageReference、`packages.config` 和工程硬编码 import 的求值顺序 | 实际编译使用的版本、include / import 路径、ABI 与部署文件 |
| ABI、JSON 与 IPC | 工厂导出、接口布局、长度 / NUL 边界、字段默认值、旧配置读取、双方协议与身份验证 | 原生加载、往返序列化、有效 / 无效消息、失败响应 |
| 生命周期与并发 | 句柄所有权、锁顺序、取消、Dispose、重复启停、迟到回调、进程退出 | 重复启动 / 停止、退出回收、延迟与异常路径 |
| AI 服务路由、存储与诊断 | 区分端点失败、本地配置、策略拒绝和调用方取消；核对公开偏好、DPAPI 密文、迁移、备份与恢复事务的完整消费者 | Main → 已启用 Fallback 的有界切换、禁用 Fallback、取消优先、损坏密文拒绝、诊断脱敏及容量 / 保留期 |
| UI、主题与资源 | 共享控件、绑定状态来源、WinUI 主题资源、Mica 背景与内容层遮蔽、图标来源 | Light / Dark、窄窗口、禁用 / 连接 / 失败状态、真实服务反馈 |

避免把 CRT、ConformanceMode、Windows App SDK、.NET 升级与模块 UI 调整混成一个无法定位回归的修改。先确认各工程当前有效属性；Kit 并非所有项目都使用相同 CRT 模式。

需要还原时使用仓库构建脚本，并保留已有包缓存。WIL 本轮目标版本为 `1.0.260126.7`；不能只检查版本字符串，也要检查最终求值的导入路径和编译 include。属性需在引用它的首次 import 前可用。Runner 中的中央 PackageReference 有消费者，不应作为“无用引用”删除。

## 4. 按差异片段保留 Kit 边界

结合 [关键契约表](common-drift-ledger.md#已核对的关键契约与待审方向) 逐段比较。保留 Kit 的存储与任务命名空间、签名 / 对端身份检查、受限任务写权限、持有进程句柄的退出回收、显式破坏性操作确认，以及 check-only 更新和关闭遥测的边界。

这些是行为约束，不是整文件永久禁止同步的理由。例如 Source 已有任务 SDDL、机器证书根信任和 IPC 测试钩子；Source 还包含 Kit 当前认证文件没有的服务端认证入口。应判断具体实现及消费者，而不是将整个文件标为“Kit 领先”。

对无同路径参考的文件，先搜索上游改名或拆分位置；对兼容 API / 协议入口，先查实际消费者。不得仅凭名字含 PowerToys、文件未进入当前解决方案、或机械差异分类删除它。

## 5. Debug 构建与验证

构建、测试按顺序在同一终端执行。项目级修改先构建受影响项目；主链路、共享库、依赖或跨模块变动再构建解决方案。仓库根目录中的全量 Debug 示例：

```powershell
./tools/build/build.ps1 -Platform x64 -Configuration Debug -Path .
```

缺少包时按 [构建说明](../../tools/build/BUILD-GUIDELINES.md) 使用还原 / essentials 流程，并显式指定 Debug。构建进程返回非零就是失败；读取对应 `build.Debug.x64.errors.log` 和完整日志，修复后重新构建当前源码。不要在失败构建上开始测试或启动 Runner。

构建成功后，先构建相关测试工程，再用 VS Test Explorer 或 `vstest.console.exe` 执行适当的现有测试。此仓库不使用 `dotnet test` 作为验证入口。记录实际测试 DLL、发现数量、通过 / 失败 / 跳过数量；命令退出 0 但未发现测试不能视为测试通过。

根据变动选择必要的回归，而不是每轮无条件扩大测试范围：

- catalog、配置与 UI 集成：Settings.UI 的 BuildCompatibility、导航和相关 ViewModel 测试。
- 管道及模块边界：原生 ABI / PipeMessage smoke，并对本次新增故障路径补充有意义的行为验证。
- worker / service：对应模块的生命周期和命令测试，检查启停、退出回收和不支持命令响应。
- 主题与页头：Light / Dark、窗口宽度、图标、连接及错误状态；合成 smoke 与真实应用检查分别记录。
- 数据操作：备份目录切换、缓存身份、取消 / 错误反馈等受影响行为；使用隔离测试数据。
- AI 服务：使用隔离端点夹具验证认证 / 权限 / 模型错误、超时、Fallback 启停和取消；用隔离用户数据目录验证旧设置迁移、DPAPI 配对、损坏拒绝、恢复与日志保留。夹具通过不能替代真实 Main / Fallback 网络请求验证。

需要运行真实应用时，记录实际 Debug 可执行文件完整路径、版本及进程加载路径，避免误测已安装的旧版本。检查本轮运行生成的 Runner、Settings 和相关模块日志，区分既有历史错误与本轮复现；不为验证而清除用户配置或全部应用数据。涉及全局主题、Awake 等系统状态时，记录原状态并在验证后恢复测试造成的变更。

## 6. 更新报告并交付证据

当产品修改、构建和相应验证完成后，再刷新机械台账：

```powershell
python tools/build/generate-common-drift-ledger.py --write
python tools/build/generate-common-drift-ledger.py --check
git diff --check
```

报告至少包含问题触发条件、根因与实际调用链、最终改动、参考身份、保留的 Kit 契约、构建退出码、测试发现及结果、运行日志观察、Debug 产物完整路径，以及未覆盖的行为。必要时把逐项结论写回 `fix.md` 的复核部分，并将证据存入 `TestResults` 对应目录。

明确区分以下结论：生成器一致性通过、源码检查通过、Debug 编译通过、单元测试通过、合成 UI smoke 通过、真实应用行为已验证。任何一项都不能替代另一项；台账中差异数量变小也不能证明安全性、功能或视觉正确。
