using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

public sealed class OptimizationAnalysisService
{
    private readonly IAiTaskEngine _engine;

    public OptimizationAnalysisService() : this(AiHubEngine.Current) { }

    internal OptimizationAnalysisService(IAiTaskEngine engine) => _engine = engine;

    public async Task<OptimizationAnalysisResult> AnalyzeAsync(
        IReadOnlyList<TempFileInfo> candidates,
        string language,
        IProgress<AiTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count == 0)
        {
            return new([], 0, AiErrorCode.None);
        }

        // IDs follow the engine's request-wide envelope order, not each scanner's IDs.
        var groups = candidates.Select((item, index) => (item, index))
            .GroupBy(entry => entry.item.Action == "delete" && !string.IsNullOrEmpty(entry.item.CacheRoot)
                ? $"{(string.IsNullOrEmpty(entry.item.DevelopmentRepository) ? entry.item.CacheRoot : entry.item.DevelopmentRepository)}|{entry.item.Category}|{entry.item.Risk}|{!string.IsNullOrEmpty(entry.item.BuildManifestPath)}"
                : $"file:{entry.index}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Select(entry => entry.item).ToList()).ToList();
        var inputs = groups.Select((group, index) => new OptimizationItemInput
        {
            ItemId = $"item-{index + 1:D6}",
            Extension = group.Count == 1 ? Path.GetExtension(group[0].FilePath).ToLowerInvariant() : "mixed",
            FileCount = group.Count,
            LocalRisk = group[0].Risk.ToString().ToLowerInvariant(),
            MinimumAgeMinutes = group[0].Action == "move" ? 10 : string.IsNullOrEmpty(group[0].DevelopmentRepository) ? 10080 : 60,
            Evidence = group[0].Action == "move" ? "download-extension" : string.IsNullOrEmpty(group[0].DevelopmentRepository) ? "cache-whitelist"
                : group.All(item => !string.IsNullOrEmpty(item.BuildManifestPath)) ? "msbuild-output-list" : "project-artifact",
            SizeInBytes = group.Sum(item => item.SizeInBytes),
            LastModifiedUtc = group.Max(item => item.LastModified).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            CategoryHint = group[0].Category,
            AllowedAction = group[0].Action,
        }).ToList();
        var byId = inputs.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        foreach (var item in candidates)
        {
            item.IsSelected = false;
            item.ReviewState = "pending";
        }

        var schema = new AiTaskSchema<OptimizationItemInput, OptimizationRecommendationContainer>
        {
            InputTypeInfo = AIHubLibJsonContext.Default.OptimizationItemInput,
            OutputTypeInfo = AIHubLibJsonContext.Default.OptimizationRecommendationContainer,
            AllowedActions = new HashSet<string>(StringComparer.Ordinal) { "move", "delete", "skip" },
            ValidateOutput = (output, ids) => Validate(output, ids, byId),
            MergeBatches = batches => new OptimizationRecommendationContainer
            {
                Recommendations = batches.SelectMany(batch => batch.Recommendations).ToList(),
            },
        };
        var result = await _engine.ExecuteTaskAsync(
            "aihub", HubTaskCatalog.OptimizationId, inputs, schema,
            new AiTaskOptions
            {
                Language = language,
                BatchSize = 16,
                TimeoutSeconds = 600,
                RouteTimeoutSeconds = 120,
                AllowPartialResults = true,
                Progress = progress,
            }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        bool partial = result.HasPartialResult && result.CompletedBatches > 0 && result.CompletedBatches < result.TotalBatches &&
            result.ErrorCode is AiErrorCode.Timeout or AiErrorCode.EndpointFailed;
        if (!result.IsSuccess && !partial)
        {
            foreach (var item in candidates) item.ReviewState = "failed";
            return new([], candidates.Count, result.ErrorCode == AiErrorCode.None ? AiErrorCode.ExecutionFailed : result.ErrorCode);
        }

        // Validate again after merging; partial, duplicate or unsolicited decisions fail closed.
        var reviewedIds = partial && result.Payload?.Recommendations is not null
            ? result.Payload.Recommendations.Where(item => item is not null).Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal)
            : byId.Keys.ToHashSet(StringComparer.Ordinal);
        if (reviewedIds.Count == 0 || !Validate(result.Payload, reviewedIds, byId))
        {
            foreach (var item in candidates) item.ReviewState = "failed";
            return new([], candidates.Count, AiErrorCode.InvalidPayload);
        }

        var recommendations = result.Payload!.Recommendations.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        var approved = new List<TempFileInfo>();
        int unreviewed = 0;
        for (int index = 0; index < groups.Count; index++)
        {
            var input = inputs[index];
            if (!recommendations.TryGetValue(input.ItemId, out var recommendation))
            {
                foreach (var item in groups[index]) item.ReviewState = "failed";
                unreviewed += groups[index].Count;
                continue;
            }
            if (recommendation.Action == "skip" || recommendation.Risk == "high" || groups[index][0].Risk == RiskLevel.High)
            {
                foreach (var item in groups[index])
                {
                    item.ReviewState = "skipped";
                    item.ReasonEn = recommendation.ReasonEn;
                    item.ReasonZh = recommendation.ReasonZh;
                }
                continue;
            }

            foreach (var item in groups[index])
            {
                item.ItemId = input.ItemId;
                item.ReasonEn = recommendation.ReasonEn;
                item.ReasonZh = recommendation.ReasonZh;
                // AI may raise risk, but cannot lower the local scanner's assessment.
                if (recommendation.Risk == "medium" && item.Risk < RiskLevel.Medium)
                {
                    item.Risk = RiskLevel.Medium;
                }

                item.IsSelected = true;
                item.ReviewState = "approved";
                approved.Add(item);
            }
        }

        return new(approved, candidates.Count - approved.Count - unreviewed, result.ErrorCode, unreviewed);
    }

    private static bool Validate(
        OptimizationRecommendationContainer? output,
        IReadOnlySet<string> ids,
        IReadOnlyDictionary<string, OptimizationItemInput> inputs)
    {
        if (output?.Recommendations is null || output.Recommendations.Count != ids.Count)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in output.Recommendations)
        {
            if (item is null || string.IsNullOrEmpty(item.ItemId) || !ids.Contains(item.ItemId) ||
                !seen.Add(item.ItemId) || !inputs.TryGetValue(item.ItemId, out var input) ||
                item.Risk is not ("low" or "medium" or "high") ||
                !ValidReason(item.ReasonEn) || !ValidReason(item.ReasonZh))
            {
                return false;
            }

            if (item.Action == "skip")
            {
                if (item.TargetRelative is not null) return false;
                continue;
            }

            if (item.Action != input.AllowedAction || item.Action is not ("move" or "delete")) return false;
            if (item.Action == "delete" && item.TargetRelative is not null) return false;
            if (item.Action == "move" &&
                (input.CategoryHint is not ("Documents" or "Compressed" or "Programs" or "Music" or "Video") ||
                 item.TargetRelative != input.CategoryHint)) return false;
        }

        return true;
    }

    private static bool ValidReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) && reason.Length <= 512 && !reason.Any(char.IsControl);
}

public sealed record OptimizationAnalysisResult(IReadOnlyList<TempFileInfo> Items, int SkippedCount, AiErrorCode ErrorCode, int UnreviewedCount = 0)
{
    public bool IsSuccess => ErrorCode == AiErrorCode.None;

    public string Describe(bool chinese) => !IsSuccess && UnreviewedCount > 0
        ? (chinese ? $"AI 部分完成（{ErrorCode}）：{Items.Count} 项可操作，{UnreviewedCount} 项未通过审核，保持禁用。" : $"AI partially completed ({ErrorCode}): {Items.Count} actionable; {UnreviewedCount} unreviewed items remain disabled.")
        : !IsSuccess
        ? (chinese ? $"AI 分析未完成（{ErrorCode}），本次候选不可执行，请重新扫描。" : $"AI analysis incomplete ({ErrorCode}); no actions enabled for this scan. Scan again.")
        : (chinese ? $"AI 分析完成：{Items.Count} 项可操作，{SkippedCount} 项跳过。" : $"AI analysis complete: {Items.Count} actionable, {SkippedCount} skipped.");
}
