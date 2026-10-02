// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.Settings.UI.Helpers;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AIHubLib.Models;
using ManagedCommon;

/// <summary>
/// Bridges the Security Audit event stream to the shared AI Hub task engine so the
/// configured kernel (Codex / Pi) can deepen the rule-based result under the
/// sandboxed "security-audit" policy chain.
/// </summary>
/// <remarks>
/// The engine half (contract, policy sandbox, kernel transport) already existed, and
/// <see cref="AIHubLibJsonContext"/> already declared the matching input/output types
/// (<see cref="EventAnalysisInput"/> / <see cref="AuditIssueContainer"/>); this type is
/// the request producer that was missing. Findings are advisory only - the engine never
/// executes a suggested action.
/// </remarks>
public static class AiHubAuditAnalysisService
{
    /// <summary>Built-in plugin identifier used for Kit's own AI Hub policy chain.</summary>
    private const string PluginId = "aihub";

    /// <summary>Task chain folder holding the security-audit AGENTS.md policy.</summary>
    private const string TaskId = "security-audit";

    /// <summary>
    /// Events sent per request.
    /// </summary>
    /// <remarks>
    /// A full 1-month scan can collect well over a thousand events. Sending them all at a
    /// "max" effort target exceeds the engine timeout, so the selection is bounded and
    /// prioritised: high-severity events are always included, then the most recent of the
    /// remainder. The rule engine still evaluates every collected event, so nothing is
    /// lost from the audit itself.
    /// </remarks>
    public const int MaxEventsPerRequest = 400;

    /// <summary>
    /// Wall-clock budget for the analysis. The engine accepts at most one hour; a large
    /// range needs materially more than the previous ten minutes, which expired mid-run
    /// and reported the whole AI pass as failed.
    /// </summary>
    public const int AnalysisTimeoutSeconds = 1_800;

    // Keep related evidence together and avoid repeating the full policy for every 15 events.
    // The engine still splits at its 96 KB encoded input limit.
    public const int AnalysisBatchSize = 100;

    // Each native route owns its retries within this budget; a slow Main can then use Fallback.
    public const int AnalysisRouteTimeoutSeconds = 300;

    public static string Language => IsChinese ? "zh-CN" : "en-US";

    private static bool IsChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a call can be attempted.
    /// </summary>
    /// <remarks>
    /// Uses the readiness verdict rather than the master switch alone: a switched-on service
    /// with no usable endpoint would otherwise be treated as available and only fail after
    /// the scan had already done its work.
    /// </remarks>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return AiHubEngine.Current.GetReadiness().CanAttempt;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Runs a sandboxed AI pass over the supplied audit events and returns the parsed
    /// advisory issues, or null when AI Hub is disabled or the call fails validation.
    /// </summary>
    public static async Task<IReadOnlyList<AuditIssue>?> AnalyzeEventsAsync(
        IReadOnlyList<SecurityEvent> events,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LastFailure = null;
        LastWasPartial = false;
        if (events is null || events.Count == 0)
        {
            return null;
        }

        if (!IsAvailable)
        {
            LastFailure = "AI Hub unavailable";
            Logger.LogInfo("AI audit skipped: service unavailable.");
            return null;
        }

        // Prioritise what matters: errors and criticals first, then the most recent
        // remaining events. A plain Take() would analyse only the oldest events in the
        // window and silently ignore every later one.
        List<SecurityEvent> selected = events
            .OrderByDescending(e => e.Level is <= 2)
            .ThenByDescending(e => e.TimeCreated ?? DateTime.MinValue)
            .Take(MaxEventsPerRequest)
            .ToList();

        List<EventAnalysisInput> items = selected
            .Select(ToInput)
            .ToList();

        if (items.Count == 0)
        {
            return null;
        }

        progress?.Report(IsChinese
            ? $"正在请求 AI 深度分析（{items.Count} 条事件）..."
            : $"Requesting AI deep analysis for {items.Count} event(s)...");

        string analysisId = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = Stopwatch.StartNew();
        Logger.LogInfo($"AI audit started: id={analysisId}, collected={events.Count}, selected={items.Count}, timeoutSeconds={AnalysisTimeoutSeconds}, batchSize={AnalysisBatchSize}, routeTimeoutSeconds={AnalysisRouteTimeoutSeconds}");
        var batchProgress = new AuditProgress(analysisId, stopwatch, progress);
        string outcome = "failed";
        try
        {
            AiTaskResult<AuditIssueContainer> result = await AiHubEngine.Current.ExecuteTaskAsync(
                PluginId,
                TaskId,
                items,
                CreateSchema(),
                new AiTaskOptions
                {
                    Language = Language,
                    TimeoutSeconds = AnalysisTimeoutSeconds,
                    BatchSize = AnalysisBatchSize,
                    RouteTimeoutSeconds = AnalysisRouteTimeoutSeconds,
                    Progress = batchProgress,
                    AllowPartialResults = true,
                },
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            outcome = result.IsSuccess ? "ok" : result.HasPartialResult ? "partial" : "failed";
            return ReadAnalysisResult(result, analysisId);
        }
        catch (OperationCanceledException)
        {
            LastFailure = "Cancelled";
            outcome = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            LastFailure = ex.GetType().Name;
            Logger.LogError($"AI audit failed: id={analysisId}, type={ex.GetType().Name}, hresult=0x{ex.HResult:X8}");
            throw;
        }
        finally
        {
            Logger.LogInfo($"AI audit finished: id={analysisId}, outcome={outcome}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
    }

    private static IReadOnlyList<AuditIssue>? ReadAnalysisResult(AiTaskResult<AuditIssueContainer> result, string analysisId)
    {
        LastWasPartial = result.HasPartialResult;
        LastFailure = result.IsSuccess ? null : $"{result.ErrorCode}; {result.CompletedBatches}/{result.TotalBatches} batches completed";
        if (!result.IsSuccess)
        {
            Logger.LogWarning($"AI audit incomplete: id={analysisId}, code={result.ErrorCode}, partial={result.HasPartialResult}, batches={result.CompletedBatches}/{result.TotalBatches}");
        }

        return result.IsSuccess || result.HasPartialResult ? result.Payload?.Issues : null;
    }

    private sealed class AuditProgress(string analysisId, Stopwatch stopwatch, IProgress<string>? progress) : IProgress<AiTaskProgress>
    {
        private readonly object _sync = new();
        private int _completed;
        private string _connectionStatus = string.Empty;

        public void Report(AiTaskProgress value)
        {
            lock (_sync)
            {
                _completed = Math.Max(_completed, value.CompletedBatches);
                Logger.LogInfo($"AI audit progress: id={analysisId}, stage={value.Stage}, batches={_completed}/{value.TotalBatches}, elapsedMs={stopwatch.ElapsedMilliseconds}");
                if (value.Stage == "Route" && !string.IsNullOrEmpty(value.StatusMessage))
                {
                    Logger.LogInfo($"AI audit id={analysisId}: {value.StatusMessage}");
                    if (value.StatusMessage.Contains("AI native event:", StringComparison.Ordinal))
                    {
                        if (value.StatusMessage.Contains("event=retry,", StringComparison.Ordinal))
                        {
                            _connectionStatus = IsChinese ? "连接重试中" : "Retrying connection";
                        }
                        else if (value.StatusMessage.Contains("reason=StreamDisconnected", StringComparison.Ordinal))
                        {
                            _connectionStatus = IsChinese ? "连接中断，等待恢复" : "Connection interrupted; waiting for recovery";
                        }
                        else if (value.StatusMessage.Contains("event=completed,", StringComparison.Ordinal))
                        {
                            _connectionStatus = string.Empty;
                        }
                    }
                }

                if (value.Stage == "Validate")
                {
                    _connectionStatus = string.Empty;
                }
                else if (value.Stage == "Failover")
                {
                    _connectionStatus = IsChinese ? "主链未完成，正在使用备用链路" : "Main did not complete; using fallback";
                }

                string status = IsChinese
                    ? $"AI 分析：已完成 {_completed}/{value.TotalBatches} 批，耗时 {stopwatch.Elapsed.TotalMinutes:F1} 分钟"
                    : $"AI analysis: {_completed}/{value.TotalBatches} batches complete, {stopwatch.Elapsed.TotalMinutes:F1} min elapsed";
                progress?.Report(string.IsNullOrEmpty(_connectionStatus) ? status : $"{status} · {_connectionStatus}");
            }
        }
    }

    /// <summary>
    /// Reason the most recent analysis produced no result, or null when it succeeded.
    /// Surfaced in the audit status so a failure is never mistaken for a clean scan.
    /// </summary>
    public static string? LastFailure { get; private set; }

    public static bool LastWasPartial { get; private set; }

    /// <summary>
    /// Schema for the built-in security-audit chain: source-generated JSON metadata for
    /// the input/output pair, plus semantic validation of the returned issues.
    /// </summary>
    private static AiTaskSchema<EventAnalysisInput, AuditIssueContainer> CreateSchema()
    {
        return new AiTaskSchema<EventAnalysisInput, AuditIssueContainer>
        {
            InputTypeInfo = AIHubLibJsonContext.Default.EventAnalysisInput,
            OutputTypeInfo = AIHubLibJsonContext.Default.AuditIssueContainer,
            ValidateOutput = ValidateOutput,

            // Large event windows are split at the record and encoded-size limits,
            // and the engine rejects a multi-batch task whose schema cannot
            // merge the per-batch outputs. Without this the audit failed with InvalidPayload
            // before any request left the machine.
            MergeBatches = containers => new AuditIssueContainer
            {
                Issues = containers.SelectMany(container => container.Issues ?? new List<AuditIssue>()).ToList(),
            },
        };
    }

    /// <summary>
    /// Accepts only issues the policy can have produced: every reference must resolve to
    /// an item the host sent, the required bilingual text must be present, and the
    /// severity/category values must stay inside the documented taxonomy.
    /// </summary>
    private static bool ValidateOutput(AuditIssueContainer output, IReadOnlySet<string> itemIds)
    {
        if (output?.Issues is null)
        {
            return RejectOutput(0, "issues", "missing");
        }

        for (int index = 0; index < output.Issues.Count; index++)
        {
            AuditIssue issue = output.Issues[index];
            int issueIndex = index + 1;
            if (issue is null)
            {
                return RejectOutput(issueIndex, "issue", "missing");
            }

            if (string.IsNullOrWhiteSpace(issue.Key) || issue.Key.Length > 48)
            {
                return RejectOutput(issueIndex, "key", "missing_or_too_long");
            }

            if (string.IsNullOrWhiteSpace(issue.EventRef) || !itemIds.Contains(issue.EventRef))
            {
                return RejectOutput(issueIndex, "eventRef", "unknown_input_reference");
            }

            if (!IsKnownSeverity(issue.Severity))
            {
                return RejectOutput(issueIndex, "severity", "unsupported_value");
            }

            if (issue.Occurrences < 1)
            {
                return RejectOutput(issueIndex, "occurrences", "below_minimum");
            }

            if (issue.RelatedEventRefs is null || issue.RelatedEventRefs.Count == 0)
            {
                return RejectOutput(issueIndex, "relatedEventRefs", "missing");
            }

            if (issue.RelatedEventRefs.Any(reference => !itemIds.Contains(reference)))
            {
                return RejectOutput(issueIndex, "relatedEventRefs", "unknown_input_reference");
            }

            foreach (var field in new[]
            {
                (Name: "title", Value: issue.Title), (Name: "titleZh", Value: issue.TitleZh),
                (Name: "description", Value: issue.Description), (Name: "descriptionZh", Value: issue.DescriptionZh),
                (Name: "rootCause", Value: issue.RootCause), (Name: "rootCauseZh", Value: issue.RootCauseZh),
                (Name: "recommendation", Value: issue.Recommendation), (Name: "recommendationZh", Value: issue.RecommendationZh),
            })
            {
                if (string.IsNullOrWhiteSpace(field.Value) || field.Value.Length > 8_192)
                {
                    return RejectOutput(issueIndex, field.Name, "missing_or_too_long");
                }
            }
        }

        return true;
    }

    private static bool IsKnownSeverity(string? severity)
    {
        return severity is "High" or "Medium" or "Low";
    }

    private static bool RejectOutput(int issueIndex, string field, string reason)
    {
        Logger.LogWarning($"AI audit output rejected: issueIndex={issueIndex}, field={field}, reason={reason}");
        return false;
    }

    /// <summary>
    /// Projects a Windows event onto the desensitized record the audit policy expects.
    /// </summary>
    private static EventAnalysisInput ToInput(SecurityEvent source)
    {
        return new EventAnalysisInput
        {
            EventId = source.EventId,
            LogName = source.LogName ?? string.Empty,
            Provider = source.ProviderName ?? string.Empty,
            Level = source.Level,
            TimeUtc = source.TimeCreated?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) ?? string.Empty,
            Summary = Truncate(source.Message, 1_024),
        };
    }

    private static string Truncate(string? value, int maximumCharacters)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maximumCharacters ? value : value[..maximumCharacters];
    }
}
