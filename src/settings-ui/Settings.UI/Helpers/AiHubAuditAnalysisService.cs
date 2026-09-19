// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.Settings.UI.Helpers;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AIHubLib.Models;

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
    /// Events sent per request. Keeps the prompt focused while staying far below the
    /// engine's 96 KB batch and 8 MB total input budgets.
    /// </summary>
    public const int MaxEventsPerRequest = 120;

    public static string Language => IsChinese ? "zh-CN" : "en-US";

    private static bool IsChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when AI Hub is switched on, i.e. a kernel may be invoked.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return AiHubEngine.Current.IsEnabled;
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
        if (events is null || events.Count == 0 || !IsAvailable)
        {
            return null;
        }

        List<EventAnalysisInput> items = events
            .Take(MaxEventsPerRequest)
            .Select(ToInput)
            .ToList();

        if (items.Count == 0)
        {
            return null;
        }

        progress?.Report(IsChinese
            ? $"正在请求 AI 深度分析（{items.Count} 条事件）..."
            : $"Requesting AI deep analysis for {items.Count} event(s)...");

        AiTaskResult<AuditIssueContainer> result = await AiHubEngine.Current.ExecuteTaskAsync(
            PluginId,
            TaskId,
            items,
            CreateSchema(),
            new AiTaskOptions
            {
                Language = Language,
                TimeoutSeconds = 600,
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? result.Payload?.Issues : null;
    }

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
            return false;
        }

        foreach (AuditIssue issue in output.Issues)
        {
            if (issue is null
                || string.IsNullOrWhiteSpace(issue.Key)
                || issue.Key.Length > 48
                || string.IsNullOrWhiteSpace(issue.EventRef)
                || !itemIds.Contains(issue.EventRef)
                || !IsKnownSeverity(issue.Severity)
                || issue.Occurrences < 1
                || issue.RelatedEventRefs is null
                || issue.RelatedEventRefs.Count == 0
                || issue.RelatedEventRefs.Any(reference => !itemIds.Contains(reference))
                || !HasBilingualText(issue.Title, issue.TitleZh)
                || !HasBilingualText(issue.Description, issue.DescriptionZh)
                || !HasBilingualText(issue.RootCause, issue.RootCauseZh)
                || !HasBilingualText(issue.Recommendation, issue.RecommendationZh))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsKnownSeverity(string? severity)
    {
        return severity is "High" or "Medium" or "Low";
    }

    private static bool HasBilingualText(string? english, string? chinese)
    {
        return !string.IsNullOrWhiteSpace(english)
            && !string.IsNullOrWhiteSpace(chinese)
            && english.Length <= 8_192
            && chinese.Length <= 8_192;
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
