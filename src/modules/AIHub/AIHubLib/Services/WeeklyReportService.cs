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
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;

namespace Kit.AIHubLib.Services;

/// <summary>Independent weekly workflow: Fast reuses a scan, Full collects seven days and requests shared-engine review.</summary>
public sealed partial class WeeklyReportService : IDisposable, IAsyncDisposable
{
    private const string TemplateIdentity = "weekly-v1";
    private static readonly string[] StabilityChannels = ["System", "Application", "Setup"];
    private readonly WindowsStabilityReportStorage _storage;
    private readonly WeeklyScanSnapshotStore _scans;
    private readonly Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<WindowsStabilityCollection>> _collect;
    private readonly Func<WeeklyReportReviewInput, IProgress<WeeklyReportProgress>?, CancellationToken, Task<WeeklyReportReview>> _review;
    private readonly Func<CancellationToken, Task<string>> _identity;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeLoads;
    private bool _disposed;
    private Task? _disposeTask;

    public WeeklyReportService(WindowsStabilityReportStorage? storage = null, WeeklyScanSnapshotStore? scans = null,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<WindowsStabilityCollection>>? collect = null,
        Func<WeeklyReportReviewInput, IProgress<WeeklyReportProgress>?, CancellationToken, Task<WeeklyReportReview>>? review = null,
        Func<CancellationToken, Task<string>>? identity = null, Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _storage = storage ?? new WindowsStabilityReportStorage(utcNow: _utcNow);
        _scans = scans ?? new WeeklyScanSnapshotStore(utcNow: _utcNow);
        _collect = collect ?? new WindowsStabilityCollector().CollectAsync;
        _review = review ?? WeeklyReportAiService.ReviewAsync;
        _identity = identity ?? WeeklyReportAiService.ReadIdentityAsync;
    }

    public async Task<WindowsStabilityReportDocument> LoadAsync(WeeklyReportMode mode, bool forceRefresh, DateTimeOffset end,
        string language, IProgress<WeeklyReportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linkedCancellation;
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
            _activeLoads++;
        }
        using (linkedCancellation)
        {
            try
            {
                return await Task.Run(() => LoadCoreAsync(mode, forceRefresh, end, language, progress, linkedCancellation.Token), linkedCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_lifetimeGate)
                {
                    if (--_activeLoads == 0 && _disposed) _drained.TrySetResult();
                }
            }
        }
    }

    /// <summary>Opens a published report without collecting evidence or calling AI, including after a window restart.</summary>
    public async Task<WindowsStabilityReportDocument?> ReadSavedAsync(WeeklyReportMode mode, DateTimeOffset end,
        string language, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode) || end > _utcNow().AddMinutes(1) || end < _utcNow().AddDays(-30))
            throw new InvalidDataException("The saved weekly window must end within the retained 30 days.");
        CancellationTokenSource linkedCancellation;
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
            _activeLoads++;
        }
        using (linkedCancellation)
        {
            try
            {
                // Reading, retention pruning and evidence parsing must not block the UI
                // when an existing Full report is reopened without generation.
                return await Task.Run(() => _storage.ReadLatestAsync(mode, end, language, linkedCancellation.Token), linkedCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_lifetimeGate)
                {
                    if (--_activeLoads == 0 && _disposed) _drained.TrySetResult();
                }
            }
        }
    }

    public void Dispose() => _ = BeginDisposal();

    public ValueTask DisposeAsync() => new(BeginDisposal());

    private Task BeginDisposal()
    {
        lock (_lifetimeGate)
        {
            if (_disposeTask is not null) return _disposeTask;
            _disposed = true;
            if (_activeLoads == 0) _drained.TrySetResult();
            _disposeTask = DrainAndDisposeAsync();
            return _disposeTask;
        }
    }

    private async Task DrainAndDisposeAsync()
    {
        _lifetimeCancellation.Cancel();
        await _drained.Task.ConfigureAwait(false);
        _gate.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task<WindowsStabilityReportDocument> LoadCoreAsync(WeeklyReportMode mode, bool forceRefresh, DateTimeOffset end,
        string language, IProgress<WeeklyReportProgress>? progress, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode) || end > _utcNow().AddMinutes(1) || end < _utcNow().AddDays(-30))
            throw new InvalidDataException("周报需要最近30天内的七天窗口。 / The weekly window must end within the retained 30 days.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = await _storage.ReadLatestAsync(mode, end, language, cancellationToken).ConfigureAwait(false);
            try
            {
                progress?.Report(new("scan", "读取已保存的七天扫描 / Reading saved seven-day scan"));
                var scan = await _scans.LoadAsync(end, cancellationToken).ConfigureAwait(false);
                if (mode == WeeklyReportMode.Fast && scan is null)
                    throw new InvalidOperationException("此日期没有可复用的七天扫描，请选择 Full 周报。 / No saved seven-day scan is available for this date. Select Full report.");
                var finish = mode == WeeklyReportMode.Fast ? new DateTimeOffset(WeeklyScanSnapshotStore.Utc(scan!.Audit.ScanEnd!.Value)).ToLocalTime() : end.ToLocalTime();
                string analysisIdentity = mode == WeeklyReportMode.Full ? await _identity(cancellationToken).ConfigureAwait(false) : "reused-scan";
                string scanFingerprint = scan?.Id ?? "none";
                if (mode == WeeklyReportMode.Full && !forceRefresh && saved?.Data.WeeklySource is { } prior
                    && prior.AnalysisIdentity == analysisIdentity && prior.TemplateVersion == TemplateIdentity && prior.ScanId == (scan?.Id ?? string.Empty)
                    && _utcNow().UtcDateTime >= saved.GeneratedUtc && _utcNow().UtcDateTime - saved.GeneratedUtc < TimeSpan.FromMinutes(15)
                    && finish.UtcDateTime >= saved.EndUtc && finish.UtcDateTime - saved.EndUtc < TimeSpan.FromMinutes(15))
                {
                    finish = new DateTimeOffset(saved.EndUtc).ToLocalTime();
                }
                var start = finish.AddDays(-7);
                string cacheIdentity = Hash(string.Join('|', TemplateIdentity, mode.ToString(), start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    finish.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), scanFingerprint, analysisIdentity));
                if (!forceRefresh)
                {
                    var cached = await _storage.ReadCacheAsync(mode, finish, cacheIdentity, language, cancellationToken).ConfigureAwait(false);
                    if (cached is not null)
                    {
                        progress?.Report(new("complete", "已加载保存的周报 / Loaded saved weekly report"));
                        return cached;
                    }
                }

                WindowsStabilityReportData data;
                WindowsStabilityReportData english;
                WeeklyReportReview review;
                string evidenceFingerprint;
                if (mode == WeeklyReportMode.Fast)
                {
                    data = FastData(scan!, start, finish);
                    english = FastEnglish(data, scan!);
                    review = ReuseReview(scan!);
                    evidenceFingerprint = scanFingerprint;
                }
                else
                {
                    progress?.Report(new("collect", "独立采集七天系统日志 / Collecting this seven-day window"));
                    var collection = await _collect(start, finish, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (collection.RequestedStart != start || collection.End != finish)
                        throw new InvalidDataException("The weekly collector returned a different time window.");
                    // Enforce channel and event-time boundaries even for a substituted collector.
                    collection = collection with
                    {
                        Events = collection.Events.Where(item => WeeklyScanSnapshotStore.IsStabilityChannel(item.Channel)
                            && item.Time >= start && item.Time < finish && item.Level is >= 1 and <= 4).ToArray(),
                        Coverage = collection.Coverage.Where(item => WeeklyScanSnapshotStore.IsStabilityChannel(item.Channel)).ToArray(),
                    };
                    progress?.Report(new("analyze", "整理事件统计与已有基线 / Aggregating evidence and saved baseline"));
                    data = await Task.Run(() => WindowsStabilityAnalyzer.Analyze(collection, start, finish, start.AddDays(-7)), cancellationToken).ConfigureAwait(false);
                    data = data with { BlindSpots = data.BlindSpots.Where(item => !item.StartsWith("对比期起点", StringComparison.Ordinal)).ToArray() };
                    var baseline = await _storage.ReadCacheAsync(WeeklyReportMode.Full, start, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (baseline is { Data.CurrentComplete: true } && baseline.Data.WeeklySource?.TemplateVersion == TemplateIdentity)
                        data = WithBaseline(data, baseline.Data);
                    english = WindowsStabilityReportLocalization.English(data);
                    if (data.Noise.Any(item => !item.CurrentKnown || !item.PreviousKnown)
                        || data.CrashClusters.Any(item => !item.CurrentKnown || !item.PreviousKnown))
                    {
                        data = data with { BlindSpots = [.. data.BlindSpots, "历史报告仅保留有限排行或缺少完整崩溃签名；无法匹配的分组对比标为未知，不按零次计算。"] };
                        english = english with { BlindSpots = [.. english.BlindSpots, "Saved rankings are bounded and some crash signatures are incomplete. Unmatched group comparisons remain unknown, not zero."] };
                    }
                    if (!data.PreviousComplete)
                    {
                        data = data with { BlindSpots = [.. data.BlindSpots, "没有保存且匹配时段的完整上期报告；本次未补采上期日志，对比为未知。"] };
                        english = english with { BlindSpots = [.. english.BlindSpots, "No complete saved report matches the preceding window. Previous logs were not collected; comparisons are unknown."] };
                    }
                    evidenceFingerprint = Fingerprint(collection);
                    await VerifyIdentityAsync(analysisIdentity, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new("review", "审核七天证据与扫描参考 / Reviewing weekly evidence and scan references"));
                    review = await _review(new(mode, data, collection.Events, scan), progress, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(review.AssessmentZh) || string.IsNullOrWhiteSpace(review.AssessmentEn)
                        || string.IsNullOrWhiteSpace(review.UsedModel) || review.Findings is null)
                        throw new InvalidDataException("The weekly AI review did not return a complete bilingual report.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (mode == WeeklyReportMode.Full) await VerifyIdentityAsync(analysisIdentity, cancellationToken).ConfigureAwait(false);
                var source = new WeeklyReportSource(mode, cacheIdentity, evidenceFingerprint, scan?.Id ?? string.Empty,
                    scan?.Audit.ScanStart is { } scanStart ? WeeklyScanSnapshotStore.Utc(scanStart) : null,
                    scan?.Audit.ScanEnd is { } scanEnd ? WeeklyScanSnapshotStore.Utc(scanEnd) : null,
                    scan?.RawEventsAvailable ?? false, scan?.RawEventsTruncated ?? false, review.UsedModel, review.UsedRoute, analysisIdentity);
                data = ApplyReview(data, review, source, chinese: true);
                english = ApplyReview(english, review, source, chinese: false);
                progress?.Report(new("save", "保存中英文报告 / Saving both report languages"));
                var document = await _storage.SaveAsync(mode, data, english, language, cancellationToken).ConfigureAwait(false);
                progress?.Report(new("complete", "周报已保存 / Weekly report saved"));
                return document;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && saved is not null)
            {
                ManagedCommon.Logger.LogWarning($"Weekly report retained previous document: mode={mode}, reason={exception.GetType().Name}");
                return saved with { RefreshFailed = true };
            }
        }
        finally { _gate.Release(); }
    }

    private static WindowsStabilityReportData FastData(WeeklyScanSnapshot scan, DateTimeOffset start, DateTimeOffset end)
    {
        var events = scan.RawEventsAvailable ? scan.Events.Where(item => item.TimeCreated is not null).Select((item, index) =>
            new WindowsStabilityEvent(item.LogName, item.ProviderName, item.EventId, index + 1,
                new DateTimeOffset(WeeklyScanSnapshotStore.Utc(item.TimeCreated!.Value)), item.Level ?? 0, item.Message ?? string.Empty,
                new Dictionary<string, string>(), [])).ToArray() : [];
        var coverage = StabilityChannels.Select(channel => new WindowsStabilityCoverage(channel, start, end,
            null, scan.RawEventsAvailable, scan.RawEventsTruncated, false, events.Count(item => item.Channel == channel),
            "快速周报仅复用审计记录；不含完整信息级别或结构化事件字段。 / Fast reuses audit records without complete information-level or structured payload coverage.")).ToArray();
        var collection = new WindowsStabilityCollection(start, end, events, coverage, new Dictionary<string, string>(), []);
        var data = WindowsStabilityAnalyzer.Analyze(collection, start, end, start.AddDays(-7));
        // Structured signatures and level-4 timelines cannot be inferred from audit findings.
        return data with
        {
            CurrentComplete = false, PreviousComplete = false, Timeline = [], CrashClusters = [], Dimensions = [],
            Metrics = data.Metrics.Where(item => item.Key is "total" or "critical" or "error" or "warning" or "crashes").ToArray(),
            BlindSpots = [scan.RawEventsAvailable
                ? "复用审计的 System/Application/Setup 记录，事件级别与结构化字段覆盖有限；数量仅为已观察下限。"
                : "历史审计只保存发现与扫描元数据，未保存原始事件；图表统计未知。",
                "未重新读取日志或调用模型。睡眠、安装变更、完整崩溃签名与上期比较不在此快速报告的覆盖范围。",
                scan.RawEventsTruncated ? "快照达到数量或体积上限；已保留部分原始记录，统计不能代表全量。" : "复用来源与实际截止时间已记录在报告中。"],
        };
    }

    private static WindowsStabilityReportData FastEnglish(WindowsStabilityReportData data, WeeklyScanSnapshot scan) => WindowsStabilityReportLocalization.English(data) with
    {
        BlindSpots = [scan.RawEventsAvailable
            ? "Reuses System/Application/Setup audit records with limited event-level and structured-field coverage; counts are observed lower bounds."
            : "Audit history contains findings and scan metadata only. Raw events were not retained; event charts are unknown.",
            "No new collection or model request. Sleep, installation changes, complete crash signatures and baseline comparisons are outside this Fast report's coverage.",
            scan.RawEventsTruncated ? "The snapshot reached its event or byte cap; retained events are only a subset." : "The reused source and its actual cutoff are recorded in this report."],
    };

    private static WeeklyReportReview ReuseReview(WeeklyScanSnapshot scan)
    {
        bool analyzed = scan.Audit.AnalysisOutcome == "ok";
        return new()
        {
            AssessmentZh = analyzed ? $"复用七天扫描已完成的审计结论，共 {scan.Audit.Findings.Count} 项发现；本次未重新调用模型。"
                : $"复用七天扫描已有的 {scan.Audit.Findings.Count} 项发现；该扫描没有已完成的 AI 审核，不能视作完整 AI 结论。",
            AssessmentEn = analyzed ? $"Reuses the completed seven-day audit with {scan.Audit.Findings.Count} findings; no new model call."
                : $"Reuses {scan.Audit.Findings.Count} existing findings; this scan has no completed AI review and is not a complete AI assessment.",
            UsedModel = scan.Audit.UsedModel,
            UsedRoute = "saved-scan",
            EvidenceIndex = scan.Audit.Findings.Select((item, index) => new WeeklyReportEvidence(
                "scan:" + scan.Id + ":finding:" + (index + 1).ToString(CultureInfo.InvariantCulture), "scan-finding", item.LogName,
                item.Source, int.TryParse(item.EventId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : null,
                item.Occurrences, null, null, First(item.DescriptionZh, item.Description), First(item.Description, item.DescriptionZh))).ToArray(),
            Findings = scan.Audit.Findings.Select((item, index) => new WeeklyReportFinding
            {
                Priority = item.Severity, TitleZh = First(item.TitleZh, item.Title), TitleEn = First(item.Title, item.TitleZh),
                FactsZh = First(item.DescriptionZh, item.Description), FactsEn = First(item.Description, item.DescriptionZh),
                HypothesisZh = First(item.RootCauseZh, item.RootCause), HypothesisEn = First(item.RootCause, item.RootCauseZh),
                StepsZh = item.EffectiveRemediationPlan.Format(true), StepsEn = item.EffectiveRemediationPlan.Format(false),
                VerificationZh = First(item.EffectiveRemediationPlan.VerificationZh, item.EffectiveRemediationPlan.Verification),
                VerificationEn = First(item.EffectiveRemediationPlan.Verification, item.EffectiveRemediationPlan.VerificationZh),
                EvidenceRefs = ["scan:" + scan.Id + ":finding:" + (index + 1).ToString(CultureInfo.InvariantCulture)],
            }).ToArray(),
        };
    }

    private static WindowsStabilityReportData ApplyReview(WindowsStabilityReportData data, WeeklyReportReview review, WeeklyReportSource source, bool chinese)
        => data with
        {
            WeeklySource = source, WeeklyReview = review, Assessment = chinese ? review.AssessmentZh : review.AssessmentEn,
            Actions = review.Findings.Select(item => new WindowsStabilityAction(ActionPriority(item.Priority), chinese ? item.TitleZh : item.TitleEn,
                (chinese ? "事实：" + item.FactsZh + "\n可能原因：" + item.HypothesisZh : "Facts: " + item.FactsEn + "\nHypothesis: " + item.HypothesisEn)
                    + "\n" + (chinese ? "证据：" : "Evidence: ") + string.Join(", ", item.EvidenceRefs),
                chinese ? item.StepsZh : item.StepsEn, chinese ? item.VerificationZh : item.VerificationEn))
                .OrderBy(item => item.Priority, StringComparer.Ordinal).ToArray(),
        };

    private static string ActionPriority(string severity) => severity.ToUpperInvariant() switch
    {
        "HIGH" => "P0",
        "MEDIUM" => "P1",
        _ => "P2",
    };

    private static WindowsStabilityReportData WithBaseline(WindowsStabilityReportData data, WindowsStabilityReportData previous)
    {
        static bool SameCrash(WindowsStabilityCrashCluster left, WindowsStabilityCrashCluster right) => left.CompleteSignature && right.CompleteSignature
            && left.Process == right.Process && left.Module == right.Module && left.ExceptionCode == right.ExceptionCode && left.Offset == right.Offset;
        // Previous report may also carry rows imported from its own baseline. Only its
        // observed current rows describe the window being reused here.
        var previousClusters = previous.CrashClusters.Where(item => item.Current > 0).ToArray();
        var previousNoise = previous.Noise.Where(item => item.Current > 0).ToArray();
        var clusters = data.CrashClusters.Select(item => item with
        {
            Previous = previousClusters.Where(before => SameCrash(item, before)).Sum(before => before.Current),
            PreviousKnown = item.CompleteSignature && (previousClusters.Any(before => SameCrash(item, before)) || previousClusters.Length < 2048),
        }).Concat(previousClusters.Where(before => !data.CrashClusters.Any(item => SameCrash(item, before))).Select(item => item with
        {
            Current = 0, Previous = item.Current, CurrentKnown = item.CompleteSignature && data.CrashClusters.Count < 2048, PreviousKnown = item.CurrentKnown,
        })).ToArray();
        var noise = data.Noise.Select(item => item with
        {
            Previous = previousNoise.Where(before => before.Provider == item.Provider && before.EventId == item.EventId).Sum(before => before.Current),
            PreviousKnown = previousNoise.Any(before => before.Provider == item.Provider && before.EventId == item.EventId) || previousNoise.Length < 10,
        }).Concat(previousNoise.Where(before => !data.Noise.Any(item => item.Provider == before.Provider && item.EventId == before.EventId)).Select(item => item with
        {
            Current = 0, Previous = item.Current, CurrentKnown = data.Noise.Count < 10, PreviousKnown = item.CurrentKnown,
        })).ToArray();
        return data with
        {
            PreviousComplete = true,
            Metrics = data.Metrics.Select(item => item with { Previous = previous.Metrics.FirstOrDefault(before => before.Key == item.Key)?.Current,
                Qualification = item.Qualification.Replace("上期至少为已观察数量；覆盖不足", "上期完整", StringComparison.Ordinal) }).ToArray(),
            Daily = previous.Daily.Where(item => item.Current).Select(item => item with { Current = false }).Concat(data.Daily.Where(item => item.Current)).ToArray(),
            Timeline = previous.Timeline.Where(item => item.Current).Select(item => item with { Current = false }).Concat(data.Timeline.Where(item => item.Current)).ToArray(),
            CrashClusters = clusters, Noise = noise,
        };
    }

    private static string Fingerprint(WindowsStabilityCollection collection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in collection.Events) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(item, AIHubLibJsonContext.Default.WindowsStabilityEvent));
        foreach (var item in collection.Coverage) hash.AppendData(Encoding.UTF8.GetBytes(item.ToString()));
        foreach (var item in collection.Environment.OrderBy(item => item.Key, StringComparer.Ordinal)) hash.AppendData(Encoding.UTF8.GetBytes(item.Key + "=" + item.Value));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string First(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private async Task VerifyIdentityAsync(string expectedIdentity, CancellationToken cancellationToken)
    {
        if (!string.Equals(expectedIdentity, await _identity(cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
            throw new InvalidDataException("周报生成期间模型配置或策略发生变化，请使用当前配置重试。 / The weekly report model configuration or policy changed during generation. Retry with the current settings.");
    }
}
