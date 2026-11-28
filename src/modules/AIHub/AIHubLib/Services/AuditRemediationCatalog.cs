using System;
using System.Globalization;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

/// <summary>Local diagnostic guides keyed by both provider and event, also available to older saved findings.</summary>
public static class AuditRemediationCatalog
{
    public static AuditRemediationPlan Resolve(AuditIssue finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.RemediationPlan?.HasContent == true) return finding.RemediationPlan;
        string provider = (string.IsNullOrWhiteSpace(finding.Source) ? finding.Affected : finding.Source) ?? string.Empty;
        bool Provider(string name) => provider.Contains(name, StringComparison.OrdinalIgnoreCase);
        bool Mentions(string value) => (finding.Title?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false)
            || (finding.Description?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false);
        int.TryParse(finding.EventId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id);

        if ((id == 41 && Provider("Kernel-Power")) || (id == 6008 && Provider("EventLog")))
        {
            return Plan(
                ("Open Reliability Monitor (Win+R → perfmon /rel) at the recorded shutdown time; compare Windows failures with any crash dump listed in the event.", "按 Win+R 输入 perfmon /rel，定位本条异常关机时间，查看 Windows 故障记录及事件中记载的转储文件。",
                 "A bugcheck code or matching dump supports a crash investigation. Event 41 alone only records an unclean restart; it does not prove a faulty PSU or RAM.", "若有对应蓝屏代码或转储，进入崩溃排查；只有事件 41 仅能确认非正常重启，不能证明电源或内存损坏。"),
                ("If a matching dump identifies a driver and the issue began after its update, record the version and use Device Manager → device → Properties → Driver → Roll Back Driver. If no dump exists, check whether power loss or a forced shutdown occurred first.", "若对应转储指向具体驱动且问题始于该驱动更新，记录版本后进入设备管理器 → 对应设备 → 属性 → 驱动程序 → 回退驱动程序；若无转储，先核对断电或长按电源关机的事实。"),
                ("Save work before any restart. Keep the current driver installer so the change can be reversed; do not change BIOS or run disruptive hardware tests based only on this event.", "重启前保存工作，保留原驱动安装包以便恢复；不要仅凭该事件修改 BIOS 或立即进行会中断工作的硬件测试。"),
                ("Repeat the workload that preceded the restart. Check for another unexpected restart and a new matching event/dump; retain the timestamp and stop code if it recurs.", "重现之前触发重启的工作负载，检查是否再次异常重启及产生新事件/转储；复发时保留时间和停止代码。"));
        }

        if (id == 1000 && Provider("Application Error") && (Mentions("AUDIODG.EXE") || Mentions("NahimicAPO")))
        {
            return Plan(
                ("Open Reliability Monitor (perfmon /rel) and confirm the recorded application is AUDIODG.EXE or its faulting module is an audio effects component. Record the module and driver versions; reproduce the same playback or microphone scenario with Settings → System → Sound → affected device → Audio enhancements set to Off.",
                 "打开可靠性监视器（perfmon /rel），确认故障应用为 AUDIODG.EXE 或故障模块属于音效组件，记录模块和驱动版本；在设置 → 系统 → 声音 → 对应设备中临时关闭“音频增强”，复现相同播放或麦克风场景。",
                 "If the crash stops only with enhancements disabled, that narrows the investigation to the effects package. A module name alone does not prove a faulty audio driver.",
                 "仅关闭音频增强后不再复发，可将范围缩小到音效组件；单凭模块名称不能证明音频驱动损坏。"),
                ("If the comparison isolates an effects package, leave enhancements off temporarily and obtain a matching vendor fix. If failures began after a driver update and still reproduce without effects, record its version and use Device Manager (Win+R → devmgmt.msc) → the affected audio device → Driver → Roll Back Driver when available.",
                 "若对照测试定位到音效组件，暂时关闭增强并查找厂商对应修复；若故障始于驱动更新且关闭增强后仍复发，记录版本后打开设备管理器（Win+R → devmgmt.msc）→ 对应音频设备 → 驱动程序，在可用时回退驱动。"),
                ("Changing the driver can interrupt calls and audio playback. Record the original enhancement setting and retain the driver installer; restore the setting or prior driver if the test makes audio worse. Do not remove shared runtimes based on this event.",
                 "驱动变更会中断通话和音频播放；记录原音效开关并保留驱动安装包，测试造成退化时恢复原设置或驱动。不要仅凭该事件卸载共享运行库。"),
                ("Repeat the same playback/recording task on the same device and check sound quality plus new AUDIODG/application-error events. Compare both settings under the same workload before attributing the cause.",
                 "在同一设备重复原播放/录音任务，检查音质和新增 AUDIODG/应用错误记录；在相同工作负载下对照两种设置后再归因。"));
        }

        if (id is 1000 or 1002 && (Provider("Application Error") || Provider("Application Hang")))
        {
            return Plan(
                ("Open Reliability Monitor → the application's failure → View technical details. Record the application version, faulting module and exception code; compare with the action that triggered the failure.", "打开可靠性监视器 → 对应应用故障 → 查看技术详细信息，记录应用版本、故障模块及异常代码，并核对触发故障的操作。",
                 "Repeated failures of the same application/module identify a reproducible target. A DLL name alone does not establish that Windows or a runtime package is corrupt.", "同一应用/模块反复失败可确定复现对象；仅有 DLL 名称不能证明 Windows 或运行库损坏。"),
                ("Reproduce once with that application's optional extensions disabled. If this isolates one extension, update or disable that extension; otherwise use the application's own repair option or a vendor fix matching the recorded version. Change one component at a time.", "临时关闭该应用的可选扩展后复现一次；若定位到某个扩展，只更新或停用该扩展；否则使用应用自身修复入口或针对已记录版本的厂商补丁，每次只改一个组件。"),
                ("Save open documents and export application settings before repair. Re-enable extensions one by one to undo the isolation; avoid resetting user data or removing shared runtimes without confirming a dependency failure.", "修复前保存文档并导出应用配置；可逐一重新启用扩展恢复原状。未证实依赖故障前，不重置用户数据或卸载共享运行库。"),
                ("Repeat the original action several times using the same data. Confirm the application completes normally and no new matching crash/hang entry appears during those attempts.", "用相同数据多次重复原操作，确认应用正常完成，并核对这些尝试期间没有新的同类崩溃/挂起记录。"));
        }

        if (id == 1001 && (Provider("BugCheck") || Provider("WER-SystemErrorReporting")))
        {
            return Plan(
                ("Open this event's General/Details tabs and record the stop code and dump path. Open that existing dump in WinDbg and compare its failing stack with recent driver or hardware changes.", "在该事件的“常规/详细信息”中记录停止代码和转储路径，用 WinDbg 打开实际存在的对应转储，比较故障栈与近期驱动/硬件变动。",
                 "A matching dump can narrow the cause. A module mentioned once is a lead, not proof that it caused the crash.", "对应转储可缩小原因范围；单次出现的模块只是线索，不足以确认它就是根因。"),
                ("Only after the dump and reproduction implicate a specific recent driver change, roll back that driver in Device Manager or install the vendor's matching fix. If the evidence is inconclusive, retain the dump and seek analysis before changing the system.", "仅在转储与复现共同指向具体近期驱动变动时，回退该驱动或安装厂商对应修复；证据不足时保留转储并进一步分析，再决定是否修改系统。"),
                ("A driver change may require a restart. Save work, retain the previous installer and record the original version; do not delete the dump before verification.", "驱动变更可能需要重启；保存工作、保留原安装包并记录原版本，验证前不要删除转储。"),
                ("Repeat the original workload and compare any new stop code and dump. A quiet idle period alone is not evidence that the original failure is fixed.", "重现原工作负载并比较新出现的停止代码与转储；仅空闲一段时间无报错，不能证明原故障已修复。"));
        }

        if (Provider("Service Control Manager") && id is 7000 or 7001 or 7022 or 7023 or 7024 or 7031 or 7034 or 7026)
        {
            return Plan(
                ("Open this event and note the exact service/driver and error code. In Services (Win+R → services.msc), open that service's Properties → Dependencies and Log On; check dependency status without changing it.", "打开本条事件，记录具体服务/驱动名称和错误码；在 Win+R → services.msc 中打开该服务的属性，查看“依存关系”和“登录”，先确认依赖状态。",
                 "A stopped required dependency, missing executable or account-logon error requires a different remedy. A one-off stop during shutdown is not a startup failure requiring repair.", "必要依赖未启动、程序文件缺失和账户登录失败需要不同处理；关机期间偶发的服务停止，不等于需要修复的启动故障。"),
                ("For a confirmed missing application component, use that application's Repair option. For a dependency or sign-in error, correct only the named dependency or configured service account with its owner. Start the affected service once after resolving the specific error.", "若确认所属应用组件缺失，使用该应用的“修复”；若为依赖或登录错误，与管理者确认后仅修正对应依赖或服务账户。解决具体错误后再启动该服务一次。"),
                ("Restarting a service may interrupt its clients. Record its startup type and account first; restore those values if changed. Do not make every service Automatic or grant broader privileges.", "重启服务可能中断使用它的程序；先记录原启动类型和账户以便恢复，不要把全部服务改为自动或扩大权限。"),
                ("Exercise the feature that uses the service, then verify its expected state in Services and check for new events with the same service name and error code.", "实际使用依赖该服务的功能，再确认服务处于预期状态，检查是否新增相同服务名和错误码的事件。"));
        }

        if (Provider("Service Control Manager") && id is 7040 or 7045)
        {
            return Plan(
                ("Read the service name, executable path, account and startup type in the event. Compare the timestamp with a software install/update you initiated; inspect the executable's publisher in Properties → Digital Signatures.", "读取事件中的服务名、程序路径、账户和启动类型，核对同一时间是否有你主动安装/更新的软件，并在程序属性 → 数字签名中查看发布者。",
                 "An expected signed installer can explain the change. A missing signature alone is not proof of malware; an unexplained path/account needs investigation.", "预期的已签名安装程序可解释变更；缺少签名本身不是恶意证据，但无法解释的路径或账户需要进一步核实。"),
                ("If the change matches an intended install, leave it as configured. If it is unexpected, run Windows Security → Virus & threat protection → Scan options and preserve the event for review before deciding whether to remove the owning application.", "若与预期安装一致，保留当前配置；若无法解释，打开 Windows 安全中心 → 病毒和威胁防护 → 扫描选项进行检查，保留事件，确认所属程序后再决定是否卸载。"),
                ("Deleting a service can break its application and loses configuration. Prefer the application's supported uninstaller after attribution; keep its installer and settings backup for recovery.", "直接删除服务可能破坏应用并丢失配置；确认归属后优先用应用支持的卸载方式，并保留安装包和配置备份。"),
                ("Confirm the expected application still works and compare the service's path/account with the intended configuration. Review any subsequent unexpected installation or startup-type changes.", "确认预期应用仍正常工作，核对服务路径/账户与预期配置一致，并检查后续是否再发生无法解释的安装或启动类型变更。"));
        }

        if (id == 10016 && Provider("DistributedCOM"))
        {
            return Plan(
                ("Compare this DistributedCOM event's timestamp with a real application failure in Reliability Monitor. Check whether the named application can be opened and its affected feature used normally.", "将本条 DistributedCOM 事件时间与可靠性监视器中的实际应用故障对照，并实际打开对应应用、使用受影响功能。",
                 "If the feature works and there is no matching failure, this permission event does not justify a repair. Recurrence count by itself does not prove user impact.", "若功能正常且没有对应故障，该权限事件不足以支持修复；重复次数本身不能证明用户功能受影响。"),
                ("If no functional problem reproduces, no change is needed. If a matching application failure is reproducible, repair/update that application using its supported options and investigate its own error details.", "没有可复现功能问题时无需修改；若有同期可复现的应用故障，使用该应用支持的修复/更新入口，并分析它自身的错误详情。"),
                ("Do not take ownership of registry keys or relax DCOM launch permissions to suppress this event. Application repair may require a restart; save work and preserve its settings.", "不要为消除该事件而取得注册表所有权或放宽 DCOM 启动权限；应用修复可能要求重启，先保存工作和配置。"),
                ("Validate the original application scenario. Continued 10016 logging without a matching failure is not a failed repair and does not require escalating permissions.", "验证原应用场景；若功能正常，10016 继续记录不等于修复失败，也不需要提升权限。"));
        }

        if (id == 1014 && Provider("DNS"))
        {
            return Plan(
                ("Read the affected hostname in this event. During the same network/VPN/proxy configuration, open Command Prompt and use nslookup with that exact hostname; inspect the DNS servers in Settings → Network & internet → active adapter.", "读取事件中的域名；保持相同网络/VPN/代理配置，在命令提示符用 nslookup 加该真实域名检查解析，并在设置 → 网络和 Internet → 当前适配器中查看 DNS 服务器。",
                 "Distinguish one hostname failing from all lookups failing. A private/corporate hostname may require the configured VPN or split DNS; public DNS is not an equivalent replacement.", "区分仅单个域名失败还是所有解析失败；内网/公司域名可能依赖 VPN 或分流 DNS，公共 DNS 不能等价替代。"),
                ("If only the expected VPN/private DNS path fails, restore that connection or its approved DNS configuration. If all lookups fail, confirm adapter connectivity and the configured DNS server with the network owner before changing one setting.", "若仅预期 VPN/内网 DNS 路径失败，恢复该连接或经过确认的 DNS 配置；若全部失败，先确认适配器连接和 DNS 服务器，再与网络管理者核实单项变更。"),
                ("Record original automatic/manual DNS values before changing them. A DNS change can break internal services or bypass VPN routing; restore the recorded values if access regresses.", "修改前记录原自动/手动 DNS 值；变更可能影响内网服务或绕过 VPN 解析，访问变差时恢复原值。"),
                ("Resolve the same hostname repeatedly and retry the original application on the same connection. Compare both lookup results and new 1014 events; a browser's cached page is not sufficient verification.", "在同一连接上多次解析原域名并重试原应用，同时检查解析结果和新增 1014 事件；仅缓存网页能打开不能作为验证。"));
        }

        if (id == 219 && Provider("Kernel-PnP"))
        {
            return Plan(
                ("Find the device instance path in the event. In Device Manager → device Properties → Details → Device instance path, match that device and read its General status and Driver version.", "在事件中找到设备实例路径，在设备管理器 → 设备属性 → 详细信息 → 设备实例路径中匹配该设备，读取“常规”状态和驱动版本。",
                 "An exact instance match plus a current device error or failed function supports a driver issue. A boot-time warning followed by normal operation may need no change.", "实例路径匹配且当前存在设备错误或功能失效，才支持驱动排查；仅启动时警告、随后正常的设备可能无需修改。"),
                ("If the matched device is malfunctioning and this began with a driver update, use its Driver tab to roll back. Otherwise use only that device manufacturer's applicable driver, then retest the failing function.", "若匹配设备确实失效且始于驱动更新，在“驱动程序”页回退；否则只采用该设备厂商适用的驱动，并复测失效功能。"),
                ("Save the original driver version and installer. Network/input-device changes can interrupt access, so arrange an alternative before changing that device.", "记录原驱动版本并保留安装包；网络或输入设备的驱动变更可能中断操作，先准备替代访问方式。"),
                ("Reboot only if requested, exercise the device, and check both Device Manager status and new events for the same instance path.", "仅在要求时重启，实际使用该设备，并核对设备状态及相同实例路径的新事件。"));
        }

        if ((Provider("Disk") || Provider("Ntfs") || Provider("storahci") || Provider("stornvme")) && id is 7 or 51 or 55 or 129 or 153)
        {
            return Plan(
                ("Read the disk/volume identity and error from the event, then match it in Disk Management. Check the drive's health report using the manufacturer's tool and look for matching errors during the affected workload.", "读取事件中的磁盘/卷标识及错误，在磁盘管理中匹配设备；用厂商工具检查健康信息，并核对受影响工作负载期间是否出现相同错误。",
                 "Repeated I/O or filesystem errors on the same device need attention. A single timeout does not distinguish a disk fault from cabling, controller, driver or transient load.", "同一设备重复 I/O 或文件系统错误需要关注；单次超时不能区分磁盘、连线、控制器、驱动或瞬时负载。"),
                ("If errors recur, back up irreplaceable data before repair. For confirmed filesystem corruption, use the affected drive's Properties → Tools → Error checking after backup; for hardware-health failures, arrange replacement instead of repeated writes.", "若错误复发，先备份不可替代数据；确认文件系统损坏后，再用该驱动器属性 → 工具 → 错误检查；若硬件健康检查失败，安排更换，不要反复写入尝试修复。"),
                ("Filesystem repairs can modify metadata and require downtime. Keep a verified backup and stop stressing a failing drive; do not format or initialize it to remove the warning.", "文件系统修复会修改元数据并可能要求停机；保留已验证备份，停止压测疑似故障盘，不要为消除警告而格式化或初始化。"),
                ("After the chosen repair, repeat a modest version of the original workload and check the same device's health and error log. Continued errors require hardware/controller investigation.", "处理后以适度负载复测原场景，检查同一设备健康状态和错误日志；持续报错时继续排查硬件/控制器。"));
        }

        if (string.Equals(finding.LogName, "Security", StringComparison.OrdinalIgnoreCase) && id is 4625 or 4740)
        {
            return Plan(
                ("In Event Viewer → Windows Logs → Security, inspect this event's account, logon type, source address/workstation and status/substatus. Compare its time with your own sign-ins or a known scheduled task.", "在事件查看器 → Windows 日志 → 安全中查看该事件的账户、登录类型、源地址/工作站和状态/子状态，并与自己的登录或已知计划任务时间对照。",
                 "An outdated saved credential, unavailable domain controller and an unknown remote source need different responses. A failed sign-in alone does not establish account compromise.", "过期保存凭据、域控制器不可用和未知远程来源需要不同处理；一次登录失败不能证明账户已泄露。"),
                ("If a known task or service is retrying an old password, update only its saved credential with the account owner. If the source is unknown or failures persist, review successful sign-ins and active sessions with the administrator before changing access rules.", "若已知任务/服务持续重试旧密码，与账户管理者确认后只更新该处保存凭据；若来源未知或失败持续，与管理员核对成功登录和活动会话，再决定访问规则变更。"),
                ("Changing a password can interrupt services and lock out remote access. Identify dependent tasks and retain a recovery sign-in method; do not disable lockout or auditing to suppress events.", "改密码可能中断服务或远程访问；先确认依赖任务并保留恢复登录方式，不要关闭锁定策略或审计来消除事件。"),
                ("Retry the intended sign-in once and confirm its success. Check whether failures from the same source/account stop during the normal task interval without disabling audit collection.", "重试一次预期登录并确认成功，再观察正常任务周期内相同来源/账户失败是否停止，同时保持审计采集。"));
        }

        return Generic(finding);
    }

    private static AuditRemediationPlan Generic(AuditIssue finding)
    {
        string location = string.IsNullOrWhiteSpace(finding.LogName) ? "the recorded log channel" : finding.LogName;
        string eventId = string.IsNullOrWhiteSpace(finding.EventId) ? "the recorded event" : finding.EventId;
        return Plan(
            ($"Open Event Viewer → {location}, find event {eventId} at the recorded time, and inspect General/Details for the exact component, error code and affected operation. Compare it with a failure you can reproduce.",
             $"打开事件查看器 → {location}，定位记录时间的事件 {eventId}；在“常规/详细信息”确认具体组件、错误码及失败操作，并对照可复现的功能问题。",
             "Proceed only when the same component/error aligns with a real failure. The event alone does not identify a confirmed cause or justify a system-wide repair.",
             "仅在相同组件/错误与实际故障对应时继续处理；单条事件不能确定根因，也不足以支持全系统修复。"),
            ("If a specific application or device is confirmed, use its supported diagnostic/repair entry for that exact error. If the component or impact remains unknown, preserve the event and obtain the missing identification before changing settings.",
             "确认具体应用或设备后，使用该组件支持的诊断/修复入口处理该错误；若组件或影响仍不明确，先保留事件并补齐身份信息，再决定变更。"),
            ("Before any confirmed change, record the original value/version and a supported recovery route; save work if a restart is required. Avoid broad registry, permission or service changes without identifying the affected component.",
             "确定变更后先记录原值/版本及支持的恢复方式；需要重启时保存工作。未定位组件前，不批量修改注册表、权限或服务。"),
            ("Repeat the affected operation under the same conditions and compare newly recorded events for the same component/error. Keep the outcome and time if it recurs.",
             "在相同条件下重复受影响操作，对比同一组件/错误的新事件；复发时记录结果和时间。"));
    }

    private static AuditRemediationPlan Plan(
        (string Check, string CheckZh, string Expected, string ExpectedZh) check,
        (string En, string Zh) action, (string En, string Zh) risk, (string En, string Zh) verification) => new()
    {
        Checks = [new() { Check = check.Check, CheckZh = check.CheckZh, ExpectedResult = check.Expected, ExpectedResultZh = check.ExpectedZh }],
        ConditionalAction = action.En, ConditionalActionZh = action.Zh,
        RiskAndRecovery = risk.En, RiskAndRecoveryZh = risk.Zh,
        Verification = verification.En, VerificationZh = verification.Zh,
    };
}
