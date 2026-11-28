namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WindowsStabilityReportTests
{
    private static readonly DateTimeOffset End = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void EventTimeDedupAndCrashSignaturesDoNotUseScanDatesOrMergeMissingFields()
    {
        var crash = Event("Application", "Application Error", 1000, 1, End.AddDays(-1), 2, new Dictionary<string, string>
        { ["AppName"] = "app.exe", ["ModuleName"] = "module.dll", ["ExceptionCode"] = "c0000005", ["FaultingOffset"] = "1" });
        var otherOffset = crash with { RecordId = 2, Fields = new Dictionary<string, string>(crash.Fields) { ["FaultingOffset"] = "2" } };
        var missing = crash with { RecordId = 3, Fields = new Dictionary<string, string>() };
        var baseline = crash with { RecordId = 5, Time = End.AddDays(-9) };
        var data = Analyze([crash, crash, otherOffset, missing, missing with { RecordId = 4 }, baseline]);

        Assert.AreEqual(4, data.Metrics.Single(item => item.Key == "crashes").Current);
        Assert.AreEqual(1, data.Metrics.Single(item => item.Key == "crashes").Previous);
        Assert.AreEqual(4, data.CrashClusters.Count);
        Assert.AreEqual(2, data.CrashClusters.Count(item => !item.CompleteSignature));
        Assert.AreEqual(4, data.Daily.Where(item => item.Current).Sum(item => item.Crashes));
        Assert.AreEqual(14, data.Daily.Count);
        Assert.AreEqual(7 * 24, data.Heatmap.Count);
        Assert.IsFalse(data.CrashClusters.Any(item => item.Current > 1));
    }

    [TestMethod]
    public void ReportRetainsCrashSignaturesBeyondTheDashboardRanking()
    {
        var events = Enumerable.Range(0, 24).Select(index => Event("Application", "Application Error", 1000, index + 1, End.AddHours(-1), 2,
            new Dictionary<string, string>
            {
                ["AppName"] = "app.exe", ["ModuleName"] = "module.dll", ["ExceptionCode"] = "c0000005", ["FaultingOffset"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })).ToArray();
        var data = Analyze(events);
        Assert.AreEqual(24, data.CrashClusters.Count, "The formal document must not inherit the dashboard's top-N limit.");
        Assert.AreEqual(24, data.CrashClusters.Sum(item => item.Current));
        Assert.AreEqual(24, data.Metrics.Single(item => item.Key == "crashes").Current);
        Assert.IsFalse(data.BlindSpots.Any(item => item.Contains("省略", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ExtremeCrashSignatureCountsDiscloseTheExactDocumentOmissionWithoutChangingTotals()
    {
        var events = Enumerable.Range(0, 2050).Select(index => Event("Application", "Application Error", 1000, index + 1, End.AddHours(-1), 2,
            new Dictionary<string, string>
            {
                ["AppName"] = "app.exe", ["ModuleName"] = "module.dll", ["ExceptionCode"] = "c0000005", ["FaultingOffset"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })).ToArray();
        var data = Analyze(events);
        Assert.AreEqual(2048, data.CrashClusters.Count);
        Assert.AreEqual(2050, data.Metrics.Single(item => item.Key == "crashes").Current);
        Assert.AreEqual(2050, data.Daily.Where(item => item.Current).Sum(item => item.Crashes));
        Assert.IsTrue(data.BlindSpots.Any(item => item.Contains("2050 个签名", StringComparison.Ordinal) && item.Contains("省略 2 个", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void IncompleteChannelsKeepPositiveLowerBoundsButNeverInventHealthyZero()
    {
        var crash = Event("Application", "Application Error", 1000, 1, End.AddHours(-1), 2);
        var collection = Collection([crash]);
        collection = collection with { Coverage = collection.Coverage.Select(item => item with { ReadSucceeded = false, Limitation = "partially read then denied" }).ToArray() };
        var data = WindowsStabilityAnalyzer.Analyze(collection, End.AddDays(-7), End, End.AddDays(-14));

        Assert.AreEqual(1, data.Metrics.Single(item => item.Key == "crashes").Current);
        Assert.IsNull(data.Metrics.Single(item => item.Key == "kernel").Current);
        Assert.IsNull(data.Metrics.Single(item => item.Key == "crashes").Previous);
        Assert.IsFalse(data.CurrentComplete);
        Assert.IsFalse(data.Assessment.Contains("零内核级故障", StringComparison.Ordinal));
        Assert.AreEqual("证据不足", data.Dimensions.Single(item => item.Name == "音频").Status);
    }

    [TestMethod]
    public void ProviderAndLevelQualificationsSeparateBenignNtfsAndWerFromKernelFaults()
    {
        var data = Analyze([
            Event("System", "Microsoft-Windows-Ntfs", 98, 1, End.AddHours(-1), 4),
            Event("Application", "Windows Error Reporting", 1001, 2, End.AddHours(-1), 2),
            Event("System", "Other-Kernel-Power", 41, 3, End.AddHours(-1), 1),
            Event("Application", "Microsoft-Windows-Kernel-Power", 42, 4, End.AddHours(-1), 4),
        ]);
        Assert.AreEqual(0, data.Metrics.Single(item => item.Key == "kernel").Current);
        Assert.AreEqual(0, data.Metrics.Single(item => item.Key == "sleep42").Current);
        Assert.AreEqual(0, data.Metrics.Single(item => item.Key == "bugcheck").Current);
        Assert.AreEqual(0, data.Metrics.Single(item => item.Key == "ntfs98").Current);
        StringAssert.Contains(data.Assessment, "零内核级故障记录");
        Assert.IsFalse(data.Actions.Any(item => item.Priority == "P0"));
    }

    [TestMethod]
    public void PowerEvidencePairsOneToOneAcrossPeriodBoundaryAndInterruptsSleepPairing()
    {
        var start = End.AddDays(-7);
        var data = Analyze([
            Event("System", "Microsoft-Windows-Kernel-Power", 41, 1, start.AddMinutes(-1), 1),
            Event("System", "EventLog", 6008, 2, start.AddMinutes(1), 2),
            Event("System", "EventLog", 6008, 3, start.AddMinutes(2), 2),
            Event("System", "Microsoft-Windows-Kernel-Power", 42, 4, End.AddHours(-5), 4),
            Event("System", "Microsoft-Windows-Kernel-Power", 41, 5, End.AddHours(-4), 1),
            Event("System", "Microsoft-Windows-Kernel-Power", 107, 6, End.AddHours(-3), 4),
        ]);
        Assert.AreEqual(2, data.Metrics.Single(item => item.Key == "power").Current);
        Assert.AreEqual(1, data.Metrics.Single(item => item.Key == "power").Previous);
        Assert.AreEqual(0, data.Metrics.Single(item => item.Key == "sleepPairs").Current);
        Assert.AreEqual(2, data.Metrics.Single(item => item.Key == "sleepUnmatched").Current);
        Assert.AreEqual(2, data.Metrics.Single(item => item.Key == "power6008").Current);
    }

    [TestMethod]
    public void NamedXmlFieldsAndCoreMetricsRetainStructuredEvidence()
    {
        var fields = WindowsStabilityCollector.NamedFields("<Event><EventData><Data Name='AppName'>audio.exe</Data><Data Name='BugcheckCode'>159</Data></EventData><UserData><ResumeDuration>367</ResumeDuration></UserData></Event>");
        Assert.AreEqual("audio.exe", fields["AppName"]);
        Assert.AreEqual("159", fields["BugcheckCode"]);
        Assert.AreEqual("367", fields["ResumeDuration"]);
        var data = Analyze([Event("System", "Microsoft-Windows-Kernel-Power", 131, 1, End.AddHours(-1), 4, fields)]);
        Assert.AreEqual(367, data.Metrics.Single(item => item.Key == "resumeDuration").Current);
        CollectionAssert.IsSubsetOf(new[] { "bugcheck", "whea", "tdr", "disk7", "disk11", "disk51", "ntfs98", "audio", "minidumps", "minidumpsTotal", "scm7045", "msi", "wu19" }, data.Metrics.Select(item => item.Key).ToArray());
    }

    [TestMethod]
    public void LogClearPayloadChannelIsNotShadowedByTheSystemEnvelope()
    {
        var fields = WindowsStabilityCollector.NamedFields("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><EventID>104</EventID><Channel>System</Channel></System><UserData><LogFileCleared xmlns='http://manifests.microsoft.com/win/2004/08/windows/eventlog'><Channel>Application</Channel><SubjectUserName>test</SubjectUserName></LogFileCleared></UserData></Event>");
        Assert.AreEqual("Application", fields["Channel"]);
        Assert.IsFalse(fields.ContainsKey("EventID"), "System metadata must not override identically named event payload fields.");
    }

    [TestMethod]
    public void LogClearCoverageUsesTheExactTargetAndUnknownTargetsRemainExplicit()
    {
        var coverage = Collection([]).Coverage.ToList();
        var blindSpots = new List<string>();
        var clear = Event("System", "Microsoft-Windows-Eventlog", 104, 1, End.AddHours(-1), 4,
            new Dictionary<string, string> { ["Channel"] = "Application" });
        WindowsStabilityCollector.ApplyLogClearCoverage([clear], coverage, blindSpots);
        Assert.IsTrue(coverage.Single(item => item.Channel == "Application").RolledOver);
        Assert.IsFalse(coverage.Single(item => item.Channel == "System").RolledOver, "The channel carrying event 104 is not necessarily the cleared channel.");
        Assert.IsFalse(coverage.Single(item => item.Channel == "Setup").RolledOver);

        coverage = Collection([]).Coverage.ToList();
        WindowsStabilityCollector.ApplyLogClearCoverage([clear with { Fields = new Dictionary<string, string> { ["Channel"] = "CustomSystem" } }], coverage, blindSpots);
        Assert.IsTrue(coverage.All(item => !item.RolledOver), "Substring channel names must not downgrade unrelated collected logs.");

        WindowsStabilityCollector.ApplyLogClearCoverage([clear with { Fields = new Dictionary<string, string> { ["LogFileName"] = @"C:\Windows\System32\winevt\Logs\Setup.evtx" } }], coverage, blindSpots);
        Assert.IsTrue(coverage.Single(item => item.Channel == "Setup").RolledOver);
        Assert.IsFalse(coverage.Single(item => item.Channel == "System").RolledOver);

        coverage = Collection([]).Coverage.ToList();
        WindowsStabilityCollector.ApplyLogClearCoverage([clear with { Fields = new Dictionary<string, string>() }], coverage, blindSpots);
        Assert.IsTrue(coverage.All(item => item.RolledOver));
        Assert.IsTrue(blindSpots.Any(item => item.Contains("缺少目标渠道", StringComparison.Ordinal)));
        var data = WindowsStabilityAnalyzer.Analyze(Collection([]) with { Coverage = coverage, BlindSpots = blindSpots }, End.AddDays(-7), End, End.AddDays(-14));
        Assert.IsFalse(data.CurrentComplete);
        Assert.IsNull(data.Metrics.Single(item => item.Key == "kernel").Current);
    }

    [TestMethod]
    public void MsiResultEventRequiresExplicitSuccessfulStatus()
    {
        var data = Analyze([
            Event("Application", "MsiInstaller", 1033, 1, End.AddHours(-1), 4, new Dictionary<string, string> { ["Status"] = "1603" }),
            Event("Application", "MsiInstaller", 1033, 2, End.AddHours(-1), 4, new Dictionary<string, string> { ["Status"] = "0" }),
            Event("Application", "MsiInstaller", 1033, 3, End.AddHours(-1), 4),
        ]);
        Assert.AreEqual(1, data.Metrics.Single(item => item.Key == "msi").Current);
        Assert.AreEqual(1, data.Timeline.Count(item => item.Kind == "change"));
    }

    [TestMethod]
    public async Task MarkdownOwnsVersionedPngDataAndFreshCacheAvoidsCollectorWhileForcedFailurePreservesOldDocument()
    {
        using var fixture = new FixtureDirectory();
        int calls = 0;
        bool fail = false;
        var storage = new WindowsStabilityReportStorage(fixture.RootPath, (start, end, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls++;
            if (fail)
            {
                throw new IOException("synthetic failure");
            }

            return Task.FromResult(Collection([]));
        }, () => End);
        var first = await storage.LoadAsync(false);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(File.Exists(first.SourcePath));
        StringAssert.Contains(first.PngPath, first.Id);
        var read = WindowsStabilityReportStorage.Read(first.SourcePath);
        Assert.AreEqual(first.Id, read.Id);
        Assert.AreEqual(first.Markdown, read.Markdown);
        Assert.AreEqual(first.Data.Metrics.Count, read.Data.Metrics.Count);
        Assert.AreEqual(first.Id, (await storage.LoadAsync(false)).Id);
        Assert.AreEqual(1, calls);
        var refreshed = await storage.LoadAsync(false, true);
        Assert.AreEqual(2, calls);
        Assert.AreNotEqual(first.Id, refreshed.Id);
        fail = true;
        var preserved = await storage.LoadAsync(false, true);
        Assert.IsTrue(preserved.RefreshFailed);
        Assert.AreEqual(refreshed.Id, preserved.Id);
        Assert.AreEqual(refreshed.Markdown, File.ReadAllText(refreshed.SourcePath).Replace("\r\n", "\n", StringComparison.Ordinal));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => storage.LoadAsync(false, true, cancellationToken: cancelled.Token));
    }

    [TestMethod]
    public void CoreIncidentEvidenceSurvivesChangeFloodAndMsiFailuresAreNotSuccessfulChanges()
    {
        var power = Event("System", "Microsoft-Windows-Kernel-Power", 41, 1, End.AddDays(-6), 1,
            new Dictionary<string, string> { ["BugcheckCode"] = "159", ["SleepInProgress"] = "1" });
        var changes = Enumerable.Range(0, 200).Select(index => Event("System", "Service Control Manager", 7045, index + 10, End.AddHours(-1).AddSeconds(index), 4));
        var failedMsi = Event("Application", "MsiInstaller", 11708, 300, End.AddMinutes(-1), 2);
        var data = Analyze([power, .. changes, failedMsi]);
        Assert.AreEqual(161, data.Timeline.Count);
        StringAssert.Contains(data.Timeline.Single(item => item.Kind == "power").Detail, "BugcheckCode=159");
        StringAssert.Contains(data.Timeline.Single(item => item.Kind == "power").Detail, "SleepInProgress=1");
        Assert.AreEqual(200, data.Metrics.Single(item => item.Key == "scm7045").Current);
        Assert.AreEqual(0, data.Metrics.Single(item => item.Key == "msi").Current);
        Assert.IsFalse(data.Timeline.Any(item => item.Detail.Contains("11708", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task MonthUsesLocalMonthBoundaryAndRetentionTruncatesOnlyCollectionNotClaimedBaseline()
    {
        using var fixture = new FixtureDirectory();
        DateTimeOffset requested = default;
        var storage = new WindowsStabilityReportStorage(fixture.RootPath, (start, end, _) =>
        {
            requested = start;
            return Task.FromResult(Collection([]) with
            {
                RequestedStart = start,
                End = end,
                Coverage = Collection([]).Coverage.Select(item => item with { RequestedStart = start, RequestedEnd = end }).ToArray(),
            });
        }, () => End);
        var document = await storage.LoadAsync(true);
        var localMonth = new DateTime(End.ToLocalTime().Year, End.ToLocalTime().Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        Assert.AreEqual(new DateTimeOffset(localMonth, TimeZoneInfo.Local.GetUtcOffset(localMonth)).UtcDateTime, document.StartUtc);
        Assert.AreEqual(End.AddDays(-30), requested);
        Assert.IsFalse(document.Data.PreviousComplete);
        Assert.IsTrue(document.Data.BlindSpots.Any(item => item.Contains("30天", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task IncompleteRefreshRetainsThePreviousCompleteMarkdownVersion()
    {
        using var fixture = new FixtureDirectory();
        bool limited = false;
        var storage = new WindowsStabilityReportStorage(fixture.RootPath, (_, _, _) => Task.FromResult(limited
            ? Collection([]) with { Coverage = Collection([]).Coverage.Select(item => item with { Capped = true }).ToArray() }
            : Collection([])), () => End);
        var original = await storage.LoadAsync(false);
        limited = true;
        var refreshed = await storage.LoadAsync(false, true);
        Assert.IsTrue(refreshed.RefreshFailed);
        Assert.AreEqual(original.Id, refreshed.Id);
        Assert.AreEqual(original.Markdown, WindowsStabilityReportStorage.Read(original.SourcePath).Markdown);
    }

    [TestMethod]
    public async Task CleanupDeletesOnlyExpiredGeneratedReportNamesAndKeepsLegacyAuditExports()
    {
        using var fixture = new FixtureDirectory();
        string id = Guid.NewGuid().ToString("N");
        string[] expired =
        [
            "stability-weekly-2026-09-01.md",
            $"stability-weekly-2026-09-01-{id}.png",
            $"stability-weekly-2026-09-01-{id}-light-v2.png",
            $"stability-weekly-2026-09-01-{id}-dark-v2.png",
            $"stability-monthly-2026-09-01-{id}-dark-v3.png",
            $"stability-weekly-2026-09-01-{id}-zh.md",
            $"stability-monthly-2026-09-01-{id}-en.md",
            $"stability-weekly-2026-09-01-{id}-zh-light-v3.png",
            $"stability-monthly-2026-09-01-{id}-en-dark-v3.png",
        ];
        string[] preserved =
        [
            "weekly-2026-09-01.md",
            "stability-weekly-not-a-date.md",
            $"stability-weekly-2026-11-28-{id}-light-v2.png",
            $"stability-monthly-2026-11-28-{id}-dark-v2.png",
            $"stability-weekly-2026-09-01-{id}-light-v2-copy.png",
            $"stability-weekly-2026-09-01-{id}-light-vunknown.png",
            $"stability-weekly-2026-09-01-{id}-sepia-v2.png",
            "stability-weekly-2026-09-01-not-a-report-id-light-v2.png",
            $"stability-weekly-2026-11-28-{id}-zh.md",
            $"stability-monthly-2026-11-28-{id}-en-light-v3.png",
            $"stability-weekly-2026-09-01-{id}-fr.md",
            $"stability-weekly-2026-09-01-{id}-english-dark-v3.png",
            $"stability-weekly-2026-09-01-{id}-zh.md.bak",
        ];
        foreach (string name in expired.Concat(preserved))
        {
            File.WriteAllText(Path.Combine(fixture.RootPath, name), "retained marker");
        }

        var storage = new WindowsStabilityReportStorage(fixture.RootPath, (_, _, _) => Task.FromResult(Collection([])), () => End);
        await storage.LoadAsync(false);
        foreach (string name in expired)
        {
            Assert.IsFalse(File.Exists(Path.Combine(fixture.RootPath, name)), name);
        }

        foreach (string name in preserved)
        {
            Assert.IsTrue(File.Exists(Path.Combine(fixture.RootPath, name)), name);
        }
    }

    private static WindowsStabilityReportData Analyze(WindowsStabilityEvent[] events) => WindowsStabilityAnalyzer.Analyze(Collection(events), End.AddDays(-7), End, End.AddDays(-14));
    private static WindowsStabilityCollection Collection(WindowsStabilityEvent[] events) => new(End.AddDays(-14), End, events,
        new[] { "System", "Application", "Setup" }.Select(channel => new WindowsStabilityCoverage(channel, End.AddDays(-14), End, End.AddDays(-40), true, false, false,
            events.Count(item => item.Channel == channel), string.Empty)).ToArray(), new Dictionary<string, string>(), []);
    private static WindowsStabilityEvent Event(string channel, string provider, int id, long record, DateTimeOffset time, int level, IReadOnlyDictionary<string, string>? fields = null) =>
        new(channel, provider, id, record, time, level, "synthetic event", fields ?? new Dictionary<string, string>(), []);
}
