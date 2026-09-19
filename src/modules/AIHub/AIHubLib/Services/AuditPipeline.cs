// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.AIHubLib.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;

/// <summary>
/// Runs a complete rule-based audit: collect events, classify them, score the result
/// and persist it to the audit history.
/// </summary>
/// <remarks>
/// Extracted from the AI Hub page so the same pipeline can run without a Settings
/// window - which is what the scheduled audit and a future headless worker need. It
/// holds no UI state and never invokes an AI kernel: deep analysis stays an explicit,
/// user-triggered step.
/// </remarks>
public sealed class AuditPipeline
{
    private readonly EventLogService _eventLogService;
    private readonly AuditHistoryStorage _historyStorage;

    public AuditPipeline(EventLogService? eventLogService = null, AuditHistoryStorage? historyStorage = null)
    {
        _eventLogService = eventLogService ?? new EventLogService();
        _historyStorage = historyStorage ?? new AuditHistoryStorage();
    }

    /// <summary>
    /// Performs one audit over the requested window and returns the stored result.
    /// </summary>
    /// <param name="fromUtc">Start of the event window (inclusive).</param>
    /// <param name="toUtc">End of the event window (inclusive).</param>
    /// <param name="mode">Extended reads the ordinary channels; Full also reads Security and the firewall.</param>
    /// <param name="maxEventsPerChannel">Per-channel event cap, keeping the scan bounded.</param>
    /// <param name="retentionDays">History retention applied when the result is stored.</param>
    /// <param name="persist">When false the result is returned without touching the history file.</param>
    /// <param name="cancellationToken">Cancels collection and analysis.</param>
    public async Task<AuditResult> RunAsync(
        DateTime fromUtc,
        DateTime toUtc,
        EventLogService.AuditMode mode = EventLogService.AuditMode.Extended,
        int maxEventsPerChannel = 2000,
        int retentionDays = 30,
        bool persist = true,
        CancellationToken cancellationToken = default)
    {
        if (toUtc < fromUtc)
        {
            (fromUtc, toUtc) = (toUtc, fromUtc);
        }

        List<SecurityEvent> events = await _eventLogService
            .CollectEventsAsync(fromUtc, toUtc, mode, maxEventsPerChannel, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        List<AuditIssueEnhanced> findings = AuditRuleEngine.Analyze(events);
        int score = HealthScoreCalculator.Calculate(findings);

        var result = new AuditResult
        {
            Timestamp = DateTime.UtcNow,
            HealthScore = score,
            ScanStart = fromUtc,
            ScanEnd = toUtc,
            EventsScanned = events.Count,
            Findings = findings,
        };

        if (persist)
        {
            await _historyStorage.SaveHistoryAsync(result, retentionDays).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Runs an audit for the current time and the given look-back window.
    /// </summary>
    public Task<AuditResult> RunForRangeAsync(
        TimeSpan range,
        EventLogService.AuditMode mode = EventLogService.AuditMode.Extended,
        int maxEventsPerChannel = 2000,
        int retentionDays = 30,
        bool persist = true,
        CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        return RunAsync(now - range, now, mode, maxEventsPerChannel, retentionDays, persist, cancellationToken);
    }

    /// <summary>Aggregated statistics over the stored history.</summary>
    public Task<AuditHistoryStatistics> GetStatisticsAsync() => _historyStorage.GetStatisticsAsync();
}
