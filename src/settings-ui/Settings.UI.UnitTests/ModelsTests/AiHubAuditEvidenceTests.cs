// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
public sealed class AiHubAuditEvidenceTests
{
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
