using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
using Kit.AiHub.Serialization;
using Kit.AiHub.Storage;
using Kit.AIHubLib.Models;
using ManagedCommon;

namespace Kit.AIHubLib.Services;

/// <summary>Independent advisory synthesis; host statistics and the audit scan are never rewritten by AI.</summary>
public static class WeeklyReportAiService
{
    internal const int MaximumInputBytes = 60000;
    private const int MaximumGroups = 160;
    private const int MaximumCandidateGroups = 600;

    internal delegate Task<AiTaskResult<WeeklyReportAiOutput>> ReviewExecutor(
        IReadOnlyList<WeeklyReportAiInput> items,
        AiTaskSchema<WeeklyReportAiInput, WeeklyReportAiOutput> schema,
        AiTaskOptions options,
        CancellationToken token);

    public static async Task<WeeklyReportReview> ReviewAsync(WeeklyReportReviewInput input,
        IProgress<WeeklyReportProgress>? progress, CancellationToken token)
    {
        string identity = await ReadIdentityAsync(token).ConfigureAwait(false);
        string requestedModel = new AiHubSettingsStore().Load().GetMainTarget()?.Model ?? string.Empty;
        var result = await ReviewCoreAsync(input, (items, schema, options, cancellation) =>
            AiHubEngine.Current.ExecuteTaskAsync("aihub", HubTaskCatalog.WeeklyReportId, items, schema, options, cancellation),
            progress, token).ConfigureAwait(false);
        if (!string.Equals(identity, await ReadIdentityAsync(token).ConfigureAwait(false), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The weekly report model configuration or policy changed during analysis. Retry with the current settings.");
        }
        if (!MatchesRequestedRoute(result, requestedModel))
        {
            throw new InvalidDataException("The weekly report was not completed by the configured primary model. The previous complete report is retained.");
        }
        return result;
    }

    internal static bool MatchesRequestedRoute(WeeklyReportReview review, string requestedModel) => !string.IsNullOrWhiteSpace(requestedModel)
        && string.Equals(review.UsedModel, requestedModel, StringComparison.Ordinal)
        && string.Equals(review.UsedRoute, "Main", StringComparison.Ordinal);

    public static async Task<string> ReadIdentityAsync(CancellationToken token)
    {
        var policies = new SecurityPolicyService();
        string global = await policies.LoadGlobalPolicyAsync(token).ConfigureAwait(false);
        string task = await policies.LoadTaskAgentsPolicyAsync("aihub", HubTaskCatalog.WeeklyReportId, token).ConfigureAwait(false);
        string security = await policies.LoadTaskSecurityPolicyAsync("aihub", HubTaskCatalog.WeeklyReportId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return CreateIdentity(new AiHubSettingsStore().Load(), global, task, security);
    }

    internal static string CreateIdentity(AiHubConfig configuration, string global, string task, string security)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
            CryptographicOperations.ZeroMemory(bytes);
        }
        Append(DefaultWeeklyReportPolicy.Version);
        Append(JsonSerializer.Serialize(configuration, AiHubJsonContext.Default.AiHubConfig));
        // ApiKey is intentionally absent from public config JSON. Hash each route's
        // credential in route order, without logging or persisting the credential.
        foreach (var target in configuration.Targets) Append(target.ApiKey ?? string.Empty);
        Append(global);
        Append(task);
        Append(security);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static async Task<WeeklyReportReview> ReviewCoreAsync(WeeklyReportReviewInput input,
        ReviewExecutor execute, IProgress<WeeklyReportProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = BuildRequest(input, token);
        var evidenceRefs = EvidenceReferences(request);
        var schema = new AiTaskSchema<WeeklyReportAiInput, WeeklyReportAiOutput>
        {
            InputTypeInfo = WeeklyReportReviewJsonContext.Default.WeeklyReportAiInput,
            OutputTypeInfo = WeeklyReportReviewJsonContext.Default.WeeklyReportAiOutput,
            ValidateOutput = (output, ids) => ValidateOutput(output, ids, evidenceRefs),
            AllowedActions = new HashSet<string>(StringComparer.Ordinal) { "skip" },
        };
        string id = Guid.NewGuid().ToString("N")[..8];
        Logger.LogInfo($"Weekly report AI started: id={id}, mode={input.Mode}, events={request.Selection.ProvidedEvents}, groups={request.Selection.TotalGroups}, selectedGroups={request.Selection.IncludedGroups}, bytes={EncodedSize(request)}");
        string outcome = "failed";
        try
        {
            var options = new AiTaskOptions
            {
                Language = "en-US",
                BatchSize = 1,
                TimeoutSeconds = 1800,
                RouteTimeoutSeconds = 900,
                AllowTimeoutFailover = false,
                AllowPartialResults = false,
                Progress = new ReviewProgress(progress),
            };
            var execution = await execute([request], schema, options, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!execution.IsSuccess || execution.Payload is null
                || !ValidateOutput(execution.Payload, new HashSet<string>(StringComparer.Ordinal) { "item-000001" }, evidenceRefs)
                || string.IsNullOrWhiteSpace(execution.UsedModel) || string.IsNullOrWhiteSpace(execution.UsedRoute))
            {
                throw new InvalidDataException($"Weekly report AI did not produce a complete validated report ({execution.ErrorCode}).");
            }
            var evidenceIndex = BuildEvidenceIndex(request);
            outcome = "ok";
            Logger.LogInfo($"Weekly report AI validated: id={id}, model={execution.UsedModel}, route={execution.UsedRoute}, findings={execution.Payload.Findings.Count}");
            return new()
            {
                AssessmentZh = execution.Payload.AssessmentZh,
                AssessmentEn = execution.Payload.AssessmentEn,
                Findings = execution.Payload.Findings,
                UsedModel = execution.UsedModel,
                UsedRoute = execution.UsedRoute,
                EvidenceIndex = evidenceIndex,
            };
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            Logger.LogInfo($"Weekly report AI finished: id={id}, mode={input.Mode}, outcome={outcome}");
        }
    }

    internal static WeeklyReportAiInput BuildRequest(WeeklyReportReviewInput input, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Data.End <= input.Data.Start || input.Data.PreviousStart > input.Data.Start)
            throw new InvalidDataException("The weekly report evidence window is invalid.");
        var events = input.Events.Where(item => item.Time >= input.Data.PreviousStart && item.Time < input.Data.End).ToArray();
        token.ThrowIfCancellationRequested();
        var groups = events.GroupBy(item => (item.Channel, item.Provider, item.EventId, item.Level, Current: item.Time >= input.Data.Start))
            .OrderBy(group => group.Key.Level is >= 1 and <= 2 ? 0 : group.Key.Level == 3 ? 1 : 2)
            .ThenByDescending(group => group.Count()).ThenBy(group => group.Key.Channel, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Provider, StringComparer.Ordinal).ThenBy(group => group.Key.EventId)
            .ThenByDescending(group => group.Key.Current)
            .Select((group, index) => (Group: group, Index: index)).ToArray();
        var counts = events.GroupBy(item => (item.Channel, item.Level, Current: item.Time >= input.Data.Start))
            .OrderBy(group => group.Key.Channel, StringComparer.Ordinal).ThenBy(group => group.Key.Level).ThenByDescending(group => group.Key.Current)
            .Select(group => new WeeklyEvidenceCount(Clip(group.Key.Channel, 120), group.Key.Level, group.Key.Current, group.Count())).ToArray();
        var request = new WeeklyReportAiInput(input.Mode.ToString(), "statistics", input.Data.Start, input.Data.End,
            input.Data.PreviousStart, input.Data.PreviousEnd, input.Data.CurrentComplete, input.Data.PreviousComplete,
            input.Data.Metrics.Select(item => item with { Label = Clip(item.Label, 120), Qualification = Clip(item.Qualification, 240) }).ToArray(),
            input.Data.Daily, input.Data.Coverage.Select(item => item with { Limitation = Clip(item.Limitation, 300) }).ToArray(),
            input.Data.BlindSpots.Select(item => Clip(item, 200)).ToArray(), counts,
            new(events.Length, groups.Length, 0, 0, groups.Length, "All supplied events counted; at most 600 candidate groups considered across providers, severity and recurrence. First/last samples are truncated; omitted groups are unknown."),
            Array.Empty<WeeklyEvidenceGroup>(), ScanReference(input.Scan));
        // Keep room for independent raw evidence when a large historical scan has
        // many findings; its exact total/included counts remain part of the contract.
        while (EncodedSize(request) > 28000 && request.ScanReference is { Findings.Count: > 0 } scan)
        {
            var retained = scan.Findings.Take(scan.Findings.Count - 1).ToArray();
            request = request with { ScanReference = scan with { Findings = retained, IncludedFindings = retained.Length } };
        }
        if (EncodedSize(request) > MaximumInputBytes)
            throw new InvalidDataException("The weekly report statistics exceed the bounded AI input size.");

        var buckets = groups.GroupBy(item => (item.Group.Key.Channel, item.Group.Key.Provider)).Select(group => group.ToArray()).ToArray();
        var selected = new List<WeeklyEvidenceGroup>();
        int considered = 0;
        for (int depth = 0; selected.Count < MaximumGroups && considered < MaximumCandidateGroups && buckets.Any(bucket => depth < bucket.Length); depth++)
        {
            foreach (var bucket in buckets)
            {
                token.ThrowIfCancellationRequested();
                if (depth >= bucket.Length || selected.Count == MaximumGroups || considered == MaximumCandidateGroups) continue;
                considered++;
                var (group, index) = bucket[depth];
                var first = group.MinBy(item => item.Time)!;
                var last = group.MaxBy(item => item.Time)!;
                var evidence = new WeeklyEvidenceGroup("event-group-" + (index + 1).ToString(CultureInfo.InvariantCulture),
                    Clip(group.Key.Channel, 120), Clip(group.Key.Provider, 160), group.Key.EventId, group.Key.Level,
                    group.Key.Current, group.Count(), first.Time, last.Time, Clip(first.Message, 280), Clip(last.Message, 280),
                    last.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal).Take(6)
                        .GroupBy(pair => Clip(pair.Key, 100), StringComparer.Ordinal)
                        .ToDictionary(field => field.Key, field => Clip(field.First().Value, 160), StringComparer.Ordinal));
                var candidate = selected.Append(evidence).ToArray();
                var updated = request with
                {
                    Evidence = candidate,
                    Selection = request.Selection with
                    {
                        IncludedGroups = candidate.Length,
                        IncludedOccurrences = candidate.Sum(item => item.Count),
                        OmittedGroups = groups.Length - candidate.Length,
                    },
                };
                if (EncodedSize(updated) <= MaximumInputBytes)
                {
                    selected.Add(evidence);
                    request = updated;
                }
            }
        }
        return SanitizeAndBound(request);
    }

    private static WeeklyReportAiInput SanitizeAndBound(WeeklyReportAiInput request)
    {
        var sanitizer = new DataSanitizer();
        JsonElement Sanitize(WeeklyReportAiInput value) => sanitizer.SanitizeJson(
            JsonSerializer.SerializeToElement(value, WeeklyReportReviewJsonContext.Default.WeeklyReportAiInput));
        request = JsonSerializer.Deserialize(Sanitize(request), WeeklyReportReviewJsonContext.Default.WeeklyReportAiInput)
            ?? throw new InvalidDataException("The weekly report input could not be sanitized.");
        // Credential redaction can expand a short value. Measure the same final
        // sanitized JSON as TaskAiEngine, and keep the returned host evidence in
        // that sanitized form so exported references describe the actual input.
        while (Encoding.UTF8.GetByteCount(Sanitize(request).GetRawText()) > MaximumInputBytes)
        {
            if (request.Evidence.Count > 0)
            {
                var retained = request.Evidence.Take(request.Evidence.Count - 1).ToArray();
                request = request with
                {
                    Evidence = retained,
                    Selection = request.Selection with
                    {
                        IncludedGroups = retained.Length,
                        IncludedOccurrences = retained.Sum(item => item.Count),
                        OmittedGroups = request.Selection.TotalGroups - retained.Length,
                    },
                };
            }
            else if (request.ScanReference is { Findings.Count: > 0 } scan)
            {
                var retained = scan.Findings.Take(scan.Findings.Count - 1).ToArray();
                request = request with { ScanReference = scan with { Findings = retained, IncludedFindings = retained.Length } };
            }
            else
            {
                throw new InvalidDataException("The sanitized weekly report statistics exceed the bounded AI input size.");
            }
        }
        return request;
    }

    internal static bool ValidateOutput(WeeklyReportAiOutput output, IReadOnlySet<string> inputIds, IReadOnlySet<string> evidenceRefs)
    {
        if (output is null || !inputIds.Contains(output.InputRef) || !Bilingual(output.AssessmentZh, output.AssessmentEn, 4000)
            || output.Findings is null || output.Findings.Count > 12) return false;
        foreach (var finding in output.Findings)
        {
            if (finding is null || finding.Priority is not ("High" or "Medium" or "Low")
                || !Bilingual(finding.TitleZh, finding.TitleEn, 240)
                || !Bilingual(finding.FactsZh, finding.FactsEn, 2400)
                || !Bilingual(finding.HypothesisZh, finding.HypothesisEn, 2400)
                || !Bilingual(finding.StepsZh, finding.StepsEn, 4000)
                || !Bilingual(finding.VerificationZh, finding.VerificationEn, 2400)
                || finding.EvidenceRefs is null || finding.EvidenceRefs.Count is < 1 or > 16
                || finding.EvidenceRefs.Distinct(StringComparer.Ordinal).Count() != finding.EvidenceRefs.Count
                || finding.EvidenceRefs.Any(reference => !evidenceRefs.Contains(reference))) return false;
        }
        return true;
    }

    internal static int EncodedSize(WeeklyReportAiInput request) => JsonSerializer.SerializeToUtf8Bytes(request, WeeklyReportReviewJsonContext.Default.WeeklyReportAiInput).Length;

    private static IReadOnlyList<WeeklyReportEvidence> BuildEvidenceIndex(WeeklyReportAiInput request)
    {
        var sanitizer = new DataSanitizer();
        string Safe(string value, int maximum = 280) => Clip(sanitizer.SanitizeText(value), maximum);
        var index = new List<WeeklyReportEvidence>
        {
            new(request.StatisticsRef, "statistics", string.Empty, string.Empty, null, request.Selection.ProvidedEvents,
                request.Start, request.End,
                $"宿主统计 / Host statistics; groups {request.Selection.IncludedGroups}/{request.Selection.TotalGroups}; omitted {request.Selection.OmittedGroups}.", string.Empty),
        };
        index.AddRange(request.Evidence.Select(item => new WeeklyReportEvidence(item.EvidenceRef,
            item.Current ? "current-event-group" : "previous-event-group", Safe(item.Channel, 120), Safe(item.Provider, 160),
            item.EventId, item.Count, item.First, item.Last, Safe(item.FirstSample), Safe(item.LastSample))));
        if (request.ScanReference is { } scan)
        {
            index.Add(new(scan.EvidenceRef, "scan-reference", string.Empty, Safe(scan.UsedModel, 160), null, scan.TotalFindings,
                scan.StartUtc is { } from ? new DateTimeOffset(from) : null, scan.EndUtc is { } to ? new DateTimeOffset(to) : null,
                Safe(scan.Summary), $"引用已有结论 / Prior findings {scan.IncludedFindings}/{scan.TotalFindings}; {Safe(scan.AnalysisOutcome, 80)}."));
            index.AddRange(scan.Findings.Select(item => new WeeklyReportEvidence(item.EvidenceRef, "scan-finding", string.Empty,
                string.Empty, null, null, null, null, Safe(item.TitleZh + ": " + item.FactsZh), Safe(item.TitleEn + ": " + item.FactsEn))));
        }
        // The selected request is already <=60 KB. The index drops raw fields and
        // adds bounded labels; a second hard cap protects persisted/exported reports.
        if (index.Count > MaximumGroups + 14
            || JsonSerializer.SerializeToUtf8Bytes(index, WeeklyReportReviewJsonContext.Default.ListWeeklyReportEvidence).Length > 100000)
            throw new InvalidDataException("The weekly report evidence index exceeds its size limit.");
        return index;
    }

    private static HashSet<string> EvidenceReferences(WeeklyReportAiInput request)
    {
        var result = request.Evidence.Select(item => item.EvidenceRef).ToHashSet(StringComparer.Ordinal);
        result.Add(request.StatisticsRef);
        if (request.ScanReference is { } scan)
        {
            result.Add(scan.EvidenceRef);
            result.UnionWith(scan.Findings.Select(item => item.EvidenceRef));
        }
        return result;
    }

    private static WeeklyScanReference? ScanReference(WeeklyScanSnapshot? scan)
    {
        if (scan is null) return null;
        var audit = scan.Audit;
        var findings = audit.Findings.OrderBy(item => item.IsHigh ? 0 : item.IsMedium ? 1 : 2).Take(12)
            .Select((item, index) => new WeeklyScanFinding("scan-finding-" + (index + 1).ToString(CultureInfo.InvariantCulture),
                item.Severity, Clip(item.TitleZh, 120), Clip(item.Title, 120), Clip(item.DescriptionZh, 240), Clip(item.Description, 240),
                Clip(item.RecommendationZh, 240), Clip(item.Recommendation, 240))).ToArray();
        return new("scan-reference", scan.Id, audit.ScanStart?.ToUniversalTime(), audit.ScanEnd?.ToUniversalTime(),
            audit.CollectionProfile ?? string.Empty, audit.AnalysisOutcome ?? "unknown", audit.UsedModel,
            audit.EvidenceIncomplete, scan.RawEventsAvailable, scan.RawEventsTruncated,
            audit.SkippedChannels ?? [], audit.LimitedChannels ?? [], audit.Findings.Count, findings.Length,
            Clip(audit.AiReportSummary, 800), findings);
    }

    private static bool Bilingual(string chinese, string english, int maximum) => !string.IsNullOrWhiteSpace(chinese)
        && !string.IsNullOrWhiteSpace(english) && chinese.Length <= maximum && english.Length <= maximum
        && chinese.Any(character => character is >= '\u3400' and <= '\u9fff')
        && english.Any(char.IsAsciiLetter);

    private static string Clip(string? text, int maximum)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maximum) return text;
        int length = char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum;
        return text[..length];
    }

    private sealed class ReviewProgress(IProgress<WeeklyReportProgress>? observer) : IProgress<AiTaskProgress>
    {
        public void Report(AiTaskProgress value)
        {
            try { observer?.Report(new("ai", value.StatusMessage.Length > 0 ? value.StatusMessage : value.Stage)); }
            catch (Exception) { /* Display observers do not change the report outcome. */ }
        }
    }
}
