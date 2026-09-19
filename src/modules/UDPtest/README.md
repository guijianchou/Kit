# UDPtest 模块与插件文档

本文档记录将 **Network**（原版独立 WinUI 3 便携应用，`NetworkMonitor`）移植为 **Kit** 原生插件模块（`UDPtest`）的架构、关键设计决策、探针实现要点与移植经验，供后续维护参考。

---

## 1. 模块基本信息与架构定位

`UDPtest` 是 Kit 内部面向 Windows 11 的**网络节点质量评估**模块。它并行探测多条线路，持续评估 TCP/UDP 连通性、延迟、抖动、出口地址与稳定性，并独立判定 NAT 类型。

| 属性 | 配置 / 路径 | 说明 |
| --- | --- | --- |
| **模块名称** | `UDPtest` | 统一标识符，与模块接口大小写一致 |
| **模块键名 (Key)** | `UDPtest` | 用于设置序列化、GPO 策略匹配及路由注册 |
| **原生接口 DLL** | `Kit.UDPtestModuleInterface.dll` | C++ 原生 DLL，导出 `kit_create()`，实现 `KitModuleIface` |
| **托管业务库** | `Kit.UDPtestLib.dll` | 探针、协调器、指标引擎、目标存储 |
| **设置与交互界面** | `UDPtestPage.xaml` / `UDPtestViewModel.cs` | WinUI 3 + Mica Alt，内嵌于 Kit Settings 进程中 |
| **设置与 DTO** | `UDPtestSettings.cs` / `UDPtestProperties.cs` | 注册至 `KitModuleCatalog` 与 `SettingsSerializationContext` |
| **路由与深度链接** | `kit://settings/UDPtest` | 注册于 `Common.UI/SettingsDeepLink.cs` |

---

## 2. 分层架构

```
Kit Settings UI (WinUI 3)
  UDPtestPage.xaml / UDPtestViewModel.cs   — 线路表格、健康图、NAT/公网信息
        │ Managed Ref / In-process
Kit.UDPtestLib
  Engine/ProbeCoordinator.cs   — 并行探测流水线、代际重建、NAT worker、优雅停机
  Probes/TcpHttpsProbe.cs      — no-proxy HTTPS 探测（TTFB）
  Probes/StunProbe.cs          — RFC 5389 STUN Binding
  Probes/UdpEchoProbe.cs       — UDP Echo
  Probes/NatTypeProbe.cs       — 独立 NAT 类型判定（映射/过滤行为）
  Probes/StunMessageCodec.cs   — STUN 编解码（MagicCookie / 事务 ID / XOR）
  Core/MetricsEngine.cs        — 平均 RTT/抖动/质量/成功率/周期平均
  Storage/TargetSettingsStore.cs — 线路目标持久化
        │ IPC / Lifecycle Sync
Kit.exe Runner (C++ Win32 Core)
  — 动态加载 Kit.UDPtestModuleInterface.dll，管理模块生命周期
```

---

## 3. 关键设计决策与移植经验

### 3.1 计时语义：延迟取 TTFB，不读完整响应体

- **决策**：`TcpHttpsProbe` 在响应头到达后立即停表（`stopwatch.Stop()` 先于响应体排空），测得的是**首字节时间 (TTFB)**。
- **理由**：线路质量评估关注的是网络往返与服务器响应速度，而非响应体下载速度；读取完整响应体会把带宽与内容长度混入延迟指标。
- **注意**：排空响应体仍会执行（释放连接、避免套接字堆积），但不再计入耗时。

### 3.2 每条线路独立 `HttpClient`，随代际重建

- 每个 probe 实例持有自己的 `HttpClient`（含独立 handler），而非共享静态单例。
- **理由**：路由变化或公网身份变化时需要取消本轮并重建全部探针（generation barrier），共享客户端会让旧代际的连接池泄漏到新代际。
- probe 实例随代际整体重建，`Dispose` 负责释放客户端。

### 3.3 单线路 single-flight 与有界结果槽位

- 每条线路内部不允许并发重叠探测（single-flight），避免同一线路的结果互相交错。
- 协调器使用 `System.Threading.Channels` 建立有界结果槽位，单 writer 消费，防止探测速率超过消费速率导致内存增长。

### 3.4 NAT 判定独立于可配置线路

- NAT worker 有**独立的启停生命周期**，不读取可配置 UDP 线路、开关、排序或线路质量。
- 使用固定公共 STUN 节点集合；映射依据 RFC 5389/8489，过滤行为优先参考 RFC 5780。
- **保守降级**：当公共服务器缺少完整 RFC 5780 能力时，输出带说明的降级结论（如 `DowngradedPortRestricted`），而不是永久显示 `N/A`，更不臆断为 Full Cone。
- 结论是**最佳判定**而非绝对事实：最终连通性仍以 ICE/P2P 实际协商为准。

### 3.5 STUN 编解码严格按规范校验

- 校验 Magic Cookie `0x2112A442`，不符即拒绝；
- 校验事务 ID，不匹配返回 `TransactionMismatch`（防止迟到的脏响应污染本轮结果）；
- 支持 `MAPPED-ADDRESS`、`XOR-MAPPED-ADDRESS`、`CHANGED-ADDRESS`、`OTHER-ADDRESS`，其中 XOR 变体按规范解码。

### 3.6 本地化遵循工程标准

- 页面文案统一使用 `x:Uid` + 内联兜底文案（resw 覆盖），不使用代码内 `IsChinese` 三元判断。
- `UDPtestPage` 是本工程内**本地化正确范本**之一，AIHub 的本地化整改亦以其为参考。

---

## 4. 与原版 Network 的差异

| 维度 | 原版 Network | Kit UDPtest |
| --- | --- | --- |
| 宿主形态 | 独立 WinUI 3 便携应用（解压即用，不装服务） | 嵌入 Kit 主框架，UI 在 Settings 进程内 |
| 数据持久化 | **SQLite 入库**（`network-monitor.db` + WAL/SHM、session、segment、逐探针 sample） | **仅内存**（清空即失），SQLite 未承接 |
| 历史与图表 | SQLite 支撑的历史页与图表 | 内存内实时曲线与健康图 |
| 探针与指标 | TCP HTTPS / UDP STUN / Echo、滑动窗口、NAT 判定 | **一致**（`MetricsEngine` 与原版 `Network.Core` 对齐） |

> 说明：SQLite 持久化能力在移植中未承接，属**已知差距**（见 `fix.md` §4）。重新引入需评估依赖与 `NOTICE.md` 更新，或改为按日期分片的文件存储。

### 4.1 与 AIHub Optimization 的关系

原版 `Network` 独立于 Monitor；Kit 侧网络探测专注**线路质量**，而磁盘空间优化由 `AIHub` 的 Optimization 承接——两者职责不重叠。

---

## 5. 源码对应关系

```text
src/modules/UDPtest/
├── UDPtestLib/                     # 托管业务库 (net10.0-windows)
│   ├── Engine/ProbeCoordinator.cs  # 探测流水线、代际、NAT worker
│   ├── Probes/                     # TcpHttps / Stun / UdpEcho / NatType / Codec
│   ├── Core/MetricsEngine.cs       # 指标计算与 UDP 结论
│   └── Storage/TargetSettingsStore.cs
├── UDPtestModuleInterface/         # C++ 原生接口 (KitModuleIface)
│   ├── dllmain.cpp                 # 导出 kit_create()，响应 enable/disable
│   └── UDPtestConstants.h
└── README.md                       # 本文档

src/settings-ui/
├── Settings.UI/SettingsXAML/Views/UDPtestPage.xaml
├── Settings.UI/ViewModels/UDPtestViewModel.cs
└── Settings.UI.Library/UDPtestSettings.cs / UDPtestProperties.cs
```

---

## 6. 测试与验证

`UDPtest.UnitTests` 覆盖 18 项，分五类：

| 测试类 | 覆盖内容 |
| --- | --- |
| `StunMessageCodecTests` | XOR 映射解析、Magic Cookie 拒绝、事务 ID 拒绝、属性顺序、Change Request 标志 |
| `TargetParserTests` | STUN/Echo/TCP 目标解析、非法端口、路径与片段拒绝、IPv6 拒绝、非 HTTPS 拒绝 |
| `NodeVerificationTests` | 三出口一致性判定、境内位置不判定、启用线路不足时不判定 |
| `StorageTests` | 目标设置持久化往返 |
| `MetricsEngineTests` | 成功率/延迟指标计算、UDP 结论评估 |

**已知覆盖缺口**：`ProbeCoordinator` 编排层（Channel 流水线、代际重建、NAT 循环）与具体 `Probe` 实现类尚无测试——见 `fix.md` §4。

```powershell
dotnet test src\modules\UDPtest\UDPtest.UnitTests\UDPtest.UnitTests.csproj -c Debug -p:Platform=x64 --nologo
```
