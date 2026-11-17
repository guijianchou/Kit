# common 上游差异台账

本台账对照用户提供的只读 `Source/PowerToys-0.102.3101` stable 快照。前次跟随 v0.102.3001.0；2026-11-16 起替代旧 main/0.103 对照。完整实施结论见 [框架复核](../../review-framework.md)、[官方模块复核](../../review-awake-lightswitch.md) 与 [同步流程](upstream-sync-runbook.md)。

## 当前基线

本次为 2026-11-17 Kit 3.5.1 实机反馈修复后的工作树快照，起点 HEAD `cf6969cf454b6d6826143d55320fa958cbdbe3f5`；包含 AI 调度、进度和对应测试的本地修改。摘要的 kit_head 是生成时的起点提交，不表示这些修改已提交。Source 的 `Version=0.0.1`、`ReleaseTrainVersion=0.102`、空 `SourceCommit` 是发布源码内的实际字段；没有独立 Git 提交号。使用文件哈希追溯参考子集，不借用外层 Kit HEAD。

| 统计 | 文件数 |
|---|---:|
| 跟踪的 common 文件 | 398 |
| 同路径比较 | 304 |
| byte-equal | 158 |
| normalized-equal | 23 |
| brand-only | 6 |
| content-diff | 117 |
| binary-diff | 0 |
| 无同路径参考 | 94 |
| 参考哈希清单 | 483 |

## 复算与范围

```powershell
python tools/build/generate-common-drift-ledger.py --write
python tools/build/generate-common-drift-ledger.py --check
```

默认比较 `git ls-files src/common` 的工作树内容，仅匹配参考同路径。未跟踪新增文件不在差异表中，必须连同消费者单独审查。参考哈希清单另外覆盖选定构建/Runner主链路和 Awake、LightSwitch 两个源码目录；这是参考身份记录，未对模块目录执行机械合并。

- [差异表](common-drift/common-drift-ledger.tsv)：分类、双方 SHA-256、文本变化预览。
- [无同路径表](common-drift/common-drift-unmatched.tsv)：不能直接解释为 Kit 独有或可删除。
- [参考清单](common-drift/upstream-baseline.sha256)：本次使用的参考子集。
- [摘要](common-drift/common-drift-summary.json)：版本字段、范围、计数和文件哈希。

参考清单 SHA-256：`d50e9e2136d3250c458861e86b04560ddbddabf20cc3ddcbca951082bb927f93`。不是整个 Source 压缩包的哈希，也不替代发布签名/提交号。

归一化只处理 UTF-8 BOM 与换行；brand-only 仅是 PowerToys/Kit 字面替换辅助，不代表 ABI、配置、权限语义等价。`--check` 通过只证明报告与输入一致，不证明构建或运行正确。

## 本次具体保留与修正

- 保留 Kit 七模块目录、kit_create/KitModuleIface ABI、任务/设置/备份命名空间、check-only 更新和遥测关闭。兼容输入不是删除目标。
- 新 stable 不含旧 main 的 AuthenticateServer 与 ForegroundInputInjector 扩展，撤销旧台账将它们称为本次缺失的结论。IPC 身份绑定、超时/限流与错误收敛按当前调用链单独加固。
- 保留受限任务 SDDL、安全 DLL 搜索、进程句柄身份及已加固的 AI 存储/路由边界；不以整文件覆盖方式回灌。
- Awake 保留 stable 行为并修复托盘线程归属；LightSwitch 同步可配置热键、CLI 生成互操作和上游测试；未带入 main 锁屏显示策略。
- 依赖跟随 stable 的实际版本，WIL 保留 Kit 统一的 1.0.260126.7。
- 当前内容差异仍包含大量 Kit 产品裁剪与兼容设计；计数不等于已完成同等数量的语义审计。

最终验证以本轮 TestResults 和 review 报告为准，历史 Release350 结果不证明当前工作树。
