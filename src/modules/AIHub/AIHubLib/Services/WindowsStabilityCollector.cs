using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Kit.AIHubLib.Models;
using Microsoft.Win32;

namespace Kit.AIHubLib.Services;

/// <summary>Read-only collection for stability reports. Security is deliberately outside this scope.</summary>
public sealed class WindowsStabilityCollector
{
    public const int MaximumEventsPerChannel = 50000;
    private static readonly string[] Channels = ["System", "Application", "Setup"];

    public Task<WindowsStabilityCollection> CollectAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default) => Task.Run(() => Collect(start, end, cancellationToken), cancellationToken);

    private static WindowsStabilityCollection Collect(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        ManagedCommon.Logger.LogInfo($"Windows stability collection started: days={(end - start).TotalDays:F1}, channels=System,Application,Setup, maxPerChannel={MaximumEventsPerChannel}");
        var records = new List<WindowsStabilityEvent>();
        var coverage = new List<WindowsStabilityCoverage>();
        var blindSpots = new List<string>();
        foreach (string channel in Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset? oldest = null;
            int count = 0;
            bool succeeded = false;
            bool capped = false;
            string limitation = string.Empty;
            try
            {
                using (var firstReader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, "*")))
                using (var first = firstReader.ReadEvent())
                {
                    if (first?.TimeCreated is { } time)
                    {
                        oldest = new DateTimeOffset(time.ToUniversalTime());
                    }
                }
                string query = string.Create(CultureInfo.InvariantCulture,
                    $"*[System[(Level=1 or Level=2 or Level=3 or Level=4) and TimeCreated[@SystemTime >= '{start.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}' and @SystemTime < '{end.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}']]]");
                using var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, query) { ReverseDirection = true });
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var record = reader.ReadEvent();
                    if (record is null)
                    {
                        succeeded = true;
                        break;
                    }
                    if (count == MaximumEventsPerChannel)
                    {
                        capped = true;
                        succeeded = true;
                        limitation = $"达到每渠道 {MaximumEventsPerChannel} 条上限，只保留最近观察。";
                        break;
                    }
                    if (record.TimeCreated is not { } timestamp)
                    {
                        limitation = "存在没有事件时间的记录，已跳过；时间分布不完整。";
                        continue;
                    }
                    string xml = record.ToXml();
                    var fields = NamedFields(xml);
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch (EventLogException) { message = string.Empty; }
                    var properties = record.Properties.Take(40).Select(property => Limit(Convert.ToString(property.Value, CultureInfo.InvariantCulture) ?? string.Empty, 1024)).ToArray();
                    records.Add(new(channel, record.ProviderName ?? string.Empty, record.Id, record.RecordId,
                        new DateTimeOffset(timestamp.ToUniversalTime()), record.Level ?? 0, Limit(message, 700), fields, properties));
                    count++;
                }
            }
            catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException or System.Security.SecurityException or System.Xml.XmlException)
            {
                limitation = "无法完整读取：" + exception.GetType().Name;
            }
            bool rolled = oldest is null || oldest > start;
            if (rolled)
            {
                limitation = Join(limitation, oldest is null ? "无法确认日志最早覆盖时间。" : $"最早可用日志为 {oldest.Value.ToLocalTime():yyyy-MM-dd HH:mm}，更早记录可能滚动丢失或日志尚未启用。");
            }
            coverage.Add(new(channel, start, end, oldest, succeeded, capped, rolled, count, limitation));
            ManagedCommon.Logger.LogInfo($"Windows stability channel completed: channel={channel}, records={count}, readSucceeded={succeeded}, capped={capped}, coverageLimited={rolled || !string.IsNullOrWhiteSpace(limitation)}");
        }
        ApplyLogClearCoverage(records, coverage, blindSpots);
        var environment = CollectEnvironment(start, end, blindSpots);
        blindSpots.Add("仅采集 System / Application / Setup 的 Critical、Error、Warning、Information；不读取 Security，不包含其他未启用的专用诊断渠道。");
        blindSpots.Add("事件日志不是连续硬件监控；电压、温度、瞬时供电、睡眠期间电池消耗与驱动内部状态没有实时证据。");
        ManagedCommon.Logger.LogInfo($"Windows stability collection completed: records={records.Count}, elapsedMs={elapsed.ElapsedMilliseconds}");
        return new(start, end, records, coverage, environment, blindSpots);
    }

    internal static void ApplyLogClearCoverage(IEnumerable<WindowsStabilityEvent> records, List<WindowsStabilityCoverage> coverage, List<string> blindSpots)
    {
        foreach (var cleared in records.Where(item => item.Channel.Equals("System", StringComparison.OrdinalIgnoreCase) && item.EventId == 104
            && item.Provider.Equals("Microsoft-Windows-Eventlog", StringComparison.OrdinalIgnoreCase)))
        {
            string target = cleared.Fields.FirstOrDefault(field => field.Key.Equals("Channel", StringComparison.OrdinalIgnoreCase)).Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(target))
            {
                string file = cleared.Fields.FirstOrDefault(field => field.Key.Equals("LogFileName", StringComparison.OrdinalIgnoreCase)).Value?.Trim() ?? string.Empty;
                target = Path.GetFileNameWithoutExtension(file.Replace('/', '\\'));
            }
            bool unknown = string.IsNullOrWhiteSpace(target);
            if (unknown)
            {
                blindSpots.Add("观察到104日志清除事件，但载荷缺少目标渠道；各采集渠道的连续覆盖均不能确认。");
            }
            for (int index = 0; index < coverage.Count; index++)
            {
                if (unknown || target.Equals(coverage[index].Channel, StringComparison.OrdinalIgnoreCase))
                {
                    coverage[index] = coverage[index] with { RolledOver = true, Limitation = Join(coverage[index].Limitation,
                        unknown ? "日志清除目标未知，连续覆盖不能确认。" : "该渠道存在日志清除记录。") };
                }
            }
        }
    }

    public static IReadOnlyDictionary<string, string> NamedFields(string xml)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = XDocument.Parse(xml);
        foreach (var element in document.Descendants().Where(element => element.Name.LocalName is "EventData" or "UserData")
            .SelectMany(payload => payload.Descendants()).Where(element => !element.HasElements).Take(120))
        {
            string name = element.Attribute("Name")?.Value ?? element.Name.LocalName;
            if (!string.IsNullOrWhiteSpace(name) && element.Name.LocalName is not "Provider" and not "Execution")
            {
                fields.TryAdd(name, Limit(element.Value, 1024));
            }
        }
        return fields;
    }

    private static IReadOnlyDictionary<string, string> CollectEnvironment(DateTimeOffset start, DateTimeOffset end, List<string> blindSpots)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OS"] = Environment.OSVersion.VersionString,
            ["Architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        };
        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            foreach (string name in new[] { "SystemManufacturer", "SystemProductName", "BIOSVersion", "BIOSReleaseDate", "BaseBoardProduct" })
            {
                if (bios?.GetValue(name) is { } value)
                {
                    result[name] = value is string[] values ? string.Join("; ", values) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (cpu?.GetValue("ProcessorNameString") is string nameValue)
            {
                result["CPU"] = nameValue.Trim();
            }
            using var video = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            if (video is not null)
            {
                var adapters = new List<string>();
                foreach (string key in video.GetSubKeyNames().Take(32))
                {
                    using var adapter = video.OpenSubKey(key + @"\0000");
                    string? description = adapter?.GetValue("DriverDesc") as string;
                    string? version = adapter?.GetValue("DriverVersion") as string;
                    if (!string.IsNullOrWhiteSpace(description)) adapters.Add(description + " " + version);
                }
                if (adapters.Count > 0) result["RegisteredDisplayDrivers"] = string.Join("; ", adapters.Distinct(StringComparer.Ordinal).Take(6));
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            blindSpots.Add("硬件/BIOS 注册表证据不可读：" + exception.GetType().Name);
        }
        try
        {
            string render = Windows.Media.Devices.MediaDevice.GetDefaultAudioRenderId(Windows.Media.Devices.AudioDeviceRole.Default);
            string capture = Windows.Media.Devices.MediaDevice.GetDefaultAudioCaptureId(Windows.Media.Devices.AudioDeviceRole.Default);
            result["DefaultAudioRenderEndpoint"] = AudioName(render);
            result["DefaultAudioCaptureEndpoint"] = AudioName(capture);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            blindSpots.Add("当前默认音频端点读取失败：" + exception.GetType().Name);
        }
        string dumps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
        try
        {
            var files = Directory.EnumerateFiles(dumps, "*.dmp").Take(1001).ToArray();
            var times = files.Take(1000).Select(File.GetLastWriteTimeUtc).Where(time => time >= start.UtcDateTime && time < end.UtcDateTime).Order().ToArray();
            result["MinidumpTimesUtc"] = string.Join("|", times.Select(time => time.ToString("O", CultureInfo.InvariantCulture)));
            result["MinidumpTotalFiles"] = Math.Min(files.Length, 1000).ToString(CultureInfo.InvariantCulture);
            if (files.Length > 1000) blindSpots.Add("Minidump 文件枚举达到1000个上限，文件计数仅为观察下界。");
            result["MinidumpCoverage"] = "文件修改时间，仅观察 .dmp 文件存在；不读取转储内容，无法据此单独确认崩溃发生时间。";
        }
        catch (DirectoryNotFoundException)
        {
            result["MinidumpTimesUtc"] = string.Empty;
            result["MinidumpTotalFiles"] = "0";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            blindSpots.Add("Minidump 目录不可读，不能以无文件证明无蓝屏：" + exception.GetType().Name);
        }
        blindSpots.Add("音频端点是采集当时的默认设备标识，未读取增强效果和故障发生时设备；未查询固件厂商更新目录。显卡信息来自注册驱动项，不能保证正在加载；不推定 BIOS 已过期。");
        blindSpots.Add("缺少上次环境快照和设备绑定对照，BIOS、Realtek、Nahimic 等此前处置是否落实尚未验证；当前配置存在不等于变更已执行。");
        return result;
    }

    private static string AudioName(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return "未配置";
        var device = Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(identifier).AsTask().GetAwaiter().GetResult();
        return string.IsNullOrWhiteSpace(device?.Name) ? "已配置；无法取得设备名称" : device.Name;
    }

    private static string Limit(string text, int maximum) => text.Length <= maximum ? text : text[..maximum];
    private static string Join(string first, string second) => string.IsNullOrEmpty(first) ? second : first + " " + second;
}
