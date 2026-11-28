namespace Kit.AiHub.UnitTests;

using System;
using System.Linq;
using System.Text.Json;
using Kit.AiHub.Engine;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AuditEvidencePackingTests
{
    [TestMethod]
    public void RepeatedEventsKeepEveryTimestampAndOccurrenceWithFewerRequests()
    {
        var inputs = Enumerable.Range(0, 257).Select(index => Event(index, "Message " + (index % 17))).ToArray();
        var packed = AuditEvidencePacking.Create(inputs);
        Assert.AreEqual(17, packed.Items.Count);
        var batches = DynamicBatcher.CreateBatches(packed.Items, AuditEvidencePacking.BatchSize,
            input => JsonSerializer.SerializeToUtf8Bytes(input, AIHubLibJsonContext.Default.EventAnalysisInput).Length, 96_000).ToArray();
        Assert.AreEqual(1, batches.Length);
        Assert.AreEqual(257, packed.Items.Sum(input => input.OccurrenceTimesUtc?.Length ?? 1));
        CollectionAssert.AreEqual(inputs.Where((_, index) => index % 17 == 0).Select(input => input.TimeUtc).ToArray(), packed.Items[0].OccurrenceTimesUtc);
        Assert.IsTrue(inputs.All(input => input.OccurrenceTimesUtc is null), "Packing must not mutate cache input identities.");
    }

    [TestMethod]
    public void DistinctEvidenceAndPotentiallyTruncatedMessagesStaySeparate()
    {
        var first = Event(0, "same");
        var differentProvider = Event(1, "same");
        differentProvider.Provider = "other";
        var differentChannel = Event(2, "same");
        differentChannel.LogName = "Application";
        var differentId = Event(3, "same");
        differentId.EventId = 2;
        var differentLevel = Event(4, "same");
        differentLevel.Level = 3;
        string truncated = new('a', AuditEvidencePacking.SummaryCharacterLimit);
        var inputs = new[]
        {
            first, differentProvider, differentChannel, differentId, differentLevel,
            Event(5, "different"), Event(6, truncated), Event(7, truncated), Event(8, string.Empty), Event(9, string.Empty),
        };
        Assert.AreEqual(inputs.Length, AuditEvidencePacking.Create(inputs).Items.Count);
    }

    [TestMethod]
    public void UniqueEventsStillRespectEncodedSizeBudgetWithoutDroppingRecords()
    {
        var inputs = Enumerable.Range(0, 257).Select(index => Event(index, new string('\u4e2d', 1020) + index)).ToArray();
        var packed = AuditEvidencePacking.Create(inputs);
        var batches = DynamicBatcher.CreateBatches(packed.Items, AuditEvidencePacking.BatchSize,
            input => JsonSerializer.SerializeToUtf8Bytes(input, AIHubLibJsonContext.Default.EventAnalysisInput).Length, 96_000).ToArray();
        Assert.AreEqual(257, batches.Sum(batch => batch.Count));
        Assert.IsTrue(batches.All(batch => batch.Count <= AuditEvidencePacking.BatchSize
            && batch.Sum(input => JsonSerializer.SerializeToUtf8Bytes(input, AIHubLibJsonContext.Default.EventAnalysisInput).Length) <= 96_000));
    }

    [TestMethod]
    public void NarrativeAndStructuredCitationsExpandToOriginalEvidenceOnlyOnce()
    {
        var packed = AuditEvidencePacking.Create([Event(0, "A"), Event(1, "B"), Event(2, "A"), Event(3, "B")]);
        var issue = new AuditIssue
        {
            EventRef = "item-000002", RelatedEventRefs = ["item-000002"],
            Description = "Compare item-000002 with item-000001.",
            RemediationPlan = new() { Verification = "Inspect item-000001." },
            Occurrences = 99,
        };
        var restored = packed.RestoreIssue(issue);
        Assert.AreEqual("item-000002", restored.EventRef);
        Assert.AreEqual(4, restored.Occurrences);
        CollectionAssert.AreEqual(new[] { "item-000001", "item-000002", "item-000003", "item-000004" }, restored.RelatedEventRefs);
        Assert.AreEqual(99, issue.Occurrences);
        Assert.AreEqual(1, issue.RelatedEventRefs.Count);
    }

    [TestMethod]
    public void PackedReferencesMapBackThroughSparseIncrementalPlan()
    {
        var cache = new AuditAnalysisCache();
        var original = cache.Prepare("scope", ["cached", "a", "b", "c"], ["C", "A", "B", "D"], false);
        cache.Commit(original, []);
        var plan = cache.Prepare("scope", ["cached", "new-a", "new-b", "new-c"], ["C", "A", "B", "D"], false);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, plan.RequestedIndices);
        var packed = AuditEvidencePacking.Create([Event(0, "A"), Event(1, "A"), Event(2, "B")]);
        var restored = plan.RestoreReference(packed.RestoreIssue(new()
        {
            EventRef = "item-000002", RelatedEventRefs = ["item-000001", "item-000002"],
            Description = "item-000002 follows item-000001.",
        }));
        Assert.AreEqual("item-000004", restored.EventRef);
        Assert.AreEqual("item-000004 follows item-000002.", restored.Description);
        CollectionAssert.AreEqual(new[] { "item-000002", "item-000003", "item-000004" }, restored.RelatedEventRefs);
    }

    [TestMethod]
    [DataRow("item-000003")]
    [DataRow("item-1")]
    [DataRow("ITEM-000001")]
    public void UnassignedPackedReferencesAreRejected(string reference)
    {
        var packed = AuditEvidencePacking.Create([Event(0, "A"), Event(1, "A")]);
        Assert.ThrowsExactly<ArgumentException>(() => packed.ExpandReferences([reference]));
    }

    private static EventAnalysisInput Event(int index, string summary) => new()
    {
        EventId = 1, LogName = "System", Provider = "provider", Level = 2,
        TimeUtc = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc).AddSeconds(index).ToString("O"), Summary = summary,
    };
}
