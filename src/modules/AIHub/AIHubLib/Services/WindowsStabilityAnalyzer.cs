using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

/// <summary>Pure event-time aggregation. Observation counts never imply an inferred cause or repair.</summary>
public static class WindowsStabilityAnalyzer
{
    private const int MaximumCrashClusters = 2048;

    public static WindowsStabilityReportData Analyze(WindowsStabilityCollection collection, DateTimeOffset start, DateTimeOffset end, DateTimeOffset previousStart)
    {
        var events = collection.Events.Where(item => item.Time >= previousStart && item.Time < end)
            .GroupBy(Identity, StringComparer.Ordinal).Select(group => group.First()).OrderBy(item => item.Time).ToArray();
        var current = events.Where(item => item.Time >= start).ToArray();
        var previous = events.Where(item => item.Time < start).ToArray();
        string[] allChannels = ["System", "Application", "Setup"];
        bool Complete(string[] channels, DateTimeOffset from, DateTimeOffset to) => channels.All(channel => collection.Coverage.Any(coverage =>
            coverage.Channel == channel && coverage.ReadSucceeded && !coverage.Capped && coverage.RequestedStart <= from && coverage.RequestedEnd >= to
            && coverage.OldestAvailable is { } oldest && oldest <= from && (!coverage.RolledOver || coverage.Limitation.Contains("最早可用日志", StringComparison.Ordinal))
            && !coverage.Limitation.Contains("日志清除", StringComparison.Ordinal) && !coverage.Limitation.Contains("已跳过", StringComparison.Ordinal)));
        int? Count(int value, string[] channels, DateTimeOffset from, DateTimeOffset to) => Complete(channels, from, to) || value > 0 ? value : null;
        var metrics = new List<WindowsStabilityMetric>();
        void Metric(string key, string label, int value, int before, string[] channels, string unit, string note)
        {
            string qualification = (Complete(channels, start, end) ? "本期完整" : "本期至少为已观察数量；覆盖不足")
                + "；" + (Complete(channels, previousStart, start) ? "上期完整" : "上期至少为已观察数量；覆盖不足") + "；" + note;
            metrics.Add(new(key, label, Count(value, channels, start, end), Count(before, channels, previousStart, start), unit, qualification));
        }
        Metric("total", "事件总量", current.Length, previous.Length, allChannels, "条", "按渠道、RecordId、事件发生时间去重。");
        Metric("critical", "严重 Critical", current.Count(item => item.Level == 1), previous.Count(item => item.Level == 1), allChannels, "条", "Windows 事件级别1。");
        Metric("error", "错误 Error", current.Count(item => item.Level == 2), previous.Count(item => item.Level == 2), allChannels, "条", "Windows 事件级别2，非唯一故障次数。");
        Metric("warning", "警告 Warning", current.Count(item => item.Level == 3), previous.Count(item => item.Level == 3), allChannels, "条", "Windows 事件级别3，不自动认定功能故障。");
        Metric("crashes", "应用崩溃", current.Count(IsCrash), previous.Count(IsCrash), ["Application"], "条", "Application Error 1000；WER 1001不重复加算。");
        var powerGroups = PowerEvents(events);
        var power = powerGroups.Where(item => item.Time >= start).ToArray();
        var previousPower = powerGroups.Where(item => item.Time < start).ToArray();
        Metric("power", "非计划关机证据", power.Length, previousPower.Length, ["System"], "组", "Kernel-Power 41与EventLog 6008按同次启动10分钟内匹配；记录时刻通常为下次启动，非准确掉电时刻。");
        int sleep = current.Count(IsSleep);
        int wake = current.Count(IsWake);
        Metric("sleep", "睡眠 / 恢复", sleep, previous.Count(IsSleep), ["System"], "次入睡", $"本期观察恢复 {wake} 条；只将42与随后24小时内107配对，不代表所有睡眠成功。");
        int kernel = current.Count(IsKernelFault) + power.Length;
        Metric("kernel", "内核与硬件异常", kernel, previous.Count(IsKernelFault) + previousPower.Length, ["System"], "条/组", "含异常关机、蓝屏、WHEA、显示驱动恢复、磁盘/文件系统异常；不等于根因数。");
        void EventMetric(string key, string label, Func<WindowsStabilityEvent, bool> predicate, string channel, string note) =>
            Metric(key, label, current.Count(predicate), previous.Count(predicate), [channel], "条", note);
        EventMetric("power41", "Kernel-Power 41", item => IsPower(item) && item.EventId == 41, "System", "启动时记录异常关机；详见时间线字段。");
        EventMetric("power6008", "EventLog 6008", item => IsPower(item) && item.EventId == 6008, "System", "与41可能指向同次异常关机，不直接相加。");
        EventMetric("bugcheck", "蓝屏 BugCheck 1001", item => IsKernelFault(item) && item.EventId == 1001, "System", "排除Application渠道的WER报告。");
        EventMetric("whea", "WHEA 硬件记录", item => item.Channel == "System" && item.Provider.Equals("Microsoft-Windows-WHEA-Logger", StringComparison.OrdinalIgnoreCase), "System", "含已纠正与未纠正记录，严重程度需按原事件核实。");
        EventMetric("tdr", "显示驱动恢复 4101", item => item.Channel == "System" && item.Provider.Equals("Display", StringComparison.OrdinalIgnoreCase) && item.EventId == 4101, "System", "TDR恢复记录，不推定显卡损坏。");
        foreach (int id in new[] { 7, 11, 51 })
        {
            EventMetric("disk" + id.ToString(CultureInfo.InvariantCulture), "磁盘事件 " + id.ToString(CultureInfo.InvariantCulture),
                item => item.Channel == "System" && item.Provider.Equals("disk", StringComparison.OrdinalIgnoreCase) && item.EventId == id, "System", "按设备与状态码核实。");
        }
        EventMetric("ntfs98", "NTFS 98 异常", item => IsKernelFault(item) && item.EventId == 98, "System", "仅级别1–3；Information卷健康通知不计为故障。");
        EventMetric("sleep42", "进入睡眠 42", IsSleep, "System", "Kernel-Power。");
        EventMetric("wake107", "睡眠恢复 107", IsWake, "System", "Kernel-Power；不表示所有恢复过程无错误。");
        var sleepCurrent = PairSleep(current);
        var sleepPrevious = PairSleep(previous);
        Metric("sleepPairs", "可配对入睡/恢复", sleepCurrent.Pairs, sleepPrevious.Pairs, ["System"], "组", "42→24小时内107；遇异常关机/启动边界中断配对。");
        Metric("sleepUnmatched", "未配对睡眠/恢复", sleepCurrent.Unmatched, sleepPrevious.Unmatched, ["System"], "条", "可能来自窗口边界或覆盖缺失，不直接判定恢复失败。");
        EventMetric("resume131", "恢复耗时记录 131", IsResumeDuration, "System", "耗时只取明确的Duration字段，不从间隔推测。");
        int? Duration(WindowsStabilityEvent[] source) => source.Where(IsResumeDuration).Select(item => Field(item, "ResumeDuration", "ResumeTime", "Duration"))
            .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int duration) && duration >= 0 ? (int?)duration : null).Max();
        metrics.Add(new("resumeDuration", "131 记录的最大恢复耗时", Duration(current), Duration(previous), "原字段值", "字段单位由事件版本决定；未明确单位时不换算成毫秒。"));
        EventMetric("scm7045", "新服务 7045", item => item.Channel == "System" && item.EventId == 7045 && item.Provider.Equals("Service Control Manager", StringComparison.OrdinalIgnoreCase), "System", "服务安装记录，不等于恶意或故障。");
        EventMetric("msi", "MSI 成功安装", IsMsiSuccess, "Application", "11707，或1033且明确安装状态为0；失败或未知状态不计为成功。");
        EventMetric("wu19", "Windows Update 19", item => item.EventId == 19 && item.Provider.Equals("Microsoft-Windows-WindowsUpdateClient", StringComparison.OrdinalIgnoreCase), "System", "安装成功记录；时间邻近不构成故障因果。");
        EventMetric("audio", "音频进程/模块崩溃", IsAudioCrash, "Application", "由AUDIODG或音频模块签名识别；不是默认设备诊断。");
        int? dumpCurrent = null;
        int? dumpPrevious = null;
        int? dumpTotal = null;
        if (collection.Environment.TryGetValue("MinidumpTimesUtc", out string? collectedDumps))
        {
            var parsedDumpTimes = collectedDumps.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(value => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? (DateTimeOffset?)time : null).Where(time => time.HasValue).Select(time => time!.Value).ToArray();
            dumpCurrent = parsedDumpTimes.Count(time => time >= start && time < end);
            dumpPrevious = collection.RequestedStart <= previousStart ? parsedDumpTimes.Count(time => time >= previousStart && time < start) : null;
        }
        if (collection.Environment.TryGetValue("MinidumpTotalFiles", out string? totalFiles) && int.TryParse(totalFiles, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTotal)) dumpTotal = parsedTotal;
        metrics.Add(new("minidumps", "本期 Minidump 文件", dumpCurrent, dumpPrevious, "个", "按文件修改时间，非崩溃时间；目录不可读为未知，未读取转储内容。"));
        metrics.Add(new("minidumpsTotal", "目录 Minidump 总文件", dumpTotal, null, "个", "采集当时目录总数，无历史目录快照；最多枚举1000个。"));

        int dayCount = Math.Max(1, (int)Math.Ceiling((end - start).TotalDays));
        var daily = new List<WindowsStabilityDaily>();
        foreach (bool isCurrent in new[] { false, true })
        {
            var periodStart = isCurrent ? start : previousStart;
            var periodEnd = isCurrent ? end : start;
            for (var date = periodStart; date < periodEnd; date = date.AddDays(1))
            {
                var dayEnd = date.AddDays(1) < periodEnd ? date.AddDays(1) : periodEnd;
                var bucket = events.Where(item => item.Time >= date && item.Time < dayEnd).ToArray();
                daily.Add(new(date, Count(bucket.Count(item => item.Level == 1), allChannels, date, dayEnd),
                    Count(bucket.Count(item => item.Level == 2), allChannels, date, dayEnd), Count(bucket.Count(item => item.Level == 3), allChannels, date, dayEnd),
                    Count(bucket.Count(item => item.Level == 4), allChannels, date, dayEnd), Count(bucket.Count(IsCrash), ["Application"], date, dayEnd), isCurrent));
            }
        }
        var heatmap = new List<WindowsStabilityHeatCell>();
        for (int day = 0; day < dayCount; day++)
        {
            for (int hour = 0; hour < 24; hour++)
            {
                var from = start.AddDays(day).AddHours(hour);
                if (from >= end) break;
                var to = from.AddHours(1) < end ? from.AddHours(1) : end;
                int count = current.Count(item => item.Time >= from && item.Time < to && item.Level is 1 or 2 or 3);
                heatmap.Add(new(start.AddDays(day), hour, Count(count, allChannels, from, to)));
            }
        }
        var allClusters = events.Where(IsCrash).GroupBy(CrashKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var signature = Signature(group.First());
                return new WindowsStabilityCrashCluster(signature.Process, signature.Module, signature.Code, signature.Offset,
                    group.Count(item => item.Time >= start), group.Count(item => item.Time < start), signature.Complete);
            }).OrderByDescending(cluster => cluster.Current).ThenByDescending(cluster => cluster.Previous).ToArray();
        var clusters = allClusters.Take(MaximumCrashClusters).ToArray();
        var noise = events.Where(item => item.Level is 1 or 2 or 3 && !IsCrash(item) && !IsKernelFault(item) && !IsPower(item))
            .GroupBy(item => (item.Provider, item.EventId)).Select(group => new WindowsStabilityNoise(group.Key.Provider, group.Key.EventId,
                group.Count(item => item.Time >= start), group.Count(item => item.Time < start),
                group.Key.EventId == 10016 && group.Key.Provider.Contains("DistributedCOM", StringComparison.OrdinalIgnoreCase)
                    ? "COM 权限警告；需结合实际功能影响，不直接判定为系统不稳定" : "待核实功能影响；重复次数不等于影响严重度"))
            .OrderByDescending(item => item.Current).ThenBy(item => item.Provider, StringComparer.Ordinal).Take(10).ToArray();
        var nonCore = events.Where(item => !IsPower(item) && !IsKernelFault(item) && (IsSleep(item) || IsWake(item) || IsChange(item) || IsResumeDuration(item))).ToArray();
        var timeline = events.Where(item => IsPower(item) || IsKernelFault(item)).Concat(nonCore.TakeLast(160))
            .Select(item => Timeline(item, item.Time >= start)).OrderBy(item => item.Time).ToArray();
        var blindSpots = collection.BlindSpots.ToList();
        if (allClusters.Length > MaximumCrashClusters)
        {
            blindSpots.Add($"应用崩溃共识别 {allClusters.Length} 个签名；文档保留前 {MaximumCrashClusters} 个，省略 {allClusters.Length - MaximumCrashClusters} 个低频签名以限制报告大小。崩溃总数、每日趋势和热力图仍计入全部已采集事件；图中排行另有展示上限。");
        }
        blindSpots.AddRange(collection.Coverage.Where(item => !string.IsNullOrWhiteSpace(item.Limitation)).Select(item => item.Channel + "：" + item.Limitation));
        if (collection.RequestedStart > previousStart)
        {
            blindSpots.Add($"对比期起点 {previousStart:yyyy-MM-dd HH:mm} 早于30天保留窗口，只采集 {collection.RequestedStart:yyyy-MM-dd HH:mm} 之后的证据；不计算改善百分比。");
        }
        if (clusters.Any(cluster => !cluster.CompleteSignature))
        {
            blindSpots.Add("部分 Application Error 1000 缺少进程、模块、异常码或偏移；这些记录分别保留，未将未知字段合并成同一故障。");
        }
        if (nonCore.Length > 160)
        {
            blindSpots.Add("时间线保留全部已采集异常关机/内核故障证据；睡眠恢复与变更仅展示最近160条，指标及聚类使用全部已采集记录。");
        }
        blindSpots.Add($"每日与热力图按窗口起点 {start:HH:mm} 对齐24小时桶，按真实事件时间统计；夏令时按UTC经过时长分桶。零值只在对应渠道覆盖完整时显示。");
        bool currentComplete = Complete(allChannels, start, end);
        bool previousComplete = Complete(allChannels, previousStart, start);
        bool systemComplete = Complete(["System"], start, end);
        bool applicationComplete = Complete(["Application"], start, end);
        string kernelEvidence = kernel > 0 ? $"观察到 {kernel} 条/组内核、硬件或异常关机相关记录。"
            : systemComplete && applicationComplete ? "零内核级故障记录：已覆盖渠道未见异常关机、蓝屏、WHEA、TDR、磁盘/文件系统异常；不等于硬件健康保证。"
            : "内核级故障数量未知：覆盖不足，不能将未观察到的记录解释为零故障。";
        int crashes = current.Count(IsCrash);
        int audio = current.Count(IsAudioCrash);
        var pairs = PairSleep(current);
        var dimensions = new List<WindowsStabilityDimension>
        {
            new("内核与硬件", kernel > 0 ? "需优先核实" : systemComplete && applicationComplete ? "已覆盖记录未见异常" : "证据不足", kernelEvidence),
            new("电源与睡眠", power.Length > 0 ? "异常关机证据" : systemComplete ? "观察" : "证据不足", $"已观察入睡 {sleep} 条、恢复 {wake} 条、可配对 {pairs.Pairs} 组、未配对 {pairs.Unmatched} 条；未配对可能来自边界或日志缺失。"),
            new("应用", crashes > 0 ? "存在崩溃" : applicationComplete ? "未观察到崩溃" : "证据不足", $"Application Error 1000 已观察 {crashes} 条；按进程×模块×异常码×偏移聚类。"),
            new("音频", audio > 0 ? "存在关联崩溃" : applicationComplete ? "未观察到音频崩溃" : "证据不足", $"已观察音频相关 Application Error 1000 共 {audio} 条；覆盖不完整时此数只是下界。未采集设备增强效果配置，不能归因到当前默认音频设备。"),
        };
        if (collection.Environment.TryGetValue("MinidumpTimesUtc", out string? dumpTimes))
        {
            int dumps = dumpTimes.Split('|', StringSplitOptions.RemoveEmptyEntries).Count(value => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) && time >= start && time < end);
            dimensions.Add(new("崩溃转储", "文件存在性观察", $"本期修改时间范围内的 Minidump 文件 {dumps} 个；未解析转储，文件时间不代表确切故障发生时间。"));
        }
        var actions = Actions(current, clusters, power.Length, kernel, currentComplete);
        string assessment = power.Length > 0 ? $"本期存在 {power.Length} 组非计划关机证据，先核实电源与崩溃链路；同时观察到 {crashes} 条应用崩溃记录。"
            : kernel > 0 ? $"本期观察到内核或硬件异常记录，应先核实对应事件与转储；另有 {crashes} 条应用崩溃记录。"
            : crashes > 0 ? $"本期主要暴露应用崩溃：共观察 {crashes} 条；{(systemComplete && applicationComplete ? "已覆盖记录为零内核级故障记录" : "内核稳定性因覆盖限制暂不能确认")}。"
            : currentComplete ? "已覆盖记录为零内核级故障记录，未观察到应用崩溃；保留当前状态，继续观察异常复现。"
                : "现有日志覆盖不足，暂不能判断本期系统是否稳定；先补齐可读取证据。";
        string machine = string.Join(" · ", collection.Environment.Where(item => !item.Key.StartsWith("Minidump", StringComparison.Ordinal)).Select(item => item.Key + ": " + item.Value));
        return new(assessment, kernel > 0 || crashes > 0 ? "需排查" : currentComplete ? "观察" : "证据不足", "以事件证据和覆盖范围评估，不从单一分数推导稳定性。", machine, start, end, previousStart, start,
            metrics, daily, timeline, clusters, heatmap, noise, dimensions, actions, blindSpots.Distinct(StringComparer.Ordinal).ToArray(), collection.Coverage)
        { CurrentComplete = currentComplete, PreviousComplete = previousComplete };
    }

    private static WindowsStabilityAction[] Actions(WindowsStabilityEvent[] current, WindowsStabilityCrashCluster[] clusters, int power, int kernel, bool complete)
    {
        var actions = new List<WindowsStabilityAction>();
        if (power > 0)
        {
            actions.Add(new("P0", "核实异常关机链路", $"本期 {power} 组41/6008异常关机证据；时间通常为重新启动时记录。",
                "对照 Kernel-Power 41 的 BugcheckCode、SleepInProgress、PowerButtonTimestamp 与6008；有转储则先保留分析。询问掉电前场景，记录当时供电、睡眠与负载，未确认前不直接改 BIOS。",
                "复现原使用/睡眠场景，保存新一轮电源与恢复记录；确认是否仍出现41/6008或蓝屏证据。"));
        }
        if (kernel > power)
        {
            actions.Add(new("P0", "定位内核、驱动或存储异常", "已观察到蓝屏/WHEA/TDR/磁盘或文件系统事件。",
                "按事件来源、ID、设备名和错误码定位组件；先备份重要数据并保留转储，针对确认的设备核对驱动/固件版本。不要仅凭事件号进行破坏性修复。",
                "重复触发场景并检查对应渠道的新事件与转储；变化必须有同口径证据支持。"));
        }
        foreach (var cluster in clusters.Where(item => item.Current > 0).Take(3))
        {
            actions.Add(new("P1", "复查 " + cluster.Process + " 崩溃", $"{cluster.Module} · {cluster.ExceptionCode} · 偏移 {cluster.Offset}，本期 {cluster.Current} 条记录。",
                "核对应用和故障模块版本，保留故障事件与现场；按原操作尝试复现。若为音频组件，记录播放/麦克风和增强效果状态，逐项对照，避免同时更改多个设置。",
                "用相同操作复测并收集 Application Error 1000；对比进程、模块、异常码、偏移是否一致。"));
        }
        if (!complete)
        {
            actions.Add(new("P1", "补齐日志覆盖", "部分渠道不可读、已滚动或达到采集上限。", "查看报告盲区与各渠道最早可用时间；确认日志启用和容量，下一周期保持同样窗口与渠道。", "确认下一份报告覆盖完整后再判断故障减少；当前未观察不能视为修复。"));
        }
        if (actions.Count == 0)
        {
            actions.Add(new("P2", "保持配置并持续观察", "本次已覆盖记录未暴露明确崩溃或内核异常。", "保留当前配置，出现异常时记录时间和操作场景；下周期使用相同渠道与窗口复查。", "以新事件和实际功能表现验证，不以评分或没有日志单独判断健康。"));
        }
        return actions.Take(5).ToArray();
    }

    private static WindowsStabilityTimeline Timeline(WindowsStabilityEvent item, bool current)
    {
        string kind = IsPower(item) ? "power" : IsSleep(item) ? "sleep" : IsWake(item) || IsResumeDuration(item) ? "wake" : IsChange(item) ? "change" : "kernel";
        string label = IsPower(item) ? "异常关机证据（启动时记录）" : IsSleep(item) ? "进入睡眠" : IsWake(item) ? "从睡眠恢复" : IsResumeDuration(item) ? "恢复耗时记录" : IsChange(item) ? "系统/软件变更" : "内核或硬件异常";
        string[] fields = ["BugcheckCode", "SleepInProgress", "PowerButtonTimestamp", "BootAppStatus", "SleepTime", "WakeTime", "ResumeDuration", "ResumeTime", "ServiceName", "ImagePath", "param1", "param2"];
        string detail = item.Provider + " / " + item.EventId.ToString(CultureInfo.InvariantCulture) + " · "
            + string.Join("; ", fields.Select(name => (Name: name, Value: Field(item, name))).Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Name + "=" + pair.Value));
        if (item.EventId == 6008)
        {
            detail += " · PreviousShutdownLocalTime=" + string.Join(" / ", item.Properties.Take(2));
        }
        return new(item.Time.ToLocalTime(), kind, label, detail, current);
    }

    private static (int Pairs, int Unmatched) PairSleep(IEnumerable<WindowsStabilityEvent> events)
    {
        DateTimeOffset? asleep = null;
        int pairs = 0;
        int unmatched = 0;
        foreach (var item in events.Where(item => IsSleep(item) || IsWake(item) || IsPower(item) || IsBoot(item)).OrderBy(item => item.Time))
        {
            if (IsPower(item) || IsBoot(item))
            {
                if (asleep is not null) unmatched++;
                asleep = null;
                continue;
            }
            if (IsSleep(item))
            {
                if (asleep is not null) unmatched++;
                asleep = item.Time;
            }
            else
            {
                if (asleep is { } start && item.Time - start <= TimeSpan.FromDays(1)) pairs++;
                else unmatched += asleep is null ? 1 : 2;
                asleep = null;
            }
        }
        return (pairs, unmatched + (asleep is null ? 0 : 1));
    }

    private static WindowsStabilityEvent[] PowerEvents(WindowsStabilityEvent[] events)
    {
        var primary = events.Where(item => IsPower(item) && item.EventId == 41).ToList();
        var secondary = events.Where(item => IsPower(item) && item.EventId == 6008).ToList();
        foreach (var power in primary)
        {
            var match = secondary.Where(item => (power.Time - item.Time).Duration() <= TimeSpan.FromMinutes(10)
                && !events.Any(boundary => IsBoot(boundary) && boundary.Time > (power.Time < item.Time ? power.Time : item.Time)
                    && boundary.Time < (power.Time > item.Time ? power.Time : item.Time)))
                .MinBy(item => (power.Time - item.Time).Duration());
            if (match is not null) secondary.Remove(match);
        }
        primary.AddRange(secondary);
        return primary.ToArray();
    }

    private static bool IsCrash(WindowsStabilityEvent item) => item.Channel == "Application" && item.EventId == 1000 && item.Provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase);
    private static bool IsAudioCrash(WindowsStabilityEvent item) => IsCrash(item) && (Signature(item).Process.Contains("audiodg", StringComparison.OrdinalIgnoreCase)
        || Signature(item).Module.Contains("nahimic", StringComparison.OrdinalIgnoreCase) || Signature(item).Module.Contains("audio", StringComparison.OrdinalIgnoreCase));
    private static bool IsPower(WindowsStabilityEvent item) => item.Channel == "System" && ((item.EventId == 41 && item.Provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase))
        || (item.EventId == 6008 && item.Provider.Equals("EventLog", StringComparison.OrdinalIgnoreCase)));
    private static bool IsSleep(WindowsStabilityEvent item) => item.Channel == "System" && item.EventId == 42 && item.Provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase);
    private static bool IsWake(WindowsStabilityEvent item) => item.Channel == "System" && item.EventId == 107 && item.Provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase);
    private static bool IsBoot(WindowsStabilityEvent item) => item.Channel == "System" && ((item.EventId == 12 && item.Provider.Equals("Microsoft-Windows-Kernel-General", StringComparison.OrdinalIgnoreCase))
        || (item.EventId == 6005 && item.Provider.Equals("EventLog", StringComparison.OrdinalIgnoreCase)));
    private static bool IsResumeDuration(WindowsStabilityEvent item) => item.Channel == "System" && item.EventId == 131 && item.Provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase);
    private static bool IsMsiSuccess(WindowsStabilityEvent item) => item.Channel == "Application" && item.Provider.Equals("MsiInstaller", StringComparison.OrdinalIgnoreCase)
        && (item.EventId == 11707 || (item.EventId == 1033 && int.TryParse(Field(item, "Status", "ErrorStatus", "InstallationStatus"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int status) && status == 0));
    private static bool IsChange(WindowsStabilityEvent item) => (item.Channel == "System" && item.EventId == 7045 && item.Provider.Equals("Service Control Manager", StringComparison.OrdinalIgnoreCase))
        || IsMsiSuccess(item)
        || (item.Channel == "System" && item.EventId == 19 && item.Provider.Equals("Microsoft-Windows-WindowsUpdateClient", StringComparison.OrdinalIgnoreCase));
    private static bool IsKernelFault(WindowsStabilityEvent item) => item.Channel == "System" &&
        ((item.EventId == 1001 && (item.Provider.Contains("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase) || item.Provider.Equals("BugCheck", StringComparison.OrdinalIgnoreCase)))
        || item.Provider.EndsWith("WHEA-Logger", StringComparison.OrdinalIgnoreCase)
        || (item.EventId == 4101 && item.Provider.Equals("Display", StringComparison.OrdinalIgnoreCase))
        || (item.EventId is 7 or 11 or 51 && item.Provider.Equals("disk", StringComparison.OrdinalIgnoreCase))
        || (item.EventId == 98 && item.Level is 1 or 2 or 3 && item.Provider.Contains("Ntfs", StringComparison.OrdinalIgnoreCase)));

    private static string Identity(WindowsStabilityEvent item) => item.Channel + "\0" + item.Provider + "\0" + item.RecordId?.ToString(CultureInfo.InvariantCulture)
        + "\0" + item.Time.ToString("O", CultureInfo.InvariantCulture) + "\0" + item.EventId.ToString(CultureInfo.InvariantCulture)
        + (item.RecordId is null ? "\0" + item.Message + "\0" + string.Join("|", item.Properties) : string.Empty);
    private static string Field(WindowsStabilityEvent item, params string[] names)
    {
        foreach (string name in names)
        {
            var match = item.Fields.FirstOrDefault(field => field.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value;
        }
        return string.Empty;
    }
    private static (string Process, string Module, string Code, string Offset, bool Complete) Signature(WindowsStabilityEvent item)
    {
        string Value(int index, params string[] names)
        {
            string value = Field(item, names);
            return !string.IsNullOrWhiteSpace(value) ? value : item.Properties.Count > index ? item.Properties[index] : string.Empty;
        }
        string process = Value(0, "AppName", "FaultingApplicationName");
        string module = Value(3, "ModuleName", "FaultingModuleName");
        string code = Value(6, "ExceptionCode");
        string offset = Value(7, "FaultingOffset", "FaultOffset");
        bool complete = new[] { process, module, code, offset }.All(value => !string.IsNullOrWhiteSpace(value));
        return (string.IsNullOrWhiteSpace(process) ? "未知进程" : process, string.IsNullOrWhiteSpace(module) ? "未知模块" : module,
            string.IsNullOrWhiteSpace(code) ? "未知异常码" : code, string.IsNullOrWhiteSpace(offset) ? "未知偏移" : offset, complete);
    }
    private static string CrashKey(WindowsStabilityEvent item)
    {
        var signature = Signature(item);
        return signature.Complete ? string.Join("\0", signature.Process, signature.Module, signature.Code, signature.Offset) : "incomplete\0" + Identity(item);
    }
}
