# NetMap

NetMap 是 Kit 内置的出口观测插件：在外部代理客户端频繁换节点时，持续显示当前 Direct、Proxy 的出口 IP、地区与可用的 ASN，并在离线地图上呈现有来源的位置。无需导入订阅或绑定代理品牌，不负责控制、自动选择节点。

与 [UDPtest](../UDPtest/README.md) 面向已配置线路的质量探测不同，NetMap 主要回答“当前从哪里出去，两个通道有什么差别”。首版没有 AI 分析或模型调用。

## 开始使用

1. 打开 Kit 设置中的 **NetMap**，启用顶部模块开关。模块和检测默认均关闭，启用模块本身不发起探测。
2. 展开底部“连接与地图设置”，按下面的表格选择代理接入方式；修改后点击“应用设置”。应用设置会停止当前检测，需手动重新开启。
3. 默认在线补齐 ASN 和节点位置，可优先使用自己的 GeoLite2-ASN、GeoLite2-City `.mmdb` 文件。仅使用本地数据时，关闭“在线补齐 ASN 和节点位置”并应用。
4. 点击“开始”。同侧提供“停止”，左侧指示灯显示运行状态。在外部客户端切换节点，等待下一次代理出口观测更新；无需在 NetMap 内重复配置节点。
5. 点击 **Stop / 停止** 结束本次检测。结果暂留在内存；重新打开页面、恢复窗口或重新启用模块均不会自动开始检测。

| Proxy 接入方式 | 适用情况 | 行为 |
| --- | --- | --- |
| 系统代理（默认） | 外部客户端管理 Windows 系统代理 | 每次请求重新取得 Windows 代理解析器，遵循系统代理/PAC 和绕过规则；可能直连，不能仅凭成功判定代理生效 |
| HTTP / HTTPS / SOCKS5 | 明确知道客户端监听地址 | 示例：`http://127.0.0.1:7890`、`socks5://127.0.0.1:1080`。这些是格式示例，端口应以自己的客户端配置为准；连接失败不回退到 Direct |
| 系统路由 / TUN | 流量由系统路由或 TUN 接管 | 不使用应用层代理；本模式不会替用户开启 TUN |

显式代理地址不支持用户名/密码、业务路径、查询参数或片段。非空地址即使暂未选用也会校验；切回其他模式时可清空无效的旧地址。

**Direct 只绕过应用层代理，无法绕过系统 TUN。** 它显示 Bilibili 实际返回的数据，不把大陆结果写死。相同 IP 不自动判为失败；多个节点共用同一出口 IP 时，仅凭出口身份无法识别切换。

## 页面与结果

页面顺序为模块开关、单行开始/停止栏、直连/代理卡片、地图与 MTR 逐跳列表、代理服务连通性检测和可展开的设置。出口卡片常驻 IP、地区、ASN、状态和最后成功时间，来源、连接方式及结果时效说明放在“详情”。窄窗口中卡片及地图/列表上下排列。界面仅使用中英文资源，跟随 Kit 语言配置；数据源返回的组织和城市专名保持原文。

| 内容 | 数据来源 | 如何理解 |
| --- | --- | --- |
| Direct | [Bilibili zone](https://api.bilibili.com/x/web-interface/zone) 的 JSON：`data.addr/country/province/city/isp` | IP 及接口提供的地区；运营商文字不是 ASN |
| Proxy | [Cloudflare trace](https://1.1.1.1/cdn-cgi/trace) 的 `ip`、`loc` | 响应是逐行 `key=value` 文本，不是 JSON；`loc` 是地区代码，不提供城市或 ASN |
| 地图 | Natural Earth v5.1.2、1:110m 国家轮廓及发布的代表点 | 无需联网、地图账号或 Key；国家点是地区示意，不是设备精确位置 |
| ASN / 城市 | 本地 GeoLite2-ASN / GeoLite2-City 优先，缺失字段由 ipwho.is 在线补齐 | 显示查询中、查询失败、限额或无匹配等原因，未知时不会伪造结果 |
| 服务连通性 | 仅 Proxy：Claude/ChatGPT trace 检查点、Gemini/Google 网页 | 校验检查点内容并展示各自出口，区分网页响应/登录/验证，不证明完整聊天功能可用 |
| 本机路径 | 到当前 Proxy IPv4 地址的 ICMP TTL 探测 | 经过本机系统路由；HTTP/SOCKS 代理不承载这类探测，不是代理隧道内部路径 |

双通道独立更新。成功的出口 IP 显示绿色；失败时立即标红并显示连续失败次数，前两次保留上次成功信息，第三次将 IP、地区和 ASN 显示为 N/A，成功后自动恢复。不会用 Direct 结果冒充 Proxy。停止后的卡片和地图属于上次结果，应结合状态、时间和详情阅读。

服务检测只使用当前 **Proxy 配置**，不再发送 Direct 网站请求。四个固定目标每 **10 秒**检测一轮，分别是：

- Claude：[api.anthropic.com/cdn-cgi/trace](https://api.anthropic.com/cdn-cgi/trace)。
- ChatGPT：[chatgpt.com/cdn-cgi/trace](https://chatgpt.com/cdn-cgi/trace)。
- Gemini：[gemini.google.com](https://gemini.google.com/)。
- Google：[www.google.com](https://www.google.com/)。

Claude 和 ChatGPT 必须收到成功的 HTTP 响应，并解析出有效 `ip`、`loc` 才显示“检查点可达”；普通 HTML、错误或未读完的 trace 不算通过。两项分别显示自己的出口 IP/地区，域名分流时可与主 Proxy 卡片不同；不改变 MTR 目标。trace 不跟随跳转。Gemini 和 Google 允许最多 5 次跳转，区分页面响应、登录、浏览器验证和访问受限。

服务结果以指示灯、文字状态和耗时共同显示：正常响应 **≤75 ms 为绿色**，**>75 ms 为黄色**；登录、浏览器验证、限流或内容不完整也显示黄色“需注意”；连接、DNS、TLS、超时及其他 HTTP 错误显示红色。耗时指收到 HTTP 响应头的时间，不是 ICMP RTT。连续三次失败后实时耗时显示 N/A，并移除上次检查点身份，下一次成功自动恢复；登录或验证页保留已收到的 HTTP 证据，不作为连接失败累计。等待或停止时状态色为灰色，明暗主题使用各自的系统语义色。

收到 HTTP 响应后保留状态码和响应头耗时，正文超时或中断单独标记。正文最多读取前 64 KiB、等待 2 秒；401/403/429/5xx 不会被混同于 DNS/TLS/连接失败。没有密钥、模型列表或生成请求，不登录账号或执行网页 JavaScript，检查点可达不等于完整聊天功能可用。

用户提供的 [Gemini API 文档](https://ai.google.dev/api/all-methods#index)描述的是需要 API Key 的开发接口，不能代表 Gemini 网页或客户端可用性，因此继续检测 `gemini.google.com`，不增加模型请求。

## 离线地图与数据库

地图轮廓随业务库内嵌，模块关闭或尚未检测时也能绘制。直连使用方形标记，代理使用圆形标记；上次结果会降低不透明度。路径节点只有查到经纬度后才进入地图，相邻且已定位的节点以实线连接；跨未定位节点及本机出口/目标示意段用虚线连接，并在图例明确区分。地图按已知位置缩放，并将经度接缝移到节点分布的最大空白区：中国经美国到新加坡时自动以太平洋为中心，底图与连线连续显示。节点标注跳数，选择后显示最近 RTT 与丢包。宽窗口将 MTR 表格收紧为 440 DIP，剩余宽度优先分给地图；内容区不足 880 DIP 时表格排到地图下方。IP 使用固定列宽，地区列可换行，丢包率和 RTT 右对齐，减少列间与行间留白。列表常驻 IP、地区、ICMP 丢包率、最近 RTT 和平均 RTT，详情补充发送数、最快/最慢 RTT 与 ASN。采样原位更新行对象，保留滚动位置和选中项。Cloudflare trace 只提供出口身份，不包含 MTR 路径；路径由本机另行探测。

目标 ICMP RTT、逐跳指示灯、最近 RTT 与地图节点同样按 ≤75 ms 绿色、>75 ms 黄色、无响应红色显示。连续两次无响应期间保留并注明上次 RTT，第三次将该跳 IP 与最近 RTT 显示为 N/A；回复后恢复。平均 RTT 和丢包率继续累计，保留路径统计。仅 RTT 变化时更新地图节点颜色，不重建连线或重置列表滚动。

MaxMind.GeoIP2 **6.1.0**（间接依赖 MaxMind.Db **5.1.0**）负责读取本地数据库。插件不附带数据库，可手动从 GitHub 下载或更新 ASN 库；城市库仍由用户提供。数据库在检测开始时打开，更换文件后应用设置并重新开始。本地存在的字段优先保留；缺失、损坏、类型不匹配或未覆盖的字段可由 [ipwho.is](https://ipwhois.io/documentation) 补齐，不阻塞出口 IP 首次显示。坐标均为近似定位，不是 GPS。在线查询的 `lang` 和本地 MMDB 的地区名称跟随 Kit 中英文配置，保留省级地区与城市；中文直连卡片保留 Bilibili 返回的省市。出口观测的国家代码与 GeoIP 冲突时，不拼接异国城市或坐标，地图回退到有标注的国家代表点。

当前源已实测返回公共大陆示例地址 `114.114.114.114` 的“江苏省 · 南京”，本轮保留它作为在线补齐源。也可从用户指定的 [P3TERX/GeoLite.mmdb Releases](https://github.com/P3TERX/GeoLite.mmdb/releases) 获取 City 库，按发布资产 SHA-256 校验后通过“选择城市数据库”优先使用；Country 库只有国家信息，不能代替 City 的省市和坐标。本轮未增加 City/Country 自动下载器。

在线补齐默认开启，通过所选代理连接方式将待查询的公网 IP 发送给 ipwho.is；内网、回环和保留地址不外发。本次会话缓存最多 512 个地址，最多 256 次在线查询，两次查询至少间隔 1 秒；遇到 429 后停止本次会话的后续在线请求。查询失败也缓存，重新开始可重试。免费服务可能限流或不可用，关闭在线开关即可仅用本地数据；不会因此停止出口或 MTR 采样。

底图来源与处理说明见 [Data/NOTICE.md](NetMapLib/Data/NOTICE.md)，阅读器许可见[第三方声明](assets/THIRD-PARTY-NOTICES.txt)与 [Apache-2.0.txt](assets/Apache-2.0.txt)。GeoLite2 数据库遵循 MaxMind 的许可；GitHub ASN 下载来源是 P3TERX 社区镜像，不是 MaxMind 官方分发接口。

## ASN 数据库下载与更新

在“连接与地图设置 → ASN 数据库更新”中点击“下载 / 更新 ASN 库”。使用已应用的代理配置，下载期间停止检测，支持取消；离开页面、隐藏/最小化窗口或关闭模块也会取消下载。完成后手动点击“开始”。底图固定内置 Natural Earth v5.1.2，不检查或下载地图更新；城市库不提供下载入口。

数据来自 [P3TERX/GeoLite.mmdb](https://github.com/P3TERX/GeoLite.mmdb) 最新 GitHub Release 的 `GeoLite2-ASN.mmdb`。先取得 Release 的 `sha256:` digest，再下载至临时文件，逐字节计算 SHA-256，并验证文件大小及 MMDB 的 `GeoLite2-ASN` 类型；全部通过后才替换托管副本。缺少哈希、哈希不匹配、取消或失败均保留旧文件，不以下载后自行生成的哈希充当验证依据。哈希确认下载与 Release 资产一致，信任来源仍是该 GitHub 仓库。

托管副本位于 `%LOCALAPPDATA%\Kit\NetMap\Data\GeoLite2-ASN.mmdb`。自定义 ASN 路径非空时继续优先使用该文件，更新器不覆盖它；清空自定义路径并应用设置后，下一次检测使用托管副本。数据库内容不包含城市坐标，节点位置仍由本地城市库或在线查询提供。

更新仅在手动点击时联网，最多跟随 5 次 HTTPS 跳转且限制在 GitHub 下载域名；元数据最多 2 MiB，数据库最多 32 MiB，下载总期限 3 分钟。已安装文件与最新 Release 哈希相同则跳过重复下载。更新结果显示版本及完整 SHA-256。GeoLite2 数据由 MaxMind 提供，使用前可在来源仓库查阅其许可。

## 检测节奏与生命周期

- Direct、Proxy 分别串行执行，每次观测结束后等待 30 秒、5 秒再开始下一次；每次请求使用新的 HTTP handler。两次成功且一致的 Proxy 观测后才启动服务和路径诊断，新出口先显示。
- Proxy 变化或失败会取消旧诊断并清空其服务/路径结果；会话与出口代次检查阻止切换、停止、重启后的迟到回调覆盖当前状态。
- 身份与地理查询取消期限为 5 秒，正文最多 64 KiB；保留标准 TLS 校验，身份检测不跟随重定向。Windows 系统代理/PAC 解析含同步调用，不能承诺这一阶段在 5 秒内返回。
- 稳定出口立即开始首轮服务检测，此后按 10 秒周期执行四个并发 Proxy 请求，单请求总预算 10 秒，正文额外限制最多等待 2 秒；各项完成即回填，同一轮的增量和最终回调只计一次失败。轮次不重叠，超时错过的周期合并，不积压请求。支持响应解压和本次请求内的跳转 Cookie，读取有限正文识别浏览器验证。这个 10 秒周期只用于服务检测，Direct/Proxy 身份观测仍使用上述 30/5 秒间隔。
- MTR 持续采样至停止或出口变化，每轮最多 24 跳、每跳等待 800 ms、轮间隔 2 秒，累计各跳丢包率。中间路由器限速或不回复 ICMP 不等于业务丢包。IPv6 出口仍显示，但首版不做 IPv6 逐跳探测。
- 切换到其他插件页面或将设置窗口最小化到任务栏时，检测继续运行；返回 NetMap 或恢复窗口后复用同一会话并显示最新结果。Stop、关闭模块、关闭/隐藏设置窗口会停止检测，普通失焦不停；这些停止操作之后返回页面不会自动重启。
- 检测运行于 Settings 进程，无独立 Worker 或历史库。结果仅保留在当前进程的内存中；正常日志不记录公网 IP 或响应正文。

## 源码、配置与产物

模块固定 Key 为 **`NetMap`**。全局开关存于 `%LOCALAPPDATA%\Kit\settings.json` 的 `enabled.NetMap`；模块设置由 Settings 经现有 IPC 交给 Runner 保存至 `%LOCALAPPDATA%\Kit\NetMap\settings.json`，包含代理模式、地址、数据库路径和 `onlineLookup` 开关（默认 true）。内置地图不落盘、不在线更新；手动下载的 ASN 库保存在该模块目录下的 `Data/GeoLite2-ASN.mmdb`。

| 位置 | 职责 |
| --- | --- |
| [NetMapModuleInterface](NetMapModuleInterface) | 轻量 C++ 模块，导出 `kit_create()`；处理启用状态和配置保存，默认关闭 |
| [NetMapLib](NetMapLib) | 身份解析、HTTP 探测、会话隔离、服务比较、ICMP、本地 MMDB 和底图 |
| [NetMap.UnitTests](NetMap.UnitTests) | 解析、非法配置、取消、重启、切换及迟到结果等行为测试 |
| [NetMapPage.xaml](../../settings-ui/Settings.UI/SettingsXAML/Views/NetMapPage.xaml) / [NetMapViewModel.cs](../../settings-ui/Settings.UI/ViewModels/NetMapViewModel.cs) | 设置页、启停、资源绑定和生命周期 |
| [NetMapSettings.cs](../../settings-ui/Settings.UI.Library/NetMapSettings.cs) / [NetMapProperties.cs](../../settings-ui/Settings.UI.Library/NetMapProperties.cs) | 设置 DTO，注册到现有序列化与模块目录 |
| [页面测试](../../../tools/tests/NetMap) | 使用真实 WinUI 和合成探针验证布局与生命周期 |
| [assets](assets) | 保留用户原始 `logo.png`，提供裁剪 Logo、36×36 小图标和 400×266 模块配图 |

运行时图标位于 Settings UI 的 `Assets/Settings/Icons/NetMap.png`、`Assets/Settings/Modules/NetMap.png`、`Assets/Settings/NetMapLogo.png`。Runner、全局开关转换器、Catalog、侧栏、Dashboard、深链和 QuickAccess 已显式注册；没有专属快捷动作时打开设置页。

默认 x64 构建产物：

```text
x64/<Configuration>/
├── Kit.exe
├── Kit.NetMapModuleInterface.dll
└── WinUI3Apps/
    ├── Kit.Settings.exe
    ├── Kit.NetMapLib.dll
    ├── MaxMind.GeoIP2.dll
    ├── MaxMind.Db.dll
    ├── Assets/Settings/...
    └── modules/NetMap/
        ├── THIRD-PARTY-NOTICES.txt
        └── Apache-2.0.txt
```

业务在 Settings 进程中运行，不能只复制原生 DLL 当作独立插件安装。保留完整 Kit 运行目录及其依赖。

## 构建与测试

使用 Windows、PowerShell 7、VS 2026 与 .NET 10，从**仓库根目录**执行。完整 Release 构建与整理流程见[主 README](../../../README_zh.md#4-构建与发布)。单独迭代 NetMap 时依次构建以下项目，避免多个独立 MSBuild 同时写共享 WinMD/PDB：

```powershell
$netMapProjects = @(
    'src/modules/NetMap/NetMap.UnitTests',
    'src/modules/NetMap/NetMapModuleInterface',
    'src/runner',
    'src/settings-ui/Settings.UI',
    'src/settings-ui/QuickAccess.UI'
)
foreach ($netMapProject in $netMapProjects) {
    pwsh -NoProfile -ExecutionPolicy Bypass -File tools/build/build.ps1 -Platform x64 -Configuration Debug -Path $netMapProject /restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $netMapProject" }
}
```

直接构建 Runner 工程不会执行解决方案中的全部模块依赖，因此这里单独构建 NetMap 接口；QuickAccess 与 Settings 共用图标输出目录，也需保持其资源清单一致。测试时从 `x64/Debug/Kit.exe` 启动 Runner。

使用 VS 自带的 VSTest 运行核心测试：

```powershell
$netMapVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$netMapVsTest = & $netMapVsWhere -latest -products '*' -find 'Common7/IDE/CommonExtensions/Microsoft/TestWindow/vstest.console.exe' | Select-Object -First 1
if (-not $netMapVsTest) { throw 'VSTest not found' }
& $netMapVsTest 'x64/Debug/tests/NetMap/NetMap.UnitTests.dll' /Platform:x64 /Logger:trx /ResultsDirectory:TestResults/NetMap
if ($LASTEXITCODE -ne 0) { throw 'NetMap tests failed' }
```

实际 WinUI 生命周期和布局检查：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File tools/tests/NetMap/Invoke-NetMapPageSmoke.ps1 -Language zh-CN
if ($LASTEXITCODE -ne 0) { throw 'NetMap page smoke failed' }
pwsh -NoProfile -ExecutionPolicy Bypass -File tools/tests/NetMap/Invoke-NetMapPageSmoke.ps1 -Language en-US
if ($LASTEXITCODE -ne 0) { throw 'NetMap page smoke failed' }
```

页面测试要求 Kit 已关闭，使用合成出口和可取消的内存探针，不请求真实检测端点。脚本暂存并还原通用设置、窗口位置、语言及日志配置，在 `TestResults/NetMap/<语言>` 输出报告和明暗/窄窗口截图；截图使用纯色底衬代替 RenderTargetBitmap 无法捕获的 Mica。

可向 `Invoke-NetMapPageSmoke.ps1` 传入 `-LifecycleOnly`，仅验证启停、跨页面持续检测、结果回填与窗口生命周期，不运行布局和截图断言。

## 验证状态与反馈

截至 **2026-10-01**，核心测试 **103/103**，包含 75 ms 边界、三次失败与恢复、服务周期不重叠及增量结果去重、Google 网页请求、双 trace 内容校验、大陆省市保留、国家冲突处理、在线查询、网页正文中断后的 HTTP 证据保留、持续 MTR 及 ASN 更新的校验/取消用例。实际 GitHub ASN 下载、SHA-256 校验、本地查询及重复更新跳过均已验证。初版的较广设置/注册回归为 **88/92**，4 项失败对应既有 GPO/实验功能资产及 Runner 清理断言。最新构建、双语 WinUI 与设置测试结果见 [plan.md](plan.md)。

最新 Settings UI x64 Debug 构建及中英文实际 WinUI 检查通过。合成 IPv4 数据下检测栏高 **54 DIP**、出口卡片高 **166 DIP**，**1200×900** 窗口地图宽 **473 DIP**，四跳地图卡片底部中文为 **802 DIP**、英文为 **818 DIP**，均低于可用高度 **842 DIP**；长 IP/文本仍允许换行。中国→美国西岸→美国中部→新加坡合成路线在图内连续连接，±179° 相邻节点保持相邻，明暗主题下底图与节点对齐。24 跳列表选中第 19 跳后连续刷新 12 次，滚动和选中项保持不变，地区异步补齐也不跳动。状态灯、75/75.1 ms 边界、第三次失败显示 N/A 及恢复均通过断言。此前实际网络检查中 Claude/ChatGPT trace 均验证成功，Gemini/Google 网页响应 HTTP 200，四项耗时均超过 75 ms，正确显示黄色；本轮地图修订只使用合成探针验证。已检查明暗主题、720×1000 窄窗口及快速返回缓存页后的操作恢复。

尚未完成真实代理/PAC/TUN 组合、有效城市 MMDB 数据、完整网页访问矩阵及真实 ICMP 行为、干净 Release 构建，以及高对比度、150%/200% 缩放、480 DIP 最小窗口和 Narrator 的完整验证。反馈时请附构建配置、窗口大小与缩放、主题、代理接入模式、操作顺序及错误状态；说明是否开启 TUN。分享截图前可遮住公网 IP 和个人文件路径，无需提供凭据。

首版不包含 Google 地图、IPv6 traceroute、代理认证、历史趋势、后台检测或自动换节点。实施决策及更详细的验收边界保留在 [plan.md](plan.md)。
