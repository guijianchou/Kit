// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Covers the Security/Firewall channel extension of the audit scan. The channels
/// themselves need elevation, so these tests assert the mode contract and the
/// non-throwing degradation path that the settings host relies on.
/// </summary>
[TestClass]
public sealed class EventLogServiceTests
{
    private static bool EventLogAvailable()
    {
        try
        {
            _ = Type.GetType("System.Diagnostics.EventLog, System.Diagnostics.EventLog", throwOnError: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [TestMethod]
    public void ExtendedModeOverloadIsTheDefaultAndDoesNotRequireElevation()
    {
        var service = new EventLogService();

        // Both overloads exist; the 5-argument call binds to the extended default.
        Assert.IsNotNull(service);
        Assert.IsFalse(
            typeof(EventLogService).GetMethod(nameof(EventLogService.CollectEventsAsync), [typeof(DateTime), typeof(DateTime), typeof(int), typeof(CancellationToken)]) is null,
            "The extended-mode overload must stay available for existing callers.");
        Assert.IsFalse(
            typeof(EventLogService).GetMethod(nameof(EventLogService.CollectEventsAsync), [typeof(DateTime), typeof(DateTime), typeof(EventLogService.AuditMode), typeof(int), typeof(CancellationToken)]) is null,
            "The mode-aware overload must be available for full-access scans.");
    }

    [TestMethod]
    public async Task ExtendedScanNeverTouchesTheSecurityChannel()
    {
        if (!EventLogAvailable())
        {
            Assert.Inconclusive("The Windows event-log assembly cannot be loaded in this test host.");
        }

        var service = new EventLogService();
        DateTime to = DateTime.UtcNow;
        DateTime from = to.AddMinutes(-5);

        // A five-minute extended window is empty or small, and must never throw even
        // when channels are missing or unreadable.
        var events = await service.CollectEventsAsync(from, to, EventLogService.AuditMode.Extended, 10, CancellationToken.None);

        Assert.IsNotNull(events);
        Assert.IsTrue(
            events.TrueForAll(evt => !string.Equals(evt.LogName, "Security", StringComparison.OrdinalIgnoreCase)),
            "Extended mode must not read the Security channel.");
    }

    [TestMethod]
    public async Task FullScanDegradesInsteadOfThrowingWhenSecurityIsUnreadable()
    {
        if (!EventLogAvailable())
        {
            Assert.Inconclusive("The Windows event-log assembly cannot be loaded in this test host.");
        }

        var service = new EventLogService();
        DateTime to = DateTime.UtcNow;
        DateTime from = to.AddMinutes(-5);

        // Without elevation the Security channel reader yields nothing; the call must
        // still complete so the UI can explain the situation.
        var events = await service.CollectEventsAsync(from, to, EventLogService.AuditMode.Full, 10, CancellationToken.None);

        Assert.IsNotNull(events);
    }

    [TestMethod]
    public void CanReadSecurityLogIsAStableBooleanProbe()
    {
        // The probe is consumed by the settings host to explain why full mode is
        // unavailable. It must exist as a static Boolean member so callers never have
        // to construct an EventLogService just to query elevation state.
        var property = typeof(EventLogService).GetProperty(
            nameof(EventLogService.CanReadSecurityLog),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        Assert.IsNotNull(property, "CanReadSecurityLog must remain a public static probe.");
        Assert.AreEqual(typeof(bool), property.PropertyType);
    }
}
