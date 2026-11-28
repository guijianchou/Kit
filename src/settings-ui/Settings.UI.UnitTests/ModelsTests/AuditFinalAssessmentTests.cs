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
    public void CompleteReviewCanDowngradeOrDismissTheSameLocalEvidence()
    {
        SecurityEvent[] events = [new(41, "System", "Kernel-Power", 1, DateTime.UtcNow, "Shutdown")];
        var rules = AuditRuleEngine.Analyze(events);
        Assert.IsTrue(rules.Single().IsHigh);
        var result = AIHubPageViewModel.MergeIssues(rules, [Finding("Low")], events, replaceReviewedEvidence: true);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Low", result[0].Severity);
        Assert.AreEqual("System", result[0].LogName);
        Assert.AreEqual("41", result[0].EventId);
        Assert.AreEqual("Kernel-Power", result[0].Source);
        Assert.IsTrue(rules[0].IsHigh, "The original evidence remains unchanged for callers holding it.");
        Assert.AreEqual(
            0,
            AIHubPageViewModel.MergeIssues(rules, [], events, replaceReviewedEvidence: true).Count,
            "A validated complete empty result can dismiss a rule false positive.");
    }

    [TestMethod]
    public void CompleteReviewRetainsEventsOutsideTheSelectedInputIncludingRepeatedValues()
    {
        var repeated = new SecurityEvent(41, "System", "Kernel-Power", 1, DateTime.UtcNow, "Shutdown");
        var events = Enumerable.Repeat(repeated, AiHubAuditAnalysisService.MaxEventsPerRequest + 3).ToArray();
        var result = AIHubPageViewModel.MergeIssues(AuditRuleEngine.Analyze(events), [], events, replaceReviewedEvidence: true);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(3, result[0].Occurrences, "Identical records outside the input cap are still independent observations.");
        Assert.IsTrue(result[0].IsHigh);
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
        Assert.AreEqual(1, result.Count);
        Assert.AreSame(missing, result[0]);
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
        EventId = "untrusted", LogName = "untrusted", Source = "untrusted", Title = "Reviewed shutdown", Severity = severity,
    };
}
