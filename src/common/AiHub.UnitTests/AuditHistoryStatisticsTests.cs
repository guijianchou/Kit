// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using Kit.AIHubLib.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The dashboard activity counters are derived from stored history; these tests pin the
/// aggregation so a placeholder value can never satisfy them again.
/// </summary>
[TestClass]
public sealed class AuditHistoryStatisticsTests
{
    [TestMethod]
    public void EmptyHistoryReportsNoActivity()
    {
        var statistics = AuditHistoryStatistics.From(null);

        Assert.IsFalse(statistics.HasHistory);
        Assert.AreEqual(0, statistics.AuditCount);
        Assert.AreEqual(0, statistics.ActiveDayCount);
        Assert.AreEqual("—", statistics.AverageHealthScoreText);
    }

    [TestMethod]
    public void CountsAuditsFindingsAndDistinctDays()
    {
        var now = DateTime.UtcNow;
        var history = new List<AuditResult>
        {
            new() { Timestamp = now, HealthScore = 90, Findings = new List<AuditIssueEnhanced> { new(), new() } },
            new() { Timestamp = now.AddHours(-2), HealthScore = 70, Findings = new List<AuditIssueEnhanced> { new() } },
            new() { Timestamp = now.AddDays(-1), HealthScore = 80, Findings = new List<AuditIssueEnhanced>() },
        };

        var statistics = AuditHistoryStatistics.From(history);

        Assert.AreEqual(3, statistics.AuditCount);
        Assert.AreEqual(3, statistics.TotalFindings);
        Assert.AreEqual(2, statistics.LatestFindingCount);
        Assert.AreEqual(2, statistics.ActiveDayCount, "Two audits share a day, so only two active days exist.");
        Assert.AreEqual("3", statistics.AuditCountText);
        Assert.AreEqual("2", statistics.ActiveDayCountText);
    }

    [TestMethod]
    public void AverageHealthScoreIsRoundedToOneDecimal()
    {
        var now = DateTime.UtcNow;
        var history = new List<AuditResult>
        {
            new() { Timestamp = now, HealthScore = 80 },
            new() { Timestamp = now.AddHours(-1), HealthScore = 81 },
            new() { Timestamp = now.AddHours(-2), HealthScore = 81 },
        };

        var statistics = AuditHistoryStatistics.From(history);

        // 242 / 3 = 80.666...
        Assert.AreEqual(80.7, statistics.AverageHealthScore, 0.001);
        Assert.AreEqual("80.7", statistics.AverageHealthScoreText);
    }

    [TestMethod]
    public void LatestAuditDrivesTimestampAndFindingCount()
    {
        var now = DateTime.UtcNow;
        var history = new List<AuditResult>
        {
            new() { Timestamp = now.AddDays(-3), HealthScore = 100 },
            new() { Timestamp = now, HealthScore = 60, Findings = new List<AuditIssueEnhanced> { new(), new(), new() } },
        };

        var statistics = AuditHistoryStatistics.From(history);

        Assert.AreEqual(3, statistics.LatestFindingCount);
        Assert.IsNotNull(statistics.LatestAuditUtc);
        Assert.AreEqual(now, statistics.LatestAuditUtc.Value);
    }
}
