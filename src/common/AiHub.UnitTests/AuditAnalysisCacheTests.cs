namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AuditAnalysisCacheTests
{
    [TestMethod]
    public void NewEvidenceRechecksRelatedContextAndRemapsUnchangedFindings()
    {
        var cache = new AuditAnalysisCache();
        var first = cache.Prepare("policy", ["system-old", "app-old"], ["system", "app"], force: false);
        cache.Commit(first, [Finding("item-000001"), Finding("item-000002")]);
        var next = cache.Prepare("policy", ["system-new", "system-old", "app-old"], ["system", "system", "app"], force: false);
        CollectionAssert.AreEqual(new[] { 0, 1 }, next.RequestedIndices);
        Assert.AreEqual("item-000003", next.ReusedIssues.Single().EventRef);
        Assert.AreEqual("item-000002", next.RestoreReference(Finding("item-000002")).EventRef);
    }

    [TestMethod]
    public void FailedOrPartialAnalysisDoesNotAdvanceAndChangedPolicyForcesReview()
    {
        var cache = new AuditAnalysisCache();
        var first = cache.Prepare("policy", ["a"], ["system"], false);
        cache.Commit(first, []); // A completed empty result is a valid analysis.
        Assert.AreEqual(0, cache.Prepare("policy", ["a"], ["system"], false).RequestedIndices.Length);
        cache.Prepare("policy", ["b", "a"], ["system", "system"], false); // No commit on cancellation/failure.
        Assert.AreEqual(2, cache.Prepare("policy", ["b", "a"], ["system", "system"], false).RequestedIndices.Length);
        Assert.AreEqual(1, cache.Prepare("new-policy", ["a"], ["system"], false).RequestedIndices.Length);
        Assert.AreEqual(1, cache.Prepare("policy", ["a"], ["system"], true).RequestedIndices.Length);
    }

    [TestMethod]
    public void RemovedEvidenceAndCrossProviderChainsAreRechecked()
    {
        var cache = new AuditAnalysisCache();
        var first = cache.Prepare("policy", ["a", "b", "c"], ["system", "app", "network"], false);
        var issue = Finding("item-000001");
        issue.RelatedEventRefs.Add("item-000002");
        cache.Commit(first, [issue, Finding("item-000003")]);
        var next = cache.Prepare("policy", ["b", "c"], ["app", "network"], false);
        CollectionAssert.AreEqual(new[] { 0 }, next.RequestedIndices);
        Assert.AreEqual("item-000002", next.ReusedIssues.Single().EventRef);
    }

    [TestMethod]
    public void InsertedEvidenceRemapsAllNarrativeCitationsOnceAndRetainsTextOnlyEvidence()
    {
        var cache = new AuditAnalysisCache();
        var first = cache.Prepare("policy", ["app", "network"], ["app", "network"], false);
        var issue = Finding("item-000001");
        const string narrative = "item-000001 precedes item-000002; item-000001 remains primary.";
        issue.Title = issue.TitleZh = issue.Description = issue.DescriptionZh = narrative;
        issue.RootCause = issue.RootCauseZh = issue.Recommendation = issue.RecommendationZh = narrative;
        cache.Commit(first, [issue]);

        var next = cache.Prepare("policy", ["disk", "app", "network"], ["disk", "app", "network"], false);
        var reused = next.ReusedIssues.Single();
        const string expected = "item-000002 precedes item-000003; item-000002 remains primary.";
        Assert.AreEqual("item-000002", reused.EventRef);
        CollectionAssert.AreEquivalent(new[] { "item-000002", "item-000003" }, reused.RelatedEventRefs);
        Assert.AreEqual(2, reused.Occurrences);
        foreach (string text in new[]
        {
            reused.Title, reused.TitleZh, reused.Description, reused.DescriptionZh,
            reused.RootCause, reused.RootCauseZh, reused.Recommendation, reused.RecommendationZh,
        })
        {
            Assert.AreEqual(expected, text);
        }

        Assert.AreEqual(narrative, issue.Description, "Projection must not mutate the worker result.");

        cache.Commit(next, [reused]);
        var removed = cache.Prepare("policy", ["disk", "app"], ["disk", "app"], false);
        CollectionAssert.AreEqual(new[] { 1 }, removed.RequestedIndices);
        Assert.AreEqual(0, removed.ReusedIssues.Count, "Removing narrative-only evidence must invalidate the whole finding.");
    }

    [TestMethod]
    public void SparseRequestRestoresNarrativesAndRejectsUnknownTextReferencesBeforeCommit()
    {
        var cache = new AuditAnalysisCache();
        var first = cache.Prepare("policy", ["unchanged", "old"], ["untouched", "changed"], false);
        cache.Commit(first, []);
        var next = cache.Prepare("policy", ["unchanged", "new", "old"], ["untouched", "changed", "changed"], false);
        CollectionAssert.AreEqual(new[] { 1, 2 }, next.RequestedIndices);
        var issue = Finding("item-000001");
        issue.Description = "item-000001 and item-000002";
        var restored = next.RestoreReference(issue);
        Assert.AreEqual("item-000002 and item-000003", restored.Description);
        CollectionAssert.AreEquivalent(new[] { "item-000002", "item-000003" }, restored.RelatedEventRefs);

        issue.RootCause = "item-000003";
        Assert.ThrowsExactly<InvalidOperationException>(() => next.RestoreReference(issue));
        var invalid = Finding("item-000001");
        invalid.Description = "item-999999";
        Assert.ThrowsExactly<ArgumentException>(() => cache.Commit(next, [invalid]));
        CollectionAssert.AreEqual(new[] { 1, 2 }, cache.Prepare("policy", ["unchanged", "new", "old"], ["untouched", "changed", "changed"], false).RequestedIndices,
            "Rejected citations must not advance the complete cache.");
    }

    [TestMethod]
    public void NewProviderRevisitsUnchangedNeighboursEvenWhenPreviousAnalysisFoundNothing()
    {
        var time = DateTime.UtcNow.AddHours(-2);
        var cache = new AuditAnalysisCache();
        var first = cache.Prepare("policy", ["app", "far"], ["app", "network"], false, [time, time.AddHours(-1)]);
        cache.Commit(first, []);
        var next = cache.Prepare("policy", ["disk", "app", "far"], ["disk", "app", "network"], false,
            [time.AddMinutes(1), time, time.AddHours(-1)]);
        CollectionAssert.AreEqual(new[] { 0, 1 }, next.RequestedIndices);
        var connected = Finding("item-000001");
        connected.RelatedEventRefs.Add("item-000002");
        cache.Commit(next, [next.RestoreReference(connected)]);
        var same = cache.Prepare("policy", ["disk", "app", "far"], ["disk", "app", "network"], false,
            [time.AddMinutes(1), time, time.AddHours(-1)]);
        Assert.AreEqual(0, same.RequestedIndices.Length, "An unchanged subsequent scan must still be fully incremental.");
        CollectionAssert.AreEquivalent(new[] { "item-000001", "item-000002" }, same.ReusedIssues.Single().RelatedEventRefs);
    }

    [TestMethod]
    public void NeighbourRecheckDropsOldFindingWithoutExpandingTimeTransitively()
    {
        var time = DateTime.UtcNow.AddHours(-2);
        var cache = new AuditAnalysisCache();
        var first = cache.Prepare("policy", ["app", "network"], ["app", "network"], false, [time.AddMinutes(4), time.AddMinutes(8)]);
        cache.Commit(first, [Finding("item-000001"), Finding("item-000002")]);
        var next = cache.Prepare("policy", ["disk", "app", "network"], ["disk", "app", "network"], false,
            [time, time.AddMinutes(4), time.AddMinutes(8)]);
        CollectionAssert.AreEqual(new[] { 0, 1 }, next.RequestedIndices);
        Assert.AreEqual("item-000003", next.ReusedIssues.Single().EventRef,
            "The app finding must be replaced, while a neighbour of that neighbour stays cached.");
    }

    [TestMethod]
    public void RemovedEventRechecksItsTimeNeighboursAfterRestart()
    {
        using var fixture = new FixtureDirectory();
        string path = fixture.PathFor("cache.json");
        var time = DateTime.UtcNow.AddHours(-2);
        var cache = new AuditAnalysisCache(path);
        var first = cache.Prepare("policy", ["disk", "app", "far"], ["disk", "app", "network"], false,
            [time, time.AddMinutes(1), time.AddHours(-1)]);
        cache.Commit(first, [Finding("item-000002"), Finding("item-000003")]);
        var next = new AuditAnalysisCache(path).Prepare("policy", ["app", "far"], ["app", "network"], false,
            [time.AddMinutes(1), time.AddHours(-1)]);
        CollectionAssert.AreEqual(new[] { 0 }, next.RequestedIndices);
        Assert.AreEqual("item-000002", next.ReusedIssues.Single().EventRef);
    }

    [TestMethod]
    public void ExplicitAffectedIdentifierRechecksRemoteFindingButNotSubstringLookalike()
    {
        var time = DateTime.UtcNow.AddHours(-2);
        var cache = new AuditAnalysisCache();
        var first = cache.Prepare("policy", ["app", "other"], ["app", "other"], false, [time, time]);
        var issue = Finding("item-000001");
        issue.Affected = "editor.exe";
        var lookalike = Finding("item-000002");
        lookalike.Affected = "tor.exe";
        cache.Commit(first, [issue, lookalike]);
        var next = cache.Prepare("policy", ["disk", "app", "other"], ["disk", "app", "other"], false,
            [time.AddHours(1), time, time], ["Storage failure affecting editor.exe.", string.Empty, string.Empty]);
        CollectionAssert.AreEqual(new[] { 0, 1 }, next.RequestedIndices);
        Assert.AreEqual("item-000003", next.ReusedIssues.Single().EventRef);
    }

    [TestMethod]
    public void LegacyCacheAndUnknownNarrativeCitationsFallBackToFullReview()
    {
        using var fixture = new FixtureDirectory();
        string path = fixture.PathFor("cache.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new AuditAnalysisSnapshot(1, "policy", ["a:0"], ["app"], [Finding("item-000001")]), AIHubLibJsonContext.Default.AuditAnalysisSnapshot));
        Assert.AreEqual(1, new AuditAnalysisCache(path).Prepare("policy", ["a"], ["app"], false).RequestedIndices.Length);

        var invalid = Finding("item-000001");
        invalid.Description = "Compare with item-000002.";
        File.WriteAllText(path, JsonSerializer.Serialize(new AuditAnalysisSnapshot(3, "policy", ["a:0"], ["app"], [invalid], [null]), AIHubLibJsonContext.Default.AuditAnalysisSnapshot));
        var next = new AuditAnalysisCache(path).Prepare("policy", ["a"], ["app"], false);
        Assert.AreEqual(1, next.RequestedIndices.Length);
        Assert.AreEqual(0, next.ReusedIssues.Count);
    }

    [TestMethod]
    public void CompletedCacheSurvivesRestartWithoutRawEvidenceAndRejectsOversizedWindows()
    {
        using var fixture = new FixtureDirectory();
        string path = fixture.PathFor("cache.json");
        var cache = new AuditAnalysisCache(path);
        var first = cache.Prepare("policy", ["hash-a", "hash-a"], ["system", "system"], false);
        cache.Commit(first, [Finding("item-000002")]);
        var restored = new AuditAnalysisCache(path).Prepare("policy", ["hash-a", "hash-a"], ["system", "system"], false);
        Assert.AreEqual(0, restored.RequestedIndices.Length);
        Assert.AreEqual("item-000002", restored.ReusedIssues.Single().EventRef);
        var repeated = new AuditAnalysisCache(path).Prepare("policy", ["hash-a", "hash-a", "hash-a"], ["system", "system", "system"], false);
        Assert.AreEqual(3, repeated.RequestedIndices.Length, "An additional identical event changes the pattern's occurrence count.");
        Assert.ThrowsExactly<ArgumentException>(() => cache.Prepare("policy", new string[401], new string[401], false));
    }

    [TestMethod]
    public void MalformedOrNullTextCacheFallsBackToReview()
    {
        using var fixture = new FixtureDirectory();
        string path = fixture.PathFor("cache.json");
        File.WriteAllText(path, "broken");
        Assert.AreEqual(1, new AuditAnalysisCache(path).Prepare("policy", ["hash"], ["system"], false).RequestedIndices.Length);
        var issue = Finding("item-000001");
        issue.Description = null!;
        File.WriteAllText(path, JsonSerializer.Serialize(new AuditAnalysisSnapshot(3, "policy", ["hash:0"], ["system"], [issue], [null]), AIHubLibJsonContext.Default.AuditAnalysisSnapshot));
        var next = new AuditAnalysisCache(path).Prepare("policy", ["hash"], ["system"], false);
        Assert.AreEqual(1, next.RequestedIndices.Length);
        Assert.AreEqual(0, next.ReusedIssues.Count);
    }

    [TestMethod]
    public void MixedAgeCachePrunesExpiredEvidenceAndRemapsRecentNarrativeReferences()
    {
        using var fixture = new FixtureDirectory();
        string path = fixture.PathFor("cache.json");
        var now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        var cache = new AuditAnalysisCache(path, () => now);
        var plan = cache.Prepare("policy", ["old", "recent", "other"], ["old-provider", "recent-provider", "other-provider"], false,
            [now.AddDays(-31), now.AddHours(-3), now.AddHours(-1)]);
        var recent = Finding("item-000002");
        recent.Description = "Inspect item-000002 and item-000003.";
        cache.Commit(plan, [Finding("item-000001"), recent], "primary-model", "main");

        var stored = JsonSerializer.Deserialize(File.ReadAllText(path), AIHubLibJsonContext.Default.AuditAnalysisSnapshot)!;
        CollectionAssert.AreEqual(new[] { "recent:0", "other:0" }, stored.Identities);
        Assert.AreEqual("Inspect item-000001 and item-000002.", stored.Issues.Single().Description);
        Assert.IsFalse(File.ReadAllText(path).Contains("old-provider", StringComparison.Ordinal));
        var same = new AuditAnalysisCache(path, () => now).Prepare("policy", ["recent", "other"], ["recent-provider", "other-provider"], false,
            [now.AddHours(-3), now.AddHours(-1)]);
        Assert.AreEqual(0, same.RequestedIndices.Length);
        Assert.AreEqual("primary-model", same.UsedModel);
        Assert.AreEqual(1, same.ReusedIssues.Count);
    }

    [TestMethod]
    public void ExpiredCrossProviderEvidenceRechecksOnlyItsConnectedRecentContexts()
    {
        var now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        var cache = new AuditAnalysisCache(utcNow: () => now);
        var plan = cache.Prepare("policy", ["old", "linked", "unrelated"], ["old-provider", "linked-provider", "other-provider"], false,
            [now.AddDays(-31), now.AddHours(-3), now.AddHours(-1)]);
        var chain = Finding("item-000002");
        chain.Description = "item-000001 supports item-000002";
        cache.Commit(plan, [chain, Finding("item-000003")]);
        var next = cache.Prepare("policy", ["linked", "unrelated"], ["linked-provider", "other-provider"], false,
            [now.AddHours(-3), now.AddHours(-1)]);
        CollectionAssert.AreEqual(new[] { 0 }, next.RequestedIndices);
        Assert.AreEqual("item-000002", next.ReusedIssues.Single().EventRef);
    }

    [TestMethod]
    public void InProcessRetentionExpiresEventsAndDoesNotRefreshUnknownEvidenceAge()
    {
        using var fixture = new FixtureDirectory();
        string path = fixture.PathFor("cache.json");
        var now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        var cache = new AuditAnalysisCache(path, () => now);
        var initial = cache.Prepare("policy", ["unknown", "recent"], ["unknown", "recent"], false, [null, now]);
        cache.Commit(initial, [Finding("item-000001"), Finding("item-000002")]);
        now = now.AddDays(29);
        var incremental = cache.Prepare("policy", ["unknown", "recent", "new"], ["unknown", "recent", "new"], false, [null, now.AddDays(-29), now]);
        CollectionAssert.AreEqual(new[] { 2 }, incremental.RequestedIndices);
        cache.Commit(incremental, incremental.ReusedIssues.Append(Finding("item-000003")).ToArray());
        now = now.AddDays(2);
        var expired = cache.Prepare("policy", ["new"], ["new"], false, [now.AddDays(-2)]);
        Assert.AreEqual(0, expired.RequestedIndices.Length, "Unrelated recent evidence must remain cached in the same running process.");
        Assert.AreEqual("item-000001", expired.ReusedIssues.Single().EventRef);
        var stored = JsonSerializer.Deserialize(File.ReadAllText(path), AIHubLibJsonContext.Default.AuditAnalysisSnapshot)!;
        CollectionAssert.AreEqual(new[] { "new:0" }, stored.Identities, "A later cache write must not extend the lifetime of unknown-time evidence.");
    }

    [TestMethod]
    public void LegacyUnknownTimesUseFileAgeAndEntirelyExpiredCommitsLeaveNoCache()
    {
        using var fixture = new FixtureDirectory();
        string path = fixture.PathFor("cache.json");
        var now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(path, JsonSerializer.Serialize(new AuditAnalysisSnapshot(3, "policy", ["unknown:0"], ["provider"], [Finding("item-000001")], [null]), AIHubLibJsonContext.Default.AuditAnalysisSnapshot));
        File.SetLastWriteTimeUtc(path, now.AddDays(-31));
        var cache = new AuditAnalysisCache(path, () => now);
        var next = cache.Prepare("policy", ["unknown"], ["provider"], false);
        Assert.AreEqual(1, next.RequestedIndices.Length);
        Assert.AreEqual(0, next.ReusedIssues.Count);
        Assert.IsFalse(File.Exists(path));

        var old = cache.Prepare("policy", ["old"], ["old-provider"], false, [now.AddDays(-40)]);
        cache.Commit(old, [Finding("item-000001")]);
        Assert.IsFalse(File.Exists(path), "Saving an old scan must not reintroduce expired analysis records.");
    }

    [TestMethod]
    public void CrossingTheCutoffRechecksTheAffectedProviderWithoutDiscardingOtherRecentProviders()
    {
        using var fixture = new FixtureDirectory();
        string path = fixture.PathFor("cache.json");
        var now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        DateTime boundary = now.AddDays(-30);
        var cache = new AuditAnalysisCache(path, () => now);
        var initial = cache.Prepare("policy", ["boundary", "same-provider", "other"], ["app", "app", "other"], false,
            [boundary, now.AddHours(-1), now]);
        cache.Commit(initial, [Finding("item-000002"), Finding("item-000003")]);
        Assert.AreEqual(0, cache.Prepare("policy", ["boundary", "same-provider", "other"], ["app", "app", "other"], false,
            [boundary, now.AddHours(-1), now]).RequestedIndices.Length, "The exact thirty-day boundary remains inside retention.");
        now = now.AddTicks(1);
        var next = cache.Prepare("policy", ["same-provider", "other"], ["app", "other"], false, [now.AddHours(-1), now]);
        CollectionAssert.AreEqual(new[] { 0 }, next.RequestedIndices, "Removing a prior observation invalidates its provider's conclusion, including no-finding evidence.");
        Assert.AreEqual("item-000002", next.ReusedIssues.Single().EventRef);
    }

    private static AuditIssue Finding(string reference) => new()
    {
        Key = "fault", EventRef = reference, RelatedEventRefs = [reference], Title = "Fault", Severity = "Medium",
        TitleZh = "故障", Description = "Evidence", DescriptionZh = "证据", RootCause = "Unknown", RootCauseZh = "未确定",
        Recommendation = "Review", RecommendationZh = "核查",
    };
}
