// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kit.AiHub.Models;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
public sealed class AiHubAuditEvidenceTests
{
    private static readonly string[] GroupedReferences = ["item-000001", "item-000002"];
    private static readonly string[] ThreeReferences = ["item-000001", "item-000002", "item-000003"];
    private static readonly string[] PrimaryReference = ["item-000001"];
    private static readonly string[] ShiftedReferences = ["item-000002", "item-000003"];

    [TestMethod]
    [DataRow("pending", false, InfoBarSeverity.Informational, "AIHub_FinishAudit_Pending")]
    [DataRow("pending", true, InfoBarSeverity.Warning, "AIHub_FinishAudit_Pending")]
    [DataRow("partial", false, InfoBarSeverity.Warning, "AIHub_FinishAudit_Partial")]
    [DataRow("failed", false, InfoBarSeverity.Warning, "AIHub_FinishAudit_Failed")]
    [DataRow("cancelled", false, InfoBarSeverity.Informational, "AIHub_DeepAnalysis_Cancelled")]
    [DataRow("ok", false, InfoBarSeverity.Success, "AIHub_FinishAudit_Completed")]
    [DataRow("ok", true, InfoBarSeverity.Warning, "AIHub_FinishAudit_Completed")]
    [DataRow("skipped", false, InfoBarSeverity.Success, "AIHub_FinishAudit_Completed")]
    public void AuditBannerSeparatesLocalFindingsFromAiCompletion(string outcome, bool skippedChannels, InfoBarSeverity severity, string resourceKey)
    {
        var presentation = AIHubPageViewModel.DescribeAuditOutcome(outcome, skippedChannels);

        Assert.AreEqual(severity, presentation.Severity);
        Assert.AreEqual(resourceKey, presentation.ResourceKey);
        if (outcome == "pending")
        {
            StringAssert.Contains(presentation.Fallback, "still running");
            Assert.IsFalse(presentation.Fallback.Contains("completed", StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public async Task LatestHistoryWaitsForQueuedFinalSnapshotInsteadOfReadingPendingLocalResult()
    {
        var writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stored = new AuditResult { AnalysisOutcome = "pending", HealthScore = 100 };
        bool readStarted = false;
        Task finalWrite = WriteFinalAsync();
        Task<List<AuditResult>> latest = AIHubPageViewModel.ReadAuditHistoryAfterWritesAsync(finalWrite, () =>
        {
            readStarted = true;
            return Task.FromResult(new List<AuditResult> { stored });
        });
        Assert.IsFalse(readStarted, "A pending write must prevent a stale local history read.");
        writeGate.SetResult();
        var history = await latest;
        Assert.AreEqual("ok", history.Single().AnalysisOutcome);
        Assert.AreEqual(60, history.Single().HealthScore);

        async Task WriteFinalAsync()
        {
            await writeGate.Task;
            stored = new AuditResult { AnalysisOutcome = "ok", HealthScore = 60 };
        }
    }

    [TestMethod]
    [DataRow("pending", true)]
    [DataRow("partial", true)]
    [DataRow("failed", true)]
    [DataRow("cancelled", true)]
    [DataRow("ok", false)]
    [DataRow("skipped", false)]
    [DataRow(null, false)]
    public void PersistedPendingAnalysisIsIncompleteRatherThanACompletedScan(string outcome, bool incomplete)
    {
        Assert.AreEqual(incomplete, AIHubPageViewModel.IsIncompleteAuditOutcome(outcome));
    }

    [TestMethod]
    public void DuplicateWorkerEvidenceUsesHighestSeverityWithoutAddingOccurrences()
    {
        var low = Finding("item-000001", "Low", 9, "item-000002");
        var high = Finding("item-000002", "High", 2, "item-000001");
        var distinct = Finding("item-000003", "Medium", 1);

        var merged = AiHubAuditAnalysisService.DeduplicateIssues([low, high, distinct]);

        Assert.AreEqual(2, merged.Count);
        Assert.AreSame(high, merged[0]);
        Assert.AreEqual(2, merged[0].Occurrences, "Workers must not multiply the same observed events.");
        Assert.AreSame(distinct, merged[1]);
    }

    [TestMethod]
    public void BatchMergeRetainsNarrativeOnlyEvidenceBeforeReferenceProjection()
    {
        var first = Finding("item-000001", "Medium", 1);
        first.Key = "app_failure";
        first.Description = "item-000002 supports item-000001.";
        var second = Finding("item-000001", "High", 1);
        second.Key = "app_failure";
        second.Description = "item-000003 supports item-000001.";

        var merged = AiHubAuditAnalysisService.DeduplicateIssues([first, second]).Single();
        CollectionAssert.AreEquivalent(ThreeReferences, merged.RelatedEventRefs);
        Assert.AreEqual(3, merged.Occurrences);
        Assert.AreEqual("High", merged.Severity);
        CollectionAssert.AreEqual(PrimaryReference, first.RelatedEventRefs);
    }

    [TestMethod]
    public void DifferentEvidenceWithTheSameEventIdSurvivesAndCannotLowerRuleSeverity()
    {
        var now = DateTime.UtcNow;
        SecurityEvent[] events =
        [
            new(42, "System", "provider", 2, now, "first"),
            new(42, "System", "provider", 2, now.AddMinutes(-1), "second"),
        ];
        var rules = new List<AuditIssueEnhanced> { new() { EventId = "42", LogName = "System", Severity = "High" } };
        var first = Finding("item-000001", "Low", 1);
        first.EventId = "untrusted";
        first.LogName = "Application";
        var second = Finding("item-000002", "Medium", 1);

        var merged = AIHubPageViewModel.MergeIssues(rules, [first, first, second], events);

        Assert.AreEqual(2, merged.Count);
        Assert.IsTrue(merged.All(issue => issue.EventId == "42" && issue.LogName == "System" && issue.Severity == "High"));
        Assert.AreEqual(2, merged.Select(issue => issue.EventRef).Distinct().Count());
        Assert.AreEqual("untrusted", first.EventId, "Projection must not mutate validated worker data.");
    }

    [TestMethod]
    public void WorkerOutputMustReferenceOnlyAssignedEvidenceBeforeItCanMerge()
    {
        var finding = Finding("item-000001", "High", 1);
        finding.Key = "failure";
        finding.TitleZh = finding.Description = finding.DescriptionZh = "Description";
        finding.RootCause = finding.RootCauseZh = finding.Recommendation = finding.RecommendationZh = "Review evidence";
        var schema = AiHubAuditAnalysisService.CreateSchema();
        var output = new AuditIssueContainer { Issues = [finding] };
        var ids = new HashSet<string>(StringComparer.Ordinal) { "item-000001" };

        Assert.IsTrue(schema.ValidateOutput(output, ids));
        finding.RelatedEventRefs.Add("item-000002");
        Assert.IsFalse(schema.ValidateOutput(output, ids));
        finding.RelatedEventRefs = ["item-000001"];
        finding.EventRef = "item-000002";
        Assert.IsFalse(schema.ValidateOutput(output, ids));
    }

    [TestMethod]
    [DataRow("See item-000003.")]
    [DataRow("See ITEM-000001.")]
    [DataRow("See item-1.")]
    public void NarrativeOnlyReferencesMustUseAssignedCanonicalIds(string invalidNarrative)
    {
        var issue = Finding("item-000001", "Medium", 1);
        issue.Key = "app_failure";
        issue.TitleZh = issue.Description = issue.DescriptionZh = "Evidence";
        issue.RootCause = issue.RootCauseZh = issue.Recommendation = issue.RecommendationZh = "Review evidence";
        issue.Description = "Compare item-000001 with item-000002.";
        var schema = AiHubAuditAnalysisService.CreateSchema();
        var output = new AuditIssueContainer { Issues = [issue] };
        var ids = new HashSet<string>(StringComparer.Ordinal) { "item-000001", "item-000002" };

        Assert.IsTrue(schema.ValidateOutput(output, ids), "A known narrative citation may add evidence omitted from the structured list.");
        var projected = AuditAnalysisCache.Project(issue, reference => reference);
        CollectionAssert.AreEquivalent(GroupedReferences, projected.RelatedEventRefs);
        Assert.AreEqual(2, projected.Occurrences);
        issue.RecommendationZh = invalidNarrative;
        Assert.IsFalse(schema.ValidateOutput(output, ids), "Unknown citations must be rejected before partial results or caching.");
    }

    [TestMethod]
    public void IncrementalRequestActuallySendsCrossProviderContextAndThenReusesCompletedResult()
    {
        var cache = new AuditAnalysisCache();
        var app = new EventAnalysisInput { EventId = 1000, LogName = "Application", Provider = "Application Error", TimeUtc = "2026-11-25T08:00:00Z", Summary = "editor.exe stopped" };
        var unrelated = new EventAnalysisInput { EventId = 4, LogName = "System", Provider = "Network", TimeUtc = "2026-11-25T06:00:00Z", Summary = "Network event" };
        var first = AiHubAuditAnalysisService.PrepareAnalysisRequest(cache, "policy", [app, unrelated], false);
        cache.Commit(first.Plan, []);

        var disk = new EventAnalysisInput { EventId = 7, LogName = "System", Provider = "Disk", TimeUtc = "2026-11-25T08:01:00Z", Summary = "Storage failure" };
        var next = AiHubAuditAnalysisService.PrepareAnalysisRequest(cache, "policy", [disk, app, unrelated], false);
        CollectionAssert.AreEqual(
            new[] { disk, app },
            next.Items,
            "The worker request must contain both the new disk evidence and the earlier app evidence, but exclude distant unrelated records.");
        var issue = Finding("item-000001", "Medium", 1);
        issue.Key = "storage_app_failure";
        issue.TitleZh = issue.DescriptionZh = "证据";
        issue.Description = "item-000001 and item-000002 need joint review.";
        issue.RootCause = issue.RootCauseZh = issue.Recommendation = issue.RecommendationZh = "Review evidence";
        var schema = AiHubAuditAnalysisService.CreateSchema();
        Assert.IsTrue(schema.ValidateOutput(new AuditIssueContainer { Issues = [issue] }, new HashSet<string>(GroupedReferences, StringComparer.Ordinal)));
        cache.Commit(next.Plan, [next.Plan.RestoreReference(issue)]);

        var same = AiHubAuditAnalysisService.PrepareAnalysisRequest(cache, "policy", [disk, app, unrelated], false);
        Assert.AreEqual(0, same.Items.Count);
        Assert.AreEqual(1, same.Plan.ReusedIssues.Count);
        CollectionAssert.AreEquivalent(GroupedReferences, same.Plan.ReusedIssues[0].RelatedEventRefs);
    }

    [TestMethod]
    public void IncrementalRequestUsesAffectedIdentifierOutsideTimeWindowAndRestoresSparseIds()
    {
        var cache = new AuditAnalysisCache();
        var unrelated = new EventAnalysisInput { EventId = 4, LogName = "System", Provider = "Network", TimeUtc = "2026-11-25T09:00:00Z" };
        var app = new EventAnalysisInput { EventId = 1000, LogName = "Application", Provider = "Application Error", TimeUtc = "2026-11-25T06:00:00Z", Summary = "editor.exe stopped" };
        var first = AiHubAuditAnalysisService.PrepareAnalysisRequest(cache, "policy", [unrelated, app], false);
        var previous = Finding("item-000002", "Medium", 1);
        previous.Affected = "editor.exe";
        cache.Commit(first.Plan, [previous]);

        var disk = new EventAnalysisInput { EventId = 7, LogName = "System", Provider = "Disk", TimeUtc = "2026-11-25T08:00:00Z", Summary = "Storage failure affecting editor.exe" };
        var next = AiHubAuditAnalysisService.PrepareAnalysisRequest(cache, "policy", [unrelated, disk, app], false);
        CollectionAssert.AreEqual(new[] { disk, app }, next.Items);
        Assert.AreEqual(0, next.Plan.ReusedIssues.Count, "The superseded app finding must not survive beside its replacement.");
        var replacement = Finding("item-000001", "Medium", 1);
        replacement.Description = "Compare item-000001 and item-000002.";
        var restored = next.Plan.RestoreReference(replacement);
        Assert.AreEqual("Compare item-000002 and item-000003.", restored.Description);
        CollectionAssert.AreEquivalent(ShiftedReferences, restored.RelatedEventRefs);
    }

    [TestMethod]
    public void EvidenceFingerprintChangesForNewOrChangedEvidence()
    {
        var item = new SecurityEvent(42, "System", "provider", 2, DateTime.UtcNow, "original");
        string initial = AiHubAuditAnalysisService.CreateEvidenceFingerprint([item]);

        Assert.AreEqual(initial, AiHubAuditAnalysisService.CreateEvidenceFingerprint([item with { }]));
        Assert.AreNotEqual(initial, AiHubAuditAnalysisService.CreateEvidenceFingerprint([item with { Message = "changed" }]));
        Assert.AreNotEqual(initial, AiHubAuditAnalysisService.CreateEvidenceFingerprint([item, item with { TimeCreated = item.TimeCreated.Value.AddSeconds(1) }]));
    }

    [TestMethod]
    public void ManualDeepAnalysisBypassesCompletedEvidenceCacheWhileAutomaticPassCanReuseIt()
    {
        var cached = new AiHubAuditAnalysisService.AuditAnalysisResult([Finding("item-000001", "High", 1)], false, null);
        const string evidence = "ABC123";

        Assert.IsTrue(
            AIHubPageViewModel.ShouldReuseCompletedAiAnalysis(forceReanalyze: false, needsEventCollection: false, cached, evidence, evidence),
            "An automatic post-scan pass may reuse a completed result for identical evidence.");
        Assert.IsFalse(
            AIHubPageViewModel.ShouldReuseCompletedAiAnalysis(forceReanalyze: true, needsEventCollection: false, cached, evidence, evidence),
            "An explicit click on AI deep analysis must bypass the completed evidence cache and re-run.");
        Assert.IsFalse(
            AIHubPageViewModel.ShouldReuseCompletedAiAnalysis(forceReanalyze: false, needsEventCollection: true, cached, evidence, evidence),
            "A historical snapshot without in-memory events cannot reuse a cached analysis without collecting events.");
        Assert.IsFalse(
            AIHubPageViewModel.ShouldReuseCompletedAiAnalysis(forceReanalyze: false, needsEventCollection: false, cached, evidence, "DIFFERENT"));
    }

    [TestMethod]
    public void PersistedCompletedAuditRestoresAiReportOnlyForRealAiOutcomes()
    {
        var persistedWithAiItem = new AuditResult
        {
            AnalysisOutcome = "ok",
            Findings = [new AuditIssueEnhanced { EventRef = "item-000046", Title = "AI finding", Severity = "Medium" }],
        };
        var persistedWithSummary = new AuditResult
        {
            AnalysisOutcome = "partial",
            AiReportSummary = "AI partially completed: retained 1 enhanced finding(s).",
            Findings = [new AuditIssueEnhanced { EventRef = "1003", Title = "Local finding", Severity = "High" }],
        };
        var localOnlySnapshot = new AuditResult
        {
            AnalysisOutcome = "ok",
            Findings = [new AuditIssueEnhanced { EventRef = "synthetic-event-0", Title = "Synthetic finding", Severity = "Medium" }],
        };
        var legacySnapshot = new AuditResult
        {
            Findings = [new AuditIssueEnhanced { EventRef = "item-000001", Title = "Unknown outcome", Severity = "Low" }],
        };

        Assert.IsTrue(AIHubPageViewModel.HasPersistedAiAnalysis(persistedWithAiItem));
        Assert.IsTrue(AIHubPageViewModel.HasPersistedAiAnalysis(persistedWithSummary));
        Assert.IsFalse(AIHubPageViewModel.HasPersistedAiAnalysis(localOnlySnapshot));
        Assert.IsFalse(AIHubPageViewModel.HasPersistedAiAnalysis(legacySnapshot));
    }

    [TestMethod]
    public void AuditResultSerializationPreservesPersistedAiReportFields()
    {
        var original = new AuditResult
        {
            HealthScore = 42,
            EventsScanned = 47,
            AnalysisOutcome = "ok",
            UsedModel = "Codex CLI / deepseek-v4.1-flash",
            AiReportSummary = "AI deep analysis completed: 4 enhanced finding(s).",
            AiReportMeta = "Kernel codex · 19:23:07",
        };

        string json = System.Text.Json.JsonSerializer.Serialize(original, AIHubLibJsonContext.Default.AuditResult);
        var restored = System.Text.Json.JsonSerializer.Deserialize(json, AIHubLibJsonContext.Default.AuditResult);

        Assert.IsNotNull(restored);
        Assert.AreEqual(original.UsedModel, restored.UsedModel);
        Assert.AreEqual(original.AiReportSummary, restored.AiReportSummary);
        Assert.AreEqual(original.AiReportMeta, restored.AiReportMeta);
    }

    [TestMethod]
    public void SamePatternAcrossBatchesGroupsByAffectedComponentAndPreservesSeparateProblems()
    {
        var first = Finding("item-000001", "Medium", 1);
        first.Key = "app_crash";
        first.Affected = "editor.exe";
        var second = Finding("item-000002", "Medium", 1);
        second.Key = "app_crash";
        second.Affected = "editor.exe";
        var otherComponent = Finding("item-000003", "Medium", 1);
        otherComponent.Key = "app_crash";
        otherComponent.Affected = "browser.exe";
        var otherProblem = Finding("item-000001", "Medium", 1);
        otherProblem.Key = "missing_module";
        otherProblem.Affected = "editor.exe";

        var grouped = AiHubAuditAnalysisService.DeduplicateIssues([first, second, otherComponent, otherProblem]);
        Assert.AreEqual(3, grouped.Count);
        var editorCrashes = grouped.Single(issue => issue.Key == "app_crash" && issue.Affected == "editor.exe");
        Assert.AreEqual(2, editorCrashes.Occurrences);
        CollectionAssert.AreEquivalent(GroupedReferences, editorCrashes.RelatedEventRefs);
        Assert.AreEqual(1, first.Occurrences, "Aggregation must not alter validated input objects.");
    }

    [TestMethod]
    public void CacheConfigurationChangesForCredentialsAndModelButNotUiPreferences()
    {
        var configuration = new AiHubConfig();
        var target = new AiTargetSettings { Name = "Main", IsActive = true, ApiKey = "test-key-a", Model = "model-a" };
        configuration.Targets.Add(target);
        string initial = AiHubAuditAnalysisService.CreateConfigurationFingerprint(configuration);
        target.IsExpanded = true;
        configuration.RetentionDays = 90;
        Assert.AreEqual(initial, AiHubAuditAnalysisService.CreateConfigurationFingerprint(configuration));
        target.ApiKey = "test-key-b";
        Assert.AreNotEqual(initial, AiHubAuditAnalysisService.CreateConfigurationFingerprint(configuration));
        target.ApiKey = "test-key-a";
        target.Model = "model-b";
        Assert.AreNotEqual(initial, AiHubAuditAnalysisService.CreateConfigurationFingerprint(configuration));
    }

    private static AuditIssue Finding(string reference, string severity, int occurrences, params string[] related) => new()
    {
        EventRef = reference,
        EventId = "42",
        LogName = "System",
        Title = "Finding",
        Severity = severity,
        Occurrences = occurrences,
        RelatedEventRefs = related.Append(reference).ToList(),
    };
}
