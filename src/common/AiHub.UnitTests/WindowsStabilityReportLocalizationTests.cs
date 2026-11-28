namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WindowsStabilityReportLocalizationTests
{
    private static readonly DateTimeOffset End = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void EnglishCoversEvidenceAssessmentsActionsAndBlindSpotsWithoutChangingCounts()
    {
        var crash = Event("Application", "Application Error", 1000, 1, new Dictionary<string, string>
        {
            ["AppName"] = "audiodg.exe", ["ModuleName"] = "audio.dll", ["ExceptionCode"] = "c0000005", ["FaultingOffset"] = "1234",
        });
        var faults = new[]
        {
            Event("System", "Microsoft-Windows-Kernel-Power", 41, 2),
            Event("System", "EventLog", 6008, 3) with { Properties = ["12:00", "2026-11-28"] },
            Event("System", "BugCheck", 1001, 4),
            Event("System", "Microsoft-Windows-Kernel-Power", 42, 5),
            Event("System", "Microsoft-Windows-Kernel-Power", 107, 6),
            Event("System", "Microsoft-Windows-Kernel-Power", 131, 7),
            Event("System", "Microsoft-Windows-DistributedCOM", 10016, 8),
            Event("System", "OtherProvider", 23, 9),
            crash,
            crash with { RecordId = 10, Fields = new Dictionary<string, string>() },
        };
        var changes = Enumerable.Range(0, 161).Select(index => Event("System", "Service Control Manager", 7045, 100 + index));
        var collection = Collection([.. faults, .. changes]) with
        {
            RequestedStart = End.AddDays(-12),
            BlindSpots =
            [
                "仅采集 System / Application / Setup 的 Critical、Error、Warning、Information；不读取 Security，不包含其他未启用的专用诊断渠道。",
                "事件日志不是连续硬件监控；电压、温度、瞬时供电、睡眠期间电池消耗与驱动内部状态没有实时证据。",
                "观察到104日志清除事件，但载荷缺少目标渠道；各采集渠道的连续覆盖均不能确认。",
                "硬件/BIOS 注册表证据不可读：UnauthorizedAccessException",
                "当前默认音频端点读取失败：COMException",
                "Minidump 目录不可读，不能以无文件证明无蓝屏：IOException",
                "Minidump 文件枚举达到1000个上限，文件计数仅为观察下界。",
                "音频端点是采集当时的默认设备标识，未读取增强效果和故障发生时设备；未查询固件厂商更新目录。显卡信息来自注册驱动项，不能保证正在加载；不推定 BIOS 已过期。",
                "缺少上次环境快照和设备绑定对照，BIOS、Realtek、Nahimic 等此前处置是否落实尚未验证；当前配置存在不等于变更已执行。",
            ],
        };
        var chinese = Analyze(collection);
        var english = WindowsStabilityReportLocalization.English(chinese);
        AssertEnglish(english);
        Assert.AreEqual("en-US", english.Language);
        StringAssert.Contains(english.Assessment, "1 unexpected-shutdown groups");
        Assert.AreEqual(chinese.Metrics.Single(item => item.Key == "power").Current, english.Metrics.Single(item => item.Key == "power").Current);
        Assert.AreEqual(chinese.Daily.Count, english.Daily.Count);
        Assert.AreEqual(chinese.Timeline.Count, english.Timeline.Count);
        Assert.AreEqual(chinese.CrashClusters.Count, english.CrashClusters.Count);
        Assert.IsTrue(english.Actions.Any(item => item.Title == "Investigate audiodg.exe crashes"));
        Assert.IsTrue(english.BlindSpots.Any(item => item.Contains("30-day", StringComparison.Ordinal)));

        foreach (var scenario in new[]
        {
            Collection([]),
            Collection([crash]),
            Collection([Event("System", "BugCheck", 1001, 100)]),
            Collection([]) with { Coverage = Collection([]).Coverage.Select(item => item with { Capped = true }).ToArray() },
            Collection([crash]) with { Coverage = Collection([]).Coverage.Select(item => item with { Capped = true }).ToArray() },
        })
        {
            AssertEnglish(WindowsStabilityReportLocalization.English(Analyze(scenario)));
        }

        var capped = Collection([]) with
        {
            Coverage = Collection([]).Coverage.Select((item, index) => item with
            {
                Capped = true,
                Limitation = index switch
                {
                    0 => "达到每渠道 50000 条上限，只保留最近观察。 最早可用日志为 2026-11-27 11:00，更早记录可能滚动丢失或日志尚未启用。 该渠道存在日志清除记录。",
                    1 => "无法完整读取：IOException 无法确认日志最早覆盖时间。 日志清除目标未知，连续覆盖不能确认。",
                    _ => "存在没有事件时间的记录，已跳过；时间分布不完整。",
                },
            }).ToArray(),
        };
        AssertEnglish(WindowsStabilityReportLocalization.English(Analyze(capped)));
    }

    [TestMethod]
    public void DynamicEvidenceNamesRemainVerbatimWhenTheyMatchReportVocabulary()
    {
        var crash = Event("Application", "Application Error", 1000, 1, new Dictionary<string, string>
        {
            ["AppName"] = "观察", ["ModuleName"] = "音频", ["ExceptionCode"] = "c0000005", ["FaultingOffset"] = "1234",
        });
        var english = WindowsStabilityReportLocalization.English(Analyze(Collection([crash])));
        Assert.AreEqual("观察", english.CrashClusters.Single().Process);
        Assert.AreEqual("音频", english.CrashClusters.Single().Module);
        Assert.AreEqual("Investigate 观察 crashes", english.Actions[0].Title);
        StringAssert.Contains(english.Actions[0].Reason, "音频");
        const string rawName = "观察 最早可用日志 上次异常关机本地时间字段：app.exe";
        var raw = Analyze(Collection([crash with { Fields = new Dictionary<string, string> { ["AppName"] = rawName, ["ModuleName"] = "raw.dll" } }]))
            with { Machine = "未配置音频设备", Timeline = [new(End, "change", "系统/软件变更", "ServiceName=" + rawName, true)] };
        var translated = WindowsStabilityReportLocalization.English(raw);
        Assert.AreEqual(raw.Machine, translated.Machine);
        Assert.AreEqual(raw.Timeline.Single().Detail, translated.Timeline.Single().Detail);
        Assert.AreEqual("Investigate " + rawName + " crashes", translated.Actions[0].Title);
    }

    [TestMethod]
    public async Task OneCollectionPersistsBothLanguagesAndSwitchingOrRepairingLanguageFilesNeverCollects()
    {
        using var fixture = new FixtureDirectory();
        int calls = 0;
        bool fail = false;
        var now = End;
        var storage = new WindowsStabilityReportStorage(fixture.RootPath, (_, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls++;
            if (fail)
            {
                throw new IOException("synthetic collection failure");
            }

            return Task.FromResult(Collection([]));
        }, () => now);
        var chinese = await storage.LoadAsync(false);
        var english = await storage.LoadAsync(false, language: "en-GB");
        Assert.AreEqual(1, calls);
        Assert.AreEqual(chinese.Id, english.Id);
        Assert.AreEqual("zh-CN", chinese.Language);
        Assert.AreEqual("en-US", english.Language);
        Assert.AreNotEqual(chinese.SourcePath, english.SourcePath);
        Assert.AreNotEqual(chinese.PngPath, english.PngPath);
        Assert.IsTrue(chinese.SourcePath.EndsWith("-zh.md", StringComparison.Ordinal));
        Assert.IsTrue(english.SourcePath.EndsWith("-en.md", StringComparison.Ordinal));
        Assert.IsTrue(english.PngPath.EndsWith("-en.png", StringComparison.Ordinal));
        AssertEnglish(english.Data);
        StringAssert.Contains(english.Markdown, "# Windows system stability weekly report");
        StringAssert.Contains(english.Markdown, "## Priority actions and verification");
        Assert.IsFalse(Regex.IsMatch(english.Markdown.Split("<!-- kit-windows-stability:v1 -->", StringSplitOptions.None)[0], "[\\u3400-\\u9fff]"));

        now = End.AddHours(1);
        File.Delete(english.SourcePath);
        var repaired = await WindowsStabilityReportStorage.LocalizeAsync(chinese with { RefreshFailed = true }, "fr-FR");
        Assert.AreEqual(1, calls);
        Assert.AreEqual(english.Id, repaired.Id);
        Assert.IsTrue(repaired.RefreshFailed);
        Assert.IsTrue(File.Exists(repaired.SourcePath));
        Assert.AreEqual(chinese.Id, (await WindowsStabilityReportStorage.LocalizeAsync(repaired, "zh-TW")).Id);
        Assert.AreEqual(chinese.Markdown, WindowsStabilityReportStorage.Read(chinese.SourcePath).Markdown);

        fail = true;
        var retained = await storage.LoadAsync(false, true, language: "en-US");
        Assert.AreEqual(2, calls);
        Assert.IsTrue(retained.RefreshFailed);
        Assert.AreEqual(english.Id, retained.Id);
        Assert.AreEqual(english.Markdown, retained.Markdown);
        Assert.AreEqual(chinese.Id, WindowsStabilityReportStorage.Read(chinese.SourcePath).Id);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => WindowsStabilityReportStorage.LocalizeAsync(chinese, "en-US", cancelled.Token));
        Assert.AreEqual(english.Markdown, WindowsStabilityReportStorage.Read(english.SourcePath).Markdown);
    }

    private static void AssertEnglish(WindowsStabilityReportData data)
    {
        var authored = new[] { data.Assessment, data.Grade, data.GradeBasis }
            .Concat(data.Metrics.SelectMany(item => new[] { item.Label, item.Unit, item.Qualification }))
            .Concat(data.Timeline.SelectMany(item => new[] { item.Label, item.Detail }))
            .Concat(data.Noise.Select(item => item.Classification))
            .Concat(data.Dimensions.SelectMany(item => new[] { item.Name, item.Status, item.Evidence }))
            .Concat(data.Actions.SelectMany(item => new[] { item.Title, item.Reason, item.Step, item.Verification }))
            .Concat(data.Coverage.Select(item => item.Limitation))
            .Concat(data.BlindSpots);
        foreach (string text in authored)
        {
            Assert.IsFalse(Regex.IsMatch(text, "[\\u3400-\\u9fff]"), text);
        }
    }

    private static WindowsStabilityReportData Analyze(WindowsStabilityCollection collection)
        => WindowsStabilityAnalyzer.Analyze(collection, End.AddDays(-7), End, End.AddDays(-14));

    private static WindowsStabilityCollection Collection(WindowsStabilityEvent[] events) => new(End.AddDays(-14), End, events,
        new[] { "System", "Application", "Setup" }.Select(channel => new WindowsStabilityCoverage(channel, End.AddDays(-14), End,
            End.AddDays(-40), true, false, false, events.Count(item => item.Channel == channel), string.Empty)).ToArray(),
        new Dictionary<string, string> { ["MinidumpTimesUtc"] = End.AddHours(-1).ToString("O"), ["MinidumpTotalFiles"] = "1" }, []);

    private static WindowsStabilityEvent Event(string channel, string provider, int id, long record, IReadOnlyDictionary<string, string>? fields = null)
        => new(channel, provider, id, record, End.AddHours(-1).AddSeconds(record), 2, "synthetic event", fields ?? new Dictionary<string, string>(), []);
}
