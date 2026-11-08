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
            lock (_gate)
            {
                UpdateTotals(safe);
                _recent.Enqueue(safe);
                while (_recent.Count > MaximumRecentRecords)
                {
                    _recent.Dequeue();
                }
            }

            int level = ReadLogLevel();
            if (level < 5 && (level < 3 || safe.Outcome is "failed" or "timeout" or "partial"))
            {
                WithHistoryLock(() =>
                {
                    // Recheck after acquiring the mutex so toggling logging off takes effect promptly.
                    int currentLevel = ReadLogLevel();
                    if (currentLevel >= 5 || (currentLevel >= 3 && safe.Outcome is not ("failed" or "timeout" or "partial")))
                    {
                        return;
                    }

                    List<AiDiagnosticRecord> history = ReadHistoryCore();
                    history.Add(safe);
                    if (history.Count > MaximumHistoryRecords)
                    {
                        history.RemoveRange(0, history.Count - MaximumHistoryRecords);
                    }

                    byte[] content = SerializeHistory(history);
                    while (content.Length > MaximumHistoryBytes && history.Count > 1)
                    {
                        history.RemoveAt(0);
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

    /// <summary>Returns newest records first. History is separate from current-session totals.</summary>
    public IReadOnlyList<AiDiagnosticRecord> LoadHistory()
    {
        IReadOnlyList<AiDiagnosticRecord> result = Array.Empty<AiDiagnosticRecord>();
        WithHistoryLock(() => result = ReadHistoryCore().AsEnumerable().Reverse().ToArray());
        return result;
    }

    private static byte[] SerializeHistory(List<AiDiagnosticRecord> records) =>
        JsonSerializer.SerializeToUtf8Bytes(records, AiHubJsonContext.Default.ListAiDiagnosticRecord);

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
            return records?.Where(record => record is not null).TakeLast(MaximumHistoryRecords).Select(Normalize).ToList() ?? [];
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
        TotalBatches = Math.Max(0, record.TotalBatches),
        CompletedBatches = Math.Clamp(record.CompletedBatches, 0, Math.Max(0, record.TotalBatches)),
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
