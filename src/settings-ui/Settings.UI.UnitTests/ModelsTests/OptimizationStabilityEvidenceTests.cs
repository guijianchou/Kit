// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
public sealed class OptimizationStabilityEvidenceTests
{
    [TestMethod]
    public void DiagnosisRetainsTheSourceScansActualWindowAndCounters()
    {
        var end = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        var source = new AuditResult
        {
            ScanStart = end.AddDays(-2),
            ScanEnd = end,
            CollectionProfile = AuditCollectionProfile.ForMode(EventLogService.AuditMode.Extended),
            AnalysisOutcome = "skipped",
            Findings = [new() { Severity = "High" }, new() { Severity = "Medium" }],
        };

        var evidence = AIHubPageViewModel.BuildStabilityEvidence(source);

        Assert.AreEqual(2, evidence.PeriodDays);
        Assert.AreEqual(new DateTimeOffset(end.AddDays(-2)), evidence.ObservedFromUtc);
        Assert.AreEqual(new DateTimeOffset(end), evidence.ObservedToUtc);
        Assert.AreEqual(1, evidence.CriticalCount);
        Assert.AreEqual(1, evidence.WarningCount);
        Assert.IsFalse(evidence.IsPartial);
        source.LimitedChannels = ["System"];
        Assert.IsTrue(AIHubPageViewModel.BuildStabilityEvidence(source).IsPartial);
    }

    [TestMethod]
    public void LocalTimestampsAreConvertedToUtcRatherThanRelabeled()
    {
        var localEnd = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Local);
        var source = new AuditResult { ScanStart = localEnd.AddDays(-2), ScanEnd = localEnd, AnalysisOutcome = "ok" };

        var evidence = AIHubPageViewModel.BuildStabilityEvidence(source);

        Assert.AreEqual(localEnd.ToUniversalTime(), evidence.ObservedToUtc.Value.UtcDateTime);
        Assert.AreEqual(localEnd.AddDays(-2).ToUniversalTime(), evidence.ObservedFromUtc.Value.UtcDateTime);
        Assert.IsTrue(evidence.IsPartial, "Old snapshots without a recorded collection profile cannot establish full coverage.");
    }

    [TestMethod]
    public void MissingAndInvalidCoverageNeverBecomesAFullPeriod()
    {
        Assert.IsNull(AIHubPageViewModel.BuildStabilityEvidence(null));
        var unknown = AIHubPageViewModel.BuildStabilityEvidence(new AuditResult { AnalysisOutcome = "ok" });
        Assert.IsTrue(unknown.IsPartial);
        Assert.AreEqual(0, unknown.PeriodDays);
        Assert.IsNull(unknown.ObservedFromUtc);
        Assert.IsNull(unknown.ObservedToUtc);

        var end = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        var invalid = AIHubPageViewModel.BuildStabilityEvidence(new AuditResult { ScanStart = end, ScanEnd = end.AddDays(-1), AnalysisOutcome = "ok" });
        Assert.IsTrue(invalid.IsPartial);
        Assert.AreEqual(0, invalid.PeriodDays);
    }
}
