using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;

namespace Kit.AIHubLib.Storage;

/// <summary>A formal Markdown document owns the serialized chart evidence and its PNG version.</summary>
public sealed partial class WindowsStabilityReportStorage
{
    private const string Marker = "<!-- kit-windows-stability:v1 -->";
    private const string Fence = "```kit-windows-stability\n";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;
    private readonly Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<WindowsStabilityCollection>> _collect;
    private readonly Func<DateTimeOffset> _utcNow;

    public WindowsStabilityReportStorage(string? directory = null,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<WindowsStabilityCollection>>? collect = null, Func<DateTimeOffset>? utcNow = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "AIHub", "State", "reports");
        _collect = collect ?? new WindowsStabilityCollector().CollectAsync;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<WindowsStabilityReportDocument> LoadAsync(bool monthly, bool forceRefresh, DateTimeOffset? end, CancellationToken cancellationToken)
        => LoadAsync(monthly, forceRefresh, end, "zh-CN", cancellationToken);

    /// <summary>Reads an exact cached window without collection or model calls.</summary>
    public async Task<WindowsStabilityReportDocument?> ReadCacheAsync(WeeklyReportMode mode, DateTimeOffset end, string? identity = null,
        string language = "zh-CN", CancellationToken cancellationToken = default)
    {
        var document = await ReadLatestAsync(mode, end, language, cancellationToken).ConfigureAwait(false);
        return document is not null && document.EndUtc == end.UtcDateTime && document.StartUtc == end.AddDays(-7).UtcDateTime
            && (identity is null || document.Data.WeeklySource?.CacheIdentity == identity) ? document : null;
    }

    /// <summary>Reads the latest published document for this mode/date, for failed-update preservation.</summary>
    public async Task<WindowsStabilityReportDocument?> ReadLatestAsync(WeeklyReportMode mode, DateTimeOffset end,
        string language = "zh-CN", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(_directory)) Prune(_utcNow());
        string path = WeeklyPath(mode, end);
        if (!File.Exists(path)) return null;
        try
        {
            var document = Read(path);
            if (document.Monthly || document.Data.WeeklySource?.Mode != mode || document.EndUtc > end.UtcDateTime) return null;
            return await LocalizeAsync(document, language, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { return null; }
    }

    /// <summary>Publishes both language documents before advancing the selected mode's anchor.</summary>
    public async Task<WindowsStabilityReportDocument> SaveAsync(WeeklyReportMode mode, WindowsStabilityReportData chinese,
        WindowsStabilityReportData english, string language = "zh-CN", CancellationToken cancellationToken = default)
    {
        if (chinese.End - chinese.Start != TimeSpan.FromDays(7) || english.Start != chinese.Start || english.End != chinese.End
            || chinese.WeeklySource?.Mode != mode || english.WeeklySource != chinese.WeeklySource
            || chinese.Language != "zh-CN" || english.Language != "en-US") throw new InvalidDataException("Weekly report evidence and language versions do not match.");
        string path = WeeklyPath(mode, chinese.End);
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await ReadLatestAsync(mode, chinese.End, language, cancellationToken).ConfigureAwait(false);
            if (previous is { Data.CurrentComplete: true } && !chinese.CurrentComplete) return previous with { RefreshFailed = true };
            var snapshot = new WindowsStabilityReportSnapshot(1, Guid.NewGuid().ToString("N"), _utcNow().UtcDateTime, false, chinese) { AlternateData = english };
            Directory.CreateDirectory(_directory);
            await WriteSnapshotAsync(LanguagePath(path, snapshot, "zh-CN"), snapshot, cancellationToken).ConfigureAwait(false);
            await WriteSnapshotAsync(LanguagePath(path, snapshot, "en-US"), snapshot with { Data = english, AlternateData = chinese }, cancellationToken).ConfigureAwait(false);
            await WriteSnapshotAsync(path, snapshot, cancellationToken).ConfigureAwait(false);
            Prune(_utcNow());
            return await LocalizeAsync(Read(path), language, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private string WeeklyPath(WeeklyReportMode mode, DateTimeOffset end) => Path.Combine(_directory,
        "stability-weekly-" + mode.ToString().ToLowerInvariant() + "-" + WeeklyScanSnapshotStore.TargetDate(end).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");

    private static string Prefix(WindowsStabilityReportSnapshot snapshot) => snapshot.Monthly ? "stability-monthly-"
        : "stability-weekly-" + (snapshot.Data.WeeklySource is { } source ? source.Mode.ToString().ToLowerInvariant() + "-" : string.Empty);

    private static string FileDate(WindowsStabilityReportSnapshot snapshot) => snapshot.Data.WeeklySource is not null
        ? WeeklyScanSnapshotStore.TargetDate(snapshot.Data.End).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : snapshot.Data.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<WindowsStabilityReportDocument> LoadAsync(bool monthly, bool forceRefresh = false, DateTimeOffset? end = null, string language = "zh-CN", CancellationToken cancellationToken = default)
    {
        var now = _utcNow();
        var finish = (end ?? now).ToLocalTime();
        if (finish > now.AddMinutes(1) || finish < now.AddDays(-30))
        {
            throw new InvalidDataException("报告时段超出可用的30天范围。");
        }
        var monthStart = new DateTime(finish.Year, finish.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var start = monthly ? new DateTimeOffset(monthStart, TimeZoneInfo.Local.GetUtcOffset(monthStart)) : finish.AddDays(-7);
        var previousMonth = monthStart.AddMonths(-1);
        var previous = monthly ? new DateTimeOffset(previousMonth, TimeZoneInfo.Local.GetUtcOffset(previousMonth)) : start.AddDays(-7);
        string name = "stability-" + (monthly ? "monthly-" : "weekly-") + finish.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string path = Path.Combine(_directory, name + ".md");
        var gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_directory)) Prune(now);
            WindowsStabilityReportDocument? saved = null;
            if (File.Exists(path))
            {
                try { saved = Read(path); }
                catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { }
            }
            if (!forceRefresh && saved is not null && saved.Monthly == monthly && now.UtcDateTime - saved.GeneratedUtc < TimeSpan.FromMinutes(15)
                && saved.EndUtc <= finish.UtcDateTime && finish.UtcDateTime - saved.EndUtc < TimeSpan.FromMinutes(15))
            {
                ManagedCommon.Logger.LogInfo($"Windows stability report cache hit: monthly={monthly}, version={saved.Id}");
                return await LocalizeAsync(saved, language, cancellationToken).ConfigureAwait(false);
            }
            try
            {
                var elapsed = Stopwatch.StartNew();
                ManagedCommon.Logger.LogInfo($"Windows stability report generation started: monthly={monthly}, forceRefresh={forceRefresh}");
                var retainedStart = previous < now.AddDays(-30) ? now.AddDays(-30) : previous;
                var collection = await _collect(retainedStart, finish, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var data = await Task.Run(() => WindowsStabilityAnalyzer.Analyze(collection, start, finish, previous), cancellationToken).ConfigureAwait(false);
                if (saved is { Data.CurrentComplete: true } && !data.CurrentComplete)
                {
                    ManagedCommon.Logger.LogWarning($"Windows stability report retained previous complete document: version={saved.Id}, reason=incomplete-collection");
                    return await LocalizeAsync(saved with { RefreshFailed = true }, language, cancellationToken).ConfigureAwait(false);
                }
                var snapshot = new WindowsStabilityReportSnapshot(1, Guid.NewGuid().ToString("N"), now.UtcDateTime, monthly, data)
                { AlternateData = WindowsStabilityReportLocalization.English(data) };
                Directory.CreateDirectory(_directory);
                // Both language documents are verified before publishing the cache anchor.
                // Failure cannot replace the previous complete anchor with half of a pair.
                await WriteSnapshotAsync(LanguagePath(path, snapshot, "zh-CN"), snapshot, cancellationToken).ConfigureAwait(false);
                var english = snapshot with { Data = snapshot.AlternateData, AlternateData = snapshot.Data };
                await WriteSnapshotAsync(LanguagePath(path, snapshot, "en-US"), english, cancellationToken).ConfigureAwait(false);
                await WriteSnapshotAsync(path, snapshot, cancellationToken).ConfigureAwait(false);
                var document = await LocalizeAsync(Read(path), language, cancellationToken).ConfigureAwait(false);
                Prune(now);
                ManagedCommon.Logger.LogInfo($"Windows stability report document saved and verified: monthly={monthly}, version={document.Id}, currentComplete={data.CurrentComplete}, previousComplete={data.PreviousComplete}, elapsedMs={elapsed.ElapsedMilliseconds}");
                return document;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or System.Diagnostics.Eventing.Reader.EventLogException)
            {
                ManagedCommon.Logger.LogWarning($"Windows stability report update failed: type={exception.GetType().Name}, previousAvailable={saved is not null}");
                if (saved is not null) return await LocalizeAsync(saved with { RefreshFailed = true }, language, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public static WindowsStabilityReportDocument Read(string path)
    {
        var (snapshot, markdown) = ReadSnapshot(path);
        string png = Prefix(snapshot) + FileDate(snapshot) + "-" + snapshot.Id
            + (snapshot.Data.Language == "en-US" ? "-en.png" : "-zh.png");
        return new(snapshot.Id, snapshot.GeneratedUtc, snapshot.Data.Start.UtcDateTime, snapshot.Data.End.UtcDateTime, snapshot.Monthly,
            markdown, Path.GetFullPath(path), Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, png), snapshot.Data);
    }

    /// <summary>Changes presentation language using the persisted evidence version; never collects or calls an AI model.</summary>
    public static async Task<WindowsStabilityReportDocument> LocalizeAsync(WindowsStabilityReportDocument document, string language, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalized = WindowsStabilityReportLocalization.NormalizeLanguage(language);
        var (snapshot, _) = ReadSnapshot(document.SourcePath);
        if (snapshot.Id != document.Id) throw new InvalidDataException("The report evidence version changed.");
        var selected = snapshot.Data.Language == normalized ? snapshot.Data : snapshot.AlternateData?.Language == normalized ? snapshot.AlternateData
            : normalized == "en-US" ? WindowsStabilityReportLocalization.English(snapshot.Data)
            : throw new InvalidDataException("The saved report does not contain its Chinese evidence version.");
        var alternate = snapshot.Data.Language != normalized ? snapshot.Data : snapshot.AlternateData
            ?? WindowsStabilityReportLocalization.English(snapshot.Data);
        var localized = snapshot with { Data = selected, AlternateData = alternate };
        string path = LanguagePath(document.SourcePath, snapshot, normalized);
        WindowsStabilityReportDocument? saved = null;
        if (File.Exists(path))
        {
            try { saved = Read(path); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { }
        }
        if (saved?.Id != snapshot.Id || saved.Language != normalized)
        {
            await WriteSnapshotAsync(path, localized, cancellationToken).ConfigureAwait(false);
            saved = Read(path);
        }
        return saved with { RefreshFailed = document.RefreshFailed };
    }

    private static string LanguagePath(string source, WindowsStabilityReportSnapshot snapshot, string language)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, Prefix(snapshot)
            + FileDate(snapshot) + "-" + snapshot.Id + (language == "en-US" ? "-en.md" : "-zh.md"));

    private static async Task WriteSnapshotAsync(string path, WindowsStabilityReportSnapshot snapshot, CancellationToken cancellationToken)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, Markdown(snapshot), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            _ = Read(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static (WindowsStabilityReportSnapshot Snapshot, string Markdown) ReadSnapshot(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("稳定性报告文件过大。");
        string markdown = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        int marker = markdown.LastIndexOf(Marker, StringComparison.Ordinal);
        int start = marker < 0 ? -1 : markdown.IndexOf(Fence, marker, StringComparison.Ordinal);
        int end = start < 0 ? -1 : markdown.IndexOf("\n```", start + Fence.Length, StringComparison.Ordinal);
        if (start < 0 || end < 0) throw new InvalidDataException("正式报告缺少图表数据版本。");
        var snapshot = JsonSerializer.Deserialize(markdown[(start + Fence.Length)..end], AIHubLibJsonContext.Default.WindowsStabilityReportSnapshot)
            ?? throw new InvalidDataException("报告数据无法读取。");
        if (snapshot.Version != 1 || !Guid.TryParseExact(snapshot.Id, "N", out _) || snapshot.Data is null || snapshot.GeneratedUtc.Kind != DateTimeKind.Utc
            || snapshot.Data.End <= snapshot.Data.Start || snapshot.Data.Metrics is null || snapshot.Data.Daily is null || snapshot.Data.Coverage is null
            || snapshot.Data.CrashClusters is null || snapshot.Data.Actions is null || snapshot.Data.Heatmap is null || snapshot.Data.BlindSpots is null
            || snapshot.Data.Timeline is null || snapshot.Data.Noise is null || snapshot.Data.Dimensions is null)
        {
            throw new InvalidDataException("报告数据版本或时段无效。");
        }
        return (snapshot, markdown);
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = DateOnly.FromDateTime(now.ToLocalTime().AddDays(-30).DateTime);
        foreach (string file in Directory.EnumerateFiles(_directory, "stability-*"))
        {
            var match = GeneratedFile().Match(Path.GetFileName(file));
            if (match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < cutoff)
            {
                try { File.Delete(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    [GeneratedRegex(@"^stability-(?:weekly-(?:(?:fast|full)-)?|monthly-)(\d{4}-\d{2}-\d{2})(?:\.md|-[0-9a-f]{32}(?:-(?:zh|en)\.md|(?:-(?:zh|en))?(?:-(?:light|dark)-v[1-9][0-9]*)?\.png))$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedFile();

    private static string Markdown(WindowsStabilityReportSnapshot snapshot)
    {
        var data = snapshot.Data;
        bool english = data.Language == "en-US";
        string T(string chinese, string translated) => english ? translated : chinese;
        string N(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? T("未知", "Unknown");
        string O(int value, bool complete) => complete ? value.ToString(CultureInfo.InvariantCulture) : value > 0 ? T("至少 ", "At least ") + value.ToString(CultureInfo.InvariantCulture) : T("未知", "Unknown");
        var text = new StringBuilder();
        text.AppendLine(snapshot.Monthly ? T("# Windows 系统稳定性月报", "# Windows system stability monthly report") : T("# Windows 系统稳定性周报", "# Windows system stability weekly report"));
        text.AppendLine(CultureInfo.InvariantCulture, $"\n{T("版本", "Version")} `{snapshot.Id}` · {T("生成于", "Generated")} {snapshot.GeneratedUtc:O}");
        text.AppendLine(CultureInfo.InvariantCulture, $"\n{T("本期", "Current period")}: {data.Start:yyyy-MM-dd HH:mm zzz} → {data.End:yyyy-MM-dd HH:mm zzz}; {T("对比期", "Comparison period")}: {data.PreviousStart:yyyy-MM-dd HH:mm zzz} → {data.PreviousEnd:yyyy-MM-dd HH:mm zzz}.");
        if (data.WeeklySource is { } source)
        {
            text.AppendLine("\n" + T("报告模式：", "Report mode: ") + source.Mode + ". " + (source.Mode == WeeklyReportMode.Fast
                ? T("复用已保存七天扫描与既有结论；本次未采集日志或调用模型。", "Reuses a saved seven-day scan and its existing findings; no collection or model call for this report.")
                : T("独立采集本期七天日志并进行 AI 审核；历史扫描仅作参考。", "Independently collects this seven-day window and requests AI review; audit scans are references only.")));
            text.AppendLine(T("参考扫描：", "Referenced scan: ") + (string.IsNullOrEmpty(source.ScanId) ? T("无", "None") : source.ScanId));
            if (source.ScanEndUtc is { } scanEnd) text.AppendLine(CultureInfo.InvariantCulture, $"{T("参考扫描截止", "Referenced scan end")}: {scanEnd:O}");
            text.AppendLine(T("实际模型 / 路由：", "Actual model / route: ") + source.UsedModel + " / " + source.UsedRoute);
            text.AppendLine(T("证据版本：", "Evidence fingerprint: ") + source.EvidenceFingerprint);
        }
        text.AppendLine(T("\n## 一句话结论\n", "\n## Executive assessment\n")).AppendLine(data.Assessment).AppendLine(data.GradeBasis);
        text.AppendLine("\n" + data.Machine);
        text.AppendLine(T("\n## 核心指标\n\n| 指标 | 本期 | 上期 | 单位与口径 |\n|---|---:|---:|---|", "\n## Core metrics\n\n| Metric | Current | Previous | Unit and qualification |\n|---|---:|---:|---|"));
        foreach (var metric in data.Metrics)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(metric.Label)} | {N(metric.Current)} | {N(metric.Previous)} | {Cell(metric.Unit + "; " + metric.Qualification)} |");
        }
        text.AppendLine(T("\n## 分层判断\n", "\n## Assessment by dimension\n"));
        foreach (var dimension in data.Dimensions)
        {
            text.AppendLine("### " + dimension.Name + " · " + dimension.Status).AppendLine(dimension.Evidence);
        }
        text.AppendLine(T("\n## 每日异常趋势\n\n按真实事件时间、窗口起点对齐24小时分桶；未知不是0。\n\n| 起点 | 周期 | Critical | Error | Warning | Information | 应用崩溃 |\n|---|---|---:|---:|---:|---:|---:|", "\n## Daily event trends\n\nActual event times in 24-hour buckets anchored at the window start; unknown is not zero.\n\n| Start | Period | Critical | Error | Warning | Information | App crashes |\n|---|---|---:|---:|---:|---:|---:|"));
        foreach (var day in data.Daily)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {day.Start:MM-dd HH:mm} | {(day.Current ? T("本期", "Current") : T("上期", "Previous"))} | {N(day.Critical)} | {N(day.Error)} | {N(day.Warning)} | {N(day.Information)} | {N(day.Crashes)} |");
        }
        text.AppendLine(T("\n## 电源、睡眠与变更时间线\n\n| 时间 | 类型 | 观察 | 证据 |\n|---|---|---|---|", "\n## Power, sleep and change timeline\n\n| Time | Type | Observation | Evidence |\n|---|---|---|---|"));
        foreach (var item in data.Timeline)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {item.Time:MM-dd HH:mm:ss} | {Cell(item.Kind)} | {Cell(item.Label)} | {Cell(item.Detail)} |");
        }
        text.AppendLine(T("\n## 应用崩溃聚类\n\n| 进程 | 模块 | 异常码 | 偏移 | 本期 | 上期 |\n|---|---|---|---|---:|---:|", "\n## Application crash clusters\n\n| Process | Module | Exception | Offset | Current | Previous |\n|---|---|---|---|---:|---:|"));
        foreach (var cluster in data.CrashClusters)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(cluster.Process)} | {Cell(cluster.Module)} | {Cell(cluster.ExceptionCode)} | {Cell(cluster.Offset)} | {(cluster.CurrentKnown ? O(cluster.Current, data.CurrentComplete) : N(null))} | {(cluster.PreviousKnown ? O(cluster.Previous, data.PreviousComplete) : N(null))} |");
        }
        text.AppendLine(T("\n## 重复警告与错误\n\n| 来源/ID | 本期 | 上期 | 判断 |\n|---|---:|---:|---|", "\n## Repeated warnings and errors\n\n| Provider/ID | Current | Previous | Assessment |\n|---|---:|---:|---|"));
        foreach (var noise in data.Noise)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(noise.Provider)}/{noise.EventId} | {(noise.CurrentKnown ? O(noise.Current, data.CurrentComplete) : N(null))} | {(noise.PreviousKnown ? O(noise.Previous, data.PreviousComplete) : N(null))} | {Cell(noise.Classification)} |");
        }
        text.AppendLine(T("\n## 重点行动与验收\n", "\n## Priority actions and verification\n"));
        foreach (var action in data.Actions)
        {
            text.AppendLine("### " + action.Priority + " · " + action.Title).AppendLine(T("依据：", "Evidence: ") + action.Reason)
                .AppendLine(T("处理：", "Action: ") + action.Step).AppendLine(T("验收：", "Verification: ") + action.Verification);
        }
        if (data.WeeklyReview is { EvidenceIndex.Count: > 0 } review)
        {
            text.AppendLine(T("\n## 可核对的证据索引\n\n以下为宿主保留的引用与脱敏样本；事件组不是全量日志导出。\n\n| 引用 | 类型 / 渠道 | 来源 / ID | 数量 | 起止时间 | 样本 |\n|---|---|---|---:|---|---|",
                "\n## Verifiable evidence index\n\nHost-retained references and redacted samples; event groups are not a full log export.\n\n| Reference | Kind / Channel | Provider / ID | Count | Time range | Samples |\n|---|---|---|---:|---|---|"));
            foreach (var evidence in review.EvidenceIndex)
            {
                string first = evidence.First?.ToString("O", CultureInfo.InvariantCulture) ?? T("未知", "Unknown");
                string last = evidence.Last?.ToString("O", CultureInfo.InvariantCulture) ?? T("未知", "Unknown");
                text.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(evidence.EvidenceRef)} | {Cell(evidence.Kind + " / " + evidence.Channel)} | {Cell(evidence.Provider)} / {N(evidence.EventId)} | {N(evidence.Count)} | {first} → {last} | {Cell(evidence.FirstSample + " / " + evidence.LastSample)} |");
            }
        }
        text.AppendLine(T("\n## 证据覆盖与盲区\n", "\n## Evidence coverage and blind spots\n"));
        foreach (var coverage in data.Coverage)
        {
            string oldest = coverage.OldestAvailable?.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) ?? T("未知", "Unknown");
            text.AppendLine(CultureInfo.InvariantCulture, $"- {coverage.Channel}: {T("读取记录", "Records read")} {coverage.Records}; {T("最早日志", "Oldest available log")} {oldest}; {coverage.Limitation}");
        }
        foreach (string blindSpot in data.BlindSpots) text.AppendLine("- " + blindSpot);
        text.AppendLine("\n" + Marker + "\n<details>\n<summary>" + T("同版图表与证据数据", "Chart and evidence data for this version") + "</summary>\n\n" + Fence)
            .AppendLine(JsonSerializer.Serialize(snapshot, AIHubLibJsonContext.Default.WindowsStabilityReportSnapshot)).AppendLine("```\n</details>");
        return text.ToString();
    }

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
