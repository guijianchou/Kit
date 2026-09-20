// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using Kit.AIHubLib.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Weighted scan progress. The dashboard showed no progress at all before, so the stage
/// arithmetic that drives the progress bar is pinned here.
/// </summary>
[TestClass]
public sealed class ScanProgressModelTests
{
    [TestMethod]
    public void FreshModelReportsZeroPercentAcrossAllStages()
    {
        var model = new ScanProgressModel();

        Assert.AreEqual(0, model.Percent);
        Assert.AreEqual("0%", model.PercentText);
        Assert.AreEqual(0, model.CompletedStageCount);
        Assert.IsFalse(model.IsComplete);
        Assert.AreEqual(0, model.TotalStageCount >= 4 ? 0 : 0);
        Assert.IsTrue(model.TotalStageCount >= 4, "The workflow has the documented stages.");
    }

    [TestMethod]
    public void CompletingEveryStageReportsOneHundredPercent()
    {
        var model = new ScanProgressModel();

        model.CompleteAll();

        Assert.AreEqual(100, model.Percent);
        Assert.AreEqual("100%", model.PercentText);
        Assert.IsTrue(model.IsComplete);
        Assert.AreEqual(model.TotalStageCount, model.CompletedStageCount);
    }

    [TestMethod]
    public void CompletingALaterStageImpliesEarlierOnes()
    {
        var model = new ScanProgressModel();

        // Completing the last stage means everything before it finished too, so progress
        // cannot jump backwards when stages are reported out of order.
        model.CompleteStage(model.Steps[^1].Title);

        Assert.AreEqual(100, model.Percent);
        Assert.AreEqual(model.TotalStageCount, model.CompletedStageCount);
    }

    [TestMethod]
    public void ProgressIsMonotonicAsStagesComplete()
    {
        var model = new ScanProgressModel();
        double previous = model.Percent;

        foreach (OptimizationStep step in model.Steps)
        {
            model.CompleteStage(step.Title);
            Assert.IsTrue(model.Percent >= previous, "Progress must never regress.");
            previous = model.Percent;
        }
    }

    [TestMethod]
    public void StartingAStageReportsItAsCurrentWithoutCompletingIt()
    {
        var model = new ScanProgressModel();

        model.StartStage(model.Steps[0].Title);

        Assert.AreEqual(model.Steps[0].Title, model.CurrentStage);
        Assert.AreEqual(0, model.CompletedStageCount, "A started stage is not a completed one.");
    }

    [TestMethod]
    public void UnknownStageNamesAreIgnored()
    {
        var model = new ScanProgressModel();

        model.StartStage("no such stage");
        model.CompleteStage("no such stage");

        Assert.AreEqual(0, model.Percent, "An unknown stage must not move the bar.");
    }

    [TestMethod]
    public void ResetReturnsTheModelToItsInitialState()
    {
        var model = new ScanProgressModel();
        model.CompleteAll();

        model.Reset();

        Assert.AreEqual(0, model.Percent);
        Assert.AreEqual(0, model.CompletedStageCount);
        Assert.IsFalse(model.IsComplete);
    }

    [TestMethod]
    public void StageCountTextDescribesProgressInStages()
    {
        var model = new ScanProgressModel();

        model.CompleteStage(model.Steps[0].Title);

        StringAssert.Contains(model.StageCountText, "1/");
    }

    [TestMethod]
    public void PercentNeverExceedsOneHundred()
    {
        var model = new ScanProgressModel();

        model.CompleteAll();
        model.CompleteAll();

        Assert.AreEqual(100, model.Percent);
    }
}
