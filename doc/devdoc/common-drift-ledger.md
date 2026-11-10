# common 上游差异台账

本台账用于定位 Kit 与本地只读 `Source/PowerToys-main` 快照之间需要人工审查的差异。它记录当前工作树的文件内容，不表示这些修改已提交，也不表示 Kit 已完整同步某个 PowerToys 发布版。同步流程见 [upstream-sync-runbook.md](upstream-sync-runbook.md)。

## 生成范围与当前基线

生成器从 `git ls-files` 枚举 Kit 的 `src/common` 文件，读取工作树中的实际字节，只比较 Source 下**相同相对路径**的文件。未跟踪的新文件不在枚举范围内；已跟踪但工作树中缺失的文件会单独列入摘要。

2026-11-09 核对时，Kit 版本为 `3.4.6`，HEAD 为 `ff04ab346e4bcd2a0fdd3a50512ac52764769e61`；工作树还包含本轮修改。Source 的版本字段为 `Version=0.0.1`、`VersionChannel=private`、`ReleaseTrainVersion=0.103`，`SourceCommit` 为空，且没有可确认的独立 Git commit。因此不能把 `0.103` 当作精确源码提交号，也不能借用外层 Kit 仓库的 HEAD 作为 Source 身份。

| 统计项 | 文件数 |
| --- | ---: |
| Git 跟踪的 `src/common` 文件 | 389 |
| Source 存在相同路径、实际参与比较 | 304 |
| 原始字节一致 `byte-equal` | 164 |
| 仅 BOM / 换行归一化后一致 `normalized-equal` | 24 |
| 通过品牌文字替换规则匹配 `brand-only` | 8 |
| 文本仍有差异 `content-diff` | 108 |
| 无法按 UTF-8 归一化且字节不同 `binary-diff` | 0 |
| Source 没有相同路径 | 85 |
| 已跟踪但当前工作树缺失 | 0 |

以上是带日期的快照。后续修改后的数值以重新生成的 [摘要 JSON](common-drift/common-drift-summary.json) 为准。108 个文本差异文件是审查队列，不能据此声称已经完成 108 个文件的语义审计。

## 产物与复算

- [生成器](../../tools/build/generate-common-drift-ledger.py)：Python 3.10+，只使用标准库。
- [同路径文件台账 TSV](common-drift/common-drift-ledger.tsv)：分类、双方 SHA-256、增删行数及首个变化预览。
- [无同路径参考文件 TSV](common-drift/common-drift-unmatched.tsv)：需查找改名对应项或确认本地新增，不能一律标为“Kit 独有”。
- [参考文件 SHA-256 清单](common-drift/upstream-baseline.sha256)：记录参与本轮比较及选定主链路参考文件的内容身份。
- [摘要 JSON](common-drift/common-drift-summary.json)：范围、规则、版本字段、计数和报告哈希。

在 Kit 根目录执行，默认模式和 `--check` 都只读：

```powershell
python tools/build/generate-common-drift-ledger.py
python tools/build/generate-common-drift-ledger.py --check
```

审查当前输入、确认需要更新台账后，显式写入四个报告，再核对：

```powershell
python tools/build/generate-common-drift-ledger.py --write
python tools/build/generate-common-drift-ledger.py --check
```

可用 `--repo-root`、`--reference-root`、`--output-dir` 指定目录。默认参考目录是 `Source/PowerToys-main`，默认报告目录是 `doc/devdoc/common-drift`。生成器不修改产品源码、Source、Git 状态、包缓存或配置；拒绝把报告写入 Kit 的 `src` 或参考快照内部，也拒绝替换报告符号链接。

`--check` 返回 0 只证明四个报告与当前输入逐字节一致。它不证明构建通过、安全策略正确或上游行为已集成。Kit HEAD、版本文件、被比较文件或参考主链路文件改变时，报告都可能过期。

## 分类规则与局限

1. 先比较原始字节；一致即 `byte-equal`。
2. 对不同字节尝试 UTF-8 解码并移除可选 BOM，将 CRLF 和独立 CR 转为 LF。双方任一无法解码即 `binary-diff`。此分类不代表文件一定是二进制，也可能是其他文本编码。
3. 归一化文本相同即 `normalized-equal`。不忽略空格、注释、大小写或其他标识符变化。
4. 仅对归一化后的 **Source 文本**依次作字面替换：`PowerToys → Kit`、`powertoys → kit`、`Powertoys → Kit`。与 Kit 文本相同即 `brand-only`，否则为 `content-diff`。

品牌规则只是检索辅助。协议名、任务名、存储路径、签名策略和兼容 API 中的名字可能影响行为，因此 `brand-only` 也不能自动合并或认定语义等价。生成器不推断重命名，不剥离注释，也不通过编译分析调用关系。

增删行数来自归一化文本、品牌替换前的 `SequenceMatcher` 行操作，不能用作风险或工作量估计。末尾换行差异可能产生 `content-diff` 而增删行数为 0。首个变化预览经过截断和转义，只便于定位，实际审查必须打开完整文件差异。

当前参考清单覆盖 304 个同路径 common 文件和 15 个选定主链路文件，共 319 个文件；其 SHA-256 为：

```text
243019adba0ff092db1f76c1bf76bd199081bc5885747a680e47fba97057f02f
```

这是**审查子集清单的哈希**，不是整个 Source 快照的哈希，也不能替代缺失的上游提交号。Source 更新后应重新记录实际版本字段和内容身份，不沿用此数值。

## 已核对的关键契约与待审方向

下表记录本轮核对到的局部契约，包含 common 之外的主链路入口；它不是整文件“领先上游”或“禁止回灌”清单。同步时逐段保留安全与产品边界，对其余差异继续审查。

| 路径 / 范围 | 已核对事实与处理方向 | 状态 |
| --- | --- | --- |
| `src/runner/auto_start_helper.cpp` | Source 也有受限任务 SDDL。保留任务写权限限制和 Kit 任务命名空间；Kit 的 `wil::unique_handle`、`unique_hlocal_string`、`unique_bstr` 属于资源管理差异，按片段比较。 | 保留具体契约，其他差异逐项评估 |
| `src/common/interop/pipe_caller_auth.cpp` | 双方均用 `HCCE_LOCAL_MACHINE` 构造证书链。Source 另有 `AuthenticateServer`，不能认定 Kit 整文件领先。核对实际客户端 / 服务端调用点及身份策略后再移植。 | 已核对共享安全机制；认证扩展待评估 |
| `src/common/interop/two_way_pipe_message_ipc.cpp` | Source 已有 `TWO_WAY_PIPE_MESSAGE_IPC_TESTS` 钩子。Kit 的 `tools/tests/NativeModules/PipeMessageSmoke.cpp` 覆盖有序交付和延迟启动服务端；不能称为“没有 IPC 测试”，故障注入钩子的消费与覆盖仍需补齐评估。 | 已有基础覆盖；故障路径待验证 |
| `src/runner/kit_module.h`、`kit_module.cpp` | 原生启动与按需加载使用单一 `KitModuleCatalog`；保留 `kit_create()` / `KitModuleIface` 及 `json_config` 长度、NUL 保护。托管目录和导航承担不同职责，模块键需保持一致。 | 保留契约，避免重新引入重复原生目录 |
| `src/common/ManagedCommon/PowerToysPathResolver.cs` | Kit 活跃路径使用 `GetKitInstallPath`；保留的兼容 API 不能仅凭名称判定无用。 | 保留 Kit 路径边界，按调用方审查 |
| `src/common/ManagedCommon/ServiceHost/KitServicePipe.cs` | 保留对端身份检查与 BOM 处理，迁移公共 IPC 时核对协议两端。 | 保留具体契约 |
| `src/common/utils/KitWorkerProcessManager.h` | 保留持有进程句柄、避免 PID 复用误杀、退出回收等生命周期约束。 | 保留具体契约 |
| `Directory.Build.props`、`Directory.Packages.props`、原生工程与 `packages.config` | 本轮将 WIL 统一到 `1.0.260126.7`，属性在早期 import 前可求值；Runner 的中央 PackageReference 是有效消费者。Source 本身也不是全部依赖统一，不能整份覆盖构建属性。 | 按最终求值的 include / import 路径验证 |
| Kit 设置、任务、协议及备份命名空间 | 保持 `%LOCALAPPDATA%\Kit` 等隔离边界，兼容入口另按调用证据判断；更新保持 check-only，遥测保持关闭。 | 保留产品边界 |
| AI Hub 删除与清理入口 | 保留显式操作、白名单及确认语义；回收失败或用户取消后改为硬删除会改变破坏性操作等级，不作为同步兜底。 | 不采纳破坏性 fallback |
| `src/common/AiHub/Engine/RouteDispatcher.cs`、`TaskAiEngine.cs` | Main 的远端认证、权限、模型、配额、限流、服务或网络失败可切换到已启用的 Fallback；本地内核目录 / 配置失败、策略拒绝、输出验证失败和调用方取消不触发切换。每批最多按 Main → Fallback 执行一次，不形成重试环。 | 按路由失败来源保留边界；真实端点结果另行验证 |
| `src/common/AiHub/Storage` | Main / Fallback 的端点配置及密钥通过 Windows DPAPI `CurrentUser` 落盘；公开偏好与密文使用配对标识和可恢复事务。保留旧配置迁移、备份恢复及损坏拒绝语义，不能通过默认配置覆盖不可解密数据。 | 属于 Kit 共享 AI 服务契约；不得仅同步明文设置消费者 |
| `src/common/AiHub/Storage/AiDiagnosticsStore.cs` | 诊断仅保存归一化元数据；历史限制为 30 天、256 条和 256 KiB，内存最近记录为 32 条。warning / error 模式保留已观察失败请求的上下文和恢复结果，关闭日志期间的观察不回填；达到硬上限时可能截断单一请求。 | 保留诊断隐私与容量边界；不代表其他文本日志已有轮转 |
| 其余 `content-diff` 与 85 个无同路径参考文件 | 需定位改名对应项、消费者、ABI / JSON / IPC 依赖与行为差异。文件缺少同路径参考不是删除依据，也不是永久分叉依据。 | 待逐项审查 |

上述 AI 服务条目来自当前源码调用链复核，不把同路径机械比对作为行为验证。本轮新增的未跟踪存储 DTO、备份类型及加密测试不在生成器枚举范围内，需连同其已跟踪消费者一起审查和构建。

后续每项审查应补充：具体差异片段、上下游调用链、保留或收敛理由、实际基线身份、验证命令与结果。只有经过这些步骤，才把某项从机械分类升级为已验证的同步结论。
