// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
public sealed class AuditFinalAssessmentTests
{
    [TestMethod]
    public void CompleteReviewCanDowngradeOrDismissWarningEvidence()
    {
        SecurityEvent[] events = [new(9999, "System", "WarningProvider", 3, DateTime.UtcNow, "Operational warning")];
        var rules = AuditRuleEngine.Analyze(events);
        Assert.IsTrue(rules.Single().IsMedium);
        var result = AIHubPageViewModel.MergeIssues(rules, [Finding("Low")], events, replaceReviewedEvidence: true);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Low", result[0].Severity);
        Assert.AreEqual("System", result[0].LogName);
        Assert.AreEqual("9999", result[0].EventId);
        Assert.AreEqual("WarningProvider", result[0].Source);
        Assert.AreEqual(3, result[0].ObservedEventLevel);
        Assert.IsTrue(rules[0].IsMedium, "The original evidence remains unchanged for callers holding it.");
        Assert.AreEqual(
            0,
            AIHubPageViewModel.MergeIssues(rules, [], events, replaceReviewedEvidence: true).Count,
            "A complete review can still dismiss a warning-level rule interpretation.");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void CompleteReviewCannotDowngradeOrOmitCriticalAndErrorEvidence(int level)
    {
        SecurityEvent[] events = [new(9999, "System", "ObservedProvider", level, DateTime.UtcNow, "Observed failure")];
        var rules = AuditRuleEngine.Analyze(events);
        var ai = Finding("Low");

        var result = AIHubPageViewModel.MergeIssues(rules, [ai], events, replaceReviewedEvidence: true);

        Assert.AreEqual(1, result.Count, "An explicit AI finding replaces its local duplicate.");
        Assert.IsTrue(result[0].IsHigh, "The original Windows level sets the minimum risk even after a complete AI review.");
        Assert.AreEqual(level, result[0].ObservedEventLevel);
        Assert.AreEqual("Low", ai.Severity, "Projection must not mutate the reusable model response.");
        var omitted = AIHubPageViewModel.MergeIssues(rules, [], events, replaceReviewedEvidence: true);
        Assert.AreEqual(1, omitted.Count, "An empty AI response cannot erase an observed Critical or Error event.");
        Assert.IsTrue(omitted[0].IsHigh);
        Assert.AreEqual(level, omitted[0].ObservedEventLevel);
    }

    [TestMethod]
    public void MergedShutdownUsesBothEventLevelsWithoutDuplicatingLocalFindings()
    {
        DateTime now = DateTime.UtcNow;
        SecurityEvent[] events =
        [
            new(6008, "System", "EventLog", 2, now, "The previous shutdown was unexpected."),
            new(41, "System", "Microsoft-Windows-Kernel-Power", 1, now.AddSeconds(-25), "The system rebooted without cleanly shutting down."),
        ];
        var ai = Finding("Medium");
        ai.RelatedEventRefs.Add("item-000002");
        ai.Occurrences = 2;

        var result = AIHubPageViewModel.MergeIssues(AuditRuleEngine.Analyze(events), [ai], events, replaceReviewedEvidence: true);

        Assert.AreEqual(1, result.Count, "The two explicitly linked shutdown records remain one AI finding.");
        Assert.AreEqual("6008", result[0].EventId, "The primary reference determines the displayed event identity.");
        Assert.AreEqual("EventLog", result[0].Source);
        Assert.IsTrue(result[0].IsHigh);
        Assert.AreEqual(1, result[0].ObservedEventLevel, "The related Critical record outranks the primary Error record.");
        Assert.AreEqual(2, result[0].Occurrences, "Occurrences remain evidence records, not an inferred shutdown incident count.");
    }

    [TestMethod]
    public void RelatedCriticalEvidenceRaisesAnOtherwiseWarningFinding()
    {
        DateTime now = DateTime.UtcNow;
        SecurityEvent[] events =
        [
            new(41, "System", "Microsoft-Windows-Kernel-Power", 1, now.AddMinutes(-1), "Shutdown"),
            new(9999, "Application", "WarningProvider", 3, now, "Related warning"),
        ];
        var ai = Finding("Low");
        ai.EventRef = "item-000002";
        ai.RelatedEventRefs = ["item-000002", "item-000001"];

        var result = AIHubPageViewModel.MergeIssues(AuditRuleEngine.Analyze(events), [ai], events, replaceReviewedEvidence: true);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("WarningProvider", result[0].Source);
        Assert.AreEqual("Application", result[0].LogName);
        Assert.AreEqual(1, result[0].ObservedEventLevel);
        Assert.IsTrue(result[0].IsHigh, "All referenced evidence contributes to the floor, not only the primary record.");
    }

    [TestMethod]
    public void CompleteReviewRetainsOnlyTheUnreferencedHighRiskPartOfAGroup()
    {
        DateTime now = DateTime.UtcNow;
        SecurityEvent[] events =
        [
            new(1000, "Application", "Application Error", 2, now, "First crash"),
            new(1000, "Application", "Application Error", 2, now.AddMinutes(-1), "Second crash"),
            new(1000, "Application", "Application Error", 3, now.AddMinutes(-2), "Related warning"),
        ];

        var result = AIHubPageViewModel.MergeIssues(AuditRuleEngine.Analyze(events), [Finding("Low")], events, replaceReviewedEvidence: true);

        Assert.AreEqual(2, result.Count, "One AI finding covers the first error; one local finding preserves the omitted second error.");
        Assert.IsTrue(result.All(issue => issue.IsHigh));
        Assert.IsTrue(result.All(issue => issue.ObservedEventLevel == 2));
        Assert.AreEqual(2, result.Sum(issue => issue.Occurrences), "Covered evidence must not be counted again in the rebuilt local group.");
    }

    [TestMethod]
    public void ReferenceIdentityPreventsUnrelatedProvidersFromSharingTheHighRiskFloor()
    {
        DateTime now = DateTime.UtcNow;
        SecurityEvent[] events =
        [
            new(9999, "System", "FailingProvider", 2, now, "Error"),
            new(9999, "System", "WarningProvider", 3, now.AddMinutes(-1), "Warning"),
        ];
        var ai = Finding("Low");
        ai.EventRef = "item-000002";
        ai.RelatedEventRefs = ["item-000002"];
        ai.EventId = "9999";
        ai.LogName = "System";
        ai.Source = "FailingProvider";

        var result = AIHubPageViewModel.MergeIssues(AuditRuleEngine.Analyze(events), [ai], events, replaceReviewedEvidence: true);

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.Single(issue => issue.Source == "FailingProvider").IsHigh, "The unreferenced provider's error remains visible.");
        var warning = result.Single(issue => issue.Source == "WarningProvider");
        Assert.AreEqual("Low", warning.Severity, "Model-authored labels cannot attach an unrelated provider's error to a warning.");
        Assert.AreEqual(3, warning.ObservedEventLevel);
    }

    [TestMethod]
    [DataRow("item-000000")]
    [DataRow("item-999999")]
    [DataRow("item-1")]
    [DataRow("41")]
    public void InvalidPrimaryReferenceCannotHideHighRiskEvidence(string reference)
    {
        SecurityEvent[] events = [new(41, "System", "Microsoft-Windows-Kernel-Power", 1, DateTime.UtcNow, "Shutdown")];
        var ai = Finding("Low");
        ai.EventRef = reference;

        var result = AIHubPageViewModel.MergeIssues(AuditRuleEngine.Analyze(events), [ai], events, replaceReviewedEvidence: true);

        Assert.AreEqual(1, result.Count);
        Assert.IsTrue(result[0].IsHigh);
        Assert.AreEqual("Unexpected Kernel Shutdown", result[0].Title, "Valid related refs cannot legitimize a finding with an invalid primary ref.");
        Assert.AreEqual(1, result[0].ObservedEventLevel);
    }

    [TestMethod]
    public void InvalidRelatedReferenceCannotClaimCoverageOfHighRiskEvidence()
    {
        SecurityEvent[] events = [new(41, "System", "Microsoft-Windows-Kernel-Power", 1, DateTime.UtcNow, "Shutdown")];
        var ai = Finding("Low");
        ai.RelatedEventRefs.Add("item-999999");

        var result = AIHubPageViewModel.MergeIssues(AuditRuleEngine.Analyze(events), [ai], events, replaceReviewedEvidence: true);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Unexpected Kernel Shutdown", result[0].Title);
        Assert.IsTrue(result[0].IsHigh);
    }

    [TestMethod]
    public void CompleteReviewRetainsEventsOutsideTheSelectedInputIncludingRepeatedValues()
    {
        var repeated = new SecurityEvent(41, "System", "Kernel-Power", 1, DateTime.UtcNow, "Shutdown");
        var events = Enumerable.Repeat(repeated, AiHubAuditAnalysisService.MaxEventsPerRequest + 3).ToArray();
        var result = AIHubPageViewModel.MergeIssues(AuditRuleEngine.Analyze(events), [], events, replaceReviewedEvidence: true);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(events.Length, result[0].Occurrences, "No high-risk record may disappear when the complete AI response omits all of them.");
        Assert.IsTrue(result[0].IsHigh);
        var allSelected = Finding("Low");
        allSelected.RelatedEventRefs = Enumerable.Range(1, AiHubAuditAnalysisService.MaxEventsPerRequest)
            .Select(index => $"item-{index:D6}").ToList();
        allSelected.Occurrences = AiHubAuditAnalysisService.MaxEventsPerRequest;
        var covered = AIHubPageViewModel.MergeIssues(AuditRuleEngine.Analyze(events), [allSelected], events, replaceReviewedEvidence: true);
        Assert.AreEqual(2, covered.Count);
        Assert.AreEqual(
            3,
            covered.Single(issue => issue.Key != allSelected.Key).Occurrences,
            "Identical observations outside the input cap remain even when every selected index is explicitly referenced.");
        Assert.AreEqual(events.Length, covered.Sum(issue => issue.Occurrences));
        var rules = AuditRuleEngine.Analyze(events);
        var partial = AIHubPageViewModel.MergeIssues(rules, [Finding("Low")], events);
        Assert.IsTrue(partial.Contains(rules.Single()), "One reference must not count as reviewing every identical observation.");
    }

    [TestMethod]
    public void PartialReviewCannotDismissOrDowngradeUnreviewedLocalEvidence()
    {
        SecurityEvent[] events =
        [
            new(41, "System", "Kernel-Power", 1, DateTime.UtcNow, "First shutdown"),
            new(41, "System", "Kernel-Power", 1, DateTime.UtcNow.AddMinutes(-1), "Second shutdown"),
        ];
        var rules = AuditRuleEngine.Analyze(events);
        var result = AIHubPageViewModel.MergeIssues(rules, [Finding("Low")], events);
        Assert.IsTrue(result.Contains(rules[0]), "A partial finding must not remove the group containing a different unreviewed event.");
        Assert.IsTrue(result.All(issue => issue.IsHigh));
        CollectionAssert.AreEqual(rules, AIHubPageViewModel.MergeIssues(rules, [], events));
    }

    [TestMethod]
    public void EvidenceAbsentFromRecollectionIsNotDismissed()
    {
        var missing = new AuditIssueEnhanced { EventId = "4625", LogName = "Security", Source = "Security-Auditing", Severity = "High" };
        SecurityEvent[] events = [new(41, "System", "Kernel-Power", 1, DateTime.UtcNow, "Shutdown")];
        var rules = AuditRuleEngine.Analyze(events);
        rules.Add(missing);
        var result = AIHubPageViewModel.MergeIssues(rules, [], events, replaceReviewedEvidence: true);
        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.Contains(missing));
        Assert.IsTrue(result.Single(issue => issue.EventId == "41").IsHigh);
    }

    [TestMethod]
    public void CoverageRequiresCompletedSelectedInputAndUnchangedConfiguration()
    {
        var complete = new AiHubAuditAnalysisService.AuditAnalysisResult([], false, null) { AnalyzedEvents = 7, ReusedEvents = 3 };
        Assert.IsTrue(AiHubAuditAnalysisService.ConfirmEvidenceCoverage(complete, 10, "model-policy-a", "model-policy-a").CompleteEvidenceCoverage);
        var changed = AiHubAuditAnalysisService.ConfirmEvidenceCoverage(complete, 10, "model-policy-a", "model-policy-b");
        Assert.IsFalse(changed.CompleteEvidenceCoverage);
        Assert.IsTrue(changed.IsPartial);
        Assert.IsFalse(AiHubAuditAnalysisService.ConfirmEvidenceCoverage(complete, 11, "scope", "scope").CompleteEvidenceCoverage);
        Assert.IsFalse(AiHubAuditAnalysisService.ConfirmEvidenceCoverage(complete with { IsPartial = true }, 10, "scope", "scope").CompleteEvidenceCoverage);
        Assert.IsFalse(AiHubAuditAnalysisService.ConfirmEvidenceCoverage(complete with { Failure = "Cancelled" }, 10, "scope", "scope").CompleteEvidenceCoverage);
        Assert.IsFalse(AiHubAuditAnalysisService.ConfirmEvidenceCoverage(complete with { Issues = null }, 10, "scope", "scope").CompleteEvidenceCoverage);
        var reused = complete with { AnalyzedEvents = 0, ReusedEvents = 10 };
        Assert.IsTrue(AiHubAuditAnalysisService.ConfirmEvidenceCoverage(reused, 10, "scope", "scope").CompleteEvidenceCoverage);
    }

    private static AuditIssue Finding(string severity) => new()
    {
        Key = "reviewed-shutdown", EventRef = "item-000001", RelatedEventRefs = ["item-000001"],
        EventId = "untrusted", LogName = "untrusted", Source = "untrusted", Title = "Reviewed shutdown", Severity = severity, Occurrences = 1,
    };
}
