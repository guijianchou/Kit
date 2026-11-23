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
    public void BatchSummaryShowsTaskTotalsWithoutTheLatestBatchActivity(bool chinese)
    {
        var progress = new AiTaskProgress("Execute", "private-provider-text", 0, 2)
        {
            ActiveBatches = 2, BatchIndex = 1, Activity = "waiting", Route = "Main",
            ElapsedMilliseconds = 180000, QueueElapsedMilliseconds = 2000,
        };

        Assert.AreEqual(
            chinese ? "已验证 0/2 批 · 运行中 2 · 排队 0" : "Validated 0/2 batches · Running 2 · Queued 0",
            AiTaskProgressText.BatchSummary(progress, chinese));
        Assert.AreEqual(
            chinese ? "已验证 1/2 批 · 运行中 0 · 排队 0 · 失败 1" : "Validated 1/2 batches · Running 0 · Queued 0 · Failed 1",
            AiTaskProgressText.BatchSummary(progress with { CompletedBatches = 1, ActiveBatches = 0, FailedBatches = 1 }, chinese));
        Assert.AreEqual(string.Empty, AiTaskProgressText.BatchSummary(progress with { TotalBatches = 0 }, chinese));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BatchActivityLabelsTheLatestBatchWithoutClaimingTaskWideActivity(bool chinese)
    {
        var progress = new AiTaskProgress("Execute", "private-provider-text", 0, 2)
        {
            ActiveBatches = 2, BatchIndex = 1, Activity = "waiting", Route = "Main",
            AgentId = Guid.NewGuid(), Role = "analysis-worker", ElapsedMilliseconds = 180000,
        };

        Assert.AreEqual(
            chinese ? "最新进展：第 1 批 · 主服务 · 等待服务响应" : "Latest update: Batch 1 · Main service · Waiting for response",
            AiTaskProgressText.BatchActivity(progress, chinese));
        Assert.AreEqual(
            chinese ? "最新进展：正在验证结果" : "Latest update: Validating results",
            AiTaskProgressText.BatchActivity(progress with { BatchIndex = null, Activity = "validating", Route = string.Empty }, chinese));
        Assert.AreEqual(
            AiTaskProgressText.BatchActivity(progress, chinese),
            AiTaskProgressText.BatchActivity(progress with { TotalBatches = 0 }, chinese));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BatchDetailsNeverExposeProviderTextRolesOrUnknownRoutes(bool chinese)
    {
        const string untrusted = "https://fixture.invalid/private-token-and-prompt";
        var progress = new AiTaskProgress(untrusted, untrusted, 0, 2)
        {
            Activity = untrusted, Route = untrusted, Role = untrusted, AgentId = Guid.NewGuid(),
            BatchIndex = 1, QueueElapsedMilliseconds = 2000, ElapsedMilliseconds = 180000,
        };

        Assert.AreEqual(
            chinese ? "最新进展：第 1 批 · 正在准备分析" : "Latest update: Batch 1 · Preparing analysis",
            AiTaskProgressText.BatchActivity(progress, chinese));
        foreach (string text in new[]
        {
            AiTaskProgressText.BatchSummary(progress, chinese),
            AiTaskProgressText.BatchActivity(progress, chinese),
            AiTaskProgressText.QueueDetail(progress, chinese),
        })
        {
            Assert.IsFalse(text.Contains(untrusted, StringComparison.Ordinal));
            Assert.IsFalse(text.Contains(progress.AgentId.ToString(), StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("3:00", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void QueueDetailIdentifiesTheBatchInsteadOfReportingTaskElapsedTime(bool chinese)
    {
        var progress = new AiTaskProgress("Queue", string.Empty, 0, 2)
        {
            BatchIndex = 1, Activity = "queued", QueueElapsedMilliseconds = 2000, ElapsedMilliseconds = 180000,
        };

        Assert.AreEqual(
            chinese ? "第 1 批排队用时 0:02" : "Batch 1 queued for 0:02",
            AiTaskProgressText.QueueDetail(progress, chinese));
        Assert.AreEqual(
            chinese ? "排队用时 0:02" : "Time queued 0:02",
            AiTaskProgressText.QueueDetail(progress with { BatchIndex = null }, chinese));
        Assert.AreEqual(
            chinese ? "第 1 批排队用时 0:01" : "Batch 1 queued for 0:01",
            AiTaskProgressText.QueueDetail(progress with { QueueElapsedMilliseconds = 1000 }, chinese));
        Assert.AreEqual(
            AiTaskProgressText.QueueDetail(progress, chinese),
            AiTaskProgressText.QueueDetail(progress with { TotalBatches = 0 }, chinese));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void QueueDetailHidesMissingOrSubsecondDurations(bool chinese)
    {
        var progress = new AiTaskProgress("Queue", string.Empty, 0, 2) { BatchIndex = 1, Activity = "queued" };
        foreach (long? duration in new long?[] { null, -1, 0, 999 })
        {
            Assert.AreEqual(string.Empty, AiTaskProgressText.QueueDetail(progress with { QueueElapsedMilliseconds = duration }, chinese));
        }
    }

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
    public void WorkerProgressUsesOnlyKnownRolesAndPreservesTaskTotals()
    {
        var progress = new AiTaskProgress("Route", "secret-provider-output", 2, 5)
        {
            RunId = Guid.NewGuid(), AgentId = Guid.NewGuid(), ParentAgentId = Guid.NewGuid(),
            Role = "analysis-worker", BatchIndex = 3, ActiveBatches = 2, Activity = "reasoning",
        };
        string text = AiTaskProgressText.Summary(progress, false);
        StringAssert.Contains(text, "Succeeded 2/5 batches");
        StringAssert.Contains(text, "Analysis worker");
        StringAssert.Contains(text, "Batch 3");
        Assert.IsFalse(text.Contains("secret-provider-output", StringComparison.Ordinal));
        Assert.IsFalse(AiTaskProgressText.Summary(progress with { Role = "private-role-text" }, false)
            .Contains("private-role-text", StringComparison.Ordinal));
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
