namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AuditPeriodReportTests
{
    [TestMethod]
    public void MonthToDateUsesCalendarBoundariesAndDoesNotAddOverlappingEvents()
    {
        var end = new DateOnly(2026, 3, 31);
        var report = AuditPeriodReport.From([
            Audit(2, 28, 12, 80, "app", 4), Audit(3, 1, 12, 90, "app", 5),
            Audit(3, 31, 10, 50, "app", 9), Audit(3, 31, 12, 70, "app", 8), Audit(4, 1, 12, 20, "later", 10),
        ], end, TimeZoneInfo.Utc, monthly: true);
        Assert.AreEqual(new DateOnly(2026, 3, 1), report.StartDate);
        Assert.AreEqual(new DateOnly(2026, 2, 28), report.PreviousEndDate);
        Assert.AreEqual(2, report.Days.Count);
        Assert.AreEqual(70, report.Days[0].HealthScore);
        Assert.AreEqual(9, report.Issues.Single().PeakOccurrences);
        Assert.AreEqual(2, report.Issues.Single().ObservedDays);
        Assert.IsTrue(report.Issues.Single().SeenPreviously);
    }

    [TestMethod]
    public void MonthToDateKeepsLocalCalendarCutoffAcrossDaylightSavingChange()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var currentLocal = new DateTime(2026, 11, 25, 23, 15, 0, DateTimeKind.Unspecified);
        var currentUtc = TimeZoneInfo.ConvertTimeToUtc(currentLocal, zone);
        var previousUtc = TimeZoneInfo.ConvertTimeToUtc(currentLocal.AddMonths(-1), zone);

        // Subtracting in UTC lands on October 26 locally because October is PDT
        // and November is PST. That scan is outside the October 1-25 baseline.
        var shiftedUtc = currentUtc.AddMonths(-1);
        Assert.AreEqual(new DateOnly(2026, 10, 26), DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(shiftedUtc, zone)));
        var report = AuditPeriodReport.From([
            new AuditResult { Timestamp = previousUtc, HealthScore = 65 },
            new AuditResult { Timestamp = shiftedUtc, HealthScore = 5 },
            new AuditResult { Timestamp = currentUtc, HealthScore = 75 },
        ], DateOnly.FromDateTime(currentLocal), zone, monthly: true);

        Assert.AreEqual(new DateOnly(2026, 10, 1), report.PreviousStartDate);
        Assert.AreEqual(new DateOnly(2026, 10, 25), report.PreviousEndDate);
        Assert.AreEqual(1, report.PreviousDays.Count);
        Assert.AreEqual(65, report.PreviousDays.Single().HealthScore);
        Assert.AreEqual(75, report.Days.Single().HealthScore);
    }

    [TestMethod]
    public void MissingDaysAndDisappearingIssuesNeverClaimResolution()
    {
        var latest = Audit(11, 20, 12, 90, "current", 1);
        latest.AnalysisOutcome = "partial";
        var report = AuditPeriodReport.From([Audit(11, 12, 12, 80, "earlier", 2), latest], new DateOnly(2026, 11, 22), TimeZoneInfo.Utc, false);
        Assert.AreEqual(1, report.IncompleteDays);
        Assert.AreEqual(1, report.NotObservedAgain);
        StringAssert.Contains(report.Format(false), "does not establish resolution");
        StringAssert.Contains(report.Format(true), "1/7");
    }

    [TestMethod]
    public void LocalRuleSequenceNumbersDoNotSplitTheSameProblemAcrossDays()
    {
        var first = Audit(11, 20, 12, 90, "app", 2);
        var second = Audit(11, 21, 12, 90, "app", 3);
        first.Findings[0].Key = "issue-0001";
        second.Findings[0].Key = "issue-0004";
        var report = AuditPeriodReport.From([first, second], new DateOnly(2026, 11, 22), TimeZoneInfo.Utc, false);
        Assert.AreEqual(2, report.Issues.Single().ObservedDays);
        Assert.AreEqual(3, report.Issues.Single().PeakOccurrences);
    }

    [TestMethod]
    public async Task SavingAnAuditRefreshesBothReportsWithoutRecreatingExpiredExports()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        store.SaveResult(new AuditResult { Timestamp = DateTime.UtcNow, AnalysisOutcome = "ok" });
        Assert.AreEqual(2, Directory.GetFiles(store.ReportsDirectory, "*.md").Length);
        var empty = await store.LoadPeriodReportAsync(new DateOnly(2000, 1, 15), TimeZoneInfo.Utc, monthly: true);
        Assert.AreEqual(0, empty.Days.Count);
        Assert.IsFalse(File.Exists(Path.Combine(store.ReportsDirectory, "monthly-2000-01-15.md")));
    }

    [TestMethod]
    public async Task SavedReportSurvivesRawHistoryRetention()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        var earlier = DateTime.UtcNow.AddDays(-20);
        var end = DateOnly.FromDateTime(earlier.ToLocalTime());
        store.SaveResult(new AuditResult { Timestamp = earlier, HealthScore = 64 }, retentionDays: 90);
        var before = await store.LoadPeriodReportAsync(end, TimeZoneInfo.Local, monthly: true);
        store.SaveResult(new AuditResult { Timestamp = DateTime.UtcNow }, retentionDays: 1);
        Assert.AreEqual(1, store.LoadHistory().Count);
        var after = await store.LoadPeriodReportAsync(end, TimeZoneInfo.Local, monthly: true);
        Assert.AreEqual(before.Days.Count, after.Days.Count);
        Assert.AreEqual(64, after.Days.Single().HealthScore);
    }

    [TestMethod]
    public async Task DailyArchiveKeepsPriorMonthAndAcceptsUpdatedAiResults()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);

        // Reports compare local calendar dates. Move to the previous month before
        // converting to UTC so a DST offset change cannot move it past the cutoff.
        var todayLocal = DateTime.Now;
        var today = todayLocal.ToUniversalTime();
        var priorMonth = todayLocal.AddMonths(-1).ToUniversalTime();
        store.SaveResult(new AuditResult { Timestamp = priorMonth, HealthScore = 65 }, retentionDays: 90);
        var current = new AuditResult { Timestamp = today, HealthScore = 80, AnalysisOutcome = "pending" };
        store.SaveResult(current, retentionDays: 1);
        current.HealthScore = 75;
        current.AnalysisOutcome = "ok";
        store.SaveResult(current, retentionDays: 1);
        var report = await store.LoadPeriodReportAsync(DateOnly.FromDateTime(todayLocal), TimeZoneInfo.Local, true);
        bool retained = priorMonth >= DateTime.UtcNow.AddDays(-30);
        Assert.AreEqual(retained ? 1 : 0, report.PreviousDays.Count,
            "A baseline snapshot must obey the same rolling 30-day retention as the current period.");
        if (retained)
        {
            Assert.AreEqual(65, report.PreviousDays.Single().HealthScore);
        }

        Assert.AreEqual(75, report.Days.Single().HealthScore);
        Assert.AreEqual(0, report.IncompleteDays);
    }

    [TestMethod]
    public async Task CorruptDailyArchiveDoesNotFailSavedAuditOrOverwriteExports()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        var audit = new AuditResult { Timestamp = DateTime.UtcNow, HealthScore = 82 };
        store.SaveResult(audit);
        string archive = Path.Combine(store.ReportsDirectory, "daily-summary.json");
        string reportFile = Directory.GetFiles(store.ReportsDirectory, "monthly-*.md").Single();
        string exported = await File.ReadAllTextAsync(reportFile);
        await File.WriteAllTextAsync(archive, "broken");
        audit.HealthScore = 74;
        store.SaveResult(audit);
        Assert.AreEqual(74, store.LoadHistory().Single().HealthScore);
        var report = await store.LoadPeriodReportAsync(DateOnly.FromDateTime(audit.Timestamp.ToLocalTime()), TimeZoneInfo.Local, true);
        Assert.IsTrue(report.ArchiveUnavailable);
        Assert.AreEqual(74, report.Days.Single().HealthScore);
        Assert.AreEqual("broken", await File.ReadAllTextAsync(archive));
        Assert.AreEqual(exported, await File.ReadAllTextAsync(reportFile));
    }

    [TestMethod]
    public async Task ValidJsonWithInvalidArchiveSchemaPreservesExistingExports()
    {
        foreach (string damaged in new[] { "{\"version\":2,\"days\":null}", "[null]" })
        {
            using var fixture = new FixtureDirectory();
            var store = new AuditHistoryStorage(fixture.RootPath);
            var audit = new AuditResult { Timestamp = DateTime.UtcNow, HealthScore = 82 };
            store.SaveResult(audit);
            string archive = Path.Combine(store.ReportsDirectory, "daily-summary.json");
            string reportFile = Directory.GetFiles(store.ReportsDirectory, "monthly-*.md").Single();
            string exported = await File.ReadAllTextAsync(reportFile);
            await File.WriteAllTextAsync(archive, damaged);
            audit.HealthScore = 74;
            store.SaveResult(audit);
            var report = await store.LoadPeriodReportAsync(DateOnly.FromDateTime(audit.Timestamp.ToLocalTime()), TimeZoneInfo.Local, true);
            Assert.IsTrue(report.ArchiveUnavailable);
            Assert.AreEqual(74, store.LoadHistory().Single().HealthScore);
            Assert.AreEqual(74, report.Days.Single().HealthScore);
            Assert.AreEqual(damaged, await File.ReadAllTextAsync(archive));
            Assert.AreEqual(exported, await File.ReadAllTextAsync(reportFile));
        }
    }

    [TestMethod]
    public void DailyIssuesKeepAiRemedyWhenLaterLocalScanIsGeneric()
    {
        var reviewed = Audit(11, 20, 9, 65, "app", 4);
        reviewed.AnalysisOutcome = "ok";
        reviewed.Findings[0].Recommendation = "Verify the driver version, install the fixed version, then repeat the failing operation.";
        var incremental = Audit(11, 20, 10, 90, "app", 2);
        incremental.AnalysisOutcome = "skipped";
        incremental.Findings[0].Recommendation = "Check the event log.";
        var empty = new AuditResult { Timestamp = incremental.Timestamp.AddHours(1), HealthScore = 100, AnalysisOutcome = "skipped" };
        var report = AuditPeriodReport.From([reviewed, incremental, empty], new DateOnly(2026, 11, 20), TimeZoneInfo.Utc, false);
        Assert.AreEqual(100, report.Days.Single().HealthScore);
        Assert.AreEqual(3, report.Days.Single().Audits);
        Assert.AreEqual(1, report.Issues.Single().ObservedDays);
        Assert.AreEqual(4, report.Issues.Single().PeakOccurrences);
        Assert.AreEqual(reviewed.Findings[0].Recommendation, report.Issues.Single().Latest.Recommendation);
        StringAssert.Contains(report.Format(false), "issues include all observations");
        StringAssert.Contains(report.Format(false), "Average health score");
    }

    [TestMethod]
    public void HigherSeverityObservationIsNotDowngradedByEarlierAiReview()
    {
        var reviewed = Audit(11, 20, 9, 90, "app", 1);
        reviewed.AnalysisOutcome = "ok";
        reviewed.Findings[0].Severity = "Low";
        var later = Audit(11, 20, 10, 50, "app", 2);
        later.AnalysisOutcome = "skipped";
        later.Findings[0].Severity = "High";
        var report = AuditPeriodReport.From([reviewed, later], new DateOnly(2026, 11, 20), TimeZoneInfo.Utc, false);
        Assert.AreEqual("High", report.Issues.Single().Latest.Severity);
    }

    [TestMethod]
    public async Task LateAiRevisionAndEmptyReplacementUpdateOnlyTheirOwnScan()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        DateTime morning = DateTime.Today.AddHours(9).ToUniversalTime();
        var first = new AuditResult { Timestamp = morning, AnalysisOutcome = "pending", Findings = [new() { Key = "first", Severity = "High" }] };
        var independent = new AuditResult { Timestamp = morning.AddMinutes(1), Findings = [new() { Key = "independent" }] };
        var empty = new AuditResult { Timestamp = morning.AddMinutes(2), HealthScore = 100, AnalysisOutcome = "skipped" };
        store.SaveResult(first);
        store.SaveResult(independent);
        store.SaveResult(empty);
        first.AnalysisOutcome = "ok";
        first.Findings = [new() { Key = "corrected", Severity = "High", Recommendation = "AI verified remedy" }];
        store.SaveResult(first);
        store.SaveResult(first);
        DateOnly end = DateOnly.FromDateTime(morning.ToLocalTime());
        var report = await store.LoadPeriodReportAsync(end, TimeZoneInfo.Local, false);
        Assert.AreEqual(3, report.Days.Single().Audits);
        Assert.AreEqual(100, report.Days.Single().HealthScore);
        Assert.AreEqual(0, report.IncompleteDays);
        CollectionAssert.AreEquivalent(new[] { "corrected", "independent" }, report.Issues.Select(issue => issue.Latest.Key).ToArray());

        first.Findings.Clear();
        store.SaveResult(first);
        report = await new AuditHistoryStorage(fixture.RootPath).LoadPeriodReportAsync(end, TimeZoneInfo.Local, false);
        Assert.AreEqual("independent", report.Issues.Single().Latest.Key);
        Assert.AreEqual(3, report.Days.Single().Audits);
    }

    [TestMethod]
    public async Task DailyEvidenceSurvivesRawCountEvictionAndDuplicateWrites()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        DateTime morning = DateTime.Today.AddHours(9).ToUniversalTime();
        var scans = Enumerable.Range(0, 225).Select(index => new AuditResult
        {
            Timestamp = morning.AddSeconds(index), AnalysisOutcome = "skipped",
            Findings = index == 0 ? [new() { Key = "morning", Recommendation = "Keep this evidence" }] : [],
        }).ToList();
        await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, "audit_history.json"), JsonSerializer.Serialize(scans, AIHubLibJsonContext.Default.ListAuditResult));
        store.SaveResult(scans[^1]);
        Assert.AreEqual(AuditHistoryStorage.MaxRetainedAudits, store.LoadHistory().Count);
        Assert.IsFalse(store.LoadHistory().Any(scan => scan.Timestamp == morning));
        store.SaveResult(scans[^1]);
        var report = await store.LoadPeriodReportAsync(DateOnly.FromDateTime(morning.ToLocalTime()), TimeZoneInfo.Local, false);
        Assert.AreEqual(225, report.Days.Single().Audits);
        Assert.AreEqual("morning", report.Issues.Single().Latest.Key);
        Assert.IsFalse(report.HistoryIncomplete);
    }

    [TestMethod]
    public async Task ArchiveRemovesScansBeyondRollingThirtyDays()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        DateTime today = DateTime.Today.AddHours(12).ToUniversalTime();
        var scans = Enumerable.Range(0, 180).Select(index => new AuditResult { Timestamp = today.AddDays(-index), HealthScore = 75 }).ToList();
        await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, "audit_history.json"), JsonSerializer.Serialize(scans, AIHubLibJsonContext.Default.ListAuditResult));
        store.SaveResult(scans[0], retentionDays: 1);
        var archive = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(store.ReportsDirectory, "daily-summary.json")), AIHubLibJsonContext.Default.AuditReportArchive)!;
        Assert.IsTrue(archive.Days.Count <= 31);
        Assert.IsTrue(archive.Days.SelectMany(day => day.Scans).All(scan => scan.Timestamp >= DateTime.UtcNow.AddDays(-30)));
        Assert.IsNotNull(archive.PrunedThrough);
    }

    [TestMethod]
    public async Task ExpiredBaselineDoesNotFreezeSameDayMonthlyReportOrBlockLegacyMarkdownUpgrade()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        DateTime timestamp = DateTime.UtcNow;
        var scan = new AuditResult { Timestamp = timestamp, AnalysisOutcome = "pending", HealthScore = 45 };
        store.SaveResult(scan);
        string path = Path.Combine(store.ReportsDirectory, $"monthly-{timestamp.ToLocalTime():yyyy-MM-dd}.md");
        File.WriteAllText(path, "# Legacy monthly report\nPrevious summary.");
        scan.AnalysisOutcome = "ok";
        scan.UsedModel = "review-model";
        scan.HealthScore = 85;
        store.SaveResult(scan);
        var document = AuditStabilityReportDocument.Read(path);
        Assert.IsFalse(document.Report.CurrentRetainedHistoryIncomplete);
        Assert.AreEqual(1, document.Report.CompletedAiAudits);
        Assert.AreEqual(0, document.Report.IncompleteAiAudits);
        Assert.AreEqual(85, document.Report.Days.Single().HealthScore);
        StringAssert.Contains(document.Format(false), "review-model");
    }

    [TestMethod]
    public async Task LegacyArchiveMigrationRemovesExpiredSnapshots()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        DateTime today = DateTime.Today.AddHours(12).ToUniversalTime();
        var legacy = Enumerable.Range(0, 93).Select(index => new AuditDailySnapshot(
            DateOnly.FromDateTime(today.AddDays(-index).ToLocalTime()), 1, new AuditResult { Timestamp = today.AddDays(-index), HealthScore = 60 })).ToList();
        legacy[^1] = legacy[^1] with { Audits = 12 };
        Directory.CreateDirectory(store.ReportsDirectory);
        await File.WriteAllTextAsync(Path.Combine(store.ReportsDirectory, "daily-summary.json"), JsonSerializer.Serialize(legacy, AIHubLibJsonContext.Default.ListAuditDailySnapshot));
        DateOnly cutoff = legacy[^1].Date;
        string oldReport = Path.Combine(store.ReportsDirectory, $"monthly-{cutoff:yyyy-MM-dd}.md");
        const string completeExport = "Previously exported complete month and baseline.";
        await File.WriteAllTextAsync(oldReport, completeExport);
        var report = await store.LoadPeriodReportAsync(DateOnly.FromDateTime(today.ToLocalTime()), TimeZoneInfo.Local, true);
        Assert.IsTrue(report.HistoryIncomplete);
        var migrated = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(store.ReportsDirectory, "daily-summary.json")), AIHubLibJsonContext.Default.AuditReportArchive)!;
        Assert.AreEqual(3, migrated.Version);
        Assert.IsTrue(migrated.Days.Count <= 31);
        Assert.IsTrue(migrated.Days.SelectMany(day => day.Scans).All(scan => scan.Timestamp >= DateTime.UtcNow.AddDays(-30)));
        Assert.IsFalse(migrated.Days.Any(day => day.Date == cutoff));
    }

    [TestMethod]
    public async Task ScanCapacityIsExplicitAndNewSnapshotsContinueToPersist()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        DateTime morning = DateTime.Today.AddHours(9).ToUniversalTime();
        var scans = Enumerable.Range(0, 520).Select(index => new AuditResult { Timestamp = morning.AddSeconds(index) }).ToList();
        await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, "audit_history.json"), JsonSerializer.Serialize(scans, AIHubLibJsonContext.Default.ListAuditResult));
        DateOnly end = DateOnly.FromDateTime(morning.ToLocalTime());
        Directory.CreateDirectory(store.ReportsDirectory);
        string existing = Path.Combine(store.ReportsDirectory, $"weekly-{end:yyyy-MM-dd}.md");
        await File.WriteAllTextAsync(existing, "Complete earlier export");
        store.SaveResult(scans[^1]);
        var fresh = new AuditResult { Timestamp = morning.AddHours(1), HealthScore = 77, Findings = [new() { Key = "newest" }] };
        store.SaveResult(fresh);
        store.SaveResult(fresh);
        var report = await store.LoadPeriodReportAsync(end, TimeZoneInfo.Local, false);
        Assert.IsTrue(report.HistoryIncomplete);
        Assert.AreEqual(521, report.Days.Single().Audits);
        Assert.AreEqual(77, report.Days.Single().HealthScore);
        Assert.AreEqual("newest", report.Issues.Single().Latest.Key);
        Assert.AreEqual("Complete earlier export", await File.ReadAllTextAsync(existing));
        var archive = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(store.ReportsDirectory, "daily-summary.json")), AIHubLibJsonContext.Default.AuditReportArchive)!;
        Assert.AreEqual(512, archive.Days.Single().Scans.Count);
    }

    [TestMethod]
    public async Task OversizedFindingIsMarkedPartialWithoutBlockingLaterSaves()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        DateTime morning = DateTime.Today.AddHours(9).ToUniversalTime();
        var large = new AuditResult
        {
            Timestamp = morning,

            // The source field itself exceeds the archive budget; this must not depend
            // on a display projection serializing the same recommendation twice.
            Findings = [new() { Key = "large", Recommendation = new string('x', 33 * 1024 * 1024) }],
        };
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(large.Findings.Single(), AIHubLibJsonContext.Default.AuditIssueEnhanced).Length > 32 * 1024 * 1024);
        store.SaveResult(large);
        string archivePath = Path.Combine(store.ReportsDirectory, "daily-summary.json");
        Assert.IsTrue(new FileInfo(archivePath).Length <= 32 * 1024 * 1024);
        var trimmed = JsonSerializer.Deserialize(await File.ReadAllTextAsync(archivePath), AIHubLibJsonContext.Default.AuditReportArchive)!;
        Assert.IsTrue(trimmed.Days.Single().EvidenceIncomplete, "The oversized finding must mark the archived evidence incomplete immediately.");
        Assert.AreEqual(0, trimmed.Days.Single().Findings.Count);
        var fresh = new AuditResult { Timestamp = morning.AddHours(1), HealthScore = 79, Findings = [new() { Key = "fresh" }] };
        store.SaveResult(fresh);
        var report = await store.LoadPeriodReportAsync(DateOnly.FromDateTime(morning.ToLocalTime()), TimeZoneInfo.Local, false);
        Assert.IsTrue(report.HistoryIncomplete);
        Assert.AreEqual(79, report.Days.Single().HealthScore);
        Assert.AreEqual("fresh", report.Issues.Single().Latest.Key);
        Assert.IsTrue(new FileInfo(archivePath).Length <= 32 * 1024 * 1024);
    }

    [TestMethod]
    public async Task RepeatedScanBodiesAreStoredOncePerDay()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        DateTime morning = DateTime.Today.AddHours(9).ToUniversalTime();
        var scans = Enumerable.Range(0, 50).Select(index => new AuditResult
        {
            Timestamp = morning.AddSeconds(index), AnalysisOutcome = "ok", UsedModel = "model",
            Findings = [new() { Key = "repeated", Recommendation = "Same reviewed remedy", Occurrences = 4 }],
        }).ToList();
        await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, "audit_history.json"), JsonSerializer.Serialize(scans, AIHubLibJsonContext.Default.ListAuditResult));
        store.SaveResult(scans[^1]);
        var archive = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(store.ReportsDirectory, "daily-summary.json")), AIHubLibJsonContext.Default.AuditReportArchive)!;
        Assert.AreEqual(50, archive.Days.Single().Scans.Count);
        Assert.AreEqual(1, archive.Days.Single().Findings.Count);
        var report = await store.LoadPeriodReportAsync(DateOnly.FromDateTime(morning.ToLocalTime()), TimeZoneInfo.Local, false);
        Assert.AreEqual(4, report.Issues.Single().PeakOccurrences);
        Assert.AreEqual(50, report.Days.Single().Audits);
    }

    [TestMethod]
    public async Task MissingArchiveProtectsExportsEvenWhenRawHistoryCoversTheirDates()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        DateTime today = DateTime.Today.AddHours(12).ToUniversalTime();
        var surviving = Enumerable.Range(0, 30).Select(index => new AuditResult { Timestamp = today.AddDays(-index) }).ToList();
        await File.WriteAllTextAsync(Path.Combine(fixture.RootPath, "audit_history.json"), JsonSerializer.Serialize(surviving, AIHubLibJsonContext.Default.ListAuditResult));
        Directory.CreateDirectory(store.ReportsDirectory);
        DateOnly end = DateOnly.FromDateTime(today.ToLocalTime());
        string oldExport = Path.Combine(store.ReportsDirectory, $"weekly-{end:yyyy-MM-dd}.md");
        await File.WriteAllTextAsync(oldExport, "Complete daily observations before archive loss");
        var report = await store.LoadPeriodReportAsync(end, TimeZoneInfo.Local, false);
        Assert.AreEqual(7, report.Days.Count);
        Assert.IsTrue(report.HistoryIncomplete);
        Assert.AreEqual("Complete daily observations before archive loss", await File.ReadAllTextAsync(oldExport));
    }

    [TestMethod]
    public async Task EmptyLegacyArchiveDoesNotPermanentlyMarkFuturePeriodsIncomplete()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        Directory.CreateDirectory(store.ReportsDirectory);
        DateOnly oldDate = DateOnly.FromDateTime(DateTime.Today);
        await File.WriteAllTextAsync(Path.Combine(store.ReportsDirectory, "daily-summary.json"), "[]");
        string oldExport = Path.Combine(store.ReportsDirectory, $"monthly-{oldDate:yyyy-MM-dd}.md");
        await File.WriteAllTextAsync(oldExport, "Previous report");
        var old = await store.LoadPeriodReportAsync(oldDate, TimeZoneInfo.Local, true);
        Assert.IsTrue(old.HistoryIncomplete);
        Assert.AreEqual("Previous report", await File.ReadAllTextAsync(oldExport));
        DateTime future = DateTime.Today.AddMonths(3).AddHours(12).ToUniversalTime();
        var audit = new AuditResult { Timestamp = future, HealthScore = 80 };
        store.SaveResult(audit);
        var report = await store.LoadPeriodReportAsync(DateOnly.FromDateTime(future.ToLocalTime()), TimeZoneInfo.Local, true);
        Assert.IsFalse(report.HistoryIncomplete);
        audit.HealthScore = 70;
        store.SaveResult(audit);
        string newExport = Path.Combine(store.ReportsDirectory, $"monthly-{future.ToLocalTime():yyyy-MM-dd}.md");
        StringAssert.Contains(await File.ReadAllTextAsync(newExport), "Average health score: 70");
    }

    private static AuditResult Audit(int month, int day, int hour, int score, string affected, int occurrences) => new()
    {
        Timestamp = new DateTime(2026, month, day, hour, 0, 0, DateTimeKind.Utc), HealthScore = score,
        Findings = [new() { Key = "failure", Category = "Application", Affected = affected, EventId = "1000", Severity = "Medium", Occurrences = occurrences }],
    };
}
