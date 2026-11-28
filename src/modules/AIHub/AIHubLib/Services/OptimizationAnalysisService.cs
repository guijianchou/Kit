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
    private static readonly HashSet<string> LogDiagnosticExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".dmp", ".mdmp", ".hdmp", ".wer", ".etl", ".evtx", ".diagcab", ".trace", ".err", ".out", ".tlog", ".txt",
    };

    private static readonly HashSet<string> InstallerArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".msi", ".msp", ".mst", ".cab", ".exe", ".msix", ".msixbundle", ".appx", ".appxbundle", ".nupkg", ".whl",
        ".zip", ".7z", ".rar", ".gz", ".tar", ".bz2", ".xz", ".tgz",
    };

    private static readonly HashSet<string> BuildBinaryCacheExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdb", ".dll", ".obj", ".lib", ".ilk", ".exp", ".pch", ".pchast", ".idb", ".winmd", ".mui", ".ocx", ".sys",
        ".node", ".so", ".pyd", ".pyc", ".o", ".a", ".class", ".jar", ".cache", ".bak", ".old", ".dat", ".bin", ".db",
        ".sqlite", ".sqlite3", ".wal", ".shm", ".lock", ".pid", ".idx", ".pack", ".pak", ".v8", ".wasm", ".map",
        ".res", ".tlb", ".sbr", ".bsc", ".suo", ".ipch",
    };

    private static readonly HashSet<string> DocumentCredentialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".rtf", ".odt", ".ods", ".odp", ".pages",
        ".numbers", ".key", ".pem", ".pfx", ".p12", ".cer", ".crt", ".jks", ".kdbx", ".env", ".pst", ".ost",
        ".mbox", ".eml", ".msg", ".one", ".vsdx", ".psd", ".ai", ".sketch", ".fig", ".blend", ".dwg", ".dxf",
    };

    private static readonly HashSet<string> MediaAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".webp", ".avif", ".heic", ".heif", ".ico", ".svg",
        ".tif", ".tiff", ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mp3", ".wav", ".flac", ".aac", ".ogg",
        ".m4a", ".ttf", ".otf", ".woff", ".woff2",
    };

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
            .GroupBy(entry =>
            {
                if (IsHostProtected(entry.item)) return $"protected:{entry.item.CacheRoot}|{entry.item.Category}|{entry.item.Action}";
                if (entry.item.Action != "delete" || string.IsNullOrEmpty(entry.item.CacheRoot))
                {
                    return $"file:{entry.index}";
                }

                string evidence = ResolveEvidence(entry.item);
                string scope = string.IsNullOrEmpty(entry.item.DevelopmentRepository) ? entry.item.CacheRoot : entry.item.DevelopmentRepository;
                if (evidence == "temp-residual")
                {
                    string bucket = ClassifyTempResidualBucket(NormalizeExtension(entry.item.FilePath));
                    return $"{scope}|{entry.item.Category}|{entry.item.Risk}|{evidence}|{bucket}";
                }

                if (evidence == "cache-residual")
                {
                    string ext = NormalizeExtension(entry.item.FilePath);
                    string bucket = DocumentCredentialExtensions.Contains(ext) ? "documents-credentials" : "cache-data";
                    return $"{scope}|{entry.item.Category}|{entry.item.Risk}|{evidence}|{bucket}";
                }

                return $"{scope}|{entry.item.Category}|{entry.item.Risk}|{!string.IsNullOrEmpty(entry.item.BuildManifestPath)}";
            }, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Select(entry => entry.item).ToList()).ToList();
        var inputs = groups.Select((group, index) =>
        {
            string evidence = ResolveEvidence(group[0]);
            return new OptimizationItemInput
            {
                ItemId = $"item-{index + 1:D6}",
                Extension = DescribeGroupExtensions(group, evidence),
                FileCount = group.Count,
                LocalRisk = group[0].Risk.ToString().ToLowerInvariant(),
                MinimumAgeMinutes = group[0].Action == "move" ? 10 : string.IsNullOrEmpty(group[0].DevelopmentRepository) ? 10080 : 60,
                Evidence = evidence,
                SizeInBytes = group.Sum(item => item.SizeInBytes),
                LastModifiedUtc = group.Max(item => item.LastModified).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                CategoryHint = group[0].Category,
                AllowedAction = IsHostProtected(group[0]) ? "skip" : group[0].Action,
            };
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
                BatchSize = 32,
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
                if (recommendation.Action == "move" && input.Evidence == "download-fallback" && !string.IsNullOrEmpty(recommendation.TargetRelative))
                {
                    item.Category = recommendation.TargetRelative;
                    item.TargetRelativePath = recommendation.TargetRelative;
                }
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
            if (item.Action == "move")
            {
                if (input.Evidence == "download-fallback")
                {
                    if (item.TargetRelative is null || !DownloadOrganizerService.AllowedCategories.Contains(item.TargetRelative))
                    {
                        return false;
                    }
                }
                else if (!DownloadOrganizerService.KnownCategories.Contains(input.CategoryHint) ||
                         item.TargetRelative != input.CategoryHint)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string ResolveEvidence(TempFileInfo item)
    {
        if (!string.IsNullOrWhiteSpace(item.EvidenceKind))
        {
            return item.EvidenceKind;
        }

        if (item.Action == "move")
        {
            return string.Equals(item.Category, DownloadOrganizerService.OthersCategory, StringComparison.Ordinal)
                ? "download-fallback"
                : "download-extension";
        }

        return string.IsNullOrEmpty(item.DevelopmentRepository)
            ? "cache-whitelist"
            : !string.IsNullOrEmpty(item.BuildManifestPath) ? "msbuild-output-list" : "project-artifact";
    }

    private static bool IsHostProtected(TempFileInfo item) => item.Action == "delete"
        ? OptimizationProtectionPolicy.IsProtectedFromCleanup(item.FilePath)
        : OptimizationProtectionPolicy.IsSensitiveState(item.FilePath);

    private static string ClassifyTempResidualBucket(string ext)
    {
        if (DocumentCredentialExtensions.Contains(ext)) return "documents-credentials";
        if (LogDiagnosticExtensions.Contains(ext)) return "logs-diagnostics";
        if (InstallerArchiveExtensions.Contains(ext)) return "installers-archives";
        if (BuildBinaryCacheExtensions.Contains(ext)) return "build-binaries-cache";
        if (MediaAssetExtensions.Contains(ext)) return "media-assets";
        return "scratch-other";
    }

    private static string DescribeGroupExtensions(List<TempFileInfo> group, string evidence)
    {
        var extCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in group)
        {
            string ext = NormalizeExtension(item.FilePath);
            extCounts[ext] = extCounts.TryGetValue(ext, out int existing) ? existing + 1 : 1;
        }

        if (extCounts.Count == 1)
        {
            return extCounts.Keys.First();
        }

        if (evidence == "temp-residual")
        {
            string bucket = ClassifyTempResidualBucket(extCounts.Keys.First());
            var top = extCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(3).Select(pair => pair.Key).ToArray();
            string extra = extCounts.Count > top.Length ? $",+{extCounts.Count - top.Length}" : string.Empty;
            return $"bucket:{bucket} ({string.Join(",", top)}{extra})";
        }

        if (evidence == "cache-residual")
        {
            string bucket = DocumentCredentialExtensions.Contains(extCounts.Keys.First()) ? "documents-credentials" : "cache-data";
            var top = extCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(3).Select(pair => pair.Key).ToArray();
            string extra = extCounts.Count > top.Length ? $",+{extCounts.Count - top.Length}" : string.Empty;
            return $"bucket:{bucket} ({string.Join(",", top)}{extra})";
        }

        return "mixed";
    }

    private static string NormalizeExtension(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return string.IsNullOrEmpty(ext) ? "(none)" : ext;
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
