namespace Kit.AiHub.Storage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Kit.AiHub.Contract;
using Kit.AiHub.Models;
using Kit.AiHub.Serialization;

/// <summary>
/// Local, bounded AI execution metadata. Session totals are available with logging off;
/// disk history follows Kit's logging level and never contains prompts, output or secrets.
/// </summary>
public sealed class AiDiagnosticsStore
{
    public const int MaximumHistoryRecords = 256;
    public const int MaximumHistoryBytes = 256 * 1024;
    public const int MaximumRecentRecords = 32;
    public const int HistoryRetentionDays = 30;
    private const string FileName = "diagnostics.json";
    private static readonly string[] KnownPlugins = ["aihub", "netmap", "udptest", "winget", "awake", "lightswitch", "localserver"];
    private static readonly string[] KnownTasks = ["self-test", "security-audit", "system-optimization", "route-analysis"];
    private static readonly string[] KnownReasons =
    [
        "None", "NativeRetryRecovered", "InvalidConfiguration", "UnsupportedNativeConfiguration",
        "Authentication", "AccessDenied", "AccountUnavailable", "ModelNotFound", "QuotaExceeded",
        "RateLimited", "ServerError", "RequestRejected", "HttpTimeout", "TransportTimeout",
        "StreamDisconnected", "DnsFailure", "ConnectionReset", "ConnectionRefused", "NetworkUnavailable",
        "TransportFailure", "KernelNotInstalled", "InvalidPayload", "PolicyViolation", "Timeout",
        "Cancelled", "ExecutionFailed", "CleanupFailed", "HubDisabled", "HostUnavailable", "EndpointFailed", "Unknown",
        "InvalidJson", "InvalidContract", "DeserializationFailed", "SchemaValidationFailed",
    ];
    private static readonly string[] KnownPhases = ["prepare", "prep", "catalog", "execute", "cleanup", "complete", "response-limit", "json", "contract", "deserialize", "schema"];
    private static readonly string[] KnownProcessFailures = ["None", "Cancelled", "Timeout", "OutputLimit", "StartFailed", "AccessDenied", "CleanupFailed"];
    private static readonly string[] KnownCancellationSources = ["none", "caller", "siblingFailure", "serviceDisabled", "task", "route", "shutdown", "taskDeadline", "routeDeadline", "processDeadline", "request", "upstream"];
    private readonly object _gate = new();
    private readonly Queue<AiDiagnosticRecord> _recent = new();
    private readonly Queue<AiDiagnosticRecord> _persistenceContext = new();
    private readonly string _logSettingsPath;
    private readonly string _mutexName;
    private AiDiagnosticsSnapshot _totals = new() { SessionStartedUtc = DateTimeOffset.UtcNow };

    public string LogDirectory { get; }

    /// <summary>Raised outside storage locks. Subscriber failures never affect AI execution.</summary>
    public event EventHandler? Changed;

    public AiDiagnosticsStore(string? dataDirectory = null, string? logSettingsPath = null)
    {
        string root = AiHubStorageFiles.NormalizeDirectory(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "AiHub"));
        LogDirectory = Path.Combine(root, "Logs");
        _logSettingsPath = logSettingsPath ?? Path.Combine(Path.GetDirectoryName(root)!, "log_settings.json");
        _mutexName = @"Global\Kit.AiHub.Diagnostics." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(LogDirectory.ToUpperInvariant())));
    }

    /// <summary>Records one terminal event. This observational path never throws into a task.</summary>
    public void Record(AiDiagnosticRecord record)
    {
        try
        {
            if (record is null)
            {
                return;
            }

            AiDiagnosticRecord safe = Normalize(record);
            int level = ReadLogLevel();
            lock (_gate)
            {
                UpdateTotals(safe);
                _recent.Enqueue(safe);
                while (_recent.Count > MaximumRecentRecords)
                {
                    _recent.Dequeue();
                }

                if (level < 5)
                {
                    _persistenceContext.Enqueue(safe);
                    while (_persistenceContext.Count > MaximumRecentRecords)
                    {
                        _persistenceContext.Dequeue();
                    }
                }
                else
                {
                    _persistenceContext.Clear();
                }
            }

            if (level < 5)
            {
                WithHistoryLock(() =>
                {
                    // Recheck after acquiring the mutex so toggling logging off takes effect promptly.
                    int currentLevel = ReadLogLevel();
                    if (currentLevel >= 5)
                    {
                        lock (_gate)
                        {
                            _persistenceContext.Clear();
                        }

                        return;
                    }

                    List<AiDiagnosticRecord> history = ReadHistoryCore();
                    if (currentLevel >= 3)
                    {
                        AiDiagnosticRecord[] related;
                        lock (_gate)
                        {
                            related = safe.RequestId == Guid.Empty ? [] : _persistenceContext
                                .Where(record => record.RequestId == safe.RequestId).ToArray();
                        }

                        // A recovered request is still useful warning/error evidence: keep
                        // Main failure, Fallback recovery and the terminal task together.
                        // Never correlate unrelated records that have no request identity.
                        bool failedRequest = IsFailure(safe) || related.Any(IsFailure) ||
                            (safe.RequestId != Guid.Empty && history.Any(record => record.RequestId == safe.RequestId && IsFailure(record)));
                        if (!failedRequest)
                        {
                            return;
                        }

                        foreach (AiDiagnosticRecord previous in related)
                        {
                            if (!history.Contains(previous))
                            {
                                history.Add(previous);
                            }
                        }
                    }

                    if (!history.Contains(safe))
                    {
                        history.Add(safe);
                    }

                    DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-HistoryRetentionDays);
                    history.RemoveAll(record => record.TimestampUtc < cutoff);
                    // Backfilled context and concurrently completed batches may arrive
                    // out of timestamp order. OrderBy preserves insertion order for ties.
                    history = history.OrderBy(record => record.TimestampUtc).ToList();
                    while (history.Count > MaximumHistoryRecords)
                    {
                        RemoveOldestRequest(history, safe.RequestId);
                    }

                    byte[] content = SerializeHistory(history);
                    while (content.Length > MaximumHistoryBytes && history.Count > 1)
                    {
                        RemoveOldestRequest(history, safe.RequestId);
                        content = SerializeHistory(history);
                    }

                    WriteHistory(content);
                });
            }
        }
        catch (Exception)
        {
            IncrementPersistenceFailures();
        }
        finally
        {
            NotifyChanged();
        }
    }

    public AiDiagnosticsSnapshot GetSnapshot()
    {
        bool enabled = ReadLogLevel() < 5;
        lock (_gate)
        {
            return _totals with
            {
                CapturedUtc = DateTimeOffset.UtcNow,
                PersistenceEnabled = enabled,
                RecentRecords = Array.AsReadOnly(_recent.Reverse().ToArray()),
            };
        }
    }

    /// <summary>
    /// Returns newest records first within the retention period. History is separate from
    /// current-session totals; expired disk records are reclaimed on the next enabled write.
    /// </summary>
    public IReadOnlyList<AiDiagnosticRecord> LoadHistory()
    {
        IReadOnlyList<AiDiagnosticRecord> result = Array.Empty<AiDiagnosticRecord>();
        WithHistoryLock(() => result = ReadHistoryCore().AsEnumerable().Reverse().ToArray());
        return result;
    }

    private static byte[] SerializeHistory(List<AiDiagnosticRecord> records) =>
        JsonSerializer.SerializeToUtf8Bytes(records, AiHubJsonContext.Default.ListAiDiagnosticRecord);

    private static bool IsFailure(AiDiagnosticRecord record) => record.Outcome is "failed" or "timeout" or "partial";

    private static void RemoveOldestRequest(List<AiDiagnosticRecord> history, Guid currentRequest)
    {
        // Keep a long-running request whose latest activity is recent. Selecting by
        // its first record would discard its new failure ahead of an older idle group.
        var lastActivity = new Dictionary<Guid, int>();
        for (int index = 0; index < history.Count; index++)
        {
            lastActivity[history[index].RequestId] = index;
        }

        AiDiagnosticRecord? oldestOther = null;
        for (int index = 0; index < history.Count; index++)
        {
            AiDiagnosticRecord candidate = history[index];
            if (candidate.RequestId != currentRequest &&
                (candidate.RequestId == Guid.Empty || lastActivity[candidate.RequestId] == index))
            {
                oldestOther = candidate;
                break;
            }
        }

        if (oldestOther is null)
        {
            // One large request must still respect the hard record/byte limit.
            history.RemoveAt(0);
        }
        else if (oldestOther.RequestId == Guid.Empty)
        {
            history.Remove(oldestOther);
        }
        else
        {
            history.RemoveAll(record => record.RequestId == oldestOther.RequestId);
        }
    }

    private void WithHistoryLock(Action action)
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, _mutexName);
            bool acquired = false;
            try
            {
                try
                {
                    // Diagnostics must never hold up model execution waiting for another process.
                    acquired = mutex.WaitOne(50);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    IncrementPersistenceFailures();
                    return;
                }

                AiHubStorageFiles.EnsureNoReparsePoints(LogDirectory);
                action();
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
        catch (Exception)
        {
            IncrementPersistenceFailures();
        }
    }

    private List<AiDiagnosticRecord> ReadHistoryCore()
    {
        try
        {
            byte[]? content = AiHubStorageFiles.ReadOptionalFile(Path.Combine(LogDirectory, FileName), MaximumHistoryBytes);
            if (content is null)
            {
                return [];
            }

            List<AiDiagnosticRecord>? records = JsonSerializer.Deserialize(content, AiHubJsonContext.Default.ListAiDiagnosticRecord);
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-HistoryRetentionDays);
            return records?.Where(record => record is not null && record.TimestampUtc >= cutoff)
                .OrderBy(record => record.TimestampUtc).TakeLast(MaximumHistoryRecords).Select(Normalize).ToList() ?? [];
        }
        catch (Exception)
        {
            IncrementPersistenceFailures();
            return [];
        }
    }

    private void WriteHistory(byte[] content)
    {
        if (content.Length > MaximumHistoryBytes)
        {
            IncrementPersistenceFailures();
            return;
        }

        Directory.CreateDirectory(LogDirectory);
        AiHubStorageFiles.EnsureNoReparsePoints(LogDirectory);
        string destination = Path.Combine(LogDirectory, FileName);
        string temporary = Path.Combine(LogDirectory, $".diagnostics-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                created = true;
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            AiHubStorageFiles.EnsureNoReparsePoints(destination);
            File.Move(temporary, destination, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                File.Delete(temporary);
            }
        }
    }

    private int ReadLogLevel()
    {
        try
        {
            byte[]? content = AiHubStorageFiles.ReadOptionalFile(_logSettingsPath, 16 * 1024);
            if (content is null)
            {
                return 0; // Match ManagedCommon.Logger's default when no preference exists.
            }

            using JsonDocument document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("logLevel", out JsonElement level) || level.ValueKind != JsonValueKind.String)
            {
                return 5;
            }

            return level.GetString()?.ToLowerInvariant() switch
            {
                "trace" => 0,
                "debug" => 1,
                "info" => 2,
                "warn" or "warning" => 3,
                "err" or "error" or "critical" => 4,
                _ => 5,
            };
        }
        catch (Exception)
        {
            return 5;
        }
    }

    private void UpdateTotals(AiDiagnosticRecord record)
    {
        if (record.Kind == "task")
        {
            _totals = _totals with
            {
                RequestCount = Add(_totals.RequestCount, 1),
                Succeeded = Add(_totals.Succeeded, record.Outcome == "succeeded" ? 1 : 0),
                Failed = Add(_totals.Failed, record.Outcome == "failed" ? 1 : 0),
                Cancelled = Add(_totals.Cancelled, record.Outcome == "cancelled" ? 1 : 0),
                Timeout = Add(_totals.Timeout, record.Outcome == "timeout" ? 1 : 0),
                Partial = Add(_totals.Partial, record.Outcome == "partial" ? 1 : 0),
                ElapsedMilliseconds = Add(_totals.ElapsedMilliseconds, record.ElapsedMilliseconds),
            };
        }
        else if (record.Kind == "route")
        {
            AiRouteDiagnostics? details = record.Diagnostics;
            bool knownUsage = details is not null && (details.InputTokens.HasValue || details.OutputTokens.HasValue ||
                details.CachedInputTokens.HasValue || details.CacheWriteTokens.HasValue);
            _totals = _totals with
            {
                RouteAttempts = Add(_totals.RouteAttempts, 1),
                MainAttempts = Add(_totals.MainAttempts, record.Route == "main" ? 1 : 0),
                FallbackAttempts = Add(_totals.FallbackAttempts, record.Route == "fallback" ? 1 : 0),
                NativeRetryCount = Add(_totals.NativeRetryCount, details?.NativeRetryCount ?? 0),
                KnownUsageRoutes = Add(_totals.KnownUsageRoutes, knownUsage ? 1 : 0),
                InputTokens = Add(_totals.InputTokens, details?.InputTokens ?? 0),
                CachedInputTokens = Add(_totals.CachedInputTokens, details?.CachedInputTokens ?? 0),
                CacheWriteTokens = Add(_totals.CacheWriteTokens, details?.CacheWriteTokens ?? 0),
                OutputTokens = Add(_totals.OutputTokens, details?.OutputTokens ?? 0),
            };
        }
    }

    private void IncrementPersistenceFailures()
    {
        lock (_gate)
        {
            _totals = _totals with { PersistenceFailures = Add(_totals.PersistenceFailures, 1) };
        }
    }

    private void NotifyChanged()
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A closed UI or third-party subscriber must not affect an AI result.
            }
        }
    }

    private static long Add(long total, long amount) => amount > long.MaxValue - total ? long.MaxValue : total + amount;

    private static string Known(string? value, string[] allowed, string fallback = "unknown") =>
        value is not null && value.Length <= 128
            ? Array.Find(allowed, candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) ?? fallback
            : fallback;

    private static long? Nonnegative(long? value) => value is >= 0 ? value : null;

    private static AiDiagnosticRecord Normalize(AiDiagnosticRecord record) => record with
    {
        Kind = Known(record.Kind, ["task", "route"]),
        TimestampUtc = record.TimestampUtc.ToUniversalTime(),
        PluginId = Known(record.PluginId, KnownPlugins, "other"),
        TaskId = Known(record.TaskId, KnownTasks, "other"),
        Kernel = Known(record.Kernel, ["codex", "pi"]),
        Route = Known(record.Route, ["main", "fallback"], string.Empty),
        Outcome = Known(record.Outcome, ["succeeded", "failed", "cancelled", "timeout", "partial"]),
        ErrorCode = Enum.IsDefined(record.ErrorCode) ? record.ErrorCode : AiErrorCode.ExecutionFailed,
        TotalBatches = Math.Clamp(record.TotalBatches, 0, 2_000),
        BatchIndex = string.Equals(record.Kind, "route", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(record.BatchIndex, 0, Math.Clamp(record.TotalBatches, 0, 2_000)) : 0,
        CompletedBatches = Math.Clamp(record.CompletedBatches, 0, Math.Clamp(record.TotalBatches, 0, 2_000)),
        ElapsedMilliseconds = Math.Max(0, record.ElapsedMilliseconds),
        Diagnostics = record.Diagnostics is { } details ? NormalizeDiagnostics(details) : null,
    };

    /// <summary>Restricts native metadata to known technical classifications before display or persistence.</summary>
    public static AiRouteDiagnostics NormalizeDiagnostics(AiRouteDiagnostics details) => details with
    {
        Phase = Known(details.Phase, KnownPhases),
        HttpStatusCode = details.HttpStatusCode is >= 100 and <= 599 ? details.HttpStatusCode : null,
        Reason = Known(details.Reason, KnownReasons, "Unknown"),
        NativeRetryCount = Math.Max(0, details.NativeRetryCount),
        NativeRetryLimit = details.NativeRetryLimit is >= 0 ? details.NativeRetryLimit : null,
        InputTokens = Nonnegative(details.InputTokens),
        CachedInputTokens = Nonnegative(details.CachedInputTokens),
        CacheWriteTokens = Nonnegative(details.CacheWriteTokens),
        OutputTokens = Nonnegative(details.OutputTokens),
        ContextWindowTokens = Math.Max(0, details.ContextWindowTokens),
        ElapsedMilliseconds = Math.Max(0, details.ElapsedMilliseconds),
        ProcessFailure = Known(details.ProcessFailure, KnownProcessFailures),
        CancellationSource = Known(details.CancellationSource, KnownCancellationSources),
    };
}
