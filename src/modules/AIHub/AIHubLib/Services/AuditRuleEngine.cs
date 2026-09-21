// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.AIHubLib.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kit.AIHubLib.Models;

/// <summary>
/// Rule-based security audit engine: turns raw Windows events into prioritized,
/// bilingual findings without invoking an AI kernel.
/// </summary>
/// <remarks>
/// Extracted from the AI Hub page view model, where it was ~345 lines of pure logic
/// with no UI coupling. Keeping it in the library makes it testable on its own and
/// lets a headless worker run audits without a Settings page.
/// </remarks>
public static class AuditRuleEngine
{
    private static readonly char[] LineSeparators = ['\r', '\n'];

    /// <summary>
    /// Analyzes events and returns findings ordered the same way the dashboard shows
    /// them (highest severity first, then most frequent).
    /// </summary>
    public static List<AuditIssueEnhanced> Analyze(IReadOnlyList<SecurityEvent> events)
    {
            var issues = new List<AuditIssueEnhanced>();
            var grouped = events.GroupBy(e => (e.LogName, e.EventId, e.ProviderName));

            int keySeq = 1;
            foreach (var g in grouped)
            {
                var first = g.First();
                int count = g.Count();
                int eventId = first.EventId;
                string provider = first.ProviderName ?? string.Empty;
                string log = first.LogName ?? "System";

                // Clean message excerpt
                string rawMsg = (first.Message ?? string.Empty).Trim();
                string cleanedMsg = CleanMessageSummary(rawMsg);

                // Windows Error Reporting events (1000/1002) put the useful detail in the
                // description rather than the provider: the provider is just "Application
                // Error" while the message names the failing application and faulting module.
                // Without this the finding read "Application Crash: Application Error" and
                // hid the actual culprit.
                string faultingApp = ExtractFaultField(rawMsg, "Faulting application name");
                string faultingModule = ExtractFaultField(rawMsg, "Faulting module name");
                string exceptionCode = ExtractFaultField(rawMsg, "Exception code");

                // Default baseline
                string severity = "Low";
                string category = string.Equals(log, "Setup", StringComparison.OrdinalIgnoreCase) ? "Configuration"
                    : string.Equals(log, "Application", StringComparison.OrdinalIgnoreCase) ? "Application"
                    : string.Equals(log, "ForwardedEvents", StringComparison.OrdinalIgnoreCase) ? "Network"
                    : "System";

                string title = $"{provider} (Event {eventId})";
                string titleZh = $"{provider} (事件 ID: {eventId})";
                string desc = !string.IsNullOrWhiteSpace(cleanedMsg) ? cleanedMsg : $"{provider} recorded event {eventId} in {log} channel.";
                string descZh = !string.IsNullOrWhiteSpace(cleanedMsg) ? cleanedMsg : $"在 {log} 日志中记录到来自 {provider} 的事件 {eventId}。";

                string rootCause = $"Operational status event {eventId} recorded {count} time(s) by {provider} under {log}.";
                string rootCauseZh = $"组件 {provider} 在系统日志 {log} 中记录了 {count} 次状态代码为 {eventId} 的事件。";
                string recommendation = "1. Open Reliability Monitor ('perfmon /rel') to inspect system stability around this timestamp.\n2. Review component status in services.msc.\n3. If routine operations are unaffected, continue regular monitoring.";
                string recommendationZh = "1. 按 Win+R 运行 'perfmon /rel' 打开可靠性监视器核查该时段系统稳定性记录。\n2. 在 services.msc 中检查相关服务或驱动状态。\n3. 若系统整体平稳且未影响日常使用，可保持常规关注。";

                // 1. Kernel & Power
                if (eventId == 41 || (eventId == 6008 && provider.Contains("EventLog", StringComparison.OrdinalIgnoreCase)))
                {
                    severity = "High";
                    category = "Stability";
                    title = eventId == 41 ? "Unexpected Kernel Shutdown" : "Unexpected System Shutdown";
                    titleZh = eventId == 41 ? "系统内核异常断电或未正常关机 (Kernel-Power 41)" : "系统上一次异常关机 (EventLog 6008)";
                    rootCause = "The system rebooted without cleanly shutting down first. Possible causes: abrupt power loss, hardware watchdog trip, overheating, or kernel bugcheck deadlock.";
                    rootCauseZh = "系统在未正常关机的情况下重新启动。可能原因包括供电突发中断、硬件看门狗超时、过热保护或系统死锁蓝屏。";
                    recommendation = "1. Inspect wall power, PSU cables, and UPS battery stability.\n2. Check for crash dump files under 'C:\\Windows\\Minidump'.\n3. Press Win+R, run 'mdsched.exe' (Windows Memory Diagnostic) to verify RAM integrity.\n4. Check CPU/GPU temperatures in BIOS/UEFI.";
                    recommendationZh = "1. 排查插座供电、电源连线及 UPS 后备电源稳定性。\n2. 检查 'C:\\Windows\\Minidump' 目录是否存在近期内核崩溃转储文件。\n3. 按 Win+R 运行 'mdsched.exe' 执行 Windows 内存诊断工具排查物理内存故障。\n4. 在 BIOS/UEFI 中查看 CPU 和主板供电温度，防范过热保护关机。";
                }
                else if (eventId == 1001 && (provider.Contains("BugCheck", StringComparison.OrdinalIgnoreCase) || provider.Contains("WER", StringComparison.OrdinalIgnoreCase)))
                {
                    // 2. Windows Error Reporting & Crash BugCheck
                    severity = "High";
                    category = "Stability";
                    title = "System BugCheck or WER Crash Report";
                    titleZh = "系统蓝屏崩溃或内核故障报告 (WER BugCheck 1001)";
                    rootCause = "Windows Error Reporting archived a fatal kernel crash stop code (BSOD) or critical application fault.";
                    rootCauseZh = "Windows 错误报告成功归档了致命内核崩溃停机码 (BSOD) 或关键进程崩溃转储。";
                    recommendation = "1. Use WinDbg or BlueScreenView to inspect the memory dump in 'C:\\Windows\\Minidump'.\n2. Identify the offending driver (.sys) and roll back or update via Device Manager ('devmgmt.msc').\n3. Run 'sfc /scannow' and 'DISM /Online /Cleanup-Image /RestoreHealth' in an elevated terminal.";
                    recommendationZh = "1. 使用 WinDbg 或 BlueScreenView 工具分析 'C:\\Windows\\Minidump' 中的转储文件调用栈。\n2. 定位引起故障的内核驱动 (.sys) 并在设备管理器 ('devmgmt.msc') 中回退或升级。\n3. 在管理员命令提示符运行 'sfc /scannow' 与 'DISM /Online /Cleanup-Image /RestoreHealth' 修复系统损坏组件。";
                }
                else if (eventId == 1000)
                {
                    // 3. Application Crash
                    severity = "Medium";
                    category = "Application";

                    // Name the crash target and the offending module when WER reported them.
                    string subject = !string.IsNullOrWhiteSpace(faultingApp) ? faultingApp : provider;
                    title = string.IsNullOrWhiteSpace(faultingModule)
                        ? $"Application Crash: {subject}"
                        : $"Application Crash: {subject} (faulting module {faultingModule})";
                    titleZh = string.IsNullOrWhiteSpace(faultingModule)
                        ? $"应用程序异常崩溃: {subject}"
                        : $"应用程序异常崩溃: {subject}（故障模块 {faultingModule}）";

                    rootCause = string.IsNullOrWhiteSpace(faultingModule)
                        ? "Application process crashed due to an unhandled exception or memory access violation."
                        : $"{subject} crashed inside {faultingModule}"
                          + (string.IsNullOrWhiteSpace(exceptionCode) ? "." : $", reporting exception {exceptionCode}.");

                    rootCauseZh = string.IsNullOrWhiteSpace(faultingModule)
                        ? "应用程序进程因未捕获的代码异常或内存访问违规崩溃退出。"
                        : $"{subject} 在 {faultingModule} 中发生崩溃"
                          + (string.IsNullOrWhiteSpace(exceptionCode) ? "。" : $"，异常代码 {exceptionCode}。");

                    // Driver-backed modules and audio processing objects need driver guidance,
                    // not generic "update the app" advice.
                    bool isSystemModule = faultingModule.Contains("APO", StringComparison.OrdinalIgnoreCase)
                        || faultingModule.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
                        || faultingApp.Contains("AUDIODG", StringComparison.OrdinalIgnoreCase);

                    recommendation = isSystemModule
                        ? "1. Update or roll back the audio driver and its effects (APO) package from the vendor, e.g. through Device Manager ('devmgmt.msc') under Sound, video and game controllers.\n2. If the crash started after a driver or vendor utility update, uninstall that package and retest.\n3. Run 'sfc /scannow' in an elevated CMD to verify system DLL integrity.\n4. Check the vendor's support site for a known issue with this module version."
                        : "1. Update the software to its latest release patch.\n2. Reinstall or repair Microsoft Visual C++ Redistributable runtime packages.\n3. Run 'sfc /scannow' in CMD to ensure system DLL integrity.\n4. Check for conflicts with anti-cheat software or third-party overlays.";

                    recommendationZh = isSystemModule
                        ? "1. 通过设备管理器 ('devmgmt.msc') 的“声音、视频和游戏控制器”更新或回退该音频驱动及其音效 (APO) 组件版本。\n2. 若崩溃始于某次驱动或厂商工具更新，卸载该组件后复测。\n3. 在管理员终端运行 'sfc /scannow' 校验系统动态链接库完整性。\n4. 前往厂商支持站点确认该模块版本是否存在已知问题。"
                        : "1. 检查并将该应用程序升级至官方最新补丁版本。\n2. 重新安装或修复 Microsoft Visual C++ Redistributable 常用运行库组件。\n3. 在管理员终端运行 'sfc /scannow' 确保系统底层动态链接库完整性。\n4. 排查后台防外挂软件或第三方屏幕覆层插件冲突。";
                }
                else if (eventId == 1002)
                {
                    // 4. Application Hang
                    severity = "Medium";
                    category = "Application";
                    title = $"Application Hang: {provider}";
                    titleZh = $"应用程序主线程冻结挂起: {provider}";
                    rootCause = "Application process stopped responding to Windows message queues due to thread deadlock or blocked I/O.";
                    rootCauseZh = "应用程序进程主线程死锁或遭遇磁盘/网络 I/O 阻塞，无法及时处理 Windows 消息循环。";
                    recommendation = "1. Open Task Manager to inspect CPU or disk saturation bottlenecks.\n2. Disable unneeded third-party plugins or extensions for this application.\n3. End unresponsive background tasks and restart the application.";
                    recommendationZh = "1. 打开任务管理器检查 CPU 或磁盘 I/O 是否达到饱和瓶颈。\n2. 禁用或卸载该软件安装的第三方非必要插件或扩展项。\n3. 结束无响应进程并排查杀毒软件实时扫描导致的文件锁定。";
                }
                else if (eventId is 7000 or 7001)
                {
                    // 5. Service Control Manager Failures
                    severity = "High";
                    category = "System";
                    title = $"Service Start Failure: {provider}";
                    titleZh = $"后台服务启动失败或依赖缺失: {provider}";
                    rootCause = "Windows Service Control Manager failed to start a service due to missing dependency services, invalid logon credentials, or missing binary path.";
                    rootCauseZh = "服务控制管理器无法启动指定服务，原因通常为依存服务未启动、服务登录账号凭据失效或可执行文件路径不存在。";
                    recommendation = "1. Press Win+R, run 'services.msc' and locate the service.\n2. Inspect the 'Dependencies' tab to ensure all required services are running.\n3. Verify logon account credentials and binary path in service properties.\n4. Set startup type to 'Automatic' or 'Manual' as appropriate.";
                    recommendationZh = "1. 按 Win+R 运行 'services.msc' 找到对应服务属性。\n2. 查看“依存关系”标签页，确认其依赖的所有上游系统服务均处于运行状态。\n3. 在“常规”与“登录”标签页中核对可执行文件路径与账户凭据。\n4. 确认服务启动类型配置为适合的“自动”或“手动”。";
                }
                else if (eventId == 7022)
                {
                    // 6. Service Hung on Starting
                    severity = "Medium";
                    category = "System";
                    title = $"Service Hung on Starting: {provider}";
                    titleZh = $"系统服务启动阶段挂起超时: {provider}";
                    rootCause = "The service hung during initialization and did not report running status within the timeout period.";
                    rootCauseZh = "服务在开机初始化阶段超时挂起，未能按时向服务控制管理器汇报就绪状态。";
                    recommendation = "1. Check network connectivity if the service relies on remote databases or directory services.\n2. In services.msc, try manually starting the service after full boot.\n3. Adjust ServicesPipeTimeout in registry if the server workload is heavy during startup.";
                    recommendationZh = "1. 若该服务依赖远程数据库或网络验证，请排查局域网连接与 DNS。\n2. 在系统完全启动后，打开 services.msc 尝试手动启动该服务。\n3. 在注册表 HKLM\\SYSTEM\\CurrentControlSet\\Control 调整 ServicesPipeTimeout 启动超时限制。";
                }
                else if (eventId is 7023 or 7024)
                {
                    // 7. Service Terminated with Error
                    severity = "Medium";
                    category = "System";
                    title = $"Service Terminated with Error: {provider}";
                    titleZh = $"系统服务异常终止并返回错误码: {provider}";
                    rootCause = "The service terminated with a service-specific error code or internal failure.";
                    rootCauseZh = "后台系统服务在运行过程中抛出服务专有错误代码并异常退出。";
                    recommendation = "1. Review specific exit error code inside event details.\n2. Open services.msc, switch to 'Recovery' tab, and configure 'Restart the Service' for subsequent failures.\n3. Verify service registry keys under 'HKLM\\SYSTEM\\CurrentControlSet\\Services'.";
                    recommendationZh = "1. 查看事件描述中附带的具体退出错误代码。\n2. 打开 services.msc，在服务属性“恢复”标签页中为后续失败配置“重新启动服务”。\n3. 核查注册表 'HKLM\\SYSTEM\\CurrentControlSet\\Services' 对应服务项是否受损。";
                }
                else if (eventId is 7031 or 7034)
                {
                    // 8. Service Crashed Unexpectedly
                    severity = "High";
                    category = "System";
                    title = $"Service Terminated Unexpectedly: {provider}";
                    titleZh = $"系统核心服务意外崩溃退出: {provider}";
                    rootCause = "The service process terminated unexpectedly without receiving a clean shutdown command.";
                    rootCauseZh = "后台服务进程意外崩溃或被外部系统机制异常终止。";
                    recommendation = "1. Check the Application event log for an Event 1000 at the exact same timestamp to identify the faulting DLL module.\n2. In services.msc, set recovery action to 'Restart the Service'.\n3. Update or reinstall the parent software package.";
                    recommendationZh = "1. 查看同一时刻 Application 日志中的 Event 1000 崩溃记录，定位报错的 DLL 动态库。\n2. 在 services.msc 服务属性“恢复”中设置“重新启动服务”。\n3. 更新或重新安装该服务所属的应用程序软件包。";
                }
                else if (eventId == 7026)
                {
                    // 9. Driver Load Failure
                    severity = "Medium";
                    category = "System";
                    title = "Driver Failed to Load on Boot";
                    titleZh = "引导或系统启动驱动程序加载失败 (SCM 7026)";
                    rootCause = "One or more boot-start or system-start drivers failed to load during Windows boot.";
                    rootCauseZh = "开机引导或系统启动型硬件驱动未能成功载入内存。";
                    recommendation = "1. Open Device Manager ('devmgmt.msc'), click 'View' -> 'Show hidden devices', and uninstall orphaned drivers.\n2. Run 'pnputil /enum-drivers' in CMD to inspect installed third-party drivers.\n3. Remove obsolete anti-cheat, virtual soundboard, or VPN filter drivers.";
                    recommendationZh = "1. 打开设备管理器 ('devmgmt.msc')，在“查看”菜单中勾选“显示隐藏的设备”，卸载失效硬件驱动。\n2. 在管理员终端运行 'pnputil /enum-drivers' 查看已安装的第三方驱动包。\n3. 清理已卸载软件残留的虚拟网卡驱动或反作弊驱动。";
                }
                else if (eventId == 7040)
                {
                    // 10. Service Start Type Changed
                    severity = "Low";
                    category = "Configuration";
                    title = $"Service Start Type Changed: {provider}";
                    titleZh = $"系统服务启动类型发生变更: {provider}";
                    rootCause = "The startup type of a service was modified (e.g., from Auto to Disabled).";
                    rootCauseZh = "指定系统服务的启动类型被管理员或安装程序修改（例如从自动更改为禁用）。";
                    recommendation = "1. Verify whether this change was performed intentionally by an administrator or optimization utility.\n2. In services.msc, restore the startup type if unexpected.";
                    recommendationZh = "1. 核实该服务的启动类型变更是否为系统优化工具或软件安装预期的行为。\n2. 若非预期，请在 services.msc 中将该服务恢复为默认启动类型。";
                }
                else if (eventId == 7045)
                {
                    severity = "Medium";
                    category = "Configuration";
                    title = $"New Service Installed: {provider}";
                    titleZh = $"系统中注册安装了新服务: {provider}";
                    rootCause = "A new service was added to the system service table.";
                    rootCauseZh = "检测到系统服务列表中注册并安装了新的后台服务。";
                    recommendation = "1. Verify the binary path and publisher digital signature of the newly registered service.\n2. Ensure it originates from trusted authorized software to prevent persistence mechanisms.";
                    recommendationZh = "1. 检查新安装服务的可执行文件路径与数字签名，确保其来自正规官方软件。\n2. 防范未知第三方程序利用服务注册实现持久化后门。";
                }
                else if (eventId == 10016)
                {
                    // 11. DCOM Local Activation
                    severity = "Low";
                    category = "Configuration";
                    title = "DCOM Local Activation Isolation";
                    titleZh = "DistributedCOM 激活权限隔离提示 (DCOM 10016)";
                    rootCause = "The application-specific permission settings do not grant Local Activation permission for the COM Server application CLSID/APPID.";
                    rootCauseZh = "特定应用程序的默认组件权限未显式授予当前账户对该 CLSID/APPID 的本地激活权限。";
                    recommendation = "1. This is standard benign Windows behavior; Microsoft officially recommends ignoring Event 10016.\n2. No remediation required unless a specific user application fails to launch.";
                    recommendationZh = "1. 这是 Windows 内部组件通信的标准安全隔离现象；微软官方技术支持明确建议安全忽略。\n2. 只要当前系统和软件运行平稳，无需进行任何注册表干预。";
                }
                else if (eventId == 10010)
                {
                    severity = "Medium";
                    category = "Configuration";
                    title = "DCOM Server Registration Timeout";
                    titleZh = "DCOM 服务器注册超时 (DCOM 10010)";
                    rootCause = "A DCOM server component did not register with DCOM within the required timeout period.";
                    rootCauseZh = "DCOM 服务器组件在规定的超时期限内未能完成与 DCOM 基础结构的注册。";
                    recommendation = "1. Check if the hosting application or background service is disabled.\n2. Restart the associated background service in services.msc.";
                    recommendationZh = "1. 检查托管该 DCOM 服务器的应用程序或服务是否被禁用。\n2. 在 services.msc 中重新启动对应的后台系统服务。";
                }
                else if (eventId == 219)
                {
                    // 12. Plug-and-Play Driver Warning
                    severity = "Medium";
                    category = "Configuration";
                    title = "PnP Driver Load Warning";
                    titleZh = "即插即用硬件驱动加载失败 (Kernel-PnP 219)";
                    rootCause = "A driver failed to load for a connected Plug-and-Play device during hardware initialization.";
                    rootCauseZh = "硬件设备在即插即用枚举识别过程中未能成功加载匹配的驱动程序。";
                    recommendation = "1. Open Device Manager ('devmgmt.msc') and check for devices with yellow exclamation marks.\n2. Right-click the affected device and select 'Update driver'.\n3. Download and install the latest motherboard chipset drivers from the manufacturer website.";
                    recommendationZh = "1. 按 Win+X 打开“设备管理器”('devmgmt.msc')，排查标有黄色感叹号的设备。\n2. 右键点击受影响的设备并选择“更新驱动程序”。\n3. 前往电脑或主板品牌官网下载安装最新的芯片组驱动。";
                }
                else if (eventId == 1530)
                {
                    // 13. User Profile Registry Hive Leak
                    severity = "Low";
                    category = "System";
                    title = "User Profile Registry Handle Leak";
                    titleZh = "用户注销时注册表句柄未释放 (User Profile 1530)";
                    rootCause = "Windows detected that the registry file (user hive) was still in use by background applications during logoff.";
                    rootCauseZh = "用户注销或关机时，后台软件或常驻服务仍占用注册表配置单元文件句柄未及时释放。";
                    recommendation = "1. Gracefully close background tray apps and torrent clients before shutdown.\n2. Update third-party security software and system utilities to their latest versions.";
                    recommendationZh = "1. 关机或注销前正常退出后台托盘程序与常驻工具。\n2. 检查并升级第三方防病毒软件和系统优化工具至最新版本。";
                }
                else if (eventId == 1014)
                {
                    // 14. DNS Resolution Failure
                    severity = "Medium";
                    category = "Network";
                    title = "DNS Name Resolution Timed Out";
                    titleZh = "DNS 域名解析响应超时 (DNS Client 1014)";
                    rootCause = "Name resolution for an internet address timed out after none of the configured DNS servers responded.";
                    rootCauseZh = "本地网络请求域名解析时超时，配置的 DNS 服务器均未在限定时间内返回解析结果。";
                    recommendation = "1. Open an elevated terminal and run 'ipconfig /flushdns' to clear local cache.\n2. Configure reliable public DNS servers (e.g. 223.5.5.5, 119.29.29.29, or 1.1.1.1) in network adapter properties.\n3. Restart your router to resolve gateway DNS latency.";
                    recommendationZh = "1. 在管理员终端运行 'ipconfig /flushdns' 刷新本地 DNS 缓存。\n2. 在网络适配器属性中配置高可用公共 DNS（如 223.5.5.5、119.29.29.29 或 1.1.1.1）。\n3. 重启路由器或光猫排查网关 DNS 转发死锁。";
                }
                else if (eventId is 1008 or 1020 or 2001 or 2003)
                {
                    // 15. Performance Counters (Perflib)
                    severity = "Low";
                    category = "System";
                    title = $"Performance Counter DLL Warning: {provider}";
                    titleZh = $"性能计数器动态库错误 (Perflib {eventId})";
                    rootCause = "The Open or Collect procedure for a performance counter service DLL failed or returned an error.";
                    rootCauseZh = "性能计数器服务打开或采集指定 DLL 动态库时失败或返回错误代码。";
                    recommendation = "1. Open an elevated Command Prompt and run 'lodctr /r' to rebuild all performance counter registry strings.\n2. Run 'winmgmt /resyncperf' to resynchronize performance counters with WMI.";
                    recommendationZh = "1. 在管理员命令提示符中运行 'lodctr /r' 重新构建系统性能计数器注册表设置。\n2. 运行 'winmgmt /resyncperf' 重新同步性能计数器与 WMI 存储库。";
                }
                else if (eventId == 10 && provider.Contains("WMI", StringComparison.OrdinalIgnoreCase))
                {
                    // 16. WMI Error
                    severity = "Medium";
                    category = "System";
                    title = "WMI Filter Query Error";
                    titleZh = "WMI 事件筛选器查询错误 (WMI 10)";
                    rootCause = "WMI Event filter query failed to activate in namespace due to syntax or query incompatibility.";
                    rootCauseZh = "WMI 事件筛选器查询无法在命名空间中激活，通常由于查询语法或权限不兼容。";
                    recommendation = "1. Run 'winmgmt /verifyrepository' in an elevated CMD to check repository integrity.\n2. If inconsistency is detected, run 'winmgmt /salvagerepository' to repair.";
                    recommendationZh = "1. 在管理员 CMD 中运行 'winmgmt /verifyrepository' 校验 WMI 存储库完整性。\n2. 若提示存在不一致，运行 'winmgmt /salvagerepository' 执行安全修复。";
                }
                else if (eventId is 131 or 200 or 201 or 202)
                {
                    // 17. DeviceSetupManager
                    severity = "Low";
                    category = "Configuration";
                    title = "Device Setup Metadata Connection Warning";
                    titleZh = "设备安装元数据服务器连接超时 (DeviceSetupManager)";
                    rootCause = "Device Setup Manager failed to connect to Windows Update servers to retrieve device metadata packages.";
                    rootCauseZh = "设备安装管理器无法连接到微软 Windows Update 服务器检索设备元数据包（通常由于网络离线或防火墙阻断）。";
                    recommendation = "1. Benign and safe to ignore for already-functioning hardware peripherals.\n2. Check general internet connectivity if newly plugged devices lack custom icons.";
                    recommendationZh = "1. 对于已正常工作的硬件外设，此提示完全无害，可安全忽略。\n2. 若新接入的外设缺少设备图标，检查外网连通性或稍后重试。";
                }
                else if (eventId is 16384 or 16394)
                {
                    // 18. Volume Shadow Copy (VSS)
                    severity = "Medium";
                    category = "System";
                    title = "Volume Shadow Copy Service Error";
                    titleZh = "卷影复制服务快照异常 (VSS)";
                    rootCause = "Volume Shadow Copy Service encountered an error during shadow copy creation or backup snapshot synchronization.";
                    rootCauseZh = "卷影复制服务 (VSS) 在创建系统还原点或进行备份快照同步时遇到错误。";
                    recommendation = "1. Run 'vssadmin list writers' in an elevated CMD to ensure all VSS writers report 'Stable'.\n2. Ensure sufficient free disk space on the system drive (at least 15% recommended).\n3. Restart the Volume Shadow Copy service in services.msc.";
                    recommendationZh = "1. 在管理员 CMD 运行 'vssadmin list writers' 检查所有 VSS 写入程序状态是否稳定 (State: [1] Stable)。\n2. 确保系统盘拥有充足的剩余可用空间（建议保留至少 15%）。\n3. 在 services.msc 中重新启动 'Volume Shadow Copy' 系统服务。";
                }
                else if (eventId is 13 or 15 or 16 or 55)
                {
                    // 19. Disk & File System (NTFS / Volume)
                    severity = "High";
                    category = "Reliability";
                    title = $"Disk File System Error: {provider}";
                    titleZh = $"磁盘文件系统或坏道异常: {provider}";
                    rootCause = "NTFS or disk subsystem detected data structure corruption or unreadable disk sectors.";
                    rootCauseZh = "NTFS 文件系统或磁盘子系统检测到数据结构受损或扇区读取异常。";
                    recommendation = "1. Immediately back up critical files to external storage.\n2. Open an elevated Command Prompt, run 'chkdsk C: /f /r', and schedule scan on next reboot.\n3. Use CrystalDiskInfo to inspect drive S.M.A.R.T. health and bad sectors.";
                    recommendationZh = "1. 立即备份重要个人数据与工作文档至外部存储设备。\n2. 在管理员命令行运行 'chkdsk C: /f /r'，并确认在下一次重启时执行全盘扇区扫描与修复。\n3. 使用 CrystalDiskInfo 查看硬盘 S.M.A.R.T. 健康状态排查硬件物理坏道。";
                }
                else if (eventId is 10000 or 10001 or 8000)
                {
                    // 20. WLAN Connection Failures
                    severity = "Medium";
                    category = "Network";
                    title = "Wireless Network Connection Interruption";
                    titleZh = "无线网络握手协商中断 (WLAN-AutoConfig)";
                    rootCause = "Wireless network adapter disconnected or failed to complete 802.11 authentication with the access point.";
                    rootCauseZh = "无线网卡意外断开连接或与 Wi-Fi 路由器访问点握手协商认证失败。";
                    recommendation = "1. Update Wi-Fi adapter drivers from the manufacturer website.\n2. Forget the Wi-Fi network in Windows Settings and reconnect.\n3. Run 'netsh winsock reset' in an elevated terminal and reboot.";
                    recommendationZh = "1. 前往网卡芯片厂商官网下载更新无线网卡驱动。\n2. 在 Windows 设置中“忘记此网络”并重新输入密码连接。\n3. 在管理员终端运行 'netsh winsock reset' 重置网络套接字并重启电脑。";
                }
                else if (first.Level <= 2)
                {
                    // 21. General Critical / Error Level
                    severity = "High";
                    category = "Reliability";
                    title = $"Critical Error: {provider} ({eventId})";
                    titleZh = $"{provider} 严重错误与异常 (事件 ID: {eventId})";
                    rootCause = $"Critical or error-level event recorded {count} time(s). Component '{provider}' under '{log}' encountered an unexpected failure.";
                    rootCauseZh = $"在“{log}”通道记录到 {count} 次严重或错误级别事件。组件“{provider}”在执行过程中遇到未处理异常。";
                    recommendation = $"1. Press Win+R and run 'perfmon /rel' to inspect system stability history around this event.\n2. Review '{provider}' service dependencies in services.msc.\n3. Run 'sfc /scannow' in an elevated terminal to verify system integrity.";
                    recommendationZh = $"1. 按 Win+R 运行 'perfmon /rel' 打开可靠性监视器核查该时刻前后的系统故障记录。\n2. 在 services.msc 中检查“{provider}”关联服务的运行状态与依赖项。\n3. 在管理员终端运行 'sfc /scannow' 扫描修复受损的 Windows 系统核心文件。";
                }
                else if (first.Level == 3)
                {
                    // 22. General Warning Level
                    severity = "Medium";
                    category = "Configuration";
                    title = $"Warning: {provider} ({eventId})";
                    titleZh = $"{provider} 运行警告 (事件 ID: {eventId})";
                    rootCause = $"Component '{provider}' reported operational warning or configuration delay (code {eventId}) {count} time(s).";
                    rootCauseZh = $"组件“{provider}”在“{log}”日志中报告了 {count} 次运行警告或配置响应延迟（状态码: {eventId}）。";
                    recommendation = $"1. Check if '{provider}' component functions properly in daily tasks.\n2. If operations are stable, monitor for repeated occurrences.\n3. Review Windows Update history for pending driver or cumulative updates.";
                    recommendationZh = $"1. 确认“{provider}”相关功能在日常使用中是否表现正常。\n2. 若系统整体运行平稳，通常属于非阻塞性运行警告，可保持常规观察。\n3. 检查 Windows 更新历史记录，安装待处理的驱动补丁或累积更新。";
                }

                // Windows event level is a floor, not a hint: a Critical (1) or Error (2)
                // event must never be reported below the tier its own level implies just
                // because a rule happened to classify that event ID as Medium or Low. The
                // rules add context and guidance; the level states how serious Windows
                // considered it.
                if (first.Level == 1 && !string.Equals(severity, "High", StringComparison.OrdinalIgnoreCase))
                {
                    severity = "High";
                    category = "Stability";
                }
                else if (first.Level == 2 && string.Equals(severity, "Low", StringComparison.OrdinalIgnoreCase))
                {
                    severity = "Medium";
                }

                issues.Add(new AuditIssueEnhanced
                {
                    Key = $"issue-{keySeq++:D4}",

                    // LogName is required by the page: the channel filter and the per-channel
                    // counters match on it. Omitting it made every rule-based finding
                    // unreachable through the source filter and pinned all channel counts at
                    // zero, so a scan that had found issues displayed none of them.
                    LogName = log,

                    EventRef = eventId.ToString(CultureInfo.InvariantCulture),
                    EventId = eventId.ToString(CultureInfo.InvariantCulture),
                    EventTimestamp = first.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
                    Title = title,
                    TitleZh = titleZh,
                    Description = desc,
                    DescriptionZh = descZh,
                    Severity = severity,
                    Category = category,
                    Affected = provider,
                    RootCause = rootCause,
                    RootCauseZh = rootCauseZh,
                    Recommendation = recommendation,
                    RecommendationZh = recommendationZh,
                    Occurrences = count,
                });
            }

            return issues.OrderByDescending(i => i.IsHigh).ThenByDescending(i => i.IsMedium).ThenByDescending(i => i.Occurrences).ToList();
    }

    /// <summary>
    /// Reads a "Name: value" line out of a Windows Error Reporting description.
    /// </summary>
    /// <remarks>
    /// The WER events carry their detail as free text, so the value is read from the line
    /// rather than a structured field. Returns an empty string when the field is absent.
    /// </remarks>
    private static string ExtractFaultField(string raw, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        foreach (string line in raw.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith(fieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int separator = trimmed.IndexOf(':');
            if (separator < 0 || separator + 1 >= trimmed.Length)
            {
                continue;
            }

            string value = trimmed[(separator + 1)..].Trim();

            // Keep the filename only for paths; the directory adds no diagnostic value.
            int lastSlash = value.LastIndexOf('\\');
            if (lastSlash >= 0 && lastSlash + 1 < value.Length)
            {
                value = value[(lastSlash + 1)..];
            }

            return value.Length > 128 ? value[..128] : value;
        }

        return string.Empty;
    }

    private static string CleanMessageSummary(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var lines = raw.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        string firstLine = lines[0];
        if (firstLine.Length > 280)
        {
            firstLine = firstLine[..280] + "...";
        }

        return firstLine;
    }
}
