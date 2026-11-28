// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
public sealed class AuditHistoricalRiskTests
{
    [TestMethod]
    public void ExactSavedEvidenceRepairsLegacyShutdownRiskWithoutDuplicatesOrMutation()
    {
        var (history, saved) = LegacyShutdown();
        var originalFinding = history.Findings.Single();

        var result = AIHubPageViewModel.ReconcileHistoricalRisk(history, saved);

        Assert.AreNotSame(history, result);
        Assert.AreEqual(1, result.Findings.Count, "The AI's linked 41 and 6008 evidence must remain one finding.");
        Assert.AreEqual("An unclean shutdown was recorded", result.Findings[0].Title);
        Assert.IsTrue(result.Findings[0].IsHigh);
        Assert.AreEqual(1, result.Findings[0].ObservedEventLevel);
        Assert.AreEqual(2, result.Findings[0].Occurrences);
        Assert.AreEqual(85, result.HealthScore, "The saved Medium score must be recalculated after applying the High floor.");
        Assert.AreNotEqual(history.AiReportSummary, result.AiReportSummary);
        Assert.IsTrue(result.AiReportSummary.Contains(originalFinding.Title, StringComparison.Ordinal));
        Assert.AreEqual("Medium", originalFinding.Severity);
        Assert.IsNull(originalFinding.ObservedEventLevel);
        Assert.AreEqual(94, history.HealthScore);
        Assert.AreEqual("Legacy summary", history.AiReportSummary);
    }

    [TestMethod]
    [DataRow("en-US", "zh-CN")]
    [DataRow("zh-CN", "en-US")]
    public void HistoricalEvidenceFromOtherUiLanguageRestoresHighRiskWithoutChangingCulture(string savedLanguage, string currentLanguage)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var currentCulture = CultureInfo.GetCultureInfo(currentLanguage);
            CultureInfo.CurrentCulture = currentCulture;
            CultureInfo.CurrentUICulture = currentCulture;
            var (history, saved) = LegacyShutdown();
            history.AiEvidenceFingerprint = AiHubAuditAnalysisService.CreateEvidenceFingerprint(saved.Events, savedLanguage);
            saved.Audit.AiEvidenceFingerprint = history.AiEvidenceFingerprint;
            Assert.AreNotEqual(
                AiHubAuditAnalysisService.CreateEvidenceFingerprint(saved.Events),
                history.AiEvidenceFingerprint,
                "The fixture must exercise evidence saved under the other UI language.");

            var result = AIHubPageViewModel.ReconcileHistoricalRisk(history, saved);

            Assert.AreEqual(1, result.Findings.Count);
            Assert.IsTrue(result.Findings.Single().IsHigh);
            Assert.AreEqual(1, result.Findings[0].ObservedEventLevel);
            Assert.AreEqual(2, result.Findings[0].Occurrences);
            Assert.AreEqual(85, result.HealthScore);
            Assert.AreEqual("Medium", history.Findings[0].Severity);
            Assert.AreSame(currentCulture, CultureInfo.CurrentCulture);
            Assert.AreSame(currentCulture, CultureInfo.CurrentUICulture);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [TestMethod]
    [DataRow("timestamp")]
    [DataRow("start")]
    [DataRow("end")]
    [DataRow("fingerprint")]
    [DataRow("missing-fingerprint")]
    [DataRow("event-content")]
    [DataRow("truncated")]
    [DataRow("count")]
    [DataRow("unavailable")]
    public void DifferentOrIncompleteRawSnapshotCannotRebindHistoricalReferences(string mismatch)
    {
        var (history, saved) = LegacyShutdown();
        switch (mismatch)
        {
            case "timestamp":
                saved.Audit.Timestamp = saved.Audit.Timestamp.AddSeconds(1);
                break;
            case "start":
                saved.Audit.ScanStart = saved.Audit.ScanStart.Value.AddSeconds(1);
                break;
            case "end":
                saved.Audit.ScanEnd = saved.Audit.ScanEnd.Value.AddSeconds(1);
                break;
            case "fingerprint":
                history.AiEvidenceFingerprint = "different-evidence";
                break;
            case "missing-fingerprint":
                history.AiEvidenceFingerprint = null;
                break;
            case "event-content":
                saved = saved with { Events = [saved.Events[0] with { Message = "Different record" }, saved.Events[1]] };
                break;
            case "truncated":
                saved = saved with { RawEventsTruncated = true };
                break;
            case "count":
                history.EventsScanned++;
                break;
            case "unavailable":
                saved = saved with { RawEventsAvailable = false };
                break;
            default:
                Assert.Fail("Unknown mismatch fixture.");
                break;
        }

        var result = AIHubPageViewModel.ReconcileHistoricalRisk(history, saved);

        Assert.AreEqual(1, result.Findings.Count);
        Assert.AreEqual("Medium", result.Findings[0].Severity, "Unknown original levels must not be inferred from another snapshot's item references.");
        Assert.IsNull(result.Findings[0].ObservedEventLevel);
        Assert.AreEqual(history.HealthScore, result.HealthScore);
        Assert.AreEqual(history.AiReportSummary, result.AiReportSummary);
        Assert.AreNotSame(history.Findings[0], result.Findings[0], "Historical presentation receives a detached copy.");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void PersistedObservedLevelRestoresHighRiskWithoutRawEvents(int level)
    {
        var (history, _) = LegacyShutdown();
        history.Findings[0].ObservedEventLevel = level;
        history.Findings[0].Severity = "Low";

        var result = AIHubPageViewModel.ReconcileHistoricalRisk(history);

        Assert.IsTrue(result.Findings.Single().IsHigh);
        Assert.AreEqual(level, result.Findings[0].ObservedEventLevel);
        Assert.AreEqual(85, result.HealthScore);
        Assert.AreEqual("Low", history.Findings[0].Severity);
    }

    [TestMethod]
    public void PersistedWarningLevelDoesNotBecomeHighRisk()
    {
        var (history, _) = LegacyShutdown();
        history.Findings[0].ObservedEventLevel = 3;

        var result = AIHubPageViewModel.ReconcileHistoricalRisk(history);

        Assert.AreEqual("Medium", result.Findings.Single().Severity);
        Assert.AreEqual(3, result.Findings[0].ObservedEventLevel);
        Assert.AreEqual(94, result.HealthScore);
    }

    [TestMethod]
    public void ObservedLevelRoundTripsWhileLegacyJsonRemainsUnknown()
    {
        var (history, _) = LegacyShutdown();
        history.Findings[0].ObservedEventLevel = 1;
        var restored = Clone(history);
        Assert.AreEqual(1, restored.Findings.Single().ObservedEventLevel);
        Assert.IsTrue(AIHubPageViewModel.ReconcileHistoricalRisk(restored).Findings.Single().IsHigh);

        const string legacyJson = """
            {"timestamp":"2026-11-28T12:00:00Z","healthScore":94,"findings":[{"title":"Legacy shutdown","eventId":"6008","source":"EventLog","severity":"Medium","eventRef":"item-000001"}]}
            """;
        var legacy = JsonSerializer.Deserialize(legacyJson, AIHubLibJsonContext.Default.AuditResult);
        Assert.IsNull(legacy.Findings.Single().ObservedEventLevel);
        var result = AIHubPageViewModel.ReconcileHistoricalRisk(legacy);
        Assert.AreEqual("Medium", result.Findings.Single().Severity, "Legacy event IDs alone do not establish an observed Windows level.");
        Assert.IsNull(result.Findings[0].ObservedEventLevel);
        Assert.AreEqual(94, result.HealthScore);
    }

    private static (AuditResult History, WeeklyScanSnapshot Saved) LegacyShutdown()
    {
        var end = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        SecurityEvent[] events =
        [
            new(6008, "System", "EventLog", 2, end.AddDays(-2), "Previous shutdown was unexpected."),
            new(41, "System", "Microsoft-Windows-Kernel-Power", 1, end.AddDays(-2).AddSeconds(-25), "The system rebooted without cleanly shutting down."),
        ];
        var history = new AuditResult
        {
            Timestamp = end,
            ScanStart = end.AddDays(-7),
            ScanEnd = end,
            EventsScanned = 2,
            HealthScore = 94,
            AnalysisOutcome = "ok",
            AiReportSummary = "Legacy summary",
            AiAnalyzedEvents = 2,
            AiEvidenceFingerprint = AiHubAuditAnalysisService.CreateEvidenceFingerprint(events),
            Findings =
            [
                new()
                {
                    Key = "shutdown", Title = "An unclean shutdown was recorded", Severity = "Medium",
                    EventId = "6008", LogName = "System", Source = "EventLog", Occurrences = 2,
                    EventRef = "item-000001", RelatedEventRefs = ["item-000001", "item-000002"],
                },
            ],
        };
        return (history, new WeeklyScanSnapshot(1, "saved-scan", end, Clone(history), events));
    }

    private static AuditResult Clone(AuditResult value) => JsonSerializer.Deserialize(
        JsonSerializer.SerializeToUtf8Bytes(value, AIHubLibJsonContext.Default.AuditResult), AIHubLibJsonContext.Default.AuditResult);
}
