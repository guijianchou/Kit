namespace Kit.AiHub.UnitTests;

using System;
using Kit.AIHubLib.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AuditSummaryTests
{
    [TestMethod]
    [DataRow(2)]
    [DataRow(7)]
    [DataRow(30)]
    public void FullScanOverviewUsesTheCompletedWindowAndItsOwnFindings(int days)
    {
        DateTime now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        var (start, end) = AuditSchedule.ComputeWindow(ScanIntent.ManualFull, now.AddMinutes(-1), now, days);
        var snapshot = new AuditResult
        {
            ScanStart = start, ScanEnd = end, Timestamp = now, HealthScore = 61, EventsScanned = 83,
            Findings = [new() { Category = "Stability", Severity = "High", Occurrences = 12 }],
        };
        var summary = AuditSummary.From(snapshot);
        Assert.AreEqual((int?)days, summary.RangeDays);
        Assert.AreEqual(now.AddDays(-days), summary.ScanStart);
        Assert.AreEqual(now, summary.ScanEnd);
        Assert.AreEqual(61, summary.HealthScore);
        Assert.AreEqual(83, summary.EventsScanned);
        Assert.AreEqual(1, summary.Findings);
        Assert.AreEqual(12L, summary.Occurrences);
    }

    [TestMethod]
    public void PartialAndLegacyWindowsAreNotPresentedAsFullScanRanges()
    {
        DateTime now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        Assert.IsNull(AuditSummary.From(new AuditResult()).RangeDays);
        Assert.IsNull(AuditSummary.From(new AuditResult { ScanStart = now.AddHours(-1), ScanEnd = now }).RangeDays);
        Assert.IsNull(AuditSummary.From(new AuditResult { ScanStart = now, ScanEnd = now.AddDays(-7) }).RangeDays);
        var local = AuditSummary.From(new AuditResult { ScanStart = now.AddDays(-7).ToLocalTime(), ScanEnd = now.ToLocalTime() });
        Assert.AreEqual((int?)7, local.RangeDays);
        Assert.AreEqual(now, local.ScanEnd);
        var legacy = AuditSummary.From(new AuditResult
        {
            ScanStart = DateTime.SpecifyKind(now.AddDays(-30), DateTimeKind.Unspecified),
            ScanEnd = DateTime.SpecifyKind(now, DateTimeKind.Unspecified),
        });
        Assert.AreEqual((int?)30, legacy.RangeDays);
        Assert.AreEqual(DateTimeKind.Utc, legacy.ScanEnd!.Value.Kind);
    }

    [TestMethod]
    public void SummaryGroupsWithoutMergingEvidenceAndOrdersByRisk()
    {
        var high = new AuditIssueEnhanced { Category = "Security", Severity = "High", Occurrences = 2, Title = "Check sign-ins" };
        var summary = AuditSummary.From([
            new() { Category = "System", Severity = "Low", Occurrences = int.MaxValue },
            new() { Category = "system", Severity = "Medium", Occurrences = int.MaxValue },
            high,
            new() { Category = "Security", Severity = "Low", Occurrences = 0 },
        ]);
        Assert.AreEqual(4, summary.Findings);
        Assert.AreEqual(2, summary.Categories.Count);
        Assert.AreEqual(4294967297L, summary.Occurrences);
        Assert.AreEqual("Security", summary.Categories[0].Category);
        Assert.AreSame(high, summary.Categories[0].Priority);
        Assert.AreEqual(3L, summary.Categories[0].Occurrences);
        Assert.AreEqual(1, summary.High);
        Assert.AreEqual(1, summary.Medium);
        Assert.AreEqual(2, summary.Low);
        Assert.AreEqual(0, AuditSummary.From(Array.Empty<AuditIssueEnhanced>()).Findings);
    }
}
