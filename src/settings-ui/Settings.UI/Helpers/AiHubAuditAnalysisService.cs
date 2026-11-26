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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.Security;
using Kit.AiHub.Storage;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
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

    // Use the engine's effort-based batch size so high-effort analysis receives smaller
    // batches within the same route deadline. The selected input set remains unchanged.
    public const int AnalysisBatchSize = 0;

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
    /// A single analysis result, including its own partial-completion and failure state.
    /// </summary>
    public sealed record AuditAnalysisResult(IReadOnlyList<AuditIssue>? Issues, bool IsPartial, string? Failure)
    {
        public int ValidatedBatchCount { get; init; }
        public int ReusedEvents { get; init; }
        public int AnalyzedEvents { get; init; }
    }

    /// <summary>
    /// Runs a sandboxed AI pass over the supplied audit events and returns the parsed
    /// advisory issues together with this request's completion state.
    /// </summary>
    public static async Task<AuditAnalysisResult> AnalyzeEventsAsync(
        IReadOnlyList<SecurityEvent> events,
        IProgress<AiTaskProgress>? progress = null,
        IProgress<AuditAnalysisResult>? partialResults = null,
        AuditAnalysisCache? cache = null,
        bool forceReanalyze = false,
        CancellationToken cancellationToken = default)
    {
        if (events is null || events.Count == 0)
        {
            return new(null, false, null);
        }

        if (!IsAvailable)
        {
            Logger.LogInfo("AI audit skipped: service unavailable.");
            return new(null, false, "AI Hub unavailable");
        }

        // Prioritise what matters: errors and criticals first, then the most recent
        // remaining events. A plain Take() would analyse only the oldest events in the
        // window and silently ignore every later one.
        List<SecurityEvent> selected = SelectEvidence(events);
        var inputs = selected.Select(ToInput).ToArray();
        string scope = await ReadAnalysisScopeAsync(cancellationToken).ConfigureAwait(false);
        cache ??= new AuditAnalysisCache();
        var (plan, items) = PrepareAnalysisRequest(cache, scope, inputs, forceReanalyze);

        if (items.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cache.Commit(plan, plan.ReusedIssues);
            return new(plan.ReusedIssues, false, null) { ReusedEvents = selected.Count };
        }

        string initialStatus = IsChinese
            ? $"正在请求 AI 深度分析（{items.Count} 条事件）..."
            : $"Requesting AI deep analysis for {items.Count} event(s)...";
        progress?.Report(new("Prepare", initialStatus, 0, 0));

        string analysisId = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = Stopwatch.StartNew();
        Logger.LogInfo($"AI audit started: id={analysisId}, collected={events.Count}, selected={items.Count}, timeoutSeconds={AnalysisTimeoutSeconds}, batchSize={AnalysisBatchSize}, routeTimeoutSeconds={AnalysisRouteTimeoutSeconds}");
        var batchProgress = new AuditProgress(analysisId, stopwatch, progress);
        var validatedIssues = new List<AuditIssue>(plan.ReusedIssues);
        int validatedBatchCount = 0;
        var partialLock = new object();
        string outcome = "failed";
        try
        {
            AiTaskResult<AuditIssueContainer> result = await AiHubEngine.Current.ExecuteTaskAsync(
                PluginId,
                TaskId,
                items,
                CreateSchema(batch =>
                {
                    lock (partialLock)
                    {
                        validatedIssues.AddRange(batch.Issues.Select(plan.RestoreReference));
                        partialResults?.Report(new(DeduplicateIssues(validatedIssues), true, null) { ValidatedBatchCount = ++validatedBatchCount });
                    }
                }),
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
            var analysis = ReadAnalysisResult(result, analysisId);
            if (analysis.Issues is not null)
            {
                analysis = analysis with
                {
                    Issues = DeduplicateIssues(plan.ReusedIssues.Concat(analysis.Issues.Select(plan.RestoreReference))),
                    ReusedEvents = selected.Count - items.Count,
                    AnalyzedEvents = items.Count,
                };
                if (!analysis.IsPartial && analysis.Failure is null)
                {
                    if (string.Equals(scope, await ReadAnalysisScopeAsync(cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        cache.Commit(plan, analysis.Issues);
                    }
                }
            }
            else if (plan.ReusedIssues.Count > 0)
            {
                analysis = analysis with { Issues = plan.ReusedIssues, IsPartial = true, ReusedEvents = selected.Count - items.Count, AnalyzedEvents = items.Count };
            }
            return analysis;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError($"AI audit failed: id={analysisId}, type={ex.GetType().Name}, hresult=0x{ex.HResult:X8}");
            throw;
        }
        finally
        {
            Logger.LogInfo($"AI audit finished: id={analysisId}, outcome={outcome}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
    }

    private static AuditAnalysisResult ReadAnalysisResult(AiTaskResult<AuditIssueContainer> result, string analysisId)
    {
        string? failure = result.IsSuccess ? null : result.TotalBatches > 0
            ? $"{result.ErrorCode}; {result.CompletedBatches}/{result.TotalBatches} batches completed"
            : result.ErrorCode.ToString();
        if (!result.IsSuccess)
        {
            string failureRoute = SafeFailureRoute(result.FailureRoute);
            string authenticationHint = AuthenticationFailureHint(result.Diagnostics, failureRoute);
            if (authenticationHint.Length > 0)
            {
                failure = authenticationHint + " " + failure;
            }

            string diagnostic = AiDiagnosticText.Format(result.Diagnostics);
            failure += "; failureRoute=" + failureRoute;
            if (diagnostic.Length > 0)
            {
                failure += "; " + diagnostic;
            }

            Logger.LogWarning($"AI audit incomplete: id={analysisId}, code={result.ErrorCode}, failureRoute={failureRoute}, partial={result.HasPartialResult}, batches={result.CompletedBatches}/{result.TotalBatches}{(diagnostic.Length > 0 ? ", " + diagnostic : string.Empty)}");
        }

        return new(result.IsSuccess || result.HasPartialResult ? DeduplicateIssues(result.Payload?.Issues ?? []) : null, result.HasPartialResult, failure);
    }

    private static string SafeFailureRoute(string? route) => route?.ToLowerInvariant() switch
    {
        "main" => "Main",
        "fallback" => "Fallback",
        _ => "unknown",
    };

    private static string AuthenticationFailureHint(AiRouteDiagnostics? diagnostics, string failureRoute)
    {
        if (diagnostics is null || (diagnostics.HttpStatusCode != 401 && !string.Equals(diagnostics.Reason, "Authentication", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Empty;
        }

        string status = diagnostics.HttpStatusCode == 401 ? " (HTTP 401)" : string.Empty;
        if (IsChinese)
        {
            string route = failureRoute == "unknown" ? "AI 路由" : failureRoute + " 路由";
            string target = failureRoute == "unknown" ? "失败路由的" : failureRoute + " 的";
            return $"{route}身份验证失败{status}。请在 AI service 中核查 {target} Endpoint 与 API key 是否配对、凭据及访问权限是否有效；保存后运行“测试连接”。";
        }

        string routeLabel = failureRoute == "unknown" ? "the AI route" : failureRoute;
        string routeTarget = failureRoute == "unknown" ? "the failed route's" : failureRoute + "'s";
        return $"Authentication failed on {routeLabel}{status}. In AI service, verify that {routeTarget} endpoint and API key belong together and that the credentials and access permissions are valid, then save and run Test connection.";
    }

    private sealed class AuditProgress(string analysisId, Stopwatch stopwatch, IProgress<AiTaskProgress>? progress) : IProgress<AiTaskProgress>
    {
        private readonly object _sync = new();
        private int _completed;
        private int _total;
        private int _failed;

        public void Report(AiTaskProgress value)
        {
            lock (_sync)
            {
                _completed = Math.Max(_completed, value.CompletedBatches);
                _total = Math.Max(_total, value.TotalBatches);
                _failed = Math.Max(_failed, value.FailedBatches);
                var current = value with
                {
                    CompletedBatches = _completed,
                    TotalBatches = _total,
                    FailedBatches = _failed,
                    ElapsedMilliseconds = Math.Max(value.ElapsedMilliseconds, stopwatch.ElapsedMilliseconds),
                };
                string stage = value.Stage is "Prepare" or "Queue" or "Execute" or "Route" or "Failover" or "Validate" or "Failed" or "Cancelled" ? value.Stage : "Other";
                string diagnostic = NetMapAiService.FormatRouteDiagnostic(value.StatusMessage);
                Logger.LogInfo($"AI audit progress: id={analysisId}, stage={stage}, batches={_completed}/{_total}, failed={_failed}, active={value.ActiveBatches}, queued={value.QueuedBatches}, elapsedMs={current.ElapsedMilliseconds}{(diagnostic.Length > 0 ? ", " + diagnostic : string.Empty)}");
                progress?.Report(current with { StatusMessage = AiTaskProgressText.Summary(current, IsChinese) });
            }
        }
    }

    /// <summary>
    /// Schema for the built-in security-audit chain: source-generated JSON metadata for
    /// the input/output pair, plus semantic validation of the returned issues.
    /// </summary>
    internal static AiTaskSchema<EventAnalysisInput, AuditIssueContainer> CreateSchema(Action<AuditIssueContainer>? reportValidatedBatch = null)
    {
        return new AiTaskSchema<EventAnalysisInput, AuditIssueContainer>
        {
            InputTypeInfo = AIHubLibJsonContext.Default.EventAnalysisInput,
            OutputTypeInfo = AIHubLibJsonContext.Default.AuditIssueContainer,
            ValidateOutput = ValidateOutput,
            ReportValidatedBatch = reportValidatedBatch,

            // Large event windows are split at the record and encoded-size limits,
            // and the engine rejects a multi-batch task whose schema cannot
            // merge the per-batch outputs. Without this the audit failed with InvalidPayload
            // before any request left the machine.
            MergeBatches = containers => new AuditIssueContainer
            {
                Issues = DeduplicateIssues(containers.SelectMany(container => container.Issues ?? [])),
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

            if (AuditAnalysisCache.GetNarrativeReferences(issue).Any(reference => !itemIds.Contains(reference)))
            {
                return RejectOutput(issueIndex, "text", "unknown_input_reference");
            }
        }

        return true;
    }

    private static bool IsKnownSeverity(string? severity)
    {
        return severity is "High" or "Medium" or "Low";
    }

    // Opaque references remain request-wide. Never sum occurrences reported by workers
    // for the same evidence: that would count one event repeatedly and inflate severity.
    internal static List<AuditIssue> DeduplicateIssues(IEnumerable<AuditIssue> issues) => issues
        .GroupBy(issue => issue.Key + "\0" + string.Join("\0", IssueReferences(issue)
            .OrderBy(reference => reference, StringComparer.Ordinal)), StringComparer.Ordinal)
        .Select(group => group.OrderByDescending(issue => issue.Severity == "High" ? 3 : issue.Severity == "Medium" ? 2 : 1)
            .ThenByDescending(issue => issue.Occurrences).First())
        .GroupBy(issue => string.IsNullOrWhiteSpace(issue.Key) || string.IsNullOrWhiteSpace(issue.Affected)
            ? issue.Key + "\0" + issue.EventRef
            : string.Join("\0", issue.Key, issue.Category, issue.LogName, issue.Source, issue.Affected), StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var latest = group.OrderByDescending(issue => issue.Severity == "High" ? 3 : issue.Severity == "Medium" ? 2 : 1)
                .ThenByDescending(issue => issue.EventTimestamp, StringComparer.Ordinal).First();
            if (group.Count() == 1)
            {
                return latest;
            }
            return new AuditIssueEnhanced(latest)
            {
                RelatedEventRefs = group.SelectMany(IssueReferences).Distinct(StringComparer.Ordinal).ToList(),
                Occurrences = group.SelectMany(IssueReferences).Distinct(StringComparer.Ordinal).Count(),
            };
        })
        .ToList();

    private static IEnumerable<string> IssueReferences(AuditIssue issue) => issue.RelatedEventRefs.Append(issue.EventRef)
        .Concat(AuditAnalysisCache.GetNarrativeReferences(issue)).Distinct(StringComparer.Ordinal);

    internal static string CreateEvidenceFingerprint(IReadOnlyList<SecurityEvent> events)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Language));
        foreach (var item in SelectEvidence(events).Select(ToInput))
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(item, AIHubLibJsonContext.Default.EventAnalysisInput);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string CreateConfigurationFingerprint(AiHubConfig configuration)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
            CryptographicOperations.ZeroMemory(bytes);
        }
        Append(configuration.SelectedKernel);
        Append(configuration.EnableSubagents.ToString(CultureInfo.InvariantCulture));
        Append(configuration.MaxAgentsPerTask.ToString(CultureInfo.InvariantCulture));
        foreach (var target in configuration.Targets.OrderBy(target => target.Name, StringComparer.Ordinal))
        {
            Append(target.Name);
            Append(target.IsActive.ToString(CultureInfo.InvariantCulture));
            Append(target.BaseUrl);
            Append(target.ApiKey);
            Append(target.Mode);
            Append(target.Model);
            Append(target.Effort);
            Append(target.ContextWindowTokens.ToString(CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static (AuditAnalysisPlan Plan, List<EventAnalysisInput> Items) PrepareAnalysisRequest(
        AuditAnalysisCache cache, string scope, IReadOnlyList<EventAnalysisInput> inputs, bool forceReanalyze)
    {
        var plan = cache.Prepare(scope,
            inputs.Select(input => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, AIHubLibJsonContext.Default.EventAnalysisInput)))).ToArray(),
            inputs.Select(input => string.Join("\0", input.LogName, input.Provider).ToUpperInvariant()).ToArray(), forceReanalyze,
            inputs.Select(input => DateTime.TryParse(input.TimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime time)
                ? (DateTime?)time : null).ToArray(),
            inputs.Select(input => input.Summary).ToArray());
        return (plan, plan.RequestedIndices.Select(index => inputs[index]).ToList());
    }

    private static async Task<string> ReadAnalysisScopeAsync(CancellationToken cancellationToken)
    {
        var policy = new SecurityPolicyService();
        string globalPolicy = await policy.LoadGlobalPolicyAsync(cancellationToken).ConfigureAwait(false);
        string taskPolicy = await policy.LoadTaskAgentsPolicyAsync(PluginId, TaskId, cancellationToken).ConfigureAwait(false);
        string taskSecurity = await policy.LoadTaskSecurityPolicyAsync(PluginId, TaskId, cancellationToken).ConfigureAwait(false);
        string configurationHash = CreateConfigurationFingerprint(new AiHubSettingsStore().Load());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", "audit-v3", Language,
            configurationHash, globalPolicy, taskPolicy, taskSecurity))));
    }

    private static List<SecurityEvent> SelectEvidence(IReadOnlyList<SecurityEvent> events) => events
        .OrderByDescending(e => e.Level is <= 2)
        .ThenByDescending(e => e.TimeCreated ?? DateTime.MinValue)
        .Take(MaxEventsPerRequest)
        .ToList();

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
