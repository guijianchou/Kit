// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using Kit.AIHubLib.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Scheduling rules and scan-window arithmetic. These decide whether a scheduled audit
/// runs and how much of the log it re-reads, so the boundaries are pinned here.
/// </summary>
[TestClass]
public sealed class AuditScheduleTests
{
    [TestMethod]
    public void ZeroOrNegativeIntervalDisablesScheduling()
    {
        Assert.IsFalse(AuditSchedule.IsSchedulingEnabled(0));
        Assert.IsFalse(AuditSchedule.IsSchedulingEnabled(-5));
        Assert.AreEqual(0, AuditSchedule.NormalizeIntervalHours(0), "Off must stay off.");
        Assert.IsNull(AuditSchedule.NextRunUtc(null, DateTime.UtcNow, 0));
        Assert.IsFalse(AuditSchedule.IsDue(null, DateTime.UtcNow, 0), "A disabled schedule is never due.");
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
        // A freshly enabled schedule must not wait a whole interval before running once.
        Assert.IsTrue(AuditSchedule.IsDue(null, DateTime.UtcNow, 24));
    }

    [TestMethod]
    public void ScheduleBecomesDueOnceTheIntervalElapsed()
    {
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.IsFalse(AuditSchedule.IsDue(now.AddHours(-1), now, 24), "One hour into a daily schedule is not due.");
        Assert.IsFalse(AuditSchedule.IsDue(now.AddHours(-23), now, 24), "Just under the interval is not due.");
        Assert.IsTrue(AuditSchedule.IsDue(now.AddHours(-24), now, 24), "Exactly the interval is due.");
        Assert.IsTrue(AuditSchedule.IsDue(now.AddHours(-30), now, 24), "Overdue is due.");
    }

    [TestMethod]
    public void NextRunNeverReportsAPastTime()
    {
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

        DateTime? upcoming = AuditSchedule.NextRunUtc(now.AddHours(-1), now, 24);
        Assert.AreEqual(now.AddHours(23), upcoming);

        DateTime? overdue = AuditSchedule.NextRunUtc(now.AddHours(-48), now, 24);
        Assert.AreEqual(now, overdue, "An overdue schedule reports now, not a past instant.");

        DateTime? fresh = AuditSchedule.NextRunUtc(null, now, 24);
        Assert.AreEqual(now.AddHours(24), fresh);
    }

    [TestMethod]
    public void WindowFallsBackToTheFullRangeWithoutAUsablePreviousScan()
    {
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan range = TimeSpan.FromDays(1);

        (DateTime from, DateTime to) = AuditSchedule.ComputeWindow(null, now, range);
        Assert.AreEqual(now - range, from);
        Assert.AreEqual(now, to);

        // A previous scan older than the range, or in the future, must not shrink the window.
        (from, _) = AuditSchedule.ComputeWindow(now - TimeSpan.FromDays(5), now, range);
        Assert.AreEqual(now - range, from);

        (from, _) = AuditSchedule.ComputeWindow(now.AddMinutes(5), now, range);
        Assert.AreEqual(now - range, from);
    }

    [TestMethod]
    public void WindowContinuesFromThePreviousScanWithOverlap()
    {
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan range = TimeSpan.FromDays(7);
        DateTime lastScan = now.AddHours(-2);

        (DateTime from, DateTime to) = AuditSchedule.ComputeWindow(lastScan, now, range);

        Assert.AreEqual(lastScan - AuditSchedule.IncrementalOverlap, from, "The window must overlap the previous scan slightly.");
        Assert.AreEqual(now, to);
        Assert.IsTrue(AuditSchedule.IsIncremental(lastScan, now, range));
    }

    [TestMethod]
    public void IncrementalWindowContinuesFromAPreviousScanInsideTheRange()
    {
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan range = TimeSpan.FromHours(3);

        // The previous scan sits comfortably inside the range, so the window starts just
        // before it (never at the range start) and stays inside the selected period.
        (DateTime from, DateTime to) = AuditSchedule.ComputeWindow(now.AddHours(-2), now, range);

        Assert.AreEqual(now.AddHours(-2) - AuditSchedule.IncrementalOverlap, from);
        Assert.IsTrue(from > now - range, "The incremental window must stay inside the selected range.");
        Assert.AreEqual(now, to);
    }

    [TestMethod]
    public void OverlapIsClampedWhenItWouldReachBeforeTheRangeStart()
    {
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan range = TimeSpan.FromMinutes(30);

        // The previous scan is 29 minutes ago: the 2 minute overlap would reach before the
        // 30 minute range start, so the clamp must pull the window back to the range start.
        DateTime lastScan = now.AddMinutes(-29);
        (DateTime from, _) = AuditSchedule.ComputeWindow(lastScan, now, range);

        Assert.AreEqual(now - range, from, "The overlap must not widen the scan beyond the selected range.");
        Assert.IsFalse(AuditSchedule.IsIncremental(lastScan, now, range), "A clamped window is the full range again.");
    }

    [TestMethod]
    public void FullRangeScanIsNotReportedAsIncremental()
    {
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        TimeSpan range = TimeSpan.FromDays(1);

        Assert.IsFalse(AuditSchedule.IsIncremental(null, now, range));
        Assert.IsFalse(AuditSchedule.IsIncremental(now - TimeSpan.FromDays(9), now, range));
    }
}
