// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The 0-100 health score drives the dashboard grade and verdict, so its weighting and
/// clamping are pinned here.
/// </summary>
[TestClass]
public sealed class HealthScoreCalculatorTests
{
    private static AuditIssueEnhanced Issue(string severity) => new() { Severity = severity };

    [TestMethod]
    public void NoIssuesScoresPerfect()
    {
        Assert.AreEqual(100, HealthScoreCalculator.Calculate(null));
        Assert.AreEqual(100, HealthScoreCalculator.Calculate(new List<AuditIssueEnhanced>()));
    }

    [TestMethod]
    public void SingleIssueDeductionsMatchTheDocumentedWeights()
    {
        Assert.AreEqual(85, HealthScoreCalculator.Calculate(new List<AuditIssueEnhanced> { Issue("High") }));
        Assert.AreEqual(94, HealthScoreCalculator.Calculate(new List<AuditIssueEnhanced> { Issue("Medium") }));

        // Low is 1.5 points rounded up per issue, so one Low deducts 2 points,
        // not 1.5 and not 1 (the policy README rounds this to "1").
        Assert.AreEqual(98, HealthScoreCalculator.Calculate(new List<AuditIssueEnhanced> { Issue("Low") }));
    }

    [TestMethod]
    public void HighSeverityDeductionIsCappedAtSixty()
    {
        var issues = new List<AuditIssueEnhanced>();
        for (int i = 0; i < 20; i++)
        {
            issues.Add(Issue("High"));
        }

        // 20 * 15 = 300, capped at 60 -> score floors at 40, never 0.
        Assert.AreEqual(40, HealthScoreCalculator.Calculate(issues));
    }

    [TestMethod]
    public void MediumAndLowDeductionsAreAlsoCapped()
    {
        var medium = new List<AuditIssueEnhanced>();
        var low = new List<AuditIssueEnhanced>();
        for (int i = 0; i < 50; i++)
        {
            medium.Add(Issue("Medium"));
            low.Add(Issue("Low"));
        }

        // Medium caps at 25 -> 75. Low: ceil(50 * 1.5) = 75, capped at 15 -> 85.
        Assert.AreEqual(75, HealthScoreCalculator.Calculate(medium));
        Assert.AreEqual(85, HealthScoreCalculator.Calculate(low));
    }

    [TestMethod]
    public void AllThreeSeveritiesCombineAndClampAtZero()
    {
        var issues = new List<AuditIssueEnhanced>();
        for (int i = 0; i < 10; i++)
        {
            issues.Add(Issue("High"));
            issues.Add(Issue("Medium"));
            issues.Add(Issue("Low"));
        }

        // 60 + 25 + 15 = 100 -> clamped to 0.
        Assert.AreEqual(0, HealthScoreCalculator.Calculate(issues));
    }

    [TestMethod]
    public void UnknownSeverityDoesNotDeduct()
    {
        // Anything that is not High/Medium/Low is ignored rather than guessed at.
        Assert.AreEqual(100, HealthScoreCalculator.Calculate(new List<AuditIssueEnhanced> { Issue("Unknown") }));
    }

    [TestMethod]
    public void ScoreIsAlwaysWithinTheZeroToHundredRange()
    {
        var allSeverities = new List<AuditIssueEnhanced>
        {
            Issue("High"), Issue("Medium"), Issue("Low"),
        };

        int score = HealthScoreCalculator.Calculate(allSeverities);

        Assert.IsTrue(score is >= 0 and <= 100, $"Score {score} must stay inside 0-100.");
    }
}
