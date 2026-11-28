namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AuditReportPresentationTests
{
    [TestMethod]
    public void MixedScanWindowsNeverProduceAnImprovementClaim()
    {
        var report = AuditPeriodReport.From([Scan(12, 30, 50), Scan(20, 2, 95)], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        Assert.IsTrue(report.Coverage.MixedWindows);
        Assert.IsFalse(report.CanCompareScores);
        StringAssert.Contains(report.FormatOverview(false), "Score trend withheld");
        Assert.IsFalse(report.FormatOverview(false).Contains("+45", StringComparison.Ordinal));

        var comparable = AuditPeriodReport.From([Scan(12, 2, 50), Scan(20, 2, 95)], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        Assert.IsTrue(comparable.CanCompareScores);
        StringAssert.Contains(comparable.FormatOverview(false), "+45");
        StringAssert.Contains(comparable.FormatOverview(false), "does not establish recovery");
    }

    [TestMethod]
    public void UnknownCoverageAndDifferentAnalysisAlsoWithholdScoreComparisons()
    {
        var unknown = Scan(12, 2, 50);
        unknown.ScanStart = null;
        var current = Scan(20, 2, 95);
        var report = AuditPeriodReport.From([unknown, current], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        Assert.AreEqual(1, report.Coverage.UnknownWindowScans);
        Assert.IsFalse(report.CanCompareScores);

        var reviewed = Scan(12, 2, 50);
        reviewed.UsedModel = "review-model";
        reviewed.AnalysisOutcome = "ok";
        report = AuditPeriodReport.From([reviewed, current], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        Assert.AreEqual("different-analysis", report.Coverage.ScoreComparisonReason);
        Assert.IsFalse(report.CanCompareScores);

        reviewed.UsedModel = string.Empty;
        reviewed.AnalysisOutcome = "skipped";
        current.LimitedChannels = ["System"];
        report = AuditPeriodReport.From([reviewed, current], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        Assert.IsFalse(report.CanCompareScores);
        CollectionAssert.AreEqual(new[] { "System" }, report.Coverage.LimitedChannels.ToArray());
    }

    [TestMethod]
    public void ExtendedFullAndLegacyCollectionProfilesAreNotComparable()
    {
        var before = Scan(12, 2, 50);
        var current = Scan(20, 2, 95);
        current.CollectionProfile = AuditCollectionProfile.ForMode(EventLogService.AuditMode.Full);
        var report = AuditPeriodReport.From([before, current], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        Assert.IsTrue(report.Coverage.MixedCollectionProfiles);
        Assert.AreEqual("different-collection", report.Coverage.ScoreComparisonReason);
        Assert.IsFalse(report.CanCompareScores);
        StringAssert.Contains(report.FormatOverview(false), "different channel sets or rule versions");

        before.CollectionProfile = null;
        report = AuditPeriodReport.From([before, current], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        Assert.AreEqual(1, report.Coverage.UnknownCollectionScans);
        Assert.AreEqual("unknown-collection", report.Coverage.ScoreComparisonReason);
        Assert.IsFalse(report.CanCompareScores);
    }

    [TestMethod]
    public void SuccessfulAiReviewDoesNotHideEvidenceGapsOrAllowScoreComparisons()
    {
        var previous = Scan(12, 2, 50);
        var current = Scan(20, 2, 95);
        previous.AnalysisOutcome = current.AnalysisOutcome = "ok";
        previous.UsedModel = current.UsedModel = "same-model";
        previous.AiAnalysisScope = current.AiAnalysisScope = "same-policy";
        current.EvidenceIncomplete = true;
        var report = AuditPeriodReport.From([previous, current], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        Assert.AreEqual(1, report.IncompleteDays);
        Assert.AreEqual(1, report.Coverage.IncompleteEvidenceScans);
        Assert.AreEqual("incomplete-collection", report.Coverage.ScoreComparisonReason);
        Assert.IsFalse(report.CanCompareScores);
        StringAssert.Contains(report.FormatOverview(false), "Completed AI analysis does not fill those evidence gaps");
        StringAssert.Contains(report.FormatOverview(true), "AI 分析完成不代表这些缺失区间已补齐");

        current.EvidenceIncomplete = false;
        previous.EvidenceIncomplete = true;
        report = AuditPeriodReport.From([previous, current], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        Assert.AreEqual(0, report.IncompleteDays, "The previous period must not inflate current incomplete days.");
        Assert.AreEqual(1, report.Coverage.IncompleteEvidenceScans);
        Assert.IsFalse(report.CanCompareScores, "An incomplete baseline must also prevent an improvement claim.");
    }

    [TestMethod]
    public void OverviewIsBoundedWhileCompleteFindingsRetainRemediesAndFallbackLanguage()
    {
        var snapshot = Scan(20, 2, 75);
        snapshot.Findings = Enumerable.Range(0, 200).Select(index => new AuditIssueEnhanced
        {
            Key = "issue_" + index, Title = "English finding " + index,
            Description = "English evidence", RootCause = "Unconfirmed cause",
            Recommendation = "Inspect the supplied component and verify the result. " + new string('x', 4000),
            Severity = "Medium", Category = "Application", EventId = "1000",
        }).ToList();
        var report = AuditPeriodReport.From([snapshot], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        string overview = report.FormatOverview(true);
        string details = report.FormatDetails(true);
        Assert.IsTrue(overview.Length < 12000, "Hundreds of findings must not inflate the initial report view.");
        Assert.IsFalse(overview.Contains("English finding 199", StringComparison.Ordinal));
        StringAssert.Contains(overview, "本期总评");
        StringAssert.Contains(overview, "处理与验证");
        StringAssert.Contains(overview, "5 项");
        StringAssert.Contains(details, "English finding 199");
        StringAssert.Contains(details, "Inspect the supplied component");
        StringAssert.Contains(details, "Unconfirmed cause");
        Assert.IsTrue(details.Length > 800000, "The complete export must retain the original steps.");
    }

    [TestMethod]
    public void StructuredRemediationIsValidatedAndReferencesAreProjectedWithoutMutation()
    {
        var plan = Plan("Inspect item-000002");
        Assert.IsTrue(plan.IsValid);
        var issue = new AuditIssue
        {
            EventRef = "item-000001", RelatedEventRefs = ["item-000001"], RemediationPlan = plan,
        };
        CollectionAssert.AreEqual(new[] { "item-000002" }, AuditAnalysisCache.GetNarrativeReferences(issue).ToArray());
        var projected = AuditAnalysisCache.Project(issue, reference => reference == "item-000001" ? "item-000002" : "item-000003");
        Assert.AreEqual("Inspect item-000003", projected.RemediationPlan!.Checks[0].Check);
        Assert.AreEqual("Inspect item-000002", issue.RemediationPlan.Checks[0].Check);
        CollectionAssert.AreEquivalent(new[] { "item-000002", "item-000003" }, projected.RelatedEventRefs);

        plan.VerificationZh = string.Empty;
        Assert.IsFalse(plan.IsValid, "AI output with no recovery verification must be rejected.");
        plan.VerificationZh = new string('x', 1201);
        Assert.IsFalse(plan.IsValid, "Unbounded model text must be rejected.");
    }

    [TestMethod]
    public void LegacyRecommendationAndPartialPlanUseLanguageFallbackWithoutInventingARepair()
    {
        var snapshot = Scan(20, 2, 75);
        snapshot.Findings =
        [
            new()
            {
                Key = "legacy", Title = "Legacy finding", Recommendation = "Read the original event before changing settings.",
            },
        ];
        var report = AuditPeriodReport.From([snapshot], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        StringAssert.Contains(report.FormatDetails(true), "Read the original event");
        snapshot.Findings[0].RemediationPlan = new() { Checks = [new() { Check = "Read the event", ExpectedResult = "Compare the account" }] };
        report = AuditPeriodReport.From([snapshot], new(2026, 11, 22), TimeZoneInfo.Utc, false);
        StringAssert.Contains(report.FormatDetails(true), "Read the event");
        StringAssert.Contains(report.FormatDetails(true), "判断标准：Compare the account");
        StringAssert.Contains(report.FormatOverview(false), "does not mark issues as repaired");
    }

    [TestMethod]
    public async Task ArchiveRetainsCoverageAnalysisIdentityAndStructuredRemediesAfterRawEviction()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        DateTime timestamp = DateTime.UtcNow.AddDays(-20);
        var old = new AuditResult
        {
            Timestamp = timestamp, ScanStart = timestamp.AddDays(-7), ScanEnd = timestamp,
            EventsScanned = 42, SkippedChannels = ["Security"], LimitedChannels = ["Application"], EvidenceIncomplete = true,
            CollectionProfile = AuditCollectionProfile.ForMode(EventLogService.AuditMode.Full),
            AnalysisOutcome = "ok", UsedModel = "test-model", AiAnalysisScope = "policy-hash", AiEvidenceFingerprint = "evidence-hash",
            AiAnalyzedEvents = 30, AiReusedEvents = 12, AiReportSummary = "Saved narrative", AiReportMeta = "Saved model details",
            Findings = [new() { Key = "driver", Recommendation = "Legacy fallback", RemediationPlan = Plan("Inspect driver") }],
        };
        store.SaveResult(old, retentionDays: 90);
        Assert.IsTrue(store.LoadHistory().Single().EvidenceIncomplete);
        store.SaveResult(new AuditResult { Timestamp = DateTime.UtcNow }, retentionDays: 1);
        Assert.AreEqual(1, store.LoadHistory().Count);
        var archive = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(store.ReportsDirectory, "daily-summary.json")), AIHubLibJsonContext.Default.AuditReportArchive)!;
        Assert.AreEqual(3, archive.Version);
        var restored = archive.Days.SelectMany(day => day.RestoreScans()).Single(scan => scan.Timestamp == timestamp);
        Assert.AreEqual(old.ScanStart, restored.ScanStart);
        Assert.AreEqual(old.ScanEnd, restored.ScanEnd);
        Assert.AreEqual(42, restored.EventsScanned);
        Assert.AreEqual(old.CollectionProfile, restored.CollectionProfile);
        CollectionAssert.AreEqual(old.SkippedChannels, restored.SkippedChannels!);
        CollectionAssert.AreEqual(old.LimitedChannels, restored.LimitedChannels!);
        Assert.IsTrue(restored.EvidenceIncomplete, "Raw-history eviction must not drop an event-window evidence gap.");
        Assert.AreEqual("policy-hash", restored.AiAnalysisScope);
        Assert.AreEqual("evidence-hash", restored.AiEvidenceFingerprint);
        Assert.AreEqual(30, restored.AiAnalyzedEvents);
        Assert.AreEqual(12, restored.AiReusedEvents);
        Assert.AreEqual("Saved narrative", restored.AiReportSummary);
        Assert.IsTrue(restored.Findings.Single().RemediationPlan!.IsValid);
        var report = await store.LoadPeriodReportAsync(DateOnly.FromDateTime(timestamp.ToLocalTime()), TimeZoneInfo.Local, false);
        Assert.AreEqual(1, report.Coverage.IncompleteEvidenceScans);
        Assert.AreEqual(1, report.IncompleteDays);
    }

    [TestMethod]
    public async Task VersionTwoArchiveMigratesWithoutInventingCoverage()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        DateTime timestamp = DateTime.Today.AddHours(12).ToUniversalTime();
        DateOnly date = DateOnly.FromDateTime(timestamp.ToLocalTime());
        var legacy = new AuditReportArchive
        {
            Version = 2,
            Days = [new() { Date = date, Audits = 1, Scans = [new() { Timestamp = timestamp, HealthScore = 80 }] }],
        };
        Directory.CreateDirectory(store.ReportsDirectory);
        string path = Path.Combine(store.ReportsDirectory, "daily-summary.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(legacy, AIHubLibJsonContext.Default.AuditReportArchive));
        var report = await store.LoadPeriodReportAsync(date, TimeZoneInfo.Local, false);
        Assert.AreEqual(1, report.Coverage.UnknownWindowScans);
        Assert.IsFalse(report.CanCompareScores);
        StringAssert.Contains(report.FormatOverview(false), "lack valid window metadata");
        var upgraded = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), AIHubLibJsonContext.Default.AuditReportArchive)!;
        Assert.AreEqual(3, upgraded.Version);
        Assert.IsNull(upgraded.Days.Single().RestoreScans().Single().ScanStart);
    }

    [TestMethod]
    public void KnownTruncatedChannelsDoNotCreateAnUnreadableArchiveChannel()
    {
        var archiveDay = new AuditArchiveDay
        {
            Scans = [new() { IncompleteLogs = true, LimitedChannels = ["Application"] }],
        };
        var restored = archiveDay.RestoreScans().Single();
        Assert.IsNull(restored.SkippedChannels);
        CollectionAssert.AreEqual(new[] { "Application" }, restored.LimitedChannels!);

        archiveDay.Scans[0].LimitedChannels = null;
        restored = archiveDay.RestoreScans().Single();
        CollectionAssert.AreEqual(new[] { "archive" }, restored.SkippedChannels!, "Legacy summaries with no channel detail must retain an incomplete-coverage marker.");
    }

    [TestMethod]
    public void EvidenceGapIsDistinctFromUnreadableChannelsAndOlderSnapshotsRemainCompatible()
    {
        var archiveDay = new AuditArchiveDay
        {
            Scans = [new() { IncompleteLogs = true, EvidenceIncomplete = true }],
        };
        var restored = archiveDay.RestoreScans().Single();
        Assert.IsTrue(restored.EvidenceIncomplete);
        Assert.IsNull(restored.SkippedChannels, "An evicted interval must not invent an unreadable archive channel.");
        Assert.IsNull(restored.LimitedChannels);

        var legacy = JsonSerializer.Deserialize("{\"eventsScanned\":12,\"analysisOutcome\":\"ok\"}", AIHubLibJsonContext.Default.AuditResult)!;
        Assert.AreEqual(12, legacy.EventsScanned);
        Assert.IsFalse(legacy.EvidenceIncomplete, "Snapshots written before this flag must remain readable without an invented gap.");
    }

    private static AuditResult Scan(int day, int windowDays, int score)
    {
        var timestamp = new DateTime(2026, 11, day, 12, 0, 0, DateTimeKind.Utc);
        return new()
        {
            Timestamp = timestamp, ScanStart = timestamp.AddDays(-windowDays), ScanEnd = timestamp, HealthScore = score, AnalysisOutcome = "skipped",
            CollectionProfile = AuditCollectionProfile.ForMode(EventLogService.AuditMode.Extended),
        };
    }

    private static AuditRemediationPlan Plan(string check) => new()
    {
        Checks = [new() { Check = check, CheckZh = "读取原始事件", ExpectedResult = "Confirm the recorded version", ExpectedResultZh = "确认记录中的版本" }],
        ConditionalAction = "Repair only if the check confirms the mismatch", ConditionalActionZh = "仅在检查确认不匹配时修复",
        RiskAndRecovery = "Save the current configuration before changing it", RiskAndRecoveryZh = "更改前保存当前配置",
        Verification = "Repeat the original operation and inspect new evidence", VerificationZh = "重复原操作并检查新证据",
    };
}
