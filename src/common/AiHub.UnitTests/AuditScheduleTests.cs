// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using Kit.AIHubLib.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Scheduling rules and scan-window arithmetic, following the original GetScanStart
/// semantics. A regression here silently produced empty audits, so the intent split
/// between manual and scheduled runs is pinned explicitly.
/// </summary>
[TestClass]
public sealed class AuditScheduleTests
{
    private static readonly DateTime Now = new(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void ZeroOrNegativeIntervalDisablesScheduling()
    {
        Assert.IsFalse(AuditSchedule.IsSchedulingEnabled(0));
        Assert.IsFalse(AuditSchedule.IsSchedulingEnabled(-5));
        Assert.AreEqual(0, AuditSchedule.NormalizeIntervalHours(0), "Off must stay off.");
        Assert.IsNull(AuditSchedule.NextRunUtc(null, Now, 0));
        Assert.IsFalse(AuditSchedule.IsDue(null, Now, 0), "A disabled schedule is never due.");
    }

    [TestMethod]
    public void IntervalIsClampedIntoTheSupportedRange()
    {
        Assert.AreEqual(AuditSchedule.MinimumIntervalHours, AuditSchedule.NormalizeIntervalHours(1));
        Assert.AreEqual(AuditSchedule.MaximumIntervalHours, AuditSchedule.NormalizeIntervalHours(999));
        Assert.AreEqual(24, AuditSchedule.NormalizeIntervalHours(24));
    }

    [TestMethod]
    public void FirstRunIsImmediatelyDue()
    {
        Assert.IsTrue(AuditSchedule.IsDue(null, Now, 24));
    }

    [TestMethod]
    public void ScheduleBecomesDueOnceTheIntervalElapsed()
    {
        Assert.IsFalse(AuditSchedule.IsDue(Now.AddHours(-1), Now, 24));
        Assert.IsFalse(AuditSchedule.IsDue(Now.AddHours(-23), Now, 24));
        Assert.IsTrue(AuditSchedule.IsDue(Now.AddHours(-24), Now, 24));
        Assert.IsTrue(AuditSchedule.IsDue(Now.AddHours(-30), Now, 24));
    }

    [TestMethod]
    public void NextRunNeverReportsAPastTime()
    {
        Assert.AreEqual(Now.AddHours(23), AuditSchedule.NextRunUtc(Now.AddHours(-1), Now, 24));
        Assert.AreEqual(Now, AuditSchedule.NextRunUtc(Now.AddHours(-48), Now, 24));
        Assert.AreEqual(Now.AddHours(24), AuditSchedule.NextRunUtc(null, Now, 24));
    }

    [TestMethod]
    public void ManualFullScanAlwaysCoversTheWholeSelectedRange()
    {
        // The regression this guards: a manual scan must not shrink to the time since the
        // previous run, otherwise every re-scan after the first returns no findings.
        DateTime previousScan = Now.AddMinutes(-5);

        DateTime from = AuditSchedule.ComputeScanStart(ScanIntent.ManualFull, previousScan, Now, rangeDays: 2);

        Assert.AreEqual(Now.AddDays(-2), from, "A manual full scan ignores the previous scan.");
    }

    [TestMethod]
    public void ReanalyzeAlsoCoversTheWholeSelectedRange()
    {
        DateTime previousScan = Now.AddMinutes(-5);

        DateTime from = AuditSchedule.ComputeScanStart(ScanIntent.Reanalyze, previousScan, Now, rangeDays: 7);

        Assert.AreEqual(Now.AddDays(-7), from);
    }

    [TestMethod]
    public void ManualScanCoversTheRangeEvenWhenThePreviousScanIsRecent()
    {
        // The regression this guards: a second manual scan must not shrink to the time since
        // the first one, which is what produced empty audits.
        DateTime from1 = AuditSchedule.ComputeScanStart(ScanIntent.ManualFull, null, Now, 2);
        DateTime from2 = AuditSchedule.ComputeScanStart(ScanIntent.ManualFull, Now.AddSeconds(-10), Now, 2);

        Assert.AreEqual(from1, from2, "Consecutive manual scans must scan the same window.");
        Assert.AreEqual(Now.AddDays(-2), from2);
    }

    [TestMethod]
    public void ScheduledRunResumesFromThePreviousScanWithOverlap()
    {
        DateTime previousScan = Now.AddHours(-2);

        DateTime from = AuditSchedule.ComputeScanStart(ScanIntent.Scheduled, previousScan, Now, rangeDays: 7);

        Assert.AreEqual(previousScan - AuditSchedule.IncrementalOverlap, from);
    }

    [TestMethod]
    public void ScheduledRunFallsBackToTheRangeWhenThePreviousScanIsStale()
    {
        DateTime from = AuditSchedule.ComputeScanStart(
            ScanIntent.Scheduled, Now.AddDays(-30), Now, rangeDays: 7);

        Assert.AreEqual(Now.AddDays(-7), from, "A previous scan outside the range is not a resume point.");
    }

    [TestMethod]
    public void ScheduledRunWithoutAPreviousScanCoversTheWholeRange()
    {
        Assert.AreEqual(Now.AddDays(-2), AuditSchedule.ComputeScanStart(ScanIntent.Scheduled, null, Now, 2));
    }

    [TestMethod]
    public void FastScanCoversTheCurrentDay()
    {
        // A quick scan covers today, matching the original app, so repeated quick scans
        // examine the same period and their results stay comparable.
        DateTime from = AuditSchedule.ComputeScanStart(ScanIntent.Fast, Now.AddMinutes(-1), Now, 7, fastRangeHours: 1);
        DateTime todayStartUtc = DateTime.SpecifyKind(Now.ToLocalTime().Date, DateTimeKind.Local).ToUniversalTime();

        Assert.AreEqual(todayStartUtc, from, "A fast scan starts at local midnight.");
        Assert.IsTrue(from <= Now, "The quick window must not start in the future.");
    }

    [TestMethod]
    public void FastScanIgnoresTheSelectedRange()
    {
        DateTime shortRange = AuditSchedule.ComputeScanStart(ScanIntent.Fast, null, Now, 2);
        DateTime longRange = AuditSchedule.ComputeScanStart(ScanIntent.Fast, null, Now, 30);

        Assert.AreEqual(shortRange, longRange, "The selected range only affects a full scan.");
    }

    [TestMethod]
    public void SupportedRangesAreTwoDaysOneWeekAndOneMonth()
    {
        CollectionAssert.AreEqual(
            new[] { 2, 7, 30 },
            AuditSchedule.SupportedRangeDays,
            "The full-scan range list is the documented 2d / 1w / 1mo set.");
    }

    [TestMethod]
    public void FullScanHonoursTheOneMonthRange()
    {
        Assert.AreEqual(Now.AddDays(-30), AuditSchedule.ComputeScanStart(ScanIntent.ManualFull, null, Now, 30));
    }

    [TestMethod]
    public void UnsupportedRangeFallsBackToTheShortestSupportedRange()
    {
        // 2 days is the shortest selectable range, so an unexpected value uses it.
        Assert.AreEqual(Now.AddDays(-2), AuditSchedule.ComputeScanStart(ScanIntent.ManualFull, null, Now, rangeDays: 5));
        Assert.AreEqual(Now.AddDays(-2), AuditSchedule.ComputeScanStart(ScanIntent.ManualFull, null, Now, rangeDays: 0));
    }

    [TestMethod]
    public void ScheduledRunWithoutAPreviousScanUsesTheShortestRange()
    {
        Assert.AreEqual(Now.AddDays(-2), AuditSchedule.ComputeScanStart(ScanIntent.Scheduled, null, Now, 2));
    }

    [TestMethod]
    public void OnlyScheduledRunsReportThemselvesAsIncremental()
    {
        DateTime previousScan = Now.AddHours(-2);

        Assert.IsTrue(AuditSchedule.IsIncremental(ScanIntent.Scheduled, previousScan, Now, 7));
        Assert.IsFalse(AuditSchedule.IsIncremental(ScanIntent.ManualFull, previousScan, Now, 7));
        Assert.IsFalse(AuditSchedule.IsIncremental(ScanIntent.Reanalyze, previousScan, Now, 7));
        Assert.IsFalse(AuditSchedule.IsIncremental(ScanIntent.Fast, previousScan, Now, 7));
    }

    [TestMethod]
    public void ResumingNeverStartsBeforeTheSelectedRange()
    {
        DateTime previousScan = Now.AddMinutes(-1);

        DateTime from = AuditSchedule.ComputeScanStart(ScanIntent.Scheduled, previousScan, Now, rangeDays: 1);

        Assert.IsTrue(from >= Now.AddDays(-1), "The resume point must stay inside the selected range.");
    }

    [TestMethod]
    public void WindowPairMatchesTheComputedStart()
    {
        (DateTime from, DateTime to) = AuditSchedule.ComputeWindow(ScanIntent.ManualFull, null, Now, 2);

        Assert.AreEqual(Now.AddDays(-2), from);
        Assert.AreEqual(Now, to);
    }
}
