// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.AIHubLib.Storage;
using Kit.Settings.UI.Services;
using ManagedCommon;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AIHubPageViewModel
{
    private readonly WeeklyScanSnapshotStore _weeklyScanStore = new();
    private WeeklyReportService _weeklyReportService;
    private Task _weeklyScanWriteTask = Task.CompletedTask;

    // Reports own their cancellation/progress/cache. Opening a report never starts
    // an audit, changes its selected period, or replaces its current AI judgment.
    internal async Task<WindowsStabilityReportDocument> LoadWindowsStabilityWeeklyReportAsync(
        WeeklyReportMode mode,
        DateOnly date,
        bool forceRefresh,
        IProgress<WeeklyReportProgress> progress,
        CancellationToken cancellationToken)
    {
        Task writes = Task.WhenAll(_weeklyScanWriteTask, _auditHistoryWriteTask);
        await writes.WaitAsync(cancellationToken);
        DateTimeOffset end = WeeklyReportEnd(date);
        _weeklyReportService ??= new WeeklyReportService(scans: _weeklyScanStore);
        return await _weeklyReportService.LoadAsync(mode, forceRefresh, end,
            RuntimeLocalization.EffectiveLanguage, progress, cancellationToken);
    }

    internal Task<WindowsStabilityReportDocument> ReadSavedWindowsStabilityWeeklyReportAsync(
        WeeklyReportMode mode, DateOnly date, CancellationToken cancellationToken)
    {
        _weeklyReportService ??= new WeeklyReportService(scans: _weeklyScanStore);
        return _weeklyReportService.ReadSavedAsync(mode, WeeklyReportEnd(date), RuntimeLocalization.EffectiveLanguage, cancellationToken);
    }

    private static DateTimeOffset WeeklyReportEnd(DateOnly date)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return date >= DateOnly.FromDateTime(now.LocalDateTime) ? now
            : new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
    }

    private void CaptureWeeklyScanSnapshot(AuditResult snapshot, IReadOnlyList<SecurityEvent> events)
    {
        if (snapshot.ScanStart is not DateTime start || snapshot.ScanEnd is not DateTime end
            || end.ToUniversalTime() - start.ToUniversalTime() != TimeSpan.FromDays(7))
        {
            return;
        }

        // Freeze mutable findings now, before later AI batches or period navigation
        // alter the UI snapshot. Persistence is serial and cannot hold up the audit.
        var frozen = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(snapshot,
            AIHubLibJsonContext.Default.AuditResult), AIHubLibJsonContext.Default.AuditResult);
        var evidence = events.ToArray();
        _weeklyScanWriteTask = SaveWeeklyScanAfterAsync(_weeklyScanWriteTask, frozen, evidence);
    }

    private async Task SaveWeeklyScanAfterAsync(Task previous, AuditResult snapshot, IReadOnlyList<SecurityEvent> events)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await _weeklyScanStore.SaveAsync(snapshot, events, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.LogWarning($"Weekly scan evidence could not be saved: type={exception.GetType().Name}");
        }
    }
}
