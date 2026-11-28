// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using Kit.AiHub.Contract;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
public sealed class AuditModelProvenanceTests
{
    private static readonly int[] RemainingRetryIndices = [1];

    [TestMethod]
    public void AuditUsesLongRouteBudgetWithoutTimeoutFailover()
    {
        var progress = new Progress<AiTaskProgress>();
        var options = AiHubAuditAnalysisService.CreateAnalysisOptions(progress);
        Assert.AreEqual(1800, options.TimeoutSeconds);
        Assert.AreEqual(900, options.RouteTimeoutSeconds);
        Assert.IsFalse(options.AllowTimeoutFailover);
        Assert.IsTrue(options.AllowPartialResults);
        Assert.AreSame(progress, options.Progress);
    }

    [TestMethod]
    [DataRow("gpt-6-luna", "Fallback")]
    [DataRow("gpt-6-astra + gpt-6-luna", "Main + Fallback")]
    [DataRow("gpt-6-luna", "Main")]
    [DataRow("", "")]
    public void CompleteOtherModelReportRemainsDisplayableButDoesNotCompleteMainCache(string model, string route)
    {
        var cache = new AuditAnalysisCache();
        var plan = cache.Prepare("requested-scope", ["evidence"], ["system"], false);
        var analysis = Provenance(model, route);
        Assert.IsTrue(analysis.CompleteEvidenceCoverage);
        Assert.IsFalse(analysis.IsPartial, "A complete fallback report is still a complete review of its evidence.");
        Assert.IsFalse(analysis.MatchesRequestedModel);
        Assert.AreEqual(model, analysis.UsedModel);
        Assert.AreEqual(route, analysis.UsedRoute);
        StringAssert.Contains(
            AIHubPageViewModel.AuditModelProvenance(analysis, false),
            string.IsNullOrEmpty(model) ? "Model unconfirmed" : model);
        Assert.AreNotEqual(plan.Scope, AiHubAuditAnalysisService.ResultScope(plan.Scope, analysis), "Readiness must continue to offer the requested model update.");
        Assert.IsFalse(AiHubAuditAnalysisService.CommitCompletedAnalysis(cache, plan, analysis));
        Assert.AreEqual(1, cache.Prepare(plan.Scope, ["evidence"], ["system"], false).RequestedIndices.Length);
    }

    [TestMethod]
    public void CompletedMainCacheRetainsActualModelAcrossRestartAndReuse()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kit-audit-source-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "cache.json");
        try
        {
            var cache = new AuditAnalysisCache(path);
            var plan = cache.Prepare("scope", ["a"], ["system"], false);
            Assert.IsTrue(AiHubAuditAnalysisService.CommitCompletedAnalysis(cache, plan, Provenance("gpt-6-astra", "Main")));
            var restored = new AuditAnalysisCache(path).Prepare("scope", ["a"], ["system"], false);
            Assert.AreEqual(0, restored.RequestedIndices.Length);
            var reused = AiHubAuditAnalysisService.CachedAnalysis(restored, "gpt-6-astra", "fingerprint");
            Assert.AreEqual("gpt-6-astra", reused.UsedModel);
            Assert.AreEqual("Main", reused.UsedRoute);
            Assert.IsTrue(reused.MatchesRequestedModel);
            Assert.AreEqual(1, reused.ReusedEvents);
            Assert.AreEqual("scope", AiHubAuditAnalysisService.ResultScope("scope", reused));
            Assert.IsFalse(AiHubAuditAnalysisService.IsRequestedModel(restored.UsedModel, restored.UsedRoute, "another-model"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LegacyCacheWithoutActualProvenanceIsNotLoaded()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kit-audit-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cache.json");
        try
        {
            File.WriteAllText(path, "{\"Version\":2,\"Scope\":\"scope\",\"Identities\":[\"a:0\"],\"Contexts\":[\"system\"],\"Issues\":[],\"EventTimes\":[null]}");
            var plan = new AuditAnalysisCache(path).Prepare("scope", ["a"], ["system"], false);
            Assert.AreEqual(1, plan.RequestedIndices.Length);
            Assert.AreEqual(string.Empty, plan.UsedModel);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void FallbackAndMixedCheckpointsCannotBeReusedAsMain()
    {
        foreach (var source in new[] { ("gpt-6-luna", "Fallback"), ("gpt-6-astra + gpt-6-luna", "Main + Fallback") })
        {
            var retry = new AiHubAuditAnalysisService.AuditAnalysisRetryState();
            var plan = retry.Prepare(new AuditAnalysisCache().Prepare("scope", ["a", "b"], ["A", "B"], false), "identity", false);
            var buffer = new AiHubAuditAnalysisService.AuditCheckpointBuffer();
            buffer.Stage(new() { Issues = [] }, new HashSet<string> { "item-000001" });
            buffer.Commit(retry, plan, "identity", Provenance(source.Item1, source.Item2));
            Assert.AreEqual(2, retry.Prepare(plan, "identity", false).RequestedIndices.Length);
        }
    }

    [TestMethod]
    public void CancelledStagedCheckpointDoesNotAlterEarlierConfirmedMainWork()
    {
        var retry = new AiHubAuditAnalysisService.AuditAnalysisRetryState();
        var original = new AuditAnalysisCache().Prepare("scope", ["a", "b"], ["A", "B"], false);
        var first = retry.Prepare(original, "identity", false);
        var confirmed = new AiHubAuditAnalysisService.AuditCheckpointBuffer();
        confirmed.Stage(new() { Issues = [] }, new HashSet<string> { "item-000001" });
        confirmed.Commit(retry, first, "identity", Provenance("gpt-6-astra", "Main") with { IsPartial = true, Failure = "Timeout" });
        var next = retry.Prepare(original, "identity", false);
        CollectionAssert.AreEqual(RemainingRetryIndices, next.RequestedIndices);
        Assert.AreEqual("gpt-6-astra", next.UsedModel);
        var cancelled = new AiHubAuditAnalysisService.AuditCheckpointBuffer();
        cancelled.Stage(new() { Issues = [] }, new HashSet<string> { "item-000001" });
        var stopped = AiHubAuditAnalysisService.ApplyExecutionProvenance(
            new(null, false, "Cancelled"),
            new() { ErrorCode = AiErrorCode.Cancelled, UsedModel = "gpt-6-luna", UsedRoute = "Fallback" },
            "gpt-6-astra");
        cancelled.Commit(retry, next, "identity", stopped);
        CollectionAssert.AreEqual(RemainingRetryIndices, retry.Prepare(original, "identity", false).RequestedIndices);
        Assert.AreEqual(string.Empty, stopped.UsedModel, "A failed route did not produce the displayed findings.");
    }

    private static AiHubAuditAnalysisService.AuditAnalysisResult Provenance(string model, string route) =>
        AiHubAuditAnalysisService.ApplyExecutionProvenance(
            new([], false, null) { CompleteEvidenceCoverage = true, AnalyzedEvents = 1 },
            new() { IsSuccess = true, Payload = new() { Issues = [] }, UsedModel = model, UsedRoute = route },
            "gpt-6-astra");
}
