namespace Kit.AiHub.UnitTests;

using System.Text.Json;
using Kit.AiHub.Models;
using Kit.AiHub.Serialization;
using Kit.AiHub.Storage;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AnalysisHistoryRetentionTests
{
    [TestMethod]
    public void ArchivePrunesExpiredScansAndTheirFindingBodiesWithinTheBoundaryDay()
    {
        using var fixture = new FixtureDirectory();
        DateTime now = new(2026, 11, 27, 20, 0, 0, DateTimeKind.Utc);
        var expired = new AuditResult
        {
            Timestamp = now.AddDays(-30).AddSeconds(-1),
            Findings = [new() { Key = "expired-body", Title = "Expired finding" }],
        };
        var boundary = new AuditResult
        {
            Timestamp = now.AddDays(-30),
            Findings = [new() { Key = "retained-body", Title = "Retained finding" }],
        };
        AuditReportArchiveStorage.Update(fixture.RootPath, [expired, boundary], now.AddDays(-1));
        var archive = AuditReportArchiveStorage.Update(fixture.RootPath, [expired, boundary], now);
        Assert.AreEqual(boundary.Timestamp, archive.Days.SelectMany(day => day.Scans).Single().Timestamp);
        Assert.AreEqual("retained-body", archive.Days.SelectMany(day => day.Findings).Single().Key);
        string stored = File.ReadAllText(Path.Combine(fixture.RootPath, "daily-summary.json"));
        Assert.IsFalse(stored.Contains("expired-body", StringComparison.Ordinal));
        Assert.IsTrue(archive.Days.Single().EvidenceIncomplete);
    }

    [TestMethod]
    public void NormalMonthEndRetentionDoesNotBlockRefreshingTheRemainingMonth()
    {
        using var fixture = new FixtureDirectory();
        DateTime now = new(2026, 12, 31, 20, 0, 0, DateTimeKind.Utc);
        var expired = new AuditResult { Timestamp = now.AddDays(-30).AddSeconds(-1) };
        var boundary = new AuditResult { Timestamp = now.AddDays(-30) };
        var current = new AuditResult { Timestamp = now };
        AuditReportArchiveStorage.Update(fixture.RootPath, [expired, boundary, current], now.AddDays(-1));
        var archive = AuditReportArchiveStorage.Update(fixture.RootPath, [expired, boundary, current], now);
        var report = AuditReportArchiveStorage.Report(archive, new DateOnly(2026, 12, 31), TimeZoneInfo.Utc, true, now);
        Assert.IsTrue(report.CurrentHistoryIncomplete, "The expired part of the first day must remain an explicit coverage gap.");
        Assert.IsFalse(report.CurrentRetainedHistoryIncomplete, "Normal expiry must not freeze the saved current-month report.");
        archive.Days.Single(day => day.Date == new DateOnly(2026, 12, 31)).EvidenceIncomplete = true;
        report = AuditReportArchiveStorage.Report(archive, new DateOnly(2026, 12, 31), TimeZoneInfo.Utc, true, now);
        Assert.IsTrue(report.CurrentRetainedHistoryIncomplete, "Lost retained evidence still protects the previous report.");
    }

    [TestMethod]
    public void ReadingDiagnosticsReclaimsExpiredDiskRecordsWithoutAnAiWrite()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiDiagnosticsStore(fixture.RootPath);
        Directory.CreateDirectory(store.LogDirectory);
        string path = Path.Combine(store.LogDirectory, "diagnostics.json");
        var records = new List<AiDiagnosticRecord>
        {
            new() { TimestampUtc = DateTimeOffset.UtcNow.AddDays(-31), Model = "expired-model" },
            new() { TimestampUtc = DateTimeOffset.UtcNow.AddDays(-1), Model = "retained-model" },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(records, AiHubJsonContext.Default.ListAiDiagnosticRecord));
        Assert.AreEqual(1, store.LoadHistory().Count);
        var persisted = JsonSerializer.Deserialize(File.ReadAllText(path), AiHubJsonContext.Default.ListAiDiagnosticRecord)!;
        Assert.AreEqual(1, persisted.Count);
        Assert.AreEqual("retained-model", persisted.Single().Model);
    }
}
