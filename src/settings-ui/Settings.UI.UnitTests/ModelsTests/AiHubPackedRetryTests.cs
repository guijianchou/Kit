// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
public sealed class AiHubPackedRetryTests
{
    private static readonly int[] RemainingIndices = [1];
    private static readonly string[] RestoredReferences = ["item-000001", "item-000003"];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ValidatedPackedBatchCheckpointsEveryOriginalEvent(bool emptyFinding)
    {
        var cache = new AuditAnalysisCache();
        var plan = cache.Prepare("scope", ["a", "b", "c"], ["A", "B", "C"], false);
        var retry = new AiHubAuditAnalysisService.AuditAnalysisRetryState();
        var active = retry.Prepare(plan, "evidence", false);
        var packed = AuditEvidencePacking.Create([
            new() { Summary = "same", TimeUtc = "first" },
            new() { Summary = "different", TimeUtc = "second" },
            new() { Summary = "same", TimeUtc = "third" }]);
        var batch = new AuditIssueContainer
        {
            Issues = emptyFinding ? [] :
            [
                new()
                {
                    Key = "observed_failure", EventRef = "item-000001", RelatedEventRefs = ["item-000001"], Occurrences = 2,
                    Severity = "Low", Title = "Finding", TitleZh = "发现", Description = "Observed failure", DescriptionZh = "发现失败事件",
                    RootCause = "Unconfirmed", RootCauseZh = "尚未确认", Recommendation = "Review evidence", RecommendationZh = "核查证据",
                },
            ],
        };
        var schema = AiHubAuditAnalysisService.CreateSchema(checkpointValidatedBatch: (output, ids) =>
            retry.Checkpoint(active, "evidence", packed.RestoreBatch(output), packed.ExpandReferences(ids)));
        var assignedIds = new HashSet<string> { "item-000001" };
        if (!emptyFinding)
        {
            batch.Issues[0].RelatedEventRefs.Add("item-000003");
            Assert.IsFalse(schema.ValidateOutput(batch, assignedIds), "A raw event ID is not a valid packed reference.");
            batch.Issues[0].RelatedEventRefs.Remove("item-000003");
        }

        Assert.IsTrue(schema.ValidateOutput(batch, assignedIds));
        Assert.IsTrue(schema.ValidateOutput(schema.MergeBatches([batch]), assignedIds), "Final merge validation must not checkpoint evidence twice.");
        var next = retry.Prepare(plan, "evidence", false);
        CollectionAssert.AreEqual(RemainingIndices, next.RequestedIndices);
        Assert.AreEqual(emptyFinding ? 0 : 1, next.ReusedIssues.Count);
        if (!emptyFinding)
        {
            CollectionAssert.AreEqual(RestoredReferences, next.ReusedIssues[0].RelatedEventRefs);
            Assert.AreEqual(2, next.ReusedIssues[0].Occurrences);
        }
    }
}
