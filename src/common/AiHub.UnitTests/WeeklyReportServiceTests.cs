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
public sealed class WeeklyReportServiceTests
{
    private static readonly DateTimeOffset End = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task FastReusesSavedSevenDaysWithoutCollectingOrReadingModelConfiguration()
    {
        using var fixture = new Fixture();
        await fixture.Scans.SaveAsync(Audit(End), [Raw(End.AddHours(-1)), Raw(End.AddHours(-1)) with { LogName = "Security" }]);
        using var service = new WeeklyReportService(fixture.Storage, fixture.Scans,
            collect: (_, _, _) => throw new AssertFailedException("Fast collected events."),
            review: (_, _, _) => throw new AssertFailedException("Fast invoked AI."),
            identity: _ => throw new AssertFailedException("Fast initialized AI configuration."), utcNow: () => End);
        var report = await service.LoadAsync(WeeklyReportMode.Fast, false, End, "zh-CN");
        Assert.AreEqual(WeeklyReportMode.Fast, report.Data.WeeklySource!.Mode);
        Assert.AreEqual(1, report.Data.Metrics.Single(item => item.Key == "total").Current);
        Assert.AreEqual(End.UtcDateTime, report.EndUtc);
        Assert.IsFalse(report.Data.CurrentComplete);
        Assert.IsFalse(report.Data.PreviousComplete);
        Assert.AreEqual(0, report.Data.CrashClusters.Count);
        StringAssert.Contains(report.Markdown, "本次未采集日志或调用模型");
    }

    [TestMethod]
    public async Task FullCollectsExactlySevenDaysAndPassesSavedScanToIndependentReview()
    {
        using var fixture = new Fixture();
        await fixture.Scans.SaveAsync(Audit(End.AddMinutes(-15)), [Raw(End.AddHours(-1))]);
        int collections = 0;
        int reviews = 0;
        var service = fixture.Service((start, end, _) =>
        {
            collections++;
            Assert.AreEqual(End.AddDays(-7), start.ToUniversalTime());
            Assert.AreEqual(End, end.ToUniversalTime());
            return Task.FromResult(Collection(start, end));
        }, (input, _, _) =>
        {
            reviews++;
            Assert.AreEqual(WeeklyReportMode.Full, input.Mode);
            Assert.IsNotNull(input.Scan);
            Assert.AreEqual(End.AddMinutes(-15).UtcDateTime, input.Scan.Audit.ScanEnd);
            Assert.AreEqual(1, input.Events.Count);
            Assert.IsFalse(input.Data.PreviousComplete);
            return Task.FromResult(Review());
        });
        var report = await service.LoadAsync(WeeklyReportMode.Full, false, End, "en-US");
        Assert.AreEqual(1, collections);
        Assert.AreEqual(1, reviews);
        Assert.IsTrue(report.Data.CurrentComplete);
        Assert.AreEqual("Independent English assessment", report.Data.Assessment);
        Assert.AreEqual("English facts", report.Data.WeeklyReview!.Findings[0].FactsEn);
        StringAssert.Contains(report.Markdown, "English verification");
        StringAssert.Contains(report.Markdown, "event-group-1");
        StringAssert.Contains(report.Markdown, "host-retained fixture sample");
        var chinese = await WindowsStabilityReportStorage.LocalizeAsync(report, "zh-CN");
        Assert.AreEqual(report.Id, chinese.Id);
        Assert.AreEqual("独立中文结论", chinese.Data.Assessment);
    }

    [TestMethod]
    public async Task FastMissingScanDoesNotSilentlyCollectOrInvokeAi()
    {
        using var fixture = new Fixture();
        var service = fixture.Service((_, _, _) => throw new AssertFailedException("Unexpected collection."));
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.LoadAsync(WeeklyReportMode.Fast, false, End, "en-US"));
        StringAssert.Contains(exception.Message, "Select Full");
    }

    [TestMethod]
    public async Task ModeAndConfigurationCacheIdentityAreIndependent()
    {
        using var fixture = new Fixture();
        await fixture.Scans.SaveAsync(Audit(End), [Raw(End.AddHours(-1))]);
        int reviews = 0;
        string identity = "astra:max:context-a:policy-a";
        var service = fixture.Service(review: (_, _, _) =>
        {
            reviews++;
            return Task.FromResult(Review());
        }, identity: _ => Task.FromResult(identity));
        var fast = await service.LoadAsync(WeeklyReportMode.Fast, false, End, "zh-CN");
        var full = await service.LoadAsync(WeeklyReportMode.Full, false, End, "zh-CN");
        var cached = await service.LoadAsync(WeeklyReportMode.Full, false, End, "en-US");
        Assert.AreEqual(full.Id, cached.Id);
        Assert.AreNotEqual(fast.Id, full.Id);
        Assert.AreEqual(1, reviews);
        identity = "astra:high:context-b:policy-a";
        var changed = await service.LoadAsync(WeeklyReportMode.Full, false, End, "zh-CN");
        Assert.AreEqual(2, reviews);
        Assert.AreNotEqual(full.Id, changed.Id);
        Assert.AreEqual(fast.Id, (await fixture.Storage.ReadCacheAsync(WeeklyReportMode.Fast, End))!.Id);
    }

    [TestMethod]
    public async Task FailedCancelledAndDegradedRefreshesPreserveCompletePublishedReport()
    {
        using var fixture = new Fixture();
        var original = await fixture.Service().LoadAsync(WeeklyReportMode.Full, false, End, "zh-CN");
        var failedService = fixture.Service(review: (_, _, _) => throw new InvalidDataException("Incomplete AI response."));
        var failed = await failedService.LoadAsync(WeeklyReportMode.Full, true, End, "zh-CN");
        Assert.AreEqual(original.Id, failed.Id);
        Assert.IsTrue(failed.RefreshFailed);
        var cancelledService = fixture.Service(review: (_, _, token) => throw new OperationCanceledException(token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => cancelledService.LoadAsync(WeeklyReportMode.Full, true, End, "zh-CN"));
        var partialService = fixture.Service((start, end, _) => Task.FromResult(Collection(start, end) with
        {
            Coverage = Collection(start, end).Coverage.Select(item => item with { ReadSucceeded = false }).ToArray(),
        }));
        var partial = await partialService.LoadAsync(WeeklyReportMode.Full, true, End, "zh-CN");
        Assert.AreEqual(original.Id, partial.Id);
        Assert.IsTrue(partial.RefreshFailed);
        Assert.AreEqual(original.Id, (await fixture.Storage.ReadCacheAsync(WeeklyReportMode.Full, End))!.Id);
    }

    [TestMethod]
    public async Task ConfigurationChangeDuringCollectionStopsBeforeCallingAiOrPublishingReport()
    {
        using var fixture = new Fixture();
        string identity = "configuration-a";
        int reviews = 0;
        var service = fixture.Service((start, end, _) =>
        {
            identity = "configuration-b";
            return Task.FromResult(Collection(start, end));
        }, (_, _, _) =>
        {
            reviews++;
            return Task.FromResult(Review());
        }, _ => Task.FromResult(identity));

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.LoadAsync(WeeklyReportMode.Full, true, End, "en-US"));
        StringAssert.Contains(exception.Message, "configuration or policy changed");
        Assert.AreEqual(0, reviews);
        Assert.IsNull(await fixture.Storage.ReadCacheAsync(WeeklyReportMode.Full, End));
    }

    [TestMethod]
    public async Task ConfigurationChangeDuringReviewPreservesOldDocumentAndItsOriginalIdentity()
    {
        using var fixture = new Fixture();
        string identity = "configuration-a";
        var initialService = fixture.Service(identity: _ => Task.FromResult(identity));
        var original = await initialService.LoadAsync(WeeklyReportMode.Full, false, End, "en-US");
        int reviews = 0;
        var changedService = fixture.Service(review: (_, _, _) =>
        {
            reviews++;
            identity = "configuration-b";
            return Task.FromResult(Review() with { UsedModel = "different-model" });
        }, identity: _ => Task.FromResult(identity));

        var retained = await changedService.LoadAsync(WeeklyReportMode.Full, true, End, "en-US");
        Assert.AreEqual(1, reviews);
        Assert.IsTrue(retained.RefreshFailed);
        Assert.AreEqual(original.Id, retained.Id);
        Assert.AreEqual("configuration-a", retained.Data.WeeklySource!.AnalysisIdentity);
        Assert.AreEqual("gpt-6-astra", retained.Data.WeeklySource.UsedModel);
        var published = await fixture.Storage.ReadCacheAsync(WeeklyReportMode.Full, End);
        Assert.AreEqual(original.Id, published!.Id);
        Assert.AreEqual("configuration-a", published.Data.WeeklySource!.AnalysisIdentity);
    }

    [TestMethod]
    public async Task ReviewSeveritiesMapToOrderedDisplayPrioritiesWithoutRewritingAiEvidence()
    {
        using var fixture = new Fixture();
        var review = Review();
        var finding = review.Findings[0];
        review = review with
        {
            Findings = [finding with { Priority = "Low" }, finding with { Priority = "High" }, finding with { Priority = "Medium" }],
        };
        var service = fixture.Service(review: (_, _, _) => Task.FromResult(review));
        var document = await service.LoadAsync(WeeklyReportMode.Full, false, End, "en-US");

        CollectionAssert.AreEqual(new[] { "P0", "P1", "P2" }, document.Data.Actions.Select(item => item.Priority).ToArray());
        CollectionAssert.AreEqual(new[] { "Low", "High", "Medium" }, document.Data.WeeklyReview!.Findings.Select(item => item.Priority).ToArray());
        var chinese = await WindowsStabilityReportStorage.LocalizeAsync(document, "zh-CN");
        CollectionAssert.AreEqual(new[] { "P0", "P1", "P2" }, chinese.Data.Actions.Select(item => item.Priority).ToArray());
    }

    [TestMethod]
    public async Task PreviousPeriodUsesMatchingSavedFullReportWithoutFourteenDayCollection()
    {
        using var fixture = new Fixture();
        var service = fixture.Service((start, end, _) =>
        {
            Assert.AreEqual(TimeSpan.FromDays(7), end - start);
            return Task.FromResult(Collection(start, end));
        });
        await service.LoadAsync(WeeklyReportMode.Full, false, End.AddDays(-7), "zh-CN");
        var report = await service.LoadAsync(WeeklyReportMode.Full, false, End, "zh-CN");
        Assert.IsTrue(report.Data.PreviousComplete);
        Assert.AreEqual(1, report.Data.Metrics.Single(item => item.Key == "total").Previous);
        Assert.AreEqual(7, report.Data.Daily.Count(item => !item.Current));
    }

    [TestMethod]
    public async Task PreviousTopTenOmissionIsUnknownWhenTheEleventhGroupBecomesCurrent()
    {
        using var fixture = new Fixture();
        var service = fixture.Service((start, end, _) =>
        {
            var items = new List<WindowsStabilityEvent>();
            bool previous = end.ToUniversalTime() == End.AddDays(-7);
            for (int provider = previous ? 0 : 10; provider <= 10; provider++)
            {
                int count = previous ? provider < 10 ? 2 : 1 : 3;
                for (int index = 0; index < count; index++)
                {
                    items.Add(new("System", "Provider" + provider, 42, items.Count + 1,
                        end.AddHours(-1), 3, "fixture warning", new Dictionary<string, string>(), []));
                }
            }

            return Task.FromResult(Collection(start, end) with { Events = items });
        });
        await service.LoadAsync(WeeklyReportMode.Full, false, End.AddDays(-7), "en-US");
        var current = await service.LoadAsync(WeeklyReportMode.Full, false, End, "en-US");
        Assert.IsTrue(current.Data.PreviousComplete, "Complete total counts are still a valid baseline.");
        var newlyRanked = current.Data.Noise.Single(item => item.Provider == "Provider10");
        Assert.AreEqual(3, newlyRanked.Current);
        Assert.IsFalse(newlyRanked.PreviousKnown, "A missing retained ranking entry does not prove zero events.");
        StringAssert.Contains(current.Markdown, "| Provider10/42 | 3 | Unknown |");
        Assert.AreEqual(21, current.Data.Metrics.Single(item => item.Key == "total").Previous);
    }

    [TestMethod]
    public async Task SnapshotRejectsOtherDurationsFutureCutoffsAndOtherDates()
    {
        using var fixture = new Fixture();
        var audit = Audit(End);
        audit.ScanStart = End.AddDays(-2).UtcDateTime;
        await fixture.Scans.SaveAsync(audit, []);
        Assert.IsNull(await fixture.Scans.LoadAsync(End));
        await fixture.Scans.SaveAsync(Audit(End), [Raw(End.AddHours(-1))]);
        Assert.IsNull(await fixture.Scans.LoadAsync(End.AddMinutes(-1)));
        Assert.IsNull(await fixture.Scans.LoadAsync(End.AddDays(-1)));
        Assert.IsNotNull(await fixture.Scans.LoadAsync(End));
    }

    [TestMethod]
    public async Task HistoricalMidnightCutoffUsesTheSelectedDayAndFastKeepsOriginalWindow()
    {
        using var fixture = new Fixture();
        var cutoff = new DateTimeOffset(End.ToLocalTime().Date, TimeZoneInfo.Local.GetUtcOffset(End.ToLocalTime().Date));
        var scanEnd = cutoff.AddHours(-4);
        await fixture.Scans.SaveAsync(Audit(scanEnd), [Raw(scanEnd.AddHours(-1))]);
        var report = await fixture.Service().LoadAsync(WeeklyReportMode.Fast, false, cutoff, "en-US");
        Assert.AreEqual(scanEnd.UtcDateTime, report.EndUtc);
        Assert.AreEqual(scanEnd.AddDays(-7).UtcDateTime, report.StartUtc);
    }

    [TestMethod]
    public async Task HistoryFallbackDoesNotManufactureRawEventCounts()
    {
        using var fixture = new Fixture();
        var scans = new WeeklyScanSnapshotStore(Path.Combine(fixture.Directory, "history-only"), () => [Audit(End)], () => End);
        using var service = new WeeklyReportService(fixture.Storage, scans, identity: _ => throw new AssertFailedException("No model needed."), utcNow: () => End);
        var report = await service.LoadAsync(WeeklyReportMode.Fast, false, End, "en-US");
        Assert.IsFalse(report.Data.WeeklySource!.RawEventsAvailable);
        Assert.IsNull(report.Data.Metrics.Single(item => item.Key == "total").Current);
        Assert.IsTrue(report.Data.Daily.All(item => item.Critical is null && item.Error is null));
    }

    [TestMethod]
    public async Task SnapshotCapsRawEvidenceAndPreservesCompletedAuditAgainstPendingRevision()
    {
        using var fixture = new Fixture();
        var audit = Audit(End);
        await fixture.Scans.SaveAsync(audit, Enumerable.Range(0, WeeklyScanSnapshotStore.MaxEvents + 1).Select(index => Raw(End.AddSeconds(-index - 1))).ToArray());
        var snapshot = await fixture.Scans.LoadAsync(End);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(WeeklyScanSnapshotStore.MaxEvents, snapshot.Events.Count);
        Assert.IsTrue(snapshot.RawEventsTruncated);
        audit.AnalysisOutcome = "pending";
        await fixture.Scans.SaveAsync(audit, []);
        Assert.AreEqual(snapshot.Id, (await fixture.Scans.LoadAsync(End))!.Id);
    }

    [TestMethod]
    public async Task RepeatedIdenticalScanSaveKeepsEvidenceIdAndFastDocument()
    {
        using var fixture = new Fixture();
        var audit = Audit(End);
        SecurityEvent[] events = [Raw(End.AddHours(-1))];
        await fixture.Scans.SaveAsync(audit, events);
        var before = await fixture.Scans.LoadAsync(End);
        var service = fixture.Service();
        var first = await service.LoadAsync(WeeklyReportMode.Fast, false, End, "zh-CN");
        await fixture.Scans.SaveAsync(audit, events);
        var after = await fixture.Scans.LoadAsync(End);
        Assert.AreEqual(before!.Id, after!.Id);
        Assert.AreEqual(before.SavedUtc, after.SavedUtc);
        Assert.AreEqual(first.Id, (await service.LoadAsync(WeeklyReportMode.Fast, false, End, "en-US")).Id);
    }

    [TestMethod]
    public async Task RecentFullReportReopensWithoutRescanningButExplicitUpdateUsesNewEnd()
    {
        using var fixture = new Fixture();
        var windows = new List<DateTimeOffset>();
        var service = fixture.Service((start, end, _) =>
        {
            windows.Add(end);
            return Task.FromResult(Collection(start, end));
        });
        var first = await service.LoadAsync(WeeklyReportMode.Full, false, End, "zh-CN");
        var reused = await service.LoadAsync(WeeklyReportMode.Full, false, End.AddSeconds(30), "zh-CN");
        Assert.AreEqual(first.Id, reused.Id);
        Assert.AreEqual(1, windows.Count);
        var updated = await service.LoadAsync(WeeklyReportMode.Full, true, End.AddSeconds(30), "zh-CN");
        Assert.AreNotEqual(first.Id, updated.Id);
        Assert.AreEqual(2, windows.Count);
        Assert.AreEqual(End.AddSeconds(30), windows[1].ToUniversalTime());
    }

    [TestMethod]
    public async Task RestartedServiceReadsPublishedFullWithoutAnyScanOrAiConfigurationAccess()
    {
        using var fixture = new Fixture();
        var originalService = fixture.Service();
        var original = await originalService.LoadAsync(WeeklyReportMode.Full, false, End, "zh-CN");
        await originalService.DisposeAsync();
        var reopenedAt = End.AddHours(1);
        var storage = new WindowsStabilityReportStorage(Path.Combine(fixture.Directory, "reports"), utcNow: () => reopenedAt);
        var scans = new WeeklyScanSnapshotStore(Path.Combine(fixture.Directory, "scans"),
            () => throw new AssertFailedException("Viewing a published document must not load scan history."), () => reopenedAt);
        using var restarted = new WeeklyReportService(storage, scans,
            collect: (_, _, _) => throw new AssertFailedException("Viewing a published document must not collect."),
            review: (_, _, _) => throw new AssertFailedException("Viewing a published document must not invoke AI."),
            identity: _ => throw new AssertFailedException("The newly selected provider/model configuration must not be consulted."),
            utcNow: () => reopenedAt);

        var saved = await restarted.ReadSavedAsync(WeeklyReportMode.Full, reopenedAt, "en-US");
        Assert.IsNotNull(saved);
        Assert.AreEqual(original.Id, saved.Id);
        Assert.AreEqual(original.EndUtc, saved.EndUtc);
        Assert.AreEqual("en-US", saved.Language);
        Assert.AreEqual("gpt-6-astra", saved.Data.WeeklySource!.UsedModel);
        Assert.AreEqual(original.Data.WeeklySource!.AnalysisIdentity, saved.Data.WeeklySource.AnalysisIdentity);
        Assert.IsNull(await restarted.ReadSavedAsync(WeeklyReportMode.Fast, reopenedAt, "en-US"));
        Assert.IsNull(await restarted.ReadSavedAsync(WeeklyReportMode.Full, End.AddDays(-1), "en-US"));
    }

    [TestMethod]
    public async Task RetentionRemovesOnlyRecognizedExpiredReportAndScanFiles()
    {
        using var fixture = new Fixture();
        await fixture.Scans.SaveAsync(Audit(End), []);
        await fixture.Service().LoadAsync(WeeklyReportMode.Full, false, End, "en-US");
        string scanDirectory = Path.Combine(fixture.Directory, "scans");
        string reports = Path.Combine(fixture.Directory, "reports");
        string oldScan = Path.Combine(scanDirectory, "weekly-scan-2026-09-01.json");
        string oldReport = Path.Combine(reports, "stability-weekly-full-2026-09-01.md");
        string oldPng = Path.Combine(reports, "stability-weekly-fast-2026-09-01-" + new string('a', 32) + "-en-dark-v5.png");
        string userReport = Path.Combine(reports, "stability-weekly-full-notes.md");
        foreach (string file in new[] { oldScan, oldReport, oldPng, userReport })
        {
            File.WriteAllText(file, "fixture");
        }

        await fixture.Scans.LoadAsync(End);
        await fixture.Storage.ReadCacheAsync(WeeklyReportMode.Full, End);
        Assert.IsFalse(File.Exists(oldScan));
        Assert.IsFalse(File.Exists(oldReport));
        Assert.IsFalse(File.Exists(oldPng));
        Assert.IsTrue(File.Exists(userReport));
    }

    [TestMethod]
    public async Task DisposalCancelsAndDrainsActiveAndQueuedLoadsBeforeReleasingTheGate()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = fixture.Service(async (start, end, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Collection(start, end);
        });
        var active = service.LoadAsync(WeeklyReportMode.Full, true, End, "en-US");
        await entered.Task;
        var queued = service.LoadAsync(WeeklyReportMode.Full, true, End, "en-US");
        await service.DisposeAsync();
        foreach (var load in new[] { active, queued })
        {
            try
            {
                await load;
                Assert.Fail("Disposal must cancel an active or queued generation.");
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => service.LoadAsync(WeeklyReportMode.Fast, false, End, "en-US"));
    }

    private static AuditResult Audit(DateTimeOffset end) => new()
    {
        Timestamp = end.UtcDateTime, ScanStart = end.AddDays(-7).UtcDateTime, ScanEnd = end.UtcDateTime,
        AnalysisOutcome = "ok", UsedModel = "saved-model", CollectionProfile = "rules-v1|extended|System,Application,Setup,ForwardedEvents",
    };

    private static SecurityEvent Raw(DateTimeOffset time) => new(1000, "Application", "Application Error", 2, time.UtcDateTime, "fixture crash");

    private static WindowsStabilityCollection Collection(DateTimeOffset start, DateTimeOffset end) => new(start, end,
        [new("Application", "Application Error", 1000, 1, end.AddHours(-1), 2, "fixture crash", new Dictionary<string, string>(), [])],
        new[] { "System", "Application", "Setup" }.Select(channel => new WindowsStabilityCoverage(channel, start, end, start.AddDays(-1), true, false, false, 1, string.Empty)).ToArray(),
        new Dictionary<string, string>(), []);

    private static WeeklyReportReview Review() => new()
    {
        AssessmentZh = "独立中文结论", AssessmentEn = "Independent English assessment", UsedModel = "gpt-6-astra", UsedRoute = "primary",
        EvidenceIndex = [new("event-group-1", "event-group", "Application", "Application Error", 1000, 1, End.AddHours(-1), End.AddHours(-1), "host-retained fixture sample", "host-retained fixture sample")],
        Findings =
        [
            new()
            {
                Priority = "Medium",
                TitleZh = "中文问题",
                TitleEn = "English issue",
                FactsZh = "中文事实",
                FactsEn = "English facts",
                HypothesisZh = "可能原因",
                HypothesisEn = "Possible cause",
                StepsZh = "核查组件版本",
                StepsEn = "Check component versions",
                VerificationZh = "复测确认",
                VerificationEn = "English verification",
                EvidenceRefs = ["stats"],
            },
        ],
    };

    private sealed class Fixture : IDisposable
    {
        private readonly List<WeeklyReportService> _services = [];

        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "Kit.WeeklyReportTests", Guid.NewGuid().ToString("N"));

        public WindowsStabilityReportStorage Storage { get; }

        public WeeklyScanSnapshotStore Scans { get; }

        public Fixture()
        {
            Storage = new(Path.Combine(Directory, "reports"), utcNow: () => End);
            Scans = new(Path.Combine(Directory, "scans"), () => [], () => End);
        }

        public WeeklyReportService Service(Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<WindowsStabilityCollection>>? collect = null,
            Func<WeeklyReportReviewInput, IProgress<WeeklyReportProgress>?, CancellationToken, Task<WeeklyReportReview>>? review = null,
            Func<CancellationToken, Task<string>>? identity = null)
        {
            var service = new WeeklyReportService(Storage, Scans,
                collect ?? ((start, end, _) => Task.FromResult(Collection(start, end))), review ?? ((_, _, _) => Task.FromResult(Review())),
                identity ?? (_ => Task.FromResult("astra:max:context-a:policy-a")), () => End);
            _services.Add(service);
            return service;
        }

        public void Dispose()
        {
            foreach (var service in _services)
            {
                service.Dispose();
            }

            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, true);
            }
        }
    }
}
