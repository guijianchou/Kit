// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using Kit.AIHubLib.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Trends aggregation. The view must reflect stored history only, so the windowing,
/// dense-day handling and category grouping are pinned here.
/// </summary>
[TestClass]
public sealed class AuditTrendsTests
{
    private static readonly DateTime Now = new(2026, 3, 15, 12, 0, 0, DateTimeKind.Local);

    private static AuditResult Audit(DateTime localTime, int health, params (string Severity, string Category)[] findings)
    {
        var result = new AuditResult
        {
            Timestamp = localTime.ToUniversalTime(),
            HealthScore = health,
            Findings = new List<AuditIssueEnhanced>(),
        };

        foreach ((string severity, string category) in findings)
        {
            result.Findings.Add(new AuditIssueEnhanced { Severity = severity, Category = category });
        }

        return result;
    }

    [TestMethod]
    public void UnsupportedWindowsFallBackToSevenDays()
    {
        Assert.AreEqual(1, AuditTrends.NormalizeWindowDays(1));
        Assert.AreEqual(7, AuditTrends.NormalizeWindowDays(7));
        Assert.AreEqual(30, AuditTrends.NormalizeWindowDays(30));
        Assert.AreEqual(7, AuditTrends.NormalizeWindowDays(5), "An unsupported window must not silently shrink the range.");
        Assert.AreEqual(7, AuditTrends.NormalizeWindowDays(0));
    }

    [TestMethod]
    public void EmptyHistoryStillProducesADenseSeriesOfEmptyDays()
    {
        var series = AuditTrends.BuildDailySeries(null, Now, 7);

        Assert.AreEqual(7, series.Count);
        Assert.IsTrue(System.Linq.Enumerable.All(series, day => !day.HasData));
        Assert.AreEqual(0, AuditTrends.CountActiveDays(series));
        Assert.AreEqual(0, series[0].IntensityLevel, "A day without activity has no intensity.");
    }

    [TestMethod]
    public void SeriesIsOldestFirstAndEndsToday()
    {
        var series = AuditTrends.BuildDailySeries(null, Now, 7);

        Assert.AreEqual(Now.Date.AddDays(-6), series[0].Date);
        Assert.AreEqual(Now.Date, series[^1].Date, "The series must end on the current day.");
        Assert.AreEqual("03-15", series[^1].Label);
    }

    [TestMethod]
    public void DaysAggregateAuditsAndSeverities()
    {
        var history = new List<AuditResult>
        {
            Audit(Now, 90, ("High", "Login"), ("Low", "System")),
            Audit(Now.AddHours(-2), 80, ("Medium", "Login")),
        };

        var series = AuditTrends.BuildDailySeries(history, Now, 7);
        AuditTrendDay today = series[^1];

        Assert.IsTrue(today.HasData);
        Assert.AreEqual(2, today.AuditCount);
        Assert.AreEqual(1, today.High);
        Assert.AreEqual(1, today.Medium);
        Assert.AreEqual(1, today.Low);
        Assert.AreEqual(3, today.Total);
        Assert.AreEqual(85, today.HealthScore);
    }

    [TestMethod]
    public void AuditsOutsideTheWindowAreExcluded()
    {
        var history = new List<AuditResult>
        {
            Audit(Now.AddDays(-30), 50, ("High", "Login")),
            Audit(Now, 95, ("Low", "System")),
        };

        var series = AuditTrends.BuildDailySeries(history, Now, 7);

        Assert.AreEqual(1, AuditTrends.CountActiveDays(series));
        Assert.AreEqual(1, series[^1].Total);
    }

    [TestMethod]
    public void IntensityGrowsWithFindingCount()
    {
        AuditTrendDay Day(int total) => new() { Date = Now.Date, AuditCount = 1, Low = total };

        Assert.AreEqual(0, Day(0).IntensityLevel);
        Assert.AreEqual(1, Day(2).IntensityLevel);
        Assert.AreEqual(2, Day(5).IntensityLevel);
        Assert.AreEqual(3, Day(12).IntensityLevel);
        Assert.AreEqual(4, Day(40).IntensityLevel);
    }

    [TestMethod]
    public void CategoryTotalsAreOrderedByFrequencyAndFoldMissingCategories()
    {
        var history = new List<AuditResult>
        {
            Audit(Now, 90, ("High", "Login"), ("High", "Login"), ("Low", "System")),
            Audit(Now.AddHours(-1), 90, ("Medium", string.Empty)),
        };

        var totals = AuditTrends.BuildCategoryTotals(history, Now, 7);

        Assert.AreEqual(3, totals.Count);
        Assert.AreEqual("Login", totals[0].Name);
        Assert.AreEqual(2, totals[0].Count);
        Assert.IsTrue(totals.Any(t => t.Name == "Other"), "A finding without a category must not be dropped.");
    }

    [TestMethod]
    public void WindowedFindingsAreNewestFirstAndScopedToTheWindow()
    {
        var history = new List<AuditResult>
        {
            Audit(Now.AddDays(-10), 90, ("High", "Old")),
            Audit(Now, 90, ("Low", "Fresh")),
        };

        var findings = AuditTrends.BuildWindowedFindings(history, Now, 7);

        Assert.AreEqual(1, findings.Count, "Findings outside the window must be excluded.");
        Assert.AreEqual("Fresh", findings[0].Issue.Category);
    }
}
