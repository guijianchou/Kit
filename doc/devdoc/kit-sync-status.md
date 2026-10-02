# 上游同步与稳定性优化记录

本轮基于本地 `Source/PowerToys` 对照，保留选择性同步 PowerToys 主框架和模块系统的方式。源码更新与已编译插件的二进制兼容是不同的验证项；当前 ABI 冒烟只验证本地参考版本，不能保证未来上游接口永不变化。

## 参考基线

- 本地路径：`Source/PowerToys`，`src/Version.props` 的 `ReleaseTrainVersion` 为 `0.101`。
- 上游接口：`src/modules/interface/powertoy_module_interface.h`。
- 接口 SHA-256：`5204A5B7FCB40B69D3BC94EF6DFE498F818EF71DF756F0F24EB7EF1B686B373B`。
- 上述信息标识当前本地快照，不代表已核实其远端最新提交。

## 本轮改动边界

| 范围 | 修改 | 同步时应保留的行为 |
| --- | --- | --- |
| Runner `main.cpp` | 关闭 Settings 后停止键盘钩子、清理模块，再释放单实例锁 | 旧模块的停止事件不能影响新 Runner 的 worker；正常退出与重启使用相同顺序 |
| Localserver | 页面和 worker 每轮共用惰性进程快照 | 无有效所有权记录不扫描进程；多服务恢复只扫描一次；每轮重新取样 |
| Localserver worker | 监护阶段通过 `finally` 完成恢复和停止，清理使用独立于监护取消的 token | 取消和监护异常不能直接跳过已加载服务的最终清理 |
| 启动测量 | 默认检查六个模块，记录逐模块耗时，检查自订 worker 是否已运行 | 按实际模块数验收；仅在空闲测试会话执行 |

本轮未更改 `KitModuleIface` 虚函数顺序、Hotkey 布局、`kit_create` / `powertoy_create` 导出回退、`module_status` IPC、设置 JSON 模式或模块注册方式。Awake、LightSwitch 及参考源码未修改，没有新增依赖或插件框架层。

## 后续同步验证

1. 更新参考源码时记录其提交或快照信息，先检查接口头、Runner 生命周期、设置 IPC 的差异。
2. 使用现有 `tools/tests/NativeModules/Build-Harness.ps1` 重新编译上游 fixture 与 Kit host，再运行 `ModuleAbiSmoke.exe`。fixture 必须来自更新后的头文件，不能复用旧 DLL。
3. 将框架修复按文件或小提交导入；保留 Kit 的存储隔离、显式模块注册，以及关闭自动更新下载和遥测的边界。
4. 定向构建受影响工程，运行设置注册/生命周期测试；涉及 worker 时补做正常退出、异常退出、禁用及立即重启验证。

## 性能证据与限制

现有部署实例日志中，Runner 初始化为 93–95 ms，六个接口 DLL 单个加载为 0–5 ms；Settings 从自身计时起点到 Dashboard 首次 Loaded 约 1.21–1.31 秒。这些是已有运行记录，不是本轮改动的前后基准，也不等于真正冷启动或画面呈现完成。

因此本轮不改变模块加载模型。设置首屏的 WinUI 初始化与布局值得后续单独测量；直接提前启动 Settings 会让其后台线程中的 `get_general_settings()` 与模块集合修改发生并发，还可能提前保存不完整的 enabled 状态，需要先解决快照和线程归属。

Localserver 的收益用行为验证：无所有权记录时扫描次数为零，两个已有服务在同一恢复轮次中只扫描一次，随后均能停止并释放进程。没有把扫描次数减少换算成未经测量的毫秒收益。

## 本轮验证

- x64 Debug：Localserver 测试工程及依赖、Settings 测试工程及依赖、Runner 定向构建均通过。Runner 使用 `/p:BuildProjectReferences=false`；本轮未改动其原生依赖。
- Localserver：28/28 通过，包括真实子进程恢复与停止、无所有权记录零扫描、两个服务共享一次扫描。报告：`TestResults/StabilityOptimization/localserver-stability.trx`。
- 设置与框架：12/12 定向回归通过，包含退出顺序和取消清理的源码约束、现有启动/注册/兼容性检查。报告：`TestResults/StabilityOptimization/framework-stability-direct.trx`。VSTest 宿主监视父进程时遇到 Windows 拒绝访问，改用已编译程序集的 MSTest 直接入口执行同一筛选；没有更改权限。
- ABI：重新编译 `UpstreamFixture.dll` 与 `ModuleAbiSmoke.exe` 后通过全部虚函数槽位、热键结构、旧导出和 Kit 侧销毁验证。
- 启动测量脚本 PowerShell 语法检查通过。

当前另一部署目录中的 Kit 仍在运行，本轮未中断该会话。完整重启计时、实际窗口体验、取消信号注入及退出后立即重启的端到端验证尚未执行；上述定向测试不替代这些检查，也不构成 Release 验收。
