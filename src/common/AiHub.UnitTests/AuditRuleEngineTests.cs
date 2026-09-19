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
/// The rule engine was extracted from the page view model, so these tests pin the
/// behaviour that used to be reachable only through the UI.
/// </summary>
[TestClass]
public sealed class AuditRuleEngineTests
{
    private static SecurityEvent Event(int id, string log = "System", string provider = "TestProvider", int? level = 2, string? message = null)
        => new(id, log, provider, level, DateTime.UtcNow, message ?? $"Event {id} happened.");

    [TestMethod]
    public void NoEventsProduceNoFindings()
    {
        List<AuditIssueEnhanced> issues = AuditRuleEngine.Analyze(new List<SecurityEvent>());

        Assert.AreEqual(0, issues.Count);
    }

    [TestMethod]
    public void KernelPowerEventIsHighSeverityStability()
    {
        List<AuditIssueEnhanced> issues = AuditRuleEngine.Analyze(new List<SecurityEvent> { Event(41, "System", "Microsoft-Windows-Kernel-Power", 1) });

        Assert.AreEqual(1, issues.Count);
        AuditIssueEnhanced issue = issues[0];
        Assert.IsTrue(issue.IsHigh, "An unexpected kernel shutdown is a high-severity finding.");
        Assert.AreEqual("Stability", issue.Category);
        StringAssert.Contains(issue.TitleZh, "内核");
    }

    [TestMethod]
    public void IdenticalEventPatternsAreGroupedIntoOneFinding()
    {
        var events = new List<SecurityEvent>
        {
            Event(1000, "Application", "App", 2),
            Event(1000, "Application", "App", 2),
            Event(1000, "Application", "App", 2),
        };

        List<AuditIssueEnhanced> issues = AuditRuleEngine.Analyze(events);

        Assert.AreEqual(1, issues.Count, "The same event pattern must collapse into one finding.");
        Assert.AreEqual(3, issues[0].Occurrences, "Grouped events report their occurrence count.");
    }

    [TestMethod]
    public void DifferentProvidersProduceSeparateFindings()
    {
        var events = new List<SecurityEvent>
        {
            Event(1000, "Application", "AppA", 2),
            Event(1000, "Application", "AppB", 2),
        };

        List<AuditIssueEnhanced> issues = AuditRuleEngine.Analyze(events);

        Assert.AreEqual(2, issues.Count, "Different providers are different findings.");
    }

    [TestMethod]
    public void EveryFindingCarriesBilingualTextAndADefaultRecommendation()
    {
        var events = new List<SecurityEvent>
        {
            Event(41, "System", "Microsoft-Windows-Kernel-Power", 1),
            Event(1000, "Application", "App", 2),
            Event(7045, "System", "Service Control Manager", 4),
        };

        List<AuditIssueEnhanced> issues = AuditRuleEngine.Analyze(events);

        Assert.IsTrue(issues.Count > 0);
        foreach (AuditIssueEnhanced issue in issues)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(issue.Title), "English title is required.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(issue.TitleZh), "Chinese title is required.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(issue.Description), "English description is required.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(issue.DescriptionZh), "Chinese description is required.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(issue.PriorityActionText), "An action must always be offered.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(issue.DisplayTitle));
            Assert.IsTrue(issue.Occurrences >= 1);
            Assert.IsFalse(string.IsNullOrWhiteSpace(issue.Key), "Findings carry a stable key.");
        }
    }

    [TestMethod]
    public void FindingsAreOrderedBySeverityThenFrequency()
    {
        var events = new List<SecurityEvent>
        {
            Event(41, "System", "Microsoft-Windows-Kernel-Power", 1),   // high
            Event(1000, "Application", "App", 2),                        // medium
            Event(1000, "Application", "App", 2),                        // same medium, now x2
            Event(7045, "System", "Service Control Manager", 4),         // medium
        };

        List<AuditIssueEnhanced> issues = AuditRuleEngine.Analyze(events);

        Assert.IsTrue(issues[0].IsHigh, "High severity findings come first.");
        Assert.IsFalse(issues[^1].IsHigh);
    }

    [TestMethod]
    public void LongMessagesAreTruncatedInTheSummary()
    {
        string longMessage = new string('x', 400);
        List<AuditIssueEnhanced> issues = AuditRuleEngine.Analyze(new List<SecurityEvent> { Event(1000, "Application", "App", 2, longMessage) });

        Assert.AreEqual(1, issues.Count);
        Assert.IsTrue(issues[0].Description.Length <= 400, "The engine summarises rather than echoing the raw message.");
    }

    [TestMethod]
    public void ServiceInstallationIsFlaggedAsConfiguration()
    {
        List<AuditIssueEnhanced> issues = AuditRuleEngine.Analyze(new List<SecurityEvent> { Event(7045, "System", "Service Control Manager", 4) });

        Assert.AreEqual(1, issues.Count);
        Assert.AreEqual("Configuration", issues[0].Category);
    }

    [TestMethod]
    public void EngineIsUsableWithoutAnyUiDependency()
    {
        // The extraction exists so a headless worker can audit; a static entry point with no
        // view-model state is what makes that possible.
        var method = typeof(AuditRuleEngine).GetMethod(nameof(AuditRuleEngine.Analyze));

        Assert.IsNotNull(method);
        Assert.IsTrue(method.IsStatic, "The engine must be callable without an instance.");
        Assert.AreEqual(typeof(List<AuditIssueEnhanced>), method.ReturnType);
    }
}
