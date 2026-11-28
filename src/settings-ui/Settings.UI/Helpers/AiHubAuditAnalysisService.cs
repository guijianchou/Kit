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

    // Exact repetitions share an envelope and the engine still enforces its encoded-size
    // limit. Avoid paying max-effort reasoning and repeated report generation every 15 events.
    public const int AnalysisBatchSize = AuditEvidencePacking.BatchSize;

    // Astra audit batches can take several minutes while still completing successfully.
    // Keep the overall audit deadline bounded, but do not silently replace a slow
    // primary model with the fallback route merely because this route budget elapsed.
    public const int AnalysisRouteTimeoutSeconds = 900;

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
        public string Scope { get; init; } = string.Empty;
        public string EvidenceFingerprint { get; init; } = string.Empty;
        public string UsedModel { get; init; } = string.Empty;
        public string UsedRoute { get; init; } = string.Empty;
        public bool MatchesRequestedModel { get; init; }

        // This proves coverage of the bounded selected inputs, not every collected event.
        public bool CompleteEvidenceCoverage { get; init; }
    }

    /// <summary>Session-only checkpoints for validated batches, including batches with no findings.</summary>
    public sealed class AuditAnalysisRetryState
    {
        private readonly object _gate = new();
        private readonly HashSet<int> _completed = [];
        private readonly List<AuditIssue> _issues = [];
        private string _identity = string.Empty;
        private AuditAnalysisPlan? _originalPlan;
        private AuditAnalysisPlan? _activePlan;
        private string _checkpointModel = string.Empty;
        private string _checkpointRoute = string.Empty;

        internal AuditAnalysisPlan Prepare(AuditAnalysisPlan plan, string identity, bool force)
        {
            lock (_gate)
            {
                if (force || _originalPlan is null || !string.Equals(_identity, identity, StringComparison.Ordinal))
                {
                    _identity = identity;
                    _originalPlan = plan;
                    _completed.Clear();
                    _issues.Clear();
                    _checkpointModel = _checkpointRoute = string.Empty;
                }
                _activePlan = _originalPlan with
                {
                    RequestedIndices = _originalPlan.RequestedIndices.Where(index => !_completed.Contains(index)).ToArray(),
                    ReusedIssues = DeduplicateIssues(_originalPlan.ReusedIssues.Concat(_issues)),
                    UsedModel = JoinProvenance(_originalPlan.UsedModel, _checkpointModel),
                    UsedRoute = JoinProvenance(_originalPlan.UsedRoute, _checkpointRoute),
                };
                return _activePlan;
            }
        }

        internal void Checkpoint(AuditAnalysisPlan plan, string identity, AuditIssueContainer batch, IReadOnlySet<string> inputIds,
            string usedModel = "", string usedRoute = "")
        {
            lock (_gate)
            {
                if (_identity != identity || _originalPlan is null || !ReferenceEquals(plan, _activePlan))
                {
                    return;
                }
                var indices = new List<int>();
                foreach (string id in inputIds)
                {
                    if (!id.StartsWith("item-", StringComparison.Ordinal) || !int.TryParse(id.AsSpan(5), out int index)
                        || index < 1 || index > plan.RequestedIndices.Length)
                    {
                        return;
                    }
                    indices.Add(plan.RequestedIndices[index - 1]);
                }
                if (indices.All(_completed.Contains))
                {
                    return;
                }
                _issues.AddRange(batch.Issues.Select(plan.RestoreReference));
                _completed.UnionWith(indices);
                _checkpointModel = JoinProvenance(_checkpointModel, usedModel);
                _checkpointRoute = JoinProvenance(_checkpointRoute, usedRoute);
            }
        }

        internal void Complete(AuditAnalysisPlan plan, string identity)
        {
            lock (_gate)
            {
                if (_identity != identity || !ReferenceEquals(plan, _activePlan))
                {
                    return;
                }
                _originalPlan = null;
                _activePlan = null;
                _completed.Clear();
                _issues.Clear();
                _checkpointModel = _checkpointRoute = string.Empty;
            }
        }
    }

    // Schema callbacks run before the engine identifies the successful route. Keep their
    // checkpoints local until final provenance is known; cancellation discards this buffer.
    internal sealed class AuditCheckpointBuffer
    {
        private readonly List<(AuditIssueContainer Batch, IReadOnlySet<string> InputIds)> _batches = [];

        internal void Stage(AuditIssueContainer batch, IReadOnlySet<string> inputIds)
        {
            lock (_batches)
            {
                _batches.Add((batch, inputIds.ToHashSet(StringComparer.Ordinal)));
            }
        }

        internal void Commit(AuditAnalysisRetryState? retry, AuditAnalysisPlan plan, string identity, AuditAnalysisResult result)
        {
            if (retry is null || !result.MatchesRequestedModel || result.Issues is null)
            {
                return;
            }
            lock (_batches)
            {
                foreach (var (batch, ids) in _batches)
                {
                    retry.Checkpoint(plan, identity, batch, ids, result.UsedModel, result.UsedRoute);
                }
                _batches.Clear();
            }
        }
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
        AuditAnalysisRetryState? retryState = null,
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
        string requestedModel = new AiHubSettingsStore().Load().GetMainTarget()?.Model?.Trim() ?? string.Empty;
        string scope = await ReadAnalysisScopeAsync(cancellationToken).ConfigureAwait(false);
        cache ??= new AuditAnalysisCache();
        var (plan, items) = PrepareAnalysisRequest(cache, scope, inputs, forceReanalyze);
        if (plan.RequestedIndices.Length < inputs.Length && !IsRequestedModel(plan.UsedModel, plan.UsedRoute, requestedModel))
        {
            (plan, items) = PrepareAnalysisRequest(cache, scope, inputs, forceReanalyze: true);
        }
        string evidenceFingerprint = CreateEvidenceFingerprint(events);
        string retryIdentity = scope + ":" + evidenceFingerprint;
        if (retryState is not null)
        {
            plan = retryState.Prepare(plan, retryIdentity, forceReanalyze);
            if (plan.RequestedIndices.Length < inputs.Length && !IsRequestedModel(plan.UsedModel, plan.UsedRoute, requestedModel))
            {
                (plan, _) = PrepareAnalysisRequest(cache, scope, inputs, forceReanalyze: true);
                plan = retryState.Prepare(plan, retryIdentity, force: true);
            }
            items = plan.RequestedIndices.Select(index => inputs[index]).ToList();
        }

        if (items.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string completedScope = await ReadAnalysisScopeAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(scope, completedScope, StringComparison.Ordinal))
            {
                return new(null, true, "AnalysisScopeChanged") { Scope = scope, EvidenceFingerprint = evidenceFingerprint };
            }
            cancellationToken.ThrowIfCancellationRequested();
            cache.Commit(plan, plan.ReusedIssues, plan.UsedModel, plan.UsedRoute);
            retryState?.Complete(plan, retryIdentity);
            return CachedAnalysis(plan, requestedModel, evidenceFingerprint);
        }

        var packed = AuditEvidencePacking.Create(items);
        int packedBytes = packed.Items.Sum(item => JsonSerializer.SerializeToUtf8Bytes(item, AIHubLibJsonContext.Default.EventAnalysisInput).Length);
        string initialStatus = IsChinese
            ? $"正在请求 AI 深度分析（{items.Count} 条事件）..."
            : $"Requesting AI deep analysis for {items.Count} event(s)...";
        progress?.Report(new("Prepare", initialStatus, 0, 0));

        string analysisId = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = Stopwatch.StartNew();
        Logger.LogInfo($"AI audit started: id={analysisId}, collected={events.Count}, selected={selected.Count}, requested={items.Count}, packedRecords={packed.Items.Count}, packedBytes={packedBytes}, reused={selected.Count - items.Count}, timeoutSeconds={AnalysisTimeoutSeconds}, batchSize={AnalysisBatchSize}, routeTimeoutSeconds={AnalysisRouteTimeoutSeconds}");
        var batchProgress = new AuditProgress(analysisId, stopwatch, progress);
        var validatedIssues = new List<AuditIssue>(plan.ReusedIssues);
        int validatedBatchCount = 0;
        var partialLock = new object();
        string outcome = "failed";
        var checkpoints = new AuditCheckpointBuffer();
        try
        {
            AiTaskResult<AuditIssueContainer> result = await AiHubEngine.Current.ExecuteTaskAsync(
                PluginId,
                TaskId,
                packed.Items,
                CreateSchema(batch =>
                {
                    lock (partialLock)
                    {
                        validatedIssues.AddRange(batch.Issues.Select(packed.RestoreIssue).Select(plan.RestoreReference));
                        partialResults?.Report(new(DeduplicateIssues(validatedIssues), true, null) { ValidatedBatchCount = ++validatedBatchCount });
                    }
                }, (batch, inputIds) => checkpoints.Stage(packed.RestoreBatch(batch), packed.ExpandReferences(inputIds))),
                CreateAnalysisOptions(batchProgress),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            outcome = result.IsSuccess ? "ok" : result.HasPartialResult ? "partial" : "failed";
            var analysis = ApplyExecutionProvenance(ReadAnalysisResult(result, analysisId), result, requestedModel);
            checkpoints.Commit(retryState, plan, retryIdentity, analysis);
            if (analysis.Issues is not null)
            {
                analysis = analysis with
                {
                    Issues = DeduplicateIssues(plan.ReusedIssues.Concat(analysis.Issues.Select(packed.RestoreIssue).Select(plan.RestoreReference))),
                    ReusedEvents = selected.Count - items.Count,
                    AnalyzedEvents = items.Count,
                    UsedModel = JoinProvenance(selected.Count > items.Count ? plan.UsedModel : string.Empty, analysis.UsedModel),
                    UsedRoute = JoinProvenance(selected.Count > items.Count ? plan.UsedRoute : string.Empty, analysis.UsedRoute),
                };
                if (!analysis.IsPartial && analysis.Failure is null)
                {
                    analysis = ConfirmEvidenceCoverage(analysis, selected.Count, scope, await ReadAnalysisScopeAsync(cancellationToken).ConfigureAwait(false));
                    if (analysis.CompleteEvidenceCoverage && analysis.MatchesRequestedModel && analysis.Issues is not null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        CommitCompletedAnalysis(cache, plan, analysis);
                        retryState?.Complete(plan, retryIdentity);
                    }
                }
            }
            else if (plan.ReusedIssues.Count > 0)
            {
                analysis = analysis with
                {
                    Issues = plan.ReusedIssues, IsPartial = true, ReusedEvents = selected.Count - items.Count, AnalyzedEvents = items.Count,
                    UsedModel = plan.UsedModel, UsedRoute = plan.UsedRoute,
                    MatchesRequestedModel = IsRequestedModel(plan.UsedModel, plan.UsedRoute, requestedModel),
                };
            }
            return analysis with { Scope = ResultScope(scope, analysis), EvidenceFingerprint = evidenceFingerprint };
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

    internal static AiTaskOptions CreateAnalysisOptions(IProgress<AiTaskProgress>? progress) => new()
    {
        Language = Language,
        TimeoutSeconds = AnalysisTimeoutSeconds,
        BatchSize = AnalysisBatchSize,
        RouteTimeoutSeconds = AnalysisRouteTimeoutSeconds,
        AllowTimeoutFailover = false,
        Progress = progress,
        AllowPartialResults = true,
    };

    internal static bool IsRequestedModel(string usedModel, string usedRoute, string requestedModel) =>
        !string.IsNullOrWhiteSpace(requestedModel)
        && string.Equals(usedModel, requestedModel, StringComparison.Ordinal)
        && string.Equals(usedRoute, "Main", StringComparison.Ordinal);

    internal static AuditAnalysisResult CachedAnalysis(AuditAnalysisPlan plan, string requestedModel, string evidenceFingerprint) => new(plan.ReusedIssues, false, null)
    {
        ReusedEvents = plan.Identities.Length,
        Scope = plan.Scope,
        EvidenceFingerprint = evidenceFingerprint,
        CompleteEvidenceCoverage = true,
        UsedModel = plan.UsedModel,
        UsedRoute = plan.UsedRoute,
        MatchesRequestedModel = IsRequestedModel(plan.UsedModel, plan.UsedRoute, requestedModel),
    };

    internal static bool CommitCompletedAnalysis(AuditAnalysisCache cache, AuditAnalysisPlan plan, AuditAnalysisResult result)
    {
        if (!result.CompleteEvidenceCoverage || !result.MatchesRequestedModel || result.IsPartial || result.Failure is not null || result.Issues is null)
        {
            return false;
        }
        cache.Commit(plan, result.Issues, result.UsedModel, result.UsedRoute);
        return true;
    }

    internal static AuditAnalysisResult ApplyExecutionProvenance(AuditAnalysisResult analysis,
        AiTaskResult<AuditIssueContainer> execution, string requestedModel) => analysis with
    {
        UsedModel = execution.Payload is not null ? execution.UsedModel : string.Empty,
        UsedRoute = execution.Payload is not null ? execution.UsedRoute : string.Empty,
        MatchesRequestedModel = execution.Payload is not null && IsRequestedModel(execution.UsedModel, execution.UsedRoute, requestedModel),
    };

    internal static string ResultScope(string requestedScope, AuditAnalysisResult result) => result.MatchesRequestedModel
        ? requestedScope
        : requestedScope + ":actual:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.UsedRoute + "\0" + result.UsedModel)));

    private static string JoinProvenance(string first, string second) => string.Join(" + ",
        new[] { first, second }.Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value.Split(" + ", StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal));

    internal static AuditAnalysisResult ConfirmEvidenceCoverage(AuditAnalysisResult result, int selectedCount, string requestedScope, string completedScope)
    {
        if (result.Issues is null || result.IsPartial || result.Failure is not null)
        {
            return result with { CompleteEvidenceCoverage = false };
        }
        bool complete = selectedCount > 0 && result.AnalyzedEvents + result.ReusedEvents == selectedCount
            && !string.IsNullOrEmpty(requestedScope) && string.Equals(requestedScope, completedScope, StringComparison.Ordinal);
        return result with { CompleteEvidenceCoverage = complete, IsPartial = !complete, Failure = complete ? null : "AnalysisCoverageChanged" };
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
                string route = SafeFailureRoute(value.Route);
                Logger.LogInfo($"AI audit progress: id={analysisId}, stage={stage}, batch={value.BatchIndex}, route={route}, batches={_completed}/{_total}, failed={_failed}, active={value.ActiveBatches}, queued={value.QueuedBatches}, elapsedMs={current.ElapsedMilliseconds}{(diagnostic.Length > 0 ? ", " + diagnostic : string.Empty)}");
                progress?.Report(current with { StatusMessage = AiTaskProgressText.Summary(current, IsChinese) });
            }
        }
    }

    /// <summary>
    /// Schema for the built-in security-audit chain: source-generated JSON metadata for
    /// the input/output pair, plus semantic validation of the returned issues.
    /// </summary>
    internal static AiTaskSchema<EventAnalysisInput, AuditIssueContainer> CreateSchema(Action<AuditIssueContainer>? reportValidatedBatch = null,
        Action<AuditIssueContainer, IReadOnlySet<string>>? checkpointValidatedBatch = null)
    {
        return new AiTaskSchema<EventAnalysisInput, AuditIssueContainer>
        {
            InputTypeInfo = AIHubLibJsonContext.Default.EventAnalysisInput,
            OutputTypeInfo = AIHubLibJsonContext.Default.AuditIssueContainer,
            ValidateOutput = (output, ids) =>
            {
                if (!ValidateOutput(output, ids))
                {
                    return false;
                }
                checkpointValidatedBatch?.Invoke(output, ids);
                return true;
            },
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
            if (issue.RemediationPlan is not null && !issue.RemediationPlan.IsValid)
            {
                return RejectOutput(issueIndex, "remediationPlan", "incomplete_or_too_long");
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

    internal static async Task<string> ReadAnalysisScopeAsync(CancellationToken cancellationToken)
    {
        var policy = new SecurityPolicyService();
        string globalPolicy = await policy.LoadGlobalPolicyAsync(cancellationToken).ConfigureAwait(false);
        string taskPolicy = await policy.LoadTaskAgentsPolicyAsync(PluginId, TaskId, cancellationToken).ConfigureAwait(false);
        string taskSecurity = await policy.LoadTaskSecurityPolicyAsync(PluginId, TaskId, cancellationToken).ConfigureAwait(false);
        string configurationHash = CreateConfigurationFingerprint(new AiHubSettingsStore().Load());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", "audit-v5-provenance", Language,
            configurationHash, globalPolicy, taskPolicy, taskSecurity))));
    }

    internal static List<SecurityEvent> SelectEvidence(IReadOnlyList<SecurityEvent> events) => events
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
            Summary = Truncate(source.Message, AuditEvidencePacking.SummaryCharacterLimit),
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
