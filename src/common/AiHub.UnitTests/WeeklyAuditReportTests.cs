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
public sealed class WeeklyAuditReportTests
{
    [TestMethod]
    public void LocalCalendarDaysUseTheLatestSnapshotAcrossTheAutumnClockChange()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var end = new DateOnly(2026, 11, 2);
        static AuditResult Scan(int month, int day, int hour, int score) => new()
        {
            Timestamp = new DateTime(2026, month, day, hour, 0, 0, DateTimeKind.Utc),
            HealthScore = score,
            Findings = [new() { Severity = score == 80 ? "High" : "Low" }],
        };

        var report = WeeklyAuditReport.From([
            Scan(11, 1, 9, 80), Scan(11, 1, 8, 90), Scan(10, 27, 7, 70),
            Scan(10, 27, 6, 100), Scan(10, 20, 6, 10), Scan(11, 3, 8, 10),
        ], end, zone);

        Assert.AreEqual(end, report.EndDate);
        CollectionAssert.AreEqual(new[] { new AuditReportDay(new(2026, 11, 1), 2, 80, 1, 0, 0), new AuditReportDay(new(2026, 10, 27), 1, 70, 0, 0, 1) }, report.Days.ToArray());
        Assert.AreEqual(new AuditReportDay(new(2026, 10, 26), 1, 100, 0, 0, 1), report.PreviousDays.Single());
    }

    [TestMethod]
    public async Task EmptyHistoryAndUnreadableHistoryProduceDifferentWeeklyReportOutcomes()
    {
        using var fixture = new FixtureDirectory();
        var store = new AuditHistoryStorage(fixture.RootPath);
        var date = new DateOnly(2026, 11, 2);
        Assert.AreEqual(0, (await store.LoadWeeklyReportAsync(date, TimeZoneInfo.Utc)).Days.Count);
        var path = fixture.PathFor("audit_history.json");
        await File.WriteAllTextAsync(path, "broken");

        await Assert.ThrowsAsync<JsonException>(() => store.LoadWeeklyReportAsync(date, TimeZoneInfo.Utc));
        Assert.AreEqual("broken", await File.ReadAllTextAsync(path));
        File.Delete(path);
        Directory.CreateDirectory(path);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.LoadWeeklyReportAsync(date, TimeZoneInfo.Utc));
    }
}
