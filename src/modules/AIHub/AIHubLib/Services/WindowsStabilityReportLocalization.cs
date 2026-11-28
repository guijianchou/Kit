using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

/// <summary>Authored report translations preserve the same observed evidence, without model calls or new collection.</summary>
public static class WindowsStabilityReportLocalization
{
    public static string NormalizeLanguage(string? language) => language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true ? "zh-CN" : "en-US";

    public static WindowsStabilityReportData English(WindowsStabilityReportData data)
    {
        if (data.Language == "en-US") return data;
        return data with
        {
            Language = "en-US",
            Assessment = Translate(data.Assessment), Grade = Translate(data.Grade), GradeBasis = Translate(data.GradeBasis),
            Metrics = data.Metrics.Select(item => item with { Label = Translate(item.Label), Unit = Translate(item.Unit), Qualification = Translate(item.Qualification) }).ToArray(),
            Timeline = data.Timeline.Select(item => item with { Label = Translate(item.Label) }).ToArray(),
            CrashClusters = data.CrashClusters.Select(item => item with { Process = MissingValue(item.Process), Module = MissingValue(item.Module), ExceptionCode = MissingValue(item.ExceptionCode), Offset = MissingValue(item.Offset) }).ToArray(),
            Noise = data.Noise.Select(item => item with { Classification = Translate(item.Classification) }).ToArray(),
            Dimensions = data.Dimensions.Select(item => item with { Name = Translate(item.Name), Status = Translate(item.Status), Evidence = Translate(item.Evidence) }).ToArray(),
            Actions = data.Actions.Select(item => item with { Title = Translate(item.Title), Reason = Translate(item.Reason), Step = Translate(item.Step), Verification = Translate(item.Verification) }).ToArray(),
            BlindSpots = data.BlindSpots.Select(TranslateBlindSpot).ToArray(),
            Coverage = data.Coverage.Select(item => item with { Limitation = TranslateCoverage(item.Limitation) }).ToArray(),
        };
    }

    private static string Translate(string value)
    {
        if (Words.TryGetValue(value, out var exact)) return exact;
        foreach (string prefix in new[] { "本期完整", "上期完整", "本期至少为已观察数量；覆盖不足", "上期至少为已观察数量；覆盖不足" })
        {
            if (value.StartsWith(prefix + "；", StringComparison.Ordinal)) return Translate(prefix) + "; " + Translate(value[(prefix.Length + 1)..]);
        }
        foreach (var (source, target) in Templates)
        {
            string pattern = "^" + Regex.Escape(source);
            for (int index = 0; index < 5; index++) pattern = pattern.Replace("\\{" + index.ToString(CultureInfo.InvariantCulture) + "}", "(.*?)", StringComparison.Ordinal);
            var match = Regex.Match(value, pattern + "$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
            if (match.Success)
            {
                var arguments = match.Groups.Cast<Group>().Skip(1).Select(group => (object)MissingValue(group.Value)).ToArray();
                if (source.StartsWith("本期主要暴露应用崩溃", StringComparison.Ordinal)) arguments[1] = Translate(match.Groups[2].Value);
                return string.Format(CultureInfo.InvariantCulture, target, arguments);
            }
        }

        // Only collector-owned compound messages and metric qualifications use these separators.
        if (value.Contains('；')) return string.Join("; ", value.Split('；').Select(Translate));
        foreach (string channel in new[] { "System", "Application", "Setup" })
        {
            if (value.StartsWith(channel + "：", StringComparison.Ordinal)) return channel + ": " + Translate(value[(channel.Length + 1)..]);
        }
        foreach (var pair in Words.Where(pair => pair.Key.EndsWith('。')).OrderByDescending(pair => pair.Key.Length)) value = value.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        return value;
    }

    private static string TranslateBlindSpot(string value)
    {
        foreach (string channel in new[] { "System", "Application", "Setup" })
        {
            if (value.StartsWith(channel + "：", StringComparison.Ordinal)) return channel + ": " + TranslateCoverage(value[(channel.Length + 1)..]);
        }
        return Translate(value);
    }

    private static string TranslateCoverage(string value)
    {
        foreach (string marker in new[] { "最早可用日志", "无法确认日志最早", "该渠道存在日志清除", "日志清除目标未知" })
        {
            int boundary = value.IndexOf(" " + marker, StringComparison.Ordinal);
            if (boundary > 0) return TranslateCoverage(value[..boundary]) + " " + TranslateCoverage(value[(boundary + 1)..]);
        }
        return Translate(value);
    }

    private static string MissingValue(string value) => value switch
    {
        "未知进程" => "Unknown process", "未知模块" => "Unknown module", "未知异常码" => "Unknown exception", "未知偏移" => "Unknown offset", _ => value,
    };

    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["本期完整"] = "Current complete", ["上期完整"] = "Previous complete",
        ["本期至少为已观察数量；覆盖不足"] = "Current at least the observed count; incomplete coverage",
        ["上期至少为已观察数量；覆盖不足"] = "Previous at least the observed count; incomplete coverage",
        ["本期至少为已观察数量"] = "Current at least the observed count", ["上期至少为已观察数量"] = "Previous at least the observed count", ["覆盖不足"] = "incomplete coverage",
        ["事件总量"] = "Total events", ["严重 Critical"] = "Critical", ["错误 Error"] = "Error", ["警告 Warning"] = "Warning",
        ["应用崩溃"] = "App crashes", ["非计划关机证据"] = "Unexpected shutdowns", ["睡眠 / 恢复"] = "Sleep / resume", ["内核与硬件异常"] = "Kernel / hardware",
        ["蓝屏 BugCheck 1001"] = "BugCheck 1001", ["WHEA 硬件记录"] = "WHEA records", ["显示驱动恢复 4101"] = "Display recovery 4101",
        ["NTFS 98 异常"] = "NTFS 98 faults", ["进入睡眠 42"] = "Sleep entry 42", ["睡眠恢复 107"] = "Resume 107",
        ["可配对入睡/恢复"] = "Paired sleep / resume", ["未配对睡眠/恢复"] = "Unpaired sleep / resume", ["恢复耗时记录 131"] = "Resume duration 131",
        ["131 记录的最大恢复耗时"] = "Maximum recorded duration", ["新服务 7045"] = "New services 7045", ["MSI 成功安装"] = "MSI successful installs",
        ["音频进程/模块崩溃"] = "Audio-related crashes", ["本期 Minidump 文件"] = "Period minidump files", ["目录 Minidump 总文件"] = "All minidump files",
        ["条"] = "records", ["组"] = "groups", ["次入睡"] = "sleep entries", ["条/组"] = "records/groups", ["原字段值"] = "raw value", ["个"] = "files",
        ["按渠道、RecordId、事件发生时间去重。"] = "Deduplicated by channel, RecordId and event time.",
        ["Windows 事件级别1。"] = "Windows event level 1.", ["Windows 事件级别2，非唯一故障次数。"] = "Windows event level 2; not a count of unique failures.",
        ["Windows 事件级别3，不自动认定功能故障。"] = "Windows event level 3; does not by itself establish functional failure.",
        ["Application Error 1000"] = "Application Error 1000", ["WER 1001不重复加算。"] = "WER 1001 is not counted again.",
        ["Kernel-Power 41与EventLog 6008按同次启动10分钟内匹配"] = "Kernel-Power 41 and EventLog 6008 are paired within 10 minutes of the same boot",
        ["记录时刻通常为下次启动，非准确掉电时刻。"] = "The timestamp is usually the next boot, not the exact loss of power.",
        ["含异常关机、蓝屏、WHEA、显示驱动恢复、磁盘/文件系统异常"] = "Includes unexpected shutdowns, bugchecks, WHEA, display recovery and disk/file-system faults",
        ["不等于根因数。"] = "Not a count of root causes.", ["启动时记录异常关机"] = "Unexpected shutdown recorded at boot", ["详见时间线字段。"] = "See timeline fields.",
        ["与41可能指向同次异常关机，不直接相加。"] = "May describe the same shutdown as event 41; not added directly.",
        ["排除Application渠道的WER报告。"] = "Excludes WER reports from the Application channel.",
        ["含已纠正与未纠正记录，严重程度需按原事件核实。"] = "Includes corrected and uncorrected records; verify severity in the source event.",
        ["TDR恢复记录，不推定显卡损坏。"] = "TDR recovery record; does not imply a defective GPU.", ["按设备与状态码核实。"] = "Verify the device and status code.",
        ["仅级别1–3"] = "Levels 1–3 only", ["Information卷健康通知不计为故障。"] = "Informational healthy-volume notices are excluded.",
        ["Kernel-Power。"] = "Kernel-Power.", ["不表示所有恢复过程无错误。"] = "Does not establish that every resume was error-free.",
        ["42→24小时内107"] = "42 followed by 107 within 24 hours", ["遇异常关机/启动边界中断配对。"] = "A shutdown or boot boundary interrupts pairing.",
        ["可能来自窗口边界或覆盖缺失，不直接判定恢复失败。"] = "May reflect period boundaries or missing coverage; does not establish resume failure.",
        ["耗时只取明确的Duration字段，不从间隔推测。"] = "Duration comes only from an explicit field, never from elapsed time between events.",
        ["字段单位由事件版本决定"] = "Units depend on the event version", ["未明确单位时不换算成毫秒。"] = "No conversion to milliseconds when units are unspecified.",
        ["服务安装记录，不等于恶意或故障。"] = "Service installation record; not proof of malware or a fault.",
        ["11707，或1033且明确安装状态为0"] = "11707, or 1033 with explicit installation status 0", ["失败或未知状态不计为成功。"] = "Failed or unknown status is not counted as success.",
        ["安装成功记录"] = "Successful installation record", ["时间邻近不构成故障因果。"] = "Temporal proximity does not establish causation.",
        ["由AUDIODG或音频模块签名识别"] = "Identified from AUDIODG or audio-module signatures", ["不是默认设备诊断。"] = "Not a diagnosis of the default device.",
        ["按文件修改时间，非崩溃时间"] = "Uses file modification time, not crash time", ["目录不可读为未知，未读取转储内容。"] = "Unreadable directories are unknown; dump contents are not read.",
        ["采集当时目录总数，无历史目录快照"] = "Directory count at collection time; no historical directory snapshot", ["最多枚举1000个。"] = "At most 1,000 files are enumerated.",
        ["COM 权限警告"] = "COM permission warning", ["需结合实际功能影响，不直接判定为系统不稳定"] = "Correlate with actual functional impact; not proof of instability",
        ["待核实功能影响"] = "Functional impact needs verification", ["重复次数不等于影响严重度"] = "Frequency does not establish severity",
        ["部分 Application Error 1000 缺少进程、模块、异常码或偏移"] = "Some Application Error 1000 records lack a process, module, exception code or offset",
        ["这些记录分别保留，未将未知字段合并成同一故障。"] = "These records remain separate; missing fields are not merged into one failure.",
        ["时间线保留全部已采集异常关机/内核故障证据"] = "The timeline retains every collected unexpected-shutdown and kernel-fault record",
        ["睡眠恢复与变更仅展示最近160条，指标及聚类使用全部已采集记录。"] = "Only the latest 160 sleep/resume and change records are shown; metrics and clusters use all collected records.",
        ["零内核级故障记录：已覆盖渠道未见异常关机、蓝屏、WHEA、TDR、磁盘/文件系统异常"] = "Zero recorded kernel-level faults: covered channels show no unexpected shutdown, bugcheck, WHEA, TDR or disk/file-system fault",
        ["不等于硬件健康保证。"] = "This is not a guarantee of hardware health.",
        ["内核级故障数量未知：覆盖不足，不能将未观察到的记录解释为零故障。"] = "Kernel-level fault count is unknown: incomplete coverage cannot be interpreted as zero faults.",
        ["内核与硬件"] = "Kernel and hardware", ["电源与睡眠"] = "Power and sleep", ["应用"] = "Applications", ["音频"] = "Audio", ["崩溃转储"] = "Crash dumps",
        ["需优先核实"] = "Investigate first", ["已覆盖记录未见异常"] = "No issue observed in covered records", ["证据不足"] = "Insufficient evidence", ["异常关机证据"] = "Unexpected shutdown evidence",
        ["观察"] = "Monitor", ["存在崩溃"] = "Crashes observed", ["未观察到崩溃"] = "No crash observed", ["存在关联崩溃"] = "Related crashes observed", ["未观察到音频崩溃"] = "No audio crash observed", ["文件存在性观察"] = "File-presence observation",
        ["已覆盖记录为零内核级故障记录"] = "covered records contain zero kernel-level faults", ["内核稳定性因覆盖限制暂不能确认"] = "kernel stability cannot be established with limited coverage",
        ["已覆盖记录为零内核级故障记录，未观察到应用崩溃"] = "Covered records contain zero kernel-level faults and no observed application crash",
        ["保留当前状态，继续观察异常复现。"] = "Keep the current configuration and watch for recurrence.",
        ["现有日志覆盖不足，暂不能判断本期系统是否稳定"] = "Log coverage is insufficient to assess this period's stability", ["先补齐可读取证据。"] = "Improve readable evidence coverage first.",
        ["需排查"] = "Investigate", ["以事件证据和覆盖范围评估，不从单一分数推导稳定性。"] = "Assessment uses event evidence and coverage, not a single score.",
        ["核实异常关机链路"] = "Investigate unexpected shutdowns",
        ["对照 Kernel-Power 41 的 BugcheckCode、SleepInProgress、PowerButtonTimestamp 与6008；有转储则先保留分析。询问掉电前场景，记录当时供电、睡眠与负载，未确认前不直接改 BIOS。"] = "Compare event 41 BugcheckCode, SleepInProgress and PowerButtonTimestamp with 6008; preserve dumps. Record the preceding power, sleep and load conditions. Do not change BIOS settings before confirming the cause.",
        ["复现原使用/睡眠场景，保存新一轮电源与恢复记录；确认是否仍出现41/6008或蓝屏证据。"] = "Repeat the original usage or sleep scenario and collect fresh power/resume records. Check whether 41/6008 or bugcheck evidence recurs.",
        ["定位内核、驱动或存储异常"] = "Investigate kernel, driver or storage faults", ["已观察到蓝屏/WHEA/TDR/磁盘或文件系统事件。"] = "Bugcheck, WHEA, TDR, disk or file-system events were observed.",
        ["按事件来源、ID、设备名和错误码定位组件；先备份重要数据并保留转储，针对确认的设备核对驱动/固件版本。不要仅凭事件号进行破坏性修复。"] = "Identify the component using provider, ID, device and error code. Back up important data and retain dumps; verify the affected device's driver/firmware version. An event ID alone does not justify destructive repairs.",
        ["重复触发场景并检查对应渠道的新事件与转储；变化必须有同口径证据支持。"] = "Repeat the triggering scenario and inspect new events and dumps. Compare changes using the same evidence criteria.",
        ["核对应用和故障模块版本，保留故障事件与现场；按原操作尝试复现。若为音频组件，记录播放/麦克风和增强效果状态，逐项对照，避免同时更改多个设置。"] = "Check app/module versions and preserve the event and reproduction context. Repeat the original steps. For audio, record playback, microphone and enhancement states; change one setting at a time.",
        ["用相同操作复测并收集 Application Error 1000；对比进程、模块、异常码、偏移是否一致。"] = "Repeat the same steps and collect Application Error 1000. Compare process, module, exception code and offset.",
        ["补齐日志覆盖"] = "Improve log coverage", ["部分渠道不可读、已滚动或达到采集上限。"] = "Some channels are unreadable, rolled over or reached the collection cap.",
        ["查看报告盲区与各渠道最早可用时间；确认日志启用和容量，下一周期保持同样窗口与渠道。"] = "Review blind spots and each channel's oldest record. Check logging and capacity; keep the same window and channels for the next period.",
        ["确认下一份报告覆盖完整后再判断故障减少；当前未观察不能视为修复。"] = "Confirm complete coverage in the next report before claiming fewer faults. An unobserved event is not proof of repair.",
        ["保持配置并持续观察"] = "Keep configuration and monitor", ["本次已覆盖记录未暴露明确崩溃或内核异常。"] = "Covered records show no explicit application crash or kernel fault.",
        ["保留当前配置，出现异常时记录时间和操作场景；下周期使用相同渠道与窗口复查。"] = "Keep the current configuration. Record the time and context of any problem; review the same channels and window next period.",
        ["以新事件和实际功能表现验证，不以评分或没有日志单独判断健康。"] = "Verify using new events and actual functionality; neither a score nor absent logs alone establishes health.",
        ["异常关机证据（启动时记录）"] = "Unexpected shutdown (recorded at boot)", ["进入睡眠"] = "Entering sleep", ["从睡眠恢复"] = "Resuming from sleep", ["恢复耗时记录"] = "Resume duration record", ["系统/软件变更"] = "System/software change", ["内核或硬件异常"] = "Kernel or hardware fault",
        ["未知进程"] = "Unknown process", ["未知模块"] = "Unknown module", ["未知异常码"] = "Unknown exception", ["未知偏移"] = "Unknown offset",
        ["存在没有事件时间的记录，已跳过"] = "Records without event time were skipped", ["时间分布不完整。"] = "The time distribution is incomplete.",
        ["无法确认日志最早覆盖时间。"] = "The oldest covered log time could not be established.",
        ["仅采集 System / Application / Setup 的 Critical、Error、Warning、Information；不读取 Security，不包含其他未启用的专用诊断渠道。"] = "Only Critical, Error, Warning and Information from System / Application / Setup are collected. Security and other disabled diagnostic channels are excluded.",
        ["事件日志不是连续硬件监控；电压、温度、瞬时供电、睡眠期间电池消耗与驱动内部状态没有实时证据。"] = "Event logs are not continuous hardware monitoring. There is no real-time evidence for voltage, temperature, transient power, sleep battery drain or internal driver state.",
        ["观察到104日志清除事件，但载荷缺少目标渠道；各采集渠道的连续覆盖均不能确认。"] = "Event 104 indicates a log clear but omits its target channel. Continuous coverage cannot be confirmed for any collected channel.",
        ["日志清除目标未知，连续覆盖不能确认。"] = "The cleared channel is unknown; continuous coverage cannot be confirmed.", ["该渠道存在日志清除记录。"] = "A log-clear record exists for this channel.",
        ["Minidump 文件枚举达到1000个上限，文件计数仅为观察下界。"] = "Minidump enumeration reached the 1,000-file cap; counts are observed lower bounds.",
        ["音频端点是采集当时的默认设备标识，未读取增强效果和故障发生时设备；未查询固件厂商更新目录。显卡信息来自注册驱动项，不能保证正在加载；不推定 BIOS 已过期。"] = "Audio endpoints identify the current default devices, not enhancements or devices active at failure time. Firmware catalogs were not queried. GPU data comes from registered drivers and may not be loaded; no BIOS obsolescence is inferred.",
        ["缺少上次环境快照和设备绑定对照，BIOS、Realtek、Nahimic 等此前处置是否落实尚未验证；当前配置存在不等于变更已执行。"] = "Without a previous environment snapshot and device mapping, earlier BIOS, Realtek or Nahimic remediation remains unverified. A present configuration does not prove that a change was made.",
    };

    private static readonly (string Source, string Target)[] Templates =
    [
        ("磁盘事件 {0}", "Disk event {0}"),
        ("本期观察恢复 {0} 条；只将42与随后24小时内107配对，不代表所有睡眠成功。", "Observed {0} resumes this period; only 42 followed by 107 within 24 hours is paired. This does not establish successful sleep in every case."),
        ("应用崩溃共识别 {0} 个签名；文档保留前 {1} 个，省略 {2} 个低频签名以限制报告大小。崩溃总数、每日趋势和热力图仍计入全部已采集事件；图中排行另有展示上限。", "Identified {0} crash signatures. This document retains the first {1} and omits {2} less frequent signatures to bound file size. Crash totals, daily trends and heatmaps include all collected events; chart rankings have a separate display cap."),
        ("对比期起点 {0} 早于30天保留窗口，只采集 {1} 之后的证据；不计算改善百分比。", "The comparison period starts at {0}, before the 30-day retention window. Only evidence after {1} was collected; no improvement percentage is calculated."),
        ("每日与热力图按窗口起点 {0} 对齐24小时桶，按真实事件时间统计；夏令时按UTC经过时长分桶。零值只在对应渠道覆盖完整时显示。", "Daily and heatmap buckets are 24 hours from the window start at {0}, using actual event times. DST follows elapsed UTC time. Zero is shown only where the relevant channels are fully covered."),
        ("观察到 {0} 条/组内核、硬件或异常关机相关记录。", "Observed {0} kernel, hardware or unexpected-shutdown records/groups."),
        ("已观察入睡 {0} 条、恢复 {1} 条、可配对 {2} 组、未配对 {3} 条；未配对可能来自边界或日志缺失。", "Observed {0} sleep entries, {1} resumes, {2} matched pairs and {3} unmatched records. Period boundaries or missing logs can explain unmatched records."),
        ("Application Error 1000 已观察 {0} 条；按进程×模块×异常码×偏移聚类。", "Observed {0} Application Error 1000 records, clustered by process × module × exception code × offset."),
        ("已观察音频相关 Application Error 1000 共 {0} 条；覆盖不完整时此数只是下界。未采集设备增强效果配置，不能归因到当前默认音频设备。", "Observed {0} audio-related Application Error 1000 records; this is a lower bound when coverage is incomplete. Device enhancement settings were not collected, so these cannot be attributed to the current default audio device."),
        ("本期修改时间范围内的 Minidump 文件 {0} 个；未解析转储，文件时间不代表确切故障发生时间。", "Found {0} minidump files modified during this period. Dumps were not parsed; file times do not establish the exact failure time."),
        ("本期存在 {0} 组非计划关机证据，先核实电源与崩溃链路；同时观察到 {1} 条应用崩溃记录。", "This period contains {0} unexpected-shutdown groups. Investigate power and crash evidence first; {1} application-crash records were also observed."),
        ("本期观察到内核或硬件异常记录，应先核实对应事件与转储；另有 {0} 条应用崩溃记录。", "Kernel or hardware fault records were observed. Verify the events and dumps first; there are also {0} application-crash records."),
        ("本期主要暴露应用崩溃：共观察 {0} 条；{1}。", "Application crashes dominate the observations: {0} records; {1}."),
        ("本期 {0} 组41/6008异常关机证据；时间通常为重新启动时记录。", "This period has {0} groups of 41/6008 shutdown evidence; timestamps usually refer to the following boot."),
        ("复查 {0} 崩溃", "Investigate {0} crashes"),
        ("{0} · {1} · 偏移 {2}，本期 {3} 条记录。", "{0} · {1} · offset {2}; {3} records this period."),
        ("达到每渠道 {0} 条上限，只保留最近观察。", "Reached the cap of {0} records per channel; only the most recent observations were retained."),
        ("无法完整读取：{0}", "Could not read the entire channel: {0}"),
        ("最早可用日志为 {0}，更早记录可能滚动丢失或日志尚未启用。", "The oldest available log is {0}; older records may have rolled over or logging may not have been enabled."),
        ("硬件/BIOS 注册表证据不可读：{0}", "Hardware/BIOS registry evidence is unreadable: {0}"),
        ("当前默认音频端点读取失败：{0}", "Could not read the current default audio endpoint: {0}"),
        ("Minidump 目录不可读，不能以无文件证明无蓝屏：{0}", "The minidump directory is unreadable; absent files cannot prove absence of bugchecks: {0}"),
    ];
}
