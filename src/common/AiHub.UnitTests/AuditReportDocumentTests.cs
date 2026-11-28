namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AuditReportDocumentTests
{
    [TestMethod]
    public async Task ReportDocumentIsReadBackFromTheSameMarkdownUsedForCopyAndCharts()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        var timestamp = DateTime.UtcNow;
        store.SaveResult(new AuditResult
        {
            Timestamp = timestamp, AnalysisOutcome = "ok", ScanStart = timestamp.AddDays(-2), ScanEnd = timestamp,
            CollectionProfile = "test", UsedModel = "test-model",
            Findings = [new() { Category = "Stability", Key = "crash", Title = "Observed crash", Severity = "High", EventId = "1000" }],
        });
        var document = await store.LoadPeriodReportDocumentAsync(DateOnly.FromDateTime(timestamp.ToLocalTime()), TimeZoneInfo.Local, false);
        var restored = AuditStabilityReportDocument.Read(document.SourcePath);
        Assert.AreEqual(document.VersionId, restored.VersionId);
        Assert.AreEqual(DateTimeKind.Utc, restored.GeneratedAtUtc.Kind);
        Assert.AreEqual(document.Markdown, File.ReadAllText(document.SourcePath));
        Assert.AreEqual(document.Format(false), restored.Format(false));
        Assert.AreEqual(document.Format(true), restored.Format(true));
        Assert.AreEqual(document.Highlights(false).Assessment, restored.Highlights(false).Assessment);
        CollectionAssert.AreEqual(document.Report.TrendDays.ToArray(), restored.Report.TrendDays.ToArray());
        Assert.AreEqual(1, restored.Report.Issues.Count);
        StringAssert.Contains(restored.Format(false), "Observed crash");
        Assert.IsFalse(restored.Format(false).Contains("kit-audit-data", StringComparison.Ordinal));
        Assert.AreEqual(0, Directory.GetFiles(store.ReportsDirectory, "*.tmp").Length);
    }

    [TestMethod]
    public async Task UnreadableArchivePreservesAndLoadsThePreviousMarkdownVersion()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        var end = DateOnly.FromDateTime(DateTime.Today);
        store.SaveResult(new AuditResult { Timestamp = DateTime.UtcNow, AnalysisOutcome = "ok" });
        var first = await store.LoadPeriodReportDocumentAsync(end, TimeZoneInfo.Local, false);
        string markdown = File.ReadAllText(first.SourcePath);
        File.WriteAllText(Path.Combine(store.ReportsDirectory, "daily-summary.json"), "invalid");
        var retained = await new AuditHistoryStorage(fixture.RootPath).LoadPeriodReportDocumentAsync(end, TimeZoneInfo.Local, false);
        Assert.IsTrue(retained.RefreshFailed);
        Assert.AreEqual(first.VersionId, retained.VersionId);
        Assert.AreEqual(markdown, File.ReadAllText(first.SourcePath));
        Assert.IsFalse(retained.Report.ArchiveUnavailable, "The stored complete report is retained rather than replaced by degraded evidence.");
    }

    [TestMethod]
    public async Task FirstReportCanSaveExplicitlyIncompleteEvidenceWithoutInventingCoverage()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        Directory.CreateDirectory(store.ReportsDirectory);
        File.WriteAllText(Path.Combine(store.ReportsDirectory, "daily-summary.json"), "invalid");
        var document = await store.LoadPeriodReportDocumentAsync(DateOnly.FromDateTime(DateTime.Today), TimeZoneInfo.Local, false);
        Assert.IsTrue(document.Report.ArchiveUnavailable);
        Assert.IsFalse(document.RefreshFailed);
        Assert.AreEqual(0, document.Report.Days.Count);
        Assert.IsTrue(document.Report.TrendDays.All(day => day.High is null));
        Assert.IsTrue(File.Exists(document.SourcePath));
    }

    [TestMethod]
    public void InvalidDocumentIsRejectedWithoutChangingItsFile()
    {
        using var fixture = new FixtureDirectory();
        string path = Path.Combine(fixture.RootPath, "report.md");
        const string source = "# Previous report\nEvidence retained.";
        File.WriteAllText(path, source);
        Assert.ThrowsExactly<InvalidDataException>(() => AuditStabilityReportDocument.Read(path));
        Assert.AreEqual(source, File.ReadAllText(path));
    }

    [TestMethod]
    public async Task ExpiredBaselineDoesNotFreezeTheCurrentMonthlyDocument()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        var scan = new AuditResult { Timestamp = DateTime.UtcNow, AnalysisOutcome = "pending" };
        var date = DateOnly.FromDateTime(scan.Timestamp.ToLocalTime());
        store.SaveResult(scan);
        var pending = await store.LoadPeriodReportDocumentAsync(date, TimeZoneInfo.Local, true);
        Assert.IsTrue(pending.Report.HistoryIncomplete, "The previous calendar month extends outside the retention window.");
        scan.AnalysisOutcome = "ok";
        scan.UsedModel = "completed-model";
        store.SaveResult(scan);
        var complete = await store.LoadPeriodReportDocumentAsync(date, TimeZoneInfo.Local, true);
        Assert.IsFalse(complete.RefreshFailed);
        Assert.AreNotEqual(pending.VersionId, complete.VersionId);
        Assert.AreEqual(1, complete.Report.CompletedAiAudits);
        Assert.AreEqual(0, complete.Report.IncompleteAiAudits);
    }

    [TestMethod]
    public void LoadingHistoryPhysicallyPrunesExpiredRecordsAndOnlyGeneratedExpiredReports()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        string historyPath = Path.Combine(fixture.RootPath, "audit_history.json");
        File.WriteAllText(historyPath, JsonSerializer.Serialize(new List<AuditResult>
        {
            new AuditResult { Timestamp = DateTime.UtcNow.AddDays(-31) },
            new AuditResult { Timestamp = DateTime.UtcNow.AddDays(-2) },
        }, AIHubLibJsonContext.Default.ListAuditResult));
        Directory.CreateDirectory(store.ReportsDirectory);
        string old = Path.Combine(store.ReportsDirectory, $"weekly-{DateTime.Today.AddDays(-31):yyyy-MM-dd}.md");
        string recent = Path.Combine(store.ReportsDirectory, $"weekly-{DateTime.Today:yyyy-MM-dd}.md");
        string notes = Path.Combine(store.ReportsDirectory, "weekly-notes.md");
        foreach (string path in new[] { old, recent, notes })
        {
            File.WriteAllText(path, "retention fixture");
        }

        Assert.AreEqual(1, store.LoadHistory().Count);
        Assert.AreEqual(1, JsonSerializer.Deserialize<AuditResult[]>(File.ReadAllText(historyPath))!.Length);
        Assert.IsFalse(File.Exists(old));
        Assert.IsTrue(File.Exists(recent));
        Assert.IsTrue(File.Exists(notes));
        store.SaveResult(new AuditResult { Timestamp = DateTime.UtcNow.AddDays(-40) }, retentionDays: 3650);
        Assert.AreEqual(1, store.LoadHistory().Count, "The requested retention cannot exceed the 30-day policy.");
        Assert.IsFalse(Directory.GetFiles(store.ReportsDirectory, "*.md").Any(path => path.Contains(DateTime.Today.AddDays(-40).ToString("yyyy-MM-dd"), StringComparison.Ordinal)));
    }
}
