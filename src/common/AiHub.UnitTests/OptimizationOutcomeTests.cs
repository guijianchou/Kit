// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using Kit.AIHubLib.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Batch optimization reporting. Failures must stay attributable: the dashboard shows
/// the reasons, so an opaque count is a regression.
/// </summary>
[TestClass]
public sealed class OptimizationOutcomeTests
{
    [TestMethod]
    public void EmptyOutcomeReportsNothingAndNoFailures()
    {
        var outcome = new OptimizationOutcome();

        Assert.AreEqual(0, outcome.TotalCount);
        Assert.IsFalse(outcome.HasFailures);
        Assert.IsNull(outcome.DescribeFailures(chinese: false));
    }

    [TestMethod]
    public void CountsMovesRecyclesAndFailuresSeparately()
    {
        var outcome = new OptimizationOutcome();
        outcome.Add(OptimizationItemResult.Success("a.pdf", "move"));
        outcome.Add(OptimizationItemResult.Success("b.pdf", "move"));
        outcome.Add(OptimizationItemResult.Success("c.tmp", "delete"));
        outcome.Add(OptimizationItemResult.Failure("d.bin", "delete", "locked"));

        Assert.AreEqual(4, outcome.TotalCount);
        Assert.AreEqual(2, outcome.MovedCount);
        Assert.AreEqual(1, outcome.RecycledCount);
        Assert.AreEqual(1, outcome.FailedCount);
        Assert.IsTrue(outcome.HasFailures);
    }

    [TestMethod]
    public void ARecycledItemDoesNotCountAsMoved()
    {
        var outcome = new OptimizationOutcome();
        outcome.Add(OptimizationItemResult.Success("cache.tmp", "delete"));

        Assert.AreEqual(0, outcome.MovedCount, "A recycled item is not a move.");
        Assert.AreEqual(1, outcome.RecycledCount);
    }

    [TestMethod]
    public void FailureDescriptionIncludesTheReason()
    {
        var outcome = new OptimizationOutcome();
        outcome.Add(OptimizationItemResult.Success("ok.pdf", "move"));
        outcome.Add(OptimizationItemResult.Failure("secret.bin", "delete", "access denied"));

        string? described = outcome.DescribeFailures(chinese: false);

        Assert.IsNotNull(described);
        StringAssert.Contains(described, "secret.bin");
        StringAssert.Contains(described, "access denied");
    }

    [TestMethod]
    public void FailureDescriptionIsNullWhenEverythingSucceeded()
    {
        var outcome = new OptimizationOutcome();
        outcome.Add(OptimizationItemResult.Success("ok.pdf", "move"));

        Assert.IsNull(outcome.DescribeFailures(chinese: true), "No failures means no failure line.");
    }

    [TestMethod]
    public void FailureDescriptionSummarizesLongLists()
    {
        var outcome = new OptimizationOutcome();
        for (int i = 0; i < 6; i++)
        {
            outcome.Add(OptimizationItemResult.Failure($"file{i}.bin", "delete", "locked"));
        }

        string? described = outcome.DescribeFailures(chinese: false, maxItems: 2);

        Assert.IsNotNull(described);
        StringAssert.Contains(described, "file0.bin");
        StringAssert.Contains(described, "4 more", "Remaining failures must be summarized, not silently dropped.");
    }

    [TestMethod]
    public void DescriptionsAreBilingual()
    {
        var outcome = new OptimizationOutcome();
        outcome.Add(OptimizationItemResult.Success("a.pdf", "move"));
        outcome.Add(OptimizationItemResult.Failure("b.bin", "delete", "locked"));

        StringAssert.Contains(outcome.Describe(chinese: false), "completed");
        StringAssert.Contains(outcome.Describe(chinese: true), "优化完成");
        StringAssert.Contains(outcome.DescribeFailures(chinese: true)!, "b.bin");
    }

    [TestMethod]
    public void FailureWithoutAReasonStillIdentifiesTheFile()
    {
        var outcome = new OptimizationOutcome();
        outcome.Add(OptimizationItemResult.Failure("mystery.bin", "delete", null));

        string? described = outcome.DescribeFailures(chinese: false);

        Assert.IsNotNull(described);
        StringAssert.Contains(described, "mystery.bin");
    }
}
