// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Kit.AiHub.Contract;
using Kit.Settings.UI.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
public class AiTaskProgressTextTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void QueuedWorkShowsWaitAndElapsedTimeWithoutClaimingModelActivity(bool chinese)
    {
        var progress = new AiTaskProgress("Queue", "private-provider-text", 0, 1)
        {
            Activity = "queued", QueuedBatches = 1,
            ElapsedMilliseconds = 293717, QueueElapsedMilliseconds = 293717,
        };

        string compact = AiTaskProgressText.Compact(progress, chinese);
        Assert.AreEqual(chinese ? "排队中 · 4:53" : "Queued · 4:53", compact);
        string summary = AiTaskProgressText.Summary(progress, chinese);
        StringAssert.Contains(summary, chinese ? "等待分析空位" : "Waiting for capacity");
        StringAssert.Contains(summary, chinese ? "排队用时 4:53" : "Time queued 4:53");
        Assert.IsFalse(summary.Contains("private-provider-text", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedBatchesAndLiveWorkRemainSeparateFromSuccessfulResults(bool chinese)
    {
        var progress = new AiTaskProgress("Execute", string.Empty, 0, 3)
        {
            FailedBatches = 2, ActiveBatches = 1, BatchIndex = 3,
            Activity = "waiting", Route = "Main", ElapsedMilliseconds = 532536,
        };

        string text = AiTaskProgressText.Summary(progress, chinese);
        StringAssert.Contains(text, chinese ? "成功 0/3 批 · 失败 2 · 进行中 1 · 排队 0" : "Succeeded 0/3 batches · Failed 2 · Active 1 · Queued 0");
        StringAssert.Contains(text, chinese ? "第 3 批" : "Batch 3");
        StringAssert.Contains(text, chinese ? "主服务 · 等待服务响应" : "Main service · Waiting for response");
        StringAssert.Contains(text, "8:52");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RetryCountsComeOnlyFromStructuredObservationAndResetForFallback(bool chinese)
    {
        var retry = new AiTaskProgress("Route", "event=retry, nativeRetries=99", 0, 1)
        {
            Activity = "retrying", Route = "Main",
        };
        string unknown = AiTaskProgressText.Summary(retry, chinese);
        Assert.IsFalse(unknown.Contains("99", StringComparison.Ordinal));
        Assert.IsFalse(unknown.Contains(chinese ? "已重试" : "retries", StringComparison.Ordinal));

        string observed = AiTaskProgressText.Summary(retry with { RetryCount = 2 }, chinese);
        StringAssert.Contains(observed, chinese ? "已重试 2 次" : "2 retries");
        string fallback = AiTaskProgressText.Summary(retry with { Stage = "Failover", Activity = "queued", Route = "Fallback", RetryCount = null }, chinese);
        StringAssert.Contains(fallback, chinese ? "备用服务 · 等待分析空位" : "Fallback service · Waiting for capacity");
        Assert.IsFalse(fallback.Contains(chinese ? "已重试" : "retries", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnrecognizedTextNeverBecomesAStatusOrRoute(bool chinese)
    {
        const string untrusted = "https://fixture.invalid/private-token-and-prompt";
        var progress = new AiTaskProgress(untrusted, untrusted, 0, 1)
        {
            Activity = untrusted, Route = untrusted,
        };
        Assert.IsFalse(AiTaskProgressText.Summary(progress, chinese).Contains(untrusted, StringComparison.Ordinal));
        Assert.IsFalse(AiTaskProgressText.Compact(progress, chinese).Contains(untrusted, StringComparison.Ordinal));
    }

    [TestMethod]
    public void AuditTranslationKeepsTypedStateAndMonotonicSuccessAndFailureCounts()
    {
        var captured = new CapturedProgress();
        var progressType = typeof(AiHubAuditAnalysisService).GetNestedType("AuditProgress", BindingFlags.NonPublic);
        var progress = (IProgress<AiTaskProgress>)Activator.CreateInstance(
            progressType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { "fixture", Stopwatch.StartNew(), captured },
            culture: CultureInfo.InvariantCulture);
        progress.Report(new AiTaskProgress("Failed", "private diagnostic", 1, 4) { FailedBatches = 1 });
        progress.Report(new AiTaskProgress("Queue", "private diagnostic", 0, 4)
        {
            BatchIndex = 3, FailedBatches = 0, ActiveBatches = 1, QueuedBatches = 1,
            Route = "Fallback", Activity = "queued", RetryCount = null,
            ElapsedMilliseconds = 42000, QueueElapsedMilliseconds = 2000,
        });

        Assert.AreEqual(1, captured.Value.CompletedBatches);
        Assert.AreEqual(1, captured.Value.FailedBatches);
        Assert.AreEqual(1, captured.Value.ActiveBatches);
        Assert.AreEqual(1, captured.Value.QueuedBatches);
        Assert.AreEqual(3, captured.Value.BatchIndex);
        Assert.AreEqual("Fallback", captured.Value.Route);
        Assert.AreEqual("queued", captured.Value.Activity);
        Assert.IsNull(captured.Value.RetryCount);
        Assert.AreEqual(42000L, captured.Value.ElapsedMilliseconds);
        Assert.AreEqual(2000L, captured.Value.QueueElapsedMilliseconds);
        Assert.IsFalse(captured.Value.StatusMessage.Contains("private diagnostic", StringComparison.Ordinal));
    }

    private sealed class CapturedProgress : IProgress<AiTaskProgress>
    {
        public AiTaskProgress Value { get; private set; }

        public void Report(AiTaskProgress value) => Value = value;
    }
}
