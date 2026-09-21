// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.Linq;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// How Windows event levels map onto the three-tier severity model. The dashboard shows a
/// severity breakdown, so a level that silently maps to the wrong tier is user-visible.
/// </summary>
[TestClass]
public sealed class EventLevelSeverityTests
{
    private static SecurityEvent Ev(int id, string provider, int level, string log = "System", string? message = null)
        => new(id, log, provider, level, DateTime.UtcNow, message);

    [TestMethod]
    public void LevelOneCriticalOutranksError()
    {
        // Level 1 is Critical and Level 2 is Error in Windows. If both land on the same tier
        // the dashboard cannot distinguish a crash from a recoverable error.
        var issues = AuditRuleEngine.Analyze(new List<SecurityEvent> { Ev(9999, "UnknownProvider", 1) });

        Assert.AreEqual(1, issues.Count);
        Assert.IsTrue(
            issues[0].IsHigh || string.Equals(issues[0].Severity, "Critical", StringComparison.OrdinalIgnoreCase),
            $"Level 1 must not rank below High; got '{issues[0].Severity}'.");
    }

    [TestMethod]
    public void LevelThreeWarningRanksBelowError()
    {
        var issues = AuditRuleEngine.Analyze(new List<SecurityEvent> { Ev(9999, "UnknownProvider", 3) });

        Assert.IsFalse(issues[0].IsHigh, "A warning must not be reported as High.");
    }

    [TestMethod]
    public void FindingsAreOrderedBySeverity()
    {
        var issues = AuditRuleEngine.Analyze(new List<SecurityEvent>
        {
            Ev(9998, "UnknownProvider", 3),
            Ev(9999, "UnknownProvider", 2),
            Ev(9997, "UnknownProvider", 1),
        });

        Assert.AreEqual(3, issues.Count);
        for (int i = 1; i < issues.Count; i++)
        {
            Assert.IsTrue(
                Rank(issues[i - 1]) >= Rank(issues[i]),
                "Findings must be ordered from most to least severe.");
        }
    }

    private static int Rank(AuditIssue i) => i.Severity.ToLowerInvariant() switch
    {
        "critical" => 3,
        "high" => 2,
        "medium" => 1,
        _ => 0,
    };

    [TestMethod]
    public void EveryLevelProducesAFinding()
    {
        // No level may be dropped: a scan that silently ignores an event is worse than one
        // that reports it at a low severity.
        foreach (int level in new[] { 1, 2, 3 })
        {
            var issues = AuditRuleEngine.Analyze(new List<SecurityEvent> { Ev(9999, "UnknownProvider", level) });
            Assert.AreEqual(1, issues.Count, $"Level {level} must produce a finding.");
        }
    }

    [TestMethod]
    public void CriticalEventIsNotDowngradedByItsRule()
    {
        // The rule table classifies some event IDs as Medium or Low for guidance purposes.
        // The Windows level still states how serious the event was, so a Critical event must
        // never surface as Medium or Low.
        var issues = AuditRuleEngine.Analyze(new List<SecurityEvent> { Ev(1000, "Application Error", 1) });

        Assert.AreEqual(1, issues.Count);
        Assert.IsTrue(issues[0].IsHigh, $"A Critical-level event must rank High; got '{issues[0].Severity}'.");
    }

    [TestMethod]
    public void ErrorEventIsNotDowngradedToLow()
    {
        // Event 1000 is a real crash; the rule marks it Medium, which is acceptable, but the
        // level must stop it from ever falling to Low.
        var issues = AuditRuleEngine.Analyze(new List<SecurityEvent> { Ev(1000, "Application Error", 2) });

        Assert.IsFalse(issues[0].IsLow, "Error-level events must not be reported as Low.");
    }

    [TestMethod]
    public void WarningEventKeepsItsRuleSeverity()
    {
        // The floor only raises severity; a warning stays where the rule put it.
        var issues = AuditRuleEngine.Analyze(new List<SecurityEvent>
        {
            Ev(7040, "Service Control Manager", 3, "System", "Start type changed."),
        });

        Assert.AreEqual(1, issues.Count);
        Assert.IsFalse(issues[0].IsHigh, "A warning must not be promoted to High.");
    }

    [TestMethod]
    public void FindingsCarryTheirChannel()
    {
        // The page filters findings by LogName and counts them per channel. A finding without
        // a channel is dropped from the list, which is what made a scan that found issues
        // display none of them.
        var issues = AuditRuleEngine.Analyze(new List<SecurityEvent>
        {
            Ev(9999, "UnknownProvider", 2, "System"),
            Ev(9998, "UnknownProvider", 3, "Application"),
        });

        Assert.AreEqual(2, issues.Count);
        foreach (var issue in issues)
        {
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(issue.LogName),
                $"Finding {issue.EventId} must carry its source channel.");
        }

        CollectionAssert.AreEquivalent(
            new[] { "System", "Application" },
            issues.Select(i => i.LogName).ToArray(),
            "Each finding keeps the channel it came from.");
    }

    [TestMethod]
    public void RepeatedEventsAreGroupedAndCounted()
    {
        var events = Enumerable.Range(0, 24)
            .Select(_ => Ev(11, "Microsoft-Windows-FilterManager", 3))
            .ToList();

        var issues = AuditRuleEngine.Analyze(events);

        Assert.AreEqual(1, issues.Count, "Identical events collapse into one finding.");
        Assert.AreEqual(24, issues[0].Occurrences);
    }
}
