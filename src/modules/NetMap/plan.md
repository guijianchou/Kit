# NetMap 实施计划

更新：2026-10-01。用户已确认：整理计划后直接实现。本文替代设计草案，按 PLUGIN_DEVELOPMENT.md 接入 Kit。

## 已冻结需求

核心是普适：用户在外部客户端频繁换节点，NetMap 自动观察出口，不读取订阅、不绑定代理品牌、不控制节点。

- 顺序：Logo / NetMap / 模块开关 → 指示灯与同侧 Start/Stop → 直连/代理卡片 → 离线地图与 MTR 逐跳列表 → 代理服务连通性 → 设置。出口卡片常驻 IP、地区、ASN、状态和时间；来源、连接方式及结果时效说明收进“详情”。界面跟随 Kit 的中英文配置。
- 模块默认关闭。页面进入/重新打开不自动检测；Stop、模块禁用、离开页面、窗口隐藏/最小化均取消会话并保留内存结果。普通失焦不停。
- 两通道分别显示 IP、来源、地区、最后成功时间、错误与可用的 ASN/城市。成功 IP 为绿色，失败标红，前两次保留上次信息，连续第三次显示 N/A；成功后恢复。Proxy 失败不能清空 Direct 或回退 Direct 冒充成功。
- 新 IP 立即显示；连续两次相同 Proxy 成功观测后开始该出口诊断。新观测、失败、停止立即使旧诊断失效，UI 队列二次检查会话与出口代次。
- 相同出口不是自动失败；多节点共享 IP 时无法仅凭 IP 辨别切换。
- 删除 NetMap AI 分析 UI、调用、任务策略和依赖。服务仅走 Proxy：Claude/ChatGPT 使用用户指定的 trace 检查点，Gemini/Google 使用网页，每 10 秒一轮，不请求模型列表或生成 API。
- 服务与 ICMP 最近 RTT：正常且 ≤75 ms 绿色、>75 ms 黄色、错误红色；登录/验证/限流或不完整响应为黄色。三次失败后不可用实时值显示 N/A，成功恢复；MTR 平均 RTT/丢包率继续累计。等待/停止为灰色，适配明暗主题。

## 来源与实现边界

| 内容 | 实现 | 边界 |
| --- | --- | --- |
| Direct | https://api.bilibili.com/x/web-interface/zone ，JSON code + data.addr/country/province/city/isp | 显示实际结果，不写死大陆；服务字段可能变更 |
| Proxy | https://1.1.1.1/cdn-cgi/trace ，逐行 ip/loc | key=value 文本；成功不证明经过代理 |
| Direct 连接 | UseProxy=false | 无法绕过系统 TUN |
| Proxy 连接 | 系统代理、显式 HTTP/HTTPS/SOCKS5、系统路由/TUN | 系统模式每次刷新 Windows 配置并遵循绕过规则；显式地址拒绝凭据和路径，连接失败不回退 |
| 地图 | Natural Earth v5.1.2 110m（公共领域），原生 WinUI 离线绘制 | 国家代表点是地区示意，不是设备精确位置；不虚构代理隧道连线 |
| ASN/城市 | 本地 GeoLite2-ASN/City MMDB 优先，ASN 可手动从 GitHub 更新；缺失字段通过 ipwho.is 补齐 | 用户已同意将待查公网 IP 发给服务商；默认开启，可关闭；内网/保留地址不外发，查询失败不阻塞采样 |
| 服务连通性 | 仅 Proxy：api.anthropic.com/cdn-cgi/trace、chatgpt.com/cdn-cgi/trace、gemini.google.com、www.google.com | trace 校验 ip/loc，分别展示各自出口；网页区分响应/登录/验证。不证明完整聊天功能可用 |
| 逐跳 | .NET Ping IPv4 TTL 到当前 Proxy IP | 本机系统路由观测；HTTP/SOCKS 不承载 ICMP；不是代理隧道内部路由；IPv6 身份仍显示 |

## 预算与所有权

- Direct 每 30 秒、Proxy 每 5 秒，分别串行。每次使用新 HTTP handler 防止连接池粘住旧节点；HTTP 取消期限 5 秒，身份正文最多 64 KiB，标准 TLS 校验，不跟随重定向。Windows 系统代理/PAC 解析含同步调用，不能承诺该步骤在 5 秒内返回；它在后台会话中执行，停止后仍通过会话/代次检查阻止回填。
- 稳定出口立即开始首轮服务检测，此后每 10 秒一轮，四个 Proxy 请求并发执行；轮次不重叠，错过的周期合并。单请求 10 秒，Gemini/Google 最多 5 次跳转，trace 不跟随跳转，正文最多等 2 秒并读取前 64 KiB 用于识别浏览器验证；收到响应头后保留 HTTP 连通证据，单项完成即回填，每个服务每轮只累计一次失败。Direct/Proxy 身份观测仍使用 30/5 秒间隔。
- MTR 每轮最多 24 跳、每跳 800 ms、轮间 2 秒，持续至停止或出口变化；串行发送，不需要 raw socket/Npcap/提权。地图以实线连接相邻已定位节点，以明确标注的虚线连接未知段/出口示意；列表保留所有跳数、ICMP 丢包率及最近/平均 RTT；无响应不等于业务丢包。
- 在线位置补齐与出口身份异步分离，同一会话缓存最多 512 个地址、最多 256 次请求、查询间隔至少 1 秒。429 会停止本会话后续在线请求；失败结果缓存至重新开始。
- 会话拥有全部任务与取消源。停止先使会话失效，再取消；不创建 Worker、后台监视器或历史库。
- 主开关仅 GeneralSettings.Enabled.NetMap。非秘密设置经现有 SndModuleSettings → Runner → 原生 set_config 保存；不直接竞争写文件。
- 公网 IP/响应正文不进日志。用户 Logo 原图保留，使用已裁剪派生素材。

## 实施进度

- [x] P1：NetMapModuleInterface（默认关闭）、NetMapLib（解析、HTTP、取消/代次隔离）、必要行为单测。
- [x] P2：设置 DTO/sourcegen、Runner、导航、深链、模块目录、Dashboard/QuickAccess 图标注册。
- [x] P3：WinUI Page/ViewModel、双出口状态、启停/可见性生命周期、响应式卡片/离线地图、中英文资源。
- [x] P4：本地 MMDB、服务响应、IPv4 逐跳统计；只有有来源的坐标进入地图。
- [x] P5：移除预览 AI 入口；顺序构建、行为测试、注册回归、视觉检查，实际验证边界见下。

## ASN 更新范围（2026-10-01）

- 按用户最终决定仅增加 ASN 下载/更新，Natural Earth 底图继续内置 v5.1.2，不实现地图或城市库更新。
- 来源固定为 P3TERX/GeoLite.mmdb 最新 GitHub Release；要求资产 `sha256:` digest，验证大小、SHA-256 与 MMDB 类型后才替换 `%LOCALAPPDATA%\Kit\NetMap\Data\GeoLite2-ASN.mmdb`。自定义 ASN 路径始终优先且不被覆盖，路径为空时使用托管副本。
- 使用已应用的代理配置，手动触发、停止检测、提供进度与取消。最多 5 次受限 GitHub HTTPS 跳转、2 MiB 元数据、32 MiB 数据文件、3 分钟下载期限。失败与取消清理临时文件并保留旧库；离页/隐藏/最小化/禁用取消下载。
- 不为数据库更新新增后台任务、配置字段或通用更新框架。城市坐标仍由可选本地城市库或在线补齐提供。

## 验收

无网络/禁用/未检测可以打开地图；Stop 后无回填；单通道失败、恢复、同出口、IPv6、错误/超限正文分别可判定。模拟旧请求迟到，切换及重启后不会覆盖。普通失焦不停，隐藏/最小化/离页/外部关闭会停止。缺失/损坏 MMDB 时在线补齐，关闭在线查询或查询失败时显示具体原因；地图含来源与地区示意说明。NetMap 无 AIHub 引用或模型请求。Windows x64 Debug 使用 tools/build/build.ps1 顺序构建，使用 VSTest 运行相关测试；真实代理/TUN 与实际 GUI 验证单独记录。

## 地图空间与跨太平洋连线修订（2026-10-01）

- 根因：地图/列表按 2:3 分配空间，MTR 的 IP/地区使用比例列且行容器留白较大；底图固定为大西洋居中，跨 ±180° 连线被拆至左右边缘，相关节点跨度超过半张地图时不缩放。
- MTR 桌面列宽固定为 440 DIP，IP 列固定 120 DIP，地区使用剩余宽度并可换行，指标右对齐；行容器收紧至 32 DIP 起。其余宽度分给地图，地图高度上限 260 DIP，内容宽度不足 880 DIP 时上下排列。
- 按节点经度的最大空白区选择地图接缝，使用相邻底图副本保持轮廓连续，位置与连线共用展开后的经度。跨太平洋路线以太平洋为中心显示，保留观测实线/示意虚线、选中状态与只更新 RTT 时不重绘的行为。
- Settings UI x64 Debug 构建 **0 警告、0 错误**，中英文 WinUI 回归通过。新增中国→美国西岸→美国中部→新加坡和 ±179° 场景，检查连线段数、端点衔接、视口留白和相邻节点距离；检查明暗主题和窄窗口截图，第 19 跳连续刷新与延迟地区补齐仍保持滚动。
- 1200×900 下地图宽 **473 DIP**、MTR 表宽 **440 DIP**，四跳地图卡片底部中文 **802 DIP**、英文 **818 DIP**，可用高度 **842 DIP**。本轮只修改 UI 绘制/布局及页面回归，没有改变网络探测；103 项核心测试结果沿用上一轮。
- 日志前缀 `TestResults/NetMap/pacific-`；各语言目录新增 `netmap-winui-pacific-dark.png`、`netmap-winui-pacific-light.png`，均使用合成 IP/位置，不代表真实物理路由验收。

## 状态灯与检测周期修订（2026-10-01）

- Proxy 服务增加 `https://www.google.com/`，与 Claude/ChatGPT trace、Gemini 网页共四项，按 10 秒周期并发检测，轮次不重叠。增量与最终回调去重，避免一次请求被计作两次失败。
- 服务结果增加状态灯、明确的连接/较慢/需注意/失败文字及突出显示的耗时。服务、目标 ICMP RTT、逐跳 RTT 和地图节点按 ≤75 ms 绿色、>75 ms 黄色、错误红色显示；登录/验证/限流等有 HTTP 响应的问题保持黄色。等待/停止使用灰色，颜色来自明暗主题系统资源。
- Direct/Proxy 成功 IP 绿色；连续失败标红并显示 1/3、2/3、3/3，第三次清除旧身份/地区并显示 N/A。服务及逐跳实时值同样在第三次失败显示 N/A，下一次成功恢复；路径身份用于统计连续性，平均 RTT/丢包率保留累计。仅 RTT 变化时原位更新地图节点样式，不重建几何或列表。
- 已参考用户提供的 [Gemini API 文档](https://ai.google.dev/api/all-methods#index)：开发 API 需要 Key，不能代表网页/客户端可用性，继续检测 `gemini.google.com`，不引入模型调用。
- 核心测试 **103/103**，Settings UI x64 Debug 构建 **0 警告、0 错误**；中英文实际 WinUI 检查通过。覆盖 75/75.1 ms 边界、三次失败 N/A 与恢复、四项服务状态、生命周期及 ASN 取消；明暗主题与 720×1000 窄窗口截图已检查。
- 合成数据检测栏 **54 DIP**、出口卡片 **166 DIP**；1200×900 窗口四跳地图底部中文 **792 DIP**、英文 **808 DIP**，可用高度 **842 DIP**。第 19 跳连续刷新 12 次后保持滚动和选中项，延迟地区补齐不跳动。
- 产品代码实测四项均 HTTP 200，Claude/ChatGPT trace 有效，Gemini/Google 页面响应；响应头耗时分别约 676/430/857/1648 ms，均正确判为黄色。这只代表当前网络。报告 `TestResults/NetMap/status-live.json` 不记录公网 IP；核心报告 `Zen_R7000P_2026-10-01_07_22_59_net10.0.trx`，构建/页面日志前缀 `status-`，双语截图新增 `netmap-winui-status-failures.png`。
- 真实代理/PAC/TUN、城市 MMDB、ICMP 完整矩阵及干净 Release 仍未完成，本轮不扩大验收结论。

## Proxy 检查点与地区修订（2026-10-01）

- 服务表取消 Direct 请求和列。最终目标为 `https://api.anthropic.com/cdn-cgi/trace`、`https://chatgpt.com/cdn-cgi/trace`、`https://gemini.google.com/`；Claude/ChatGPT 校验完整 trace 的 IP/国家代码，拒绝将普通 HTTP 200 HTML 或部分内容当作检查点成功。各域名的出口单独展示，不改变主 Proxy/MTR 目标。Gemini 保留网页响应、登录、浏览器验证与错误分类。
- MTR 的 IP 与 Loss 之间新增 Loc 列，保留地区未知、查询中、内网和限额状态；地区异步到达时原位更新。地图、卡片和节点详情展示完整地区；列表空间调整为地图/列表 2:3，窄窗口仍上下排列。
- 保留 Bilibili 省市字段、本地 MMDB subdivision 和在线 region；本地与在线地区语言跟随 Kit 中英文。中文直连卡片使用 Bilibili 已知省市，其他节点显示 IP 查询的国家/省/市。观测国家与 GeoIP 冲突时，不拼接异国城市或坐标。
- 现有源实测可查公共大陆地址 `114.114.114.114`，返回江苏省/南京及坐标，因此没有替换在线源。用户指定的 P3TERX City 库可通过现有本地 City 入口优先使用；Country 库不能补足省市。本轮仍只有 ASN 的 SHA-256 下载更新接口，没有新增 City/Country 下载器。
- 核心测试 **90/90**；最终 Settings UI x64 Debug 构建 **0 警告、0 错误**；中英文 WinUI 检查均通过，包括省市/trace 出口展示、第 19 跳连续 12 次采样与延迟地区补齐保持滚动。检测栏 **54 DIP**、出口卡片 **166 DIP**；1200×900 四跳地图底部 **792 DIP**，可用高度 **842 DIP**。
- 产品代码真实检查：Claude/ChatGPT 均 HTTP 200 且 trace 有效，Gemini HTTP 200 页面响应；三项均 IsProxy=true。报告 `TestResults/NetMap/proxy-loc-live.json` 不记录本机公网 IP。核心报告 `Zen_R7000P_2026-10-01_06_48_43_net10.0.trx`，构建/页面日志前缀 `proxy-loc-`；双语截图新增 `netmap-winui-services.png`。真实结果仅代表本轮网络，不替代所有代理模式验证。

## 延迟与滚动修订（2026-10-01）

- 根因：ViewModel 每次采样重新创建 Hops/Services 数组并通知整页，ListView 重置数据源和滚动位置；地图对未定位跳直接断线，RTT 详情主要藏在 tooltip 中。
- Hops 改为保持身份的可观察集合，行内更新 RTT/丢包；只有诊断代次改变才清空。服务行也原位更新，MTR 采样不再重建出口卡片。地图仅在位置/节点/选择变化时重绘。
- 地图对相邻已定位跳画实线，未定位跨度和出口示意画虚线；补齐到目标的示意段，跨日期变更线分段绘制。根据已知节点放大底图、标注跳数，选中节点或目标显示最近 RTT/丢包。常驻最近/平均 RTT 和目标指标，详情显示最快/最慢 RTT。RTT 均为本机到该跳的往返时间，不当作相邻链路耗时。
- 网页收到响应头即保留 HTTP 连通证据；正文最多等待 2 秒，失败不覆盖已知 HTTP 状态。支持响应解压与单请求内 Cookie 跳转，保留浏览器验证/登录等分类，响应头耗时与正文读取分离。两并发执行，各项结果完成即回填。
- 核心测试 **77/77** 通过，新增正文中断、部分验证页、登录、响应头计时、用户取消与逐项回填验证。报告：`TestResults/NetMap/Zen_R7000P_2026-10-01_05_57_01_net10.0.trx`。
- 本轮真实网络中 ChatGPT 返回 HTTP 403 浏览器验证，Claude/Gemini 在响应头前超时；不把这些环境结果推广到所有节点。结果在 `TestResults/NetMap/latency-web-live.json`。
- 最终 Settings UI x64 Debug 构建通过，0 警告、0 错误；`zh-CN`、`en-US` 实际 WinUI 检查均通过。24 跳列表滚动并选中第 19 跳后连续更新 12 次，数据源、行对象和地图线段对象保持不变，选中项保留，滚动偏移变化小于 1 DIP。测试使用合成网络数据。
- 检测栏 **54 DIP**、出口卡片 **166 DIP**；1200×900 窗口中，四跳合成数据的地图卡片底部 **801 DIP**，可用高度 **842 DIP**。已检查中英文、明暗主题与 720×1000 窄窗口；地图延迟标签和跳数使用随页面主题更新的资源，出口标记不再遮住跳数。
- 验证还发现快速返回缓存页可能不再次触发 Loaded；在 OnNavigatedTo 恢复页面可操作状态，并用当前导航状态限制窗口可见性回调，保留返回后不自动检测的行为。两种语言均通过返回页及 ASN 下载取消检查。
- 构建日志：`TestResults/NetMap/latency-ui-build.log`；页面日志：`latency-smoke-zh.log`、`latency-smoke-en.log`；截图位于各语言目录，新增 `netmap-winui-scrolled-mtr.png` 展示刷新后第 19 跳的保留状态。

## 首轮反馈修订验证（2026-10-01）

- 定向 x64 Debug 构建通过，核心测试 **68/68**，设置测试 **3/3**。新增用例覆盖公网地址过滤、在线结果校验与缓存/限额、网页登录/验证/错误分类、MTR 持续采样、逐跳无响应率与终点超时后的跳数边界。
- 实际 WinUI 分别使用 Kit 的 `zh-CN`、`en-US` 配置运行，验证开始/停止、动态出口名称、网页结果、ASN 展示、地图相邻节点连线、逐跳丢包率和既有离页/隐藏/最小化取消行为。检测栏 **54 DIP**，卡片 **166 DIP**，1200×900 下地图区域底部 **753 DIP**，可用高度 **842 DIP**。
- 报告与明暗/窄窗口截图在 `TestResults/NetMap/zh-CN` 和 `TestResults/NetMap/en-US`；页面测试仅使用合成数据，暂存并还原 Kit 语言及设置。
- 最终双语页面检查也通过 ASN 下载入口、重复操作禁用、取消后的控件恢复、页面失活取消，以及国家名称独立于 Windows 语言的断言；更新区域截图为各语言目录下的 `netmap-winui-asn-update.png`。核心测试报告：`TestResults/NetMap/Zen_R7000P_2026-10-01_04_30_36_net10.0.trx`。
- 独立实际网络检查：ipwho.is 对公共示例地址返回 ASN 和坐标；用产品的网页检测代码观察到 Claude/ChatGPT 的浏览器验证响应，以及 Gemini 连接失败，未使用账号、密钥或调用模型。这只是当前网络结果，不是所有代理模式或网站功能的验收。
- ASN 更新新增 15 项测试覆盖哈希缺失/无效/不匹配、伪造 MMDB、超限、取消及越界跳转，失败保留旧文件并清理临时文件。实际产品下载代码将 `2026.10.01` Release 下载到 `TestResults/NetMap/asn-live`，SHA-256 为 `e16e2db7ed72adc4443e16d7e33a25120ca58bd8d0f2e902b49ac6c50c3f4815`；离线查公共示例地址得到 AS15169，再次更新跳过下载。没有改动用户的 ASN 路径或托管副本。
- 尚待真实 HTTP/HTTPS/SOCKS5、PAC/TUN 组合与用户实机 MTR 反馈；有效本地城市 MMDB、干净 Release、高对比度、150%/200% 缩放、480 DIP 及 Narrator 完整矩阵仍未覆盖。

## 初版验证记录（2026-10-01，反馈修订前）

- Windows x64 Debug：NetMapLib、NetMapModuleInterface、Runner、Settings UI、Settings UI tests、QuickAccess 顺序构建通过。已检查运行目录中的 NetMap 图标、Logo 和第三方许可文件。
- NetMap 核心行为测试 **28/28 通过**：解析、异常/超限正文、IPv6、代理配置校验、缺失/损坏数据库、切换/停止/重启后的迟到结果隔离等。报告：`TestResults/NetMap/Zen_R7000P_2026-10-01_02_48_01_net10.0.trx`。
- 设置与接入回归 **92 项，88 通过、4 失败**；新增 NetMap 设置测试 **3/3 通过**。VSTest testhost 在监视父进程时被 Windows 拒绝访问，改用已构建的 MSTest 测试可执行文件运行相同筛选。报告：`TestResults/NetMap/Zen_R7000P_2026-10-01_09_52_39.104.trx`。
- 四个回归失败与本次 NetMap 变更无关，已核对 HEAD 对应源码：`KitGpoPolicySurfaceShouldStayInActiveModuleSurface`、`KitGpoPolicyAssetsShouldStayInActiveModuleSurface`（既有实验功能 GPO 接口/策略资产缺失）、`KitSettingsLaunchFailureShouldNotLeaveLaunchInProgress`（既有 Runner 退出清理逻辑）、`SettingsXamlNamedElementsShouldUseXNameForReleaseGeneratedFields`（既有 GeneralPage 实验功能命名元素缺失）。未修改这些既有问题。
- 实际 WinUI 页面测试通过：默认关闭、禁用时离线地图、手动启动、最小化/隐藏/外部禁用/离页停止、恢复和缓存返回不自动启动。缓存页在导航动画中仍可能处于 Loaded，因此在 `OnNavigatedFrom` 立即停采，不能只依赖 `Unloaded`。
- 针对卡片过高的反馈，实际合成 IPv4 数据布局测得：检测栏 **62 DIP**、出口卡片 **166 DIP**；地图绘图区最高 **280 DIP**，1200×900 窗口内地图卡片底部为 762 DIP，可用页面高度为 842 DIP。窄窗口将卡片堆叠，并移除空列间距；长 IP/文本允许自然换行，以上数值不是强制固定高度。
- 已检查浅色、深色、720×1000 窄窗口截图，修正地图分隔符乱码及深色地图标记；报告和截图在 `TestResults/NetMap/page-smoke.log`、`netmap-winui-light.png`、`netmap-winui-dark.png`、`netmap-winui-narrow.png`。测试使用合成出口，不发起真实端点请求。
- **尚未验证**：真实 HTTP/HTTPS/SOCKS5、Windows PAC 和 TUN 的组合、有效 MMDB 文件中的真实数据、真实网络下的服务及 ICMP 行为；干净 Release 构建、高对比度、150%/200% 缩放、480 DIP 最小窗口和 Narrator 完整矩阵。上述实现完成不等于这些环境已经验收。

## 后置

地图在线更新、城市库下载、Google 地图、IPv6 traceroute、固定目标编辑、历史趋势、后台采样、自动换节点及代理认证不在首版，不为这些能力预留架构。在线 GeoIP 已按用户反馈纳入本次修订。
