namespace Kit.AiHub.Storage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kit.AiHub.Contract;
using Kit.AiHub.Models;
using Kit.AiHub.Serialization;

/// <summary>
/// SQLite-backed persistent Codex usage store using Windows' built-in winsqlite3.dll.
/// Persists strictly sanitized Codex execution and token metadata (no prompts, outputs, URLs, or secrets)
/// so usage history, per-model breakdowns, and recent calls survive app restarts independently of debug log level.
/// </summary>
public sealed class AiUsageStore
{
    public const string DatabaseFileName = "ai-usage.db";
    public const int HistoryRetentionDays = 30;
    private const string CurrentSchemaVersion = "2";
    private const int MaximumModelLength = 128;
    private const int MaximumIdentifierLength = 64;
    private const int MaximumRecentCalls = 100;
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS usage_meta (
            key TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS codex_usage_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            dedup_key TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL,
            timestamp_utc TEXT NOT NULL,
            timestamp_unix_ms INTEGER NOT NULL,
            session_id TEXT NOT NULL,
            request_id TEXT NOT NULL,
            agent_id TEXT NOT NULL,
            parent_agent_id TEXT NOT NULL,
            attempt_id TEXT NOT NULL,
            plugin_id TEXT NOT NULL,
            task_id TEXT NOT NULL,
            kernel TEXT NOT NULL,
            model TEXT NOT NULL,
            configured_model TEXT NOT NULL DEFAULT '',
            reported_model TEXT NOT NULL DEFAULT '',
            model_source TEXT NOT NULL DEFAULT '',
            codex_thread_id TEXT NOT NULL DEFAULT '',
            route TEXT NOT NULL,
            effort TEXT NOT NULL,
            api_mode TEXT NOT NULL,
            outcome TEXT NOT NULL,
            error_code TEXT NOT NULL,
            reason TEXT NOT NULL,
            http_status INTEGER NULL,
            phase TEXT NOT NULL,
            batch_index INTEGER NOT NULL,
            completed_batches INTEGER NOT NULL,
            total_batches INTEGER NOT NULL,
            input_item_count INTEGER NOT NULL DEFAULT 0,
            prompt_bytes INTEGER NOT NULL DEFAULT 0,
            output_chars INTEGER NOT NULL DEFAULT 0,
            native_retry_count INTEGER NOT NULL,
            native_retry_limit INTEGER NULL,
            exit_code INTEGER NULL,
            process_failure TEXT NOT NULL DEFAULT '',
            cancellation_source TEXT NOT NULL DEFAULT '',
            input_tokens INTEGER NULL,
            cached_input_tokens INTEGER NULL,
            uncached_input_tokens INTEGER NULL,
            output_tokens INTEGER NULL,
            total_tokens INTEGER NULL,
            context_window_tokens INTEGER NOT NULL,
            elapsed_ms INTEGER NOT NULL,
            queue_ms INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_codex_usage_time ON codex_usage_events(timestamp_unix_ms DESC);
        CREATE INDEX IF NOT EXISTS idx_codex_usage_kind_time ON codex_usage_events(kind, timestamp_unix_ms DESC);
        CREATE INDEX IF NOT EXISTS idx_codex_usage_session ON codex_usage_events(session_id, kind);
        CREATE INDEX IF NOT EXISTS idx_codex_usage_model ON codex_usage_events(model, kind);
        """;

    private static readonly (string Name, string Definition)[] SchemaV2Columns =
    [
        ("configured_model", "TEXT NOT NULL DEFAULT ''"),
        ("reported_model", "TEXT NOT NULL DEFAULT ''"),
        ("model_source", "TEXT NOT NULL DEFAULT ''"),
        ("codex_thread_id", "TEXT NOT NULL DEFAULT ''"),
        ("input_item_count", "INTEGER NOT NULL DEFAULT 0"),
        ("prompt_bytes", "INTEGER NOT NULL DEFAULT 0"),
        ("output_chars", "INTEGER NOT NULL DEFAULT 0"),
        ("native_retry_limit", "INTEGER NULL"),
        ("exit_code", "INTEGER NULL"),
        ("process_failure", "TEXT NOT NULL DEFAULT ''"),
        ("cancellation_source", "TEXT NOT NULL DEFAULT ''"),
        ("uncached_input_tokens", "INTEGER NULL"),
        ("total_tokens", "INTEGER NULL"),
    ];

    private static readonly string[] KnownPlugins = ["aihub", "netmap", "udptest", "winget", "awake", "lightswitch", "localserver"];
    private static readonly string[] KnownTasks = ["self-test", "security-audit", "system-optimization", "route-analysis", "windows-stability-report"];
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
    private static readonly string[] KnownModelSources = ["codex", "config", "legacy"];

    private readonly object _gate = new();
    private readonly string _dataDirectory;
    private readonly string _stateDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeZoneInfo _localZone;
    private bool _schemaInitialized;

    public string DatabasePath { get; }

    /// <summary>Raised outside storage locks after writes or history clears. Subscriber errors never propagate.</summary>
    public event EventHandler? Changed;

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "AiHub");

    public AiUsageStore(string? dataDirectory = null)
        : this(dataDirectory, () => DateTimeOffset.UtcNow, TimeZoneInfo.Local)
    {
    }

    internal AiUsageStore(string? dataDirectory, Func<DateTimeOffset> utcNow, TimeZoneInfo localZone)
    {
        _dataDirectory = AiHubStorageFiles.NormalizeDirectory(dataDirectory ?? DefaultDataDirectory);
        _stateDirectory = Path.Combine(_dataDirectory, "State");
        DatabasePath = Path.Combine(_stateDirectory, DatabaseFileName);
        _utcNow = utcNow;
        _localZone = localZone;
    }

    /// <summary>Records one Codex task or route event into SQLite. Non-Codex kernels are ignored; never throws into callers.</summary>
    public void Record(AiDiagnosticRecord record, string sessionId)
    {
        if (record is null || !string.Equals(record.Kernel, AiKernelCatalog.Codex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool wrote = false;
        try
        {
            lock (_gate)
            {
                using WinSqlite3.SqliteConnection connection = OpenWriteConnectionLocked();
                InsertRecord(connection, record, SanitizeIdentifier(sessionId), isLegacyImport: false);
                wrote = true;
            }
        }
        catch (Exception)
        {
            // Observational persistence must never interrupt an AI call.
        }

        if (wrote)
        {
            NotifyChanged();
        }
    }

    /// <summary>Loads an aggregated Codex usage overview for the requested time window.</summary>
    public AiUsageOverview GetOverview(AiUsageTimeRange range = AiUsageTimeRange.Today, string? currentSessionId = null, int maxRecentCalls = 40)
    {
        string safeSessionId = SanitizeIdentifier(currentSessionId);
        int boundedRecent = Math.Clamp(maxRecentCalls, 1, MaximumRecentCalls);
        DateTimeOffset nowUtc = _utcNow();

        try
        {
            lock (_gate)
            {
                // Avoid creating State/ai-usage.db during passive reads unless legacy diagnostics exist to import.
                string legacyDiagnosticsPath = Path.Combine(_dataDirectory, "Logs", "diagnostics.json");
                if (!File.Exists(DatabasePath) && !File.Exists(legacyDiagnosticsPath))
                {
                    return EmptyOverview(range, safeSessionId, nowUtc, persistenceAvailable: true);
                }

                using WinSqlite3.SqliteConnection connection = OpenWriteConnectionLocked();
                return QueryOverview(connection, range, safeSessionId, boundedRecent, nowUtc);
            }
        }
        catch (Exception)
        {
            return EmptyOverview(range, safeSessionId, nowUtc, persistenceAvailable: false);
        }
    }

    /// <summary>Deletes all persisted Codex usage history without re-importing legacy diagnostics.</summary>
    public bool ClearHistory()
    {
        bool cleared = false;
        try
        {
            lock (_gate)
            {
                if (!File.Exists(DatabasePath))
                {
                    return true;
                }

                using WinSqlite3.SqliteConnection connection = OpenWriteConnectionLocked();
                connection.ExecuteNonQuery("BEGIN IMMEDIATE;");
                try
                {
                    connection.ExecuteNonQuery("DELETE FROM codex_usage_events;");
                    SetMeta(connection, "diagnostics_backfill_v1", "1");
                    connection.ExecuteNonQuery("COMMIT;");
                }
                catch
                {
                    try { connection.ExecuteNonQuery("ROLLBACK;"); } catch { }
                    throw;
                }

                try
                {
                    connection.ExecuteNonQuery("PRAGMA wal_checkpoint(TRUNCATE);");
                }
                catch
                {
                    // Checkpoint failure is benign if another reader briefly holds WAL.
                }

                cleared = true;
            }
        }
        catch (Exception)
        {
            return false;
        }

        if (cleared)
        {
            NotifyChanged();
        }

        return true;
    }

    internal static string SanitizeModelName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaximumModelLength
            || trimmed.Contains("://", StringComparison.Ordinal)
            || trimmed.Contains("sk-", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains('@', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        foreach (char ch in trimmed)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                return string.Empty;
            }

            bool allowed = char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or ':' or '/' or '+';
            if (!allowed)
            {
                return string.Empty;
            }
        }

        return trimmed;
    }

    internal static string SanitizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaximumIdentifierLength)
        {
            return string.Empty;
        }

        foreach (char ch in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_'))
            {
                return string.Empty;
            }
        }

        return trimmed;
    }

    private WinSqlite3.SqliteConnection OpenWriteConnectionLocked()
    {
        AiHubStorageFiles.EnsureNoReparsePoints(_dataDirectory);
        AiHubStorageFiles.EnsureNoReparsePoints(_stateDirectory);
        Directory.CreateDirectory(_stateDirectory);
        AiHubStorageFiles.EnsureNoReparsePoints(_stateDirectory);
        EnsureDatabaseFilesSafe();

        try
        {
            WinSqlite3.SqliteConnection connection = WinSqlite3.Open(DatabasePath, readOnly: false);
            try
            {
                ConfigureAndInitialize(connection);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
        catch (WinSqlite3.SqliteNativeException ex) when (WinSqlite3.IsCorruptionError(ex.ErrorCode))
        {
            ResetCorruptDatabaseFiles();
            WinSqlite3.SqliteConnection connection = WinSqlite3.Open(DatabasePath, readOnly: false);
            try
            {
                ConfigureAndInitialize(connection);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    private void ConfigureAndInitialize(WinSqlite3.SqliteConnection connection)
    {
        connection.ExecuteNonQuery("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
        if (!_schemaInitialized || !TableExists(connection, "codex_usage_events"))
        {
            connection.ExecuteNonQuery(SchemaSql);
            EnsureSchemaV2Migration(connection);
            TryBackfillLegacyDiagnostics(connection);
            _schemaInitialized = true;
        }

        PruneExpired(connection);
    }

    private void PruneExpired(WinSqlite3.SqliteConnection connection)
    {
        using var statement = connection.Prepare("DELETE FROM codex_usage_events WHERE timestamp_unix_ms < ?1;");
        statement.BindInt64(1, _utcNow().AddDays(-HistoryRetentionDays).ToUnixTimeMilliseconds());
        statement.ExecuteNonQuery();
    }

    private static void EnsureSchemaV2Migration(WinSqlite3.SqliteConnection connection)
    {
        HashSet<string> existingColumns = GetTableColumns(connection, "codex_usage_events");
        bool addedAnyColumn = false;
        foreach ((string name, string definition) in SchemaV2Columns)
        {
            if (!existingColumns.Contains(name))
            {
                connection.ExecuteNonQuery($"ALTER TABLE codex_usage_events ADD COLUMN {name} {definition};");
                addedAnyColumn = true;
            }
        }

        connection.ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_codex_usage_model_source ON codex_usage_events(model_source, kind);");

        string? currentVersion = GetMeta(connection, "schema_version");
        if (addedAnyColumn || !string.Equals(currentVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            connection.ExecuteNonQuery("""
                UPDATE codex_usage_events
                SET model_source = 'legacy'
                WHERE model = '' AND model_source = '';

                UPDATE codex_usage_events
                SET model_source = 'config',
                    configured_model = CASE WHEN configured_model = '' THEN model ELSE configured_model END
                WHERE model != '' AND model_source = '';

                UPDATE codex_usage_events
                SET uncached_input_tokens = MAX(0, input_tokens - COALESCE(cached_input_tokens, 0))
                WHERE input_tokens IS NOT NULL AND uncached_input_tokens IS NULL;

                UPDATE codex_usage_events
                SET total_tokens = COALESCE(input_tokens, 0) + COALESCE(output_tokens, 0)
                WHERE (input_tokens IS NOT NULL OR output_tokens IS NOT NULL) AND total_tokens IS NULL;
                """);
            SetMeta(connection, "schema_version", CurrentSchemaVersion);
        }
    }

    private static HashSet<string> GetTableColumns(WinSqlite3.SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using WinSqlite3.SqliteStatement stmt = connection.Prepare($"PRAGMA table_info({tableName});");
        while (stmt.Step())
        {
            columns.Add(stmt.GetString(1));
        }

        return columns;
    }

    private void EnsureDatabaseFilesSafe()
    {
        AiHubStorageFiles.EnsureNoReparsePoints(DatabasePath);
        AiHubStorageFiles.EnsureNoReparsePoints(DatabasePath + "-wal");
        AiHubStorageFiles.EnsureNoReparsePoints(DatabasePath + "-shm");
    }

    private void ResetCorruptDatabaseFiles()
    {
        _schemaInitialized = false;
        foreach (string path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    AiHubStorageFiles.EnsureNoReparsePoints(path);
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private void TryBackfillLegacyDiagnostics(WinSqlite3.SqliteConnection connection)
    {
        if (string.Equals(GetMeta(connection, "diagnostics_backfill_v1"), "1", StringComparison.Ordinal))
        {
            return;
        }

        string legacyPath = Path.Combine(_dataDirectory, "Logs", "diagnostics.json");
        try
        {
            byte[]? content = AiHubStorageFiles.ReadOptionalFile(legacyPath, AiDiagnosticsStore.MaximumHistoryBytes);
            if (content is not null)
            {
                List<AiDiagnosticRecord>? records = JsonSerializer.Deserialize(content, AiHubJsonContext.Default.ListAiDiagnosticRecord);
                if (records is { Count: > 0 })
                {
                    connection.ExecuteNonQuery("BEGIN IMMEDIATE;");
                    try
                    {
                        foreach (AiDiagnosticRecord record in records)
                        {
                            if (record is not null && string.Equals(record.Kernel, AiKernelCatalog.Codex, StringComparison.OrdinalIgnoreCase))
                            {
                                InsertRecord(connection, record, "legacy-import", isLegacyImport: true);
                            }
                        }

                        SetMeta(connection, "diagnostics_backfill_v1", "1");
                        connection.ExecuteNonQuery("COMMIT;");
                        return;
                    }
                    catch
                    {
                        try { connection.ExecuteNonQuery("ROLLBACK;"); } catch { }
                        throw;
                    }
                }
            }

            SetMeta(connection, "diagnostics_backfill_v1", "1");
        }
        catch (Exception)
        {
            // Never let malformed legacy JSON prevent SQLite usage tracking.
            try { SetMeta(connection, "diagnostics_backfill_v1", "1"); } catch { }
        }
    }

    private void InsertRecord(WinSqlite3.SqliteConnection connection, AiDiagnosticRecord record, string sessionId, bool isLegacyImport)
    {
        string kind = Known(record.Kind, ["task", "route"], "task");
        DateTimeOffset timestampUtc = record.TimestampUtc == default ? _utcNow() : record.TimestampUtc.ToUniversalTime();
        if (timestampUtc < _utcNow().AddDays(-HistoryRetentionDays))
        {
            return;
        }

        long timestampUnixMs = timestampUtc.ToUnixTimeMilliseconds();
        string pluginId = Known(record.PluginId, KnownPlugins, "other");
        string taskId = Known(record.TaskId, KnownTasks, "other");
        string route = Known(record.Route, ["main", "fallback"], string.Empty);
        string outcome = Known(record.Outcome, ["succeeded", "failed", "cancelled", "timeout", "partial"], "failed");
        string errorCode = Enum.IsDefined(record.ErrorCode) ? record.ErrorCode.ToString() : AiErrorCode.ExecutionFailed.ToString();

        AiRouteDiagnostics? details = record.Diagnostics;
        string configuredModel = SanitizeModelName(record.ConfiguredModel);
        string reportedModel = SanitizeModelName(!string.IsNullOrWhiteSpace(record.ReportedModel) ? record.ReportedModel : details?.ReportedModel);
        string model = SanitizeModelName(record.Model);
        if (string.IsNullOrEmpty(model))
        {
            model = !string.IsNullOrEmpty(reportedModel) ? reportedModel : configuredModel;
        }

        if (string.IsNullOrEmpty(configuredModel) && !string.IsNullOrEmpty(model) && string.IsNullOrEmpty(reportedModel))
        {
            configuredModel = model;
        }

        string modelSource = Known(record.ModelSource, KnownModelSources, string.Empty);
        if (string.IsNullOrEmpty(modelSource))
        {
            modelSource = !string.IsNullOrEmpty(reportedModel)
                ? "codex"
                : !string.IsNullOrEmpty(model)
                    ? "config"
                    : (isLegacyImport ? "legacy" : string.Empty);
        }

        string codexThreadId = SanitizeIdentifier(details?.CodexThreadId);
        string effort = AiTargetSettings.IsValidNativeIdentifier(record.Effort) ? record.Effort.Trim().ToLowerInvariant() : string.Empty;
        string apiMode = AiTargetSettings.IsValidNativeIdentifier(record.ApiMode) ? record.ApiMode.Trim().ToLowerInvariant() : string.Empty;

        string reason = Known(details?.Reason, KnownReasons, "None");
        string phase = Known(details?.Phase, KnownPhases, "complete");
        int? httpStatus = details?.HttpStatusCode is >= 100 and <= 599 ? details.HttpStatusCode : null;
        int totalBatches = Math.Clamp(record.TotalBatches, 0, 2_000);
        int batchIndex = kind == "route" ? Math.Clamp(record.BatchIndex, 0, totalBatches) : 0;
        int completedBatches = Math.Clamp(record.CompletedBatches, 0, totalBatches);
        int inputItemCount = Math.Clamp(record.InputItemCount, 0, 100_000);
        int promptBytes = Math.Max(0, record.PromptBytes);
        int outputChars = Math.Max(0, record.OutputCharacters);
        int nativeRetries = Math.Max(0, details?.NativeRetryCount ?? 0);
        int? nativeRetryLimit = details?.NativeRetryLimit is >= 0 ? details.NativeRetryLimit : null;
        int? exitCode = details?.ExitCode;
        string processFailure = Known(details?.ProcessFailure, KnownProcessFailures, string.Empty);
        string cancellationSource = Known(details?.CancellationSource, KnownCancellationSources, string.Empty);

        long? inputTokens = kind == "route" ? Nonnegative(details?.InputTokens) : null;
        long? cachedInputTokens = kind == "route" ? Nonnegative(details?.CachedInputTokens) : null;
        long? uncachedInputTokens = inputTokens.HasValue ? Math.Max(0, inputTokens.Value - (cachedInputTokens ?? 0)) : null;
        long? outputTokens = kind == "route" ? Nonnegative(details?.OutputTokens) : null;
        long? totalTokens = (inputTokens.HasValue || outputTokens.HasValue)
            ? SaturatingAdd(inputTokens ?? 0, outputTokens ?? 0)
            : null;
        int contextWindow = Math.Max(0, details?.ContextWindowTokens ?? 0);
        long elapsedMs = Math.Max(0, record.ElapsedMilliseconds > 0 ? record.ElapsedMilliseconds : (details?.ElapsedMilliseconds ?? 0));
        long queueMs = Math.Max(0, record.QueueMilliseconds);

        string requestIdStr = record.RequestId == Guid.Empty ? string.Empty : record.RequestId.ToString("D");
        string agentIdStr = record.AgentId == Guid.Empty ? string.Empty : record.AgentId.ToString("D");
        string parentAgentIdStr = record.ParentAgentId == Guid.Empty ? string.Empty : record.ParentAgentId.ToString("D");
        string attemptIdStr = record.AttemptId == Guid.Empty ? string.Empty : record.AttemptId.ToString("D");
        string dedupKey = $"{kind}|{requestIdStr}|{agentIdStr}|{attemptIdStr}|{batchIndex}|{route}|{timestampUnixMs}";

        const string sql = """
            INSERT OR IGNORE INTO codex_usage_events (
                dedup_key, kind, timestamp_utc, timestamp_unix_ms, session_id,
                request_id, agent_id, parent_agent_id, attempt_id,
                plugin_id, task_id, kernel, model, configured_model, reported_model, model_source, codex_thread_id,
                route, effort, api_mode,
                outcome, error_code, reason, http_status, phase,
                batch_index, completed_batches, total_batches, input_item_count, prompt_bytes, output_chars,
                native_retry_count, native_retry_limit, exit_code, process_failure, cancellation_source,
                input_tokens, cached_input_tokens, uncached_input_tokens, output_tokens, total_tokens,
                context_window_tokens, elapsed_ms, queue_ms
            ) VALUES (
                ?1, ?2, ?3, ?4, ?5,
                ?6, ?7, ?8, ?9,
                ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17,
                ?18, ?19, ?20,
                ?21, ?22, ?23, ?24, ?25,
                ?26, ?27, ?28, ?29, ?30, ?31,
                ?32, ?33, ?34, ?35, ?36,
                ?37, ?38, ?39, ?40, ?41,
                ?42, ?43, ?44
            );
            """;

        using WinSqlite3.SqliteStatement stmt = connection.Prepare(sql);
        stmt.BindText(1, dedupKey);
        stmt.BindText(2, kind);
        stmt.BindText(3, timestampUtc.ToString("O"));
        stmt.BindInt64(4, timestampUnixMs);
        stmt.BindText(5, sessionId);
        stmt.BindText(6, requestIdStr);
        stmt.BindText(7, agentIdStr);
        stmt.BindText(8, parentAgentIdStr);
        stmt.BindText(9, attemptIdStr);
        stmt.BindText(10, pluginId);
        stmt.BindText(11, taskId);
        stmt.BindText(12, AiKernelCatalog.Codex);
        stmt.BindText(13, model);
        stmt.BindText(14, configuredModel);
        stmt.BindText(15, reportedModel);
        stmt.BindText(16, modelSource);
        stmt.BindText(17, codexThreadId);
        stmt.BindText(18, route);
        stmt.BindText(19, effort);
        stmt.BindText(20, apiMode);
        stmt.BindText(21, outcome);
        stmt.BindText(22, errorCode);
        stmt.BindText(23, reason);
        stmt.BindNullableInt32(24, httpStatus);
        stmt.BindText(25, phase);
        stmt.BindInt64(26, batchIndex);
        stmt.BindInt64(27, completedBatches);
        stmt.BindInt64(28, totalBatches);
        stmt.BindInt64(29, inputItemCount);
        stmt.BindInt64(30, promptBytes);
        stmt.BindInt64(31, outputChars);
        stmt.BindInt64(32, nativeRetries);
        stmt.BindNullableInt32(33, nativeRetryLimit);
        stmt.BindNullableInt32(34, exitCode);
        stmt.BindText(35, processFailure);
        stmt.BindText(36, cancellationSource);
        stmt.BindNullableInt64(37, inputTokens);
        stmt.BindNullableInt64(38, cachedInputTokens);
        stmt.BindNullableInt64(39, uncachedInputTokens);
        stmt.BindNullableInt64(40, outputTokens);
        stmt.BindNullableInt64(41, totalTokens);
        stmt.BindInt64(42, contextWindow);
        stmt.BindInt64(43, elapsedMs);
        stmt.BindInt64(44, queueMs);
        stmt.ExecuteNonQuery();
    }

    private AiUsageOverview QueryOverview(
        WinSqlite3.SqliteConnection connection,
        AiUsageTimeRange range,
        string currentSessionId,
        int maxRecentCalls,
        DateTimeOffset nowUtc)
    {
        (string whereClause, Action<WinSqlite3.SqliteStatement> bindFilter) = BuildFilter(range, currentSessionId, nowUtc);
        if (range == AiUsageTimeRange.Session && string.IsNullOrEmpty(currentSessionId))
        {
            return EmptyOverview(range, currentSessionId, nowUtc, persistenceAvailable: true);
        }

        // 1. Task summary
        long taskCount = 0, succeededTasks = 0, failedTasks = 0, cancelledTasks = 0, timeoutTasks = 0, partialTasks = 0, taskElapsed = 0;
        using (WinSqlite3.SqliteStatement stmt = connection.Prepare($"""
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN outcome = 'succeeded' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'failed' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'cancelled' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'timeout' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'partial' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(elapsed_ms), 0)
            FROM codex_usage_events
            WHERE kernel = 'codex' AND kind = 'task' {whereClause};
            """))
        {
            bindFilter(stmt);
            if (stmt.Step())
            {
                taskCount = stmt.GetInt64(0);
                succeededTasks = stmt.GetInt64(1);
                failedTasks = stmt.GetInt64(2);
                cancelledTasks = stmt.GetInt64(3);
                timeoutTasks = stmt.GetInt64(4);
                partialTasks = stmt.GetInt64(5);
                taskElapsed = stmt.GetInt64(6);
            }
        }

        // 2. Route call & Codex token summary
        long routeCalls = 0, mainCalls = 0, fallbackCalls = 0, succeededCalls = 0, failedCalls = 0, cancelledCalls = 0, timeoutCalls = 0;
        long nativeRetries = 0, routeElapsed = 0, queueElapsed = 0;
        long knownUsage = 0, reportedInput = 0, reportedCached = 0, reportedOutput = 0;
        long inputTokens = 0, cachedInputTokens = 0, uncachedInputTokens = 0, outputTokens = 0;
        long cacheEligibleInput = 0, cacheEligibleCached = 0;
        using (WinSqlite3.SqliteStatement stmt = connection.Prepare($"""
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN route = 'main' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN route = 'fallback' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'succeeded' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'failed' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'cancelled' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'timeout' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(native_retry_count), 0),
                COALESCE(SUM(elapsed_ms), 0),
                COALESCE(SUM(queue_ms), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL OR cached_input_tokens IS NOT NULL OR output_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN cached_input_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN output_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(COALESCE(input_tokens, 0)), 0),
                COALESCE(SUM(COALESCE(cached_input_tokens, 0)), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL THEN MAX(0, input_tokens - COALESCE(cached_input_tokens, 0)) ELSE 0 END), 0),
                COALESCE(SUM(COALESCE(output_tokens, 0)), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL AND input_tokens > 0 AND cached_input_tokens IS NOT NULL THEN input_tokens ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL AND input_tokens > 0 AND cached_input_tokens IS NOT NULL THEN MIN(input_tokens, cached_input_tokens) ELSE 0 END), 0)
            FROM codex_usage_events
            WHERE kernel = 'codex' AND kind = 'route' {whereClause};
            """))
        {
            bindFilter(stmt);
            if (stmt.Step())
            {
                routeCalls = stmt.GetInt64(0);
                mainCalls = stmt.GetInt64(1);
                fallbackCalls = stmt.GetInt64(2);
                succeededCalls = stmt.GetInt64(3);
                failedCalls = stmt.GetInt64(4);
                cancelledCalls = stmt.GetInt64(5);
                timeoutCalls = stmt.GetInt64(6);
                nativeRetries = stmt.GetInt64(7);
                routeElapsed = stmt.GetInt64(8);
                queueElapsed = stmt.GetInt64(9);
                knownUsage = stmt.GetInt64(10);
                reportedInput = stmt.GetInt64(11);
                reportedCached = stmt.GetInt64(12);
                reportedOutput = stmt.GetInt64(13);
                inputTokens = stmt.GetInt64(14);
                cachedInputTokens = stmt.GetInt64(15);
                uncachedInputTokens = stmt.GetInt64(16);
                outputTokens = stmt.GetInt64(17);
                cacheEligibleInput = stmt.GetInt64(18);
                cacheEligibleCached = stmt.GetInt64(19);
            }
        }

        // 3. Session & time span summary
        long sessionCount = 0;
        DateTimeOffset? firstEventUtc = null;
        DateTimeOffset? lastEventUtc = null;
        using (WinSqlite3.SqliteStatement stmt = connection.Prepare($"""
            SELECT
                COUNT(DISTINCT CASE WHEN session_id != '' THEN session_id ELSE NULL END),
                MIN(timestamp_unix_ms),
                MAX(timestamp_unix_ms)
            FROM codex_usage_events
            WHERE kernel = 'codex' {whereClause};
            """))
        {
            bindFilter(stmt);
            if (stmt.Step())
            {
                sessionCount = stmt.GetInt64(0);
                long? minMs = stmt.GetNullableInt64(1);
                long? maxMs = stmt.GetNullableInt64(2);
                firstEventUtc = minMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(minMs.Value) : null;
                lastEventUtc = maxMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(maxMs.Value) : null;
            }
        }

        long totalTokens = SaturatingAdd(inputTokens, outputTokens);

        // 4a. Per-model breakdown for attributed models (model != '')
        var rawModels = new List<AiModelUsageSummary>();
        using (WinSqlite3.SqliteStatement stmt = connection.Prepare($"""
            SELECT
                model,
                COUNT(*),
                COALESCE(SUM(CASE WHEN outcome = 'succeeded' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'failed' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'cancelled' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'timeout' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN route = 'main' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN route = 'fallback' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN model_source = 'codex' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(native_retry_count), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN cached_input_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN output_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(COALESCE(input_tokens, 0)), 0),
                COALESCE(SUM(COALESCE(cached_input_tokens, 0)), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL THEN MAX(0, input_tokens - COALESCE(cached_input_tokens, 0)) ELSE 0 END), 0),
                COALESCE(SUM(COALESCE(output_tokens, 0)), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL AND input_tokens > 0 AND cached_input_tokens IS NOT NULL THEN input_tokens ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL AND input_tokens > 0 AND cached_input_tokens IS NOT NULL THEN MIN(input_tokens, cached_input_tokens) ELSE 0 END), 0),
                COALESCE(SUM(elapsed_ms), 0),
                COALESCE(SUM(queue_ms), 0),
                COALESCE(SUM(input_item_count), 0),
                COALESCE(MAX(context_window_tokens), 0),
                COALESCE(GROUP_CONCAT(DISTINCT NULLIF(effort, '')), ''),
                COALESCE(GROUP_CONCAT(DISTINCT NULLIF(api_mode, '')), ''),
                MAX(timestamp_unix_ms)
            FROM codex_usage_events
            WHERE kernel = 'codex' AND kind = 'route' AND model != '' {whereClause}
            GROUP BY model
            ORDER BY (COALESCE(SUM(COALESCE(input_tokens, 0)), 0) + COALESCE(SUM(COALESCE(output_tokens, 0)), 0)) DESC, COUNT(*) DESC, MAX(timestamp_unix_ms) DESC;
            """))
        {
            bindFilter(stmt);
            while (stmt.Step())
            {
                rawModels.Add(ReadModelSummaryRow(stmt, isLegacy: false));
            }
        }

        // Normalize TokenSharePercent across attributed models so pre-upgrade unattributed logs never distort real model shares.
        long attributedTokens = rawModels.Aggregate(0L, (acc, m) => SaturatingAdd(acc, m.TotalTokens));
        long attributedCalls = rawModels.Aggregate(0L, (acc, m) => SaturatingAdd(acc, m.Calls));
        var models = new List<AiModelUsageSummary>(rawModels.Count);
        foreach (AiModelUsageSummary item in rawModels)
        {
            double share = attributedTokens > 0
                ? (double)item.TotalTokens * 100.0 / attributedTokens
                : (attributedCalls > 0 ? (double)item.Calls * 100.0 / attributedCalls : 0.0);
            models.Add(item with { TokenSharePercent = share });
        }

        // 4b. Separate legacy unattributed records (model = '')
        AiModelUsageSummary? legacyUnattributed = null;
        using (WinSqlite3.SqliteStatement stmt = connection.Prepare($"""
            SELECT
                '',
                COUNT(*),
                COALESCE(SUM(CASE WHEN outcome = 'succeeded' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'failed' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'cancelled' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN outcome = 'timeout' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN route = 'main' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN route = 'fallback' THEN 1 ELSE 0 END), 0),
                0,
                COALESCE(SUM(native_retry_count), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN cached_input_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN output_tokens IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(COALESCE(input_tokens, 0)), 0),
                COALESCE(SUM(COALESCE(cached_input_tokens, 0)), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL THEN MAX(0, input_tokens - COALESCE(cached_input_tokens, 0)) ELSE 0 END), 0),
                COALESCE(SUM(COALESCE(output_tokens, 0)), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL AND input_tokens > 0 AND cached_input_tokens IS NOT NULL THEN input_tokens ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN input_tokens IS NOT NULL AND input_tokens > 0 AND cached_input_tokens IS NOT NULL THEN MIN(input_tokens, cached_input_tokens) ELSE 0 END), 0),
                COALESCE(SUM(elapsed_ms), 0),
                COALESCE(SUM(queue_ms), 0),
                COALESCE(SUM(input_item_count), 0),
                COALESCE(MAX(context_window_tokens), 0),
                COALESCE(GROUP_CONCAT(DISTINCT NULLIF(effort, '')), ''),
                COALESCE(GROUP_CONCAT(DISTINCT NULLIF(api_mode, '')), ''),
                MAX(timestamp_unix_ms)
            FROM codex_usage_events
            WHERE kernel = 'codex' AND kind = 'route' AND model = '' {whereClause};
            """))
        {
            bindFilter(stmt);
            if (stmt.Step() && stmt.GetInt64(1) > 0)
            {
                AiModelUsageSummary rawLegacy = ReadModelSummaryRow(stmt, isLegacy: true);
                double legacyShare = totalTokens > 0
                    ? (double)rawLegacy.TotalTokens * 100.0 / totalTokens
                    : (routeCalls > 0 ? (double)rawLegacy.Calls * 100.0 / routeCalls : 0.0);
                legacyUnattributed = rawLegacy with { TokenSharePercent = legacyShare };
            }
        }

        // 5. Per-plugin / task breakdown
        var tasks = new List<AiPluginTaskUsageSummary>();
        using (WinSqlite3.SqliteStatement stmt = connection.Prepare($"""
            SELECT
                plugin_id,
                task_id,
                COALESCE(SUM(CASE WHEN kind = 'task' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN kind = 'task' AND outcome = 'succeeded' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN kind = 'task' AND outcome IN ('failed', 'timeout', 'partial') THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN kind = 'route' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN kind = 'route' THEN COALESCE(input_tokens, 0) ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN kind = 'route' THEN COALESCE(cached_input_tokens, 0) ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN kind = 'route' THEN COALESCE(output_tokens, 0) ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN kind = 'route' THEN elapsed_ms ELSE 0 END), 0),
                MAX(timestamp_unix_ms)
            FROM codex_usage_events
            WHERE kernel = 'codex' {whereClause}
            GROUP BY plugin_id, task_id
            ORDER BY (COALESCE(SUM(CASE WHEN kind = 'route' THEN COALESCE(input_tokens, 0) + COALESCE(output_tokens, 0) ELSE 0 END), 0)) DESC, COUNT(*) DESC;
            """))
        {
            bindFilter(stmt);
            while (stmt.Step())
            {
                long pRouteCalls = stmt.GetInt64(5);
                long pInput = stmt.GetInt64(6);
                long pCached = stmt.GetInt64(7);
                long pOutput = stmt.GetInt64(8);
                long pElapsed = stmt.GetInt64(9);
                long pLastMs = stmt.GetInt64(10);

                tasks.Add(new AiPluginTaskUsageSummary
                {
                    PluginId = stmt.GetString(0),
                    TaskId = stmt.GetString(1),
                    TaskCount = stmt.GetInt64(2),
                    SucceededTasks = stmt.GetInt64(3),
                    FailedTasks = stmt.GetInt64(4),
                    RouteCalls = pRouteCalls,
                    InputTokens = pInput,
                    CachedInputTokens = pCached,
                    OutputTokens = pOutput,
                    TotalTokens = SaturatingAdd(pInput, pOutput),
                    AverageElapsedMilliseconds = pRouteCalls > 0 ? (double)pElapsed / pRouteCalls : null,
                    LastUsedUtc = DateTimeOffset.FromUnixTimeMilliseconds(pLastMs),
                });
            }
        }

        // 6. Recent Codex calls (full v2 metadata)
        var recentCalls = new List<AiUsageCallRecord>();
        using (WinSqlite3.SqliteStatement stmt = connection.Prepare($"""
            SELECT
                id, timestamp_unix_ms, session_id, request_id, plugin_id, task_id,
                model, configured_model, reported_model, model_source, codex_thread_id,
                route, effort, api_mode, outcome, error_code, reason, http_status,
                batch_index, total_batches, input_item_count, prompt_bytes, output_chars,
                native_retry_count, native_retry_limit, exit_code, process_failure, cancellation_source,
                input_tokens, cached_input_tokens, uncached_input_tokens, output_tokens, total_tokens,
                context_window_tokens, elapsed_ms, queue_ms
            FROM codex_usage_events
            WHERE kernel = 'codex' AND kind = 'route' {whereClause}
            ORDER BY timestamp_unix_ms DESC, id DESC
            LIMIT {maxRecentCalls};
            """))
        {
            bindFilter(stmt);
            while (stmt.Step())
            {
                _ = Guid.TryParse(stmt.GetString(3), out Guid reqId);
                _ = Enum.TryParse(stmt.GetString(15), ignoreCase: true, out AiErrorCode errCode);
                recentCalls.Add(new AiUsageCallRecord
                {
                    Id = stmt.GetInt64(0),
                    TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(stmt.GetInt64(1)),
                    SessionId = stmt.GetString(2),
                    RequestId = reqId,
                    PluginId = stmt.GetString(4),
                    TaskId = stmt.GetString(5),
                    Model = stmt.GetString(6),
                    ConfiguredModel = stmt.GetString(7),
                    ReportedModel = stmt.GetString(8),
                    ModelSource = stmt.GetString(9),
                    CodexThreadId = stmt.GetString(10),
                    Route = stmt.GetString(11),
                    Effort = stmt.GetString(12),
                    ApiMode = stmt.GetString(13),
                    Outcome = stmt.GetString(14),
                    ErrorCode = errCode,
                    Reason = stmt.GetString(16),
                    HttpStatusCode = stmt.GetNullableInt32(17),
                    BatchIndex = stmt.GetInt32(18),
                    TotalBatches = stmt.GetInt32(19),
                    InputItemCount = stmt.GetInt32(20),
                    PromptBytes = stmt.GetInt32(21),
                    OutputCharacters = stmt.GetInt32(22),
                    NativeRetryCount = stmt.GetInt32(23),
                    NativeRetryLimit = stmt.GetNullableInt32(24),
                    ExitCode = stmt.GetNullableInt32(25),
                    ProcessFailure = stmt.GetString(26),
                    CancellationSource = stmt.GetString(27),
                    InputTokens = stmt.GetNullableInt64(28),
                    CachedInputTokens = stmt.GetNullableInt64(29),
                    UncachedInputTokens = stmt.GetNullableInt64(30),
                    OutputTokens = stmt.GetNullableInt64(31),
                    TotalTokens = stmt.GetNullableInt64(32),
                    ContextWindowTokens = stmt.GetInt32(33),
                    ElapsedMilliseconds = stmt.GetInt64(34),
                    QueueMilliseconds = stmt.GetInt64(35),
                });
            }
        }

        return new AiUsageOverview
        {
            Range = range,
            CapturedUtc = nowUtc,
            SessionId = currentSessionId,
            DatabasePath = DatabasePath,
            PersistenceAvailable = true,
            TaskCount = taskCount,
            SucceededTasks = succeededTasks,
            FailedTasks = failedTasks,
            CancelledTasks = cancelledTasks,
            TimeoutTasks = timeoutTasks,
            PartialTasks = partialTasks,
            TaskElapsedMilliseconds = taskElapsed,
            RouteCalls = routeCalls,
            MainCalls = mainCalls,
            FallbackCalls = fallbackCalls,
            SucceededCalls = succeededCalls,
            FailedCalls = failedCalls,
            CancelledCalls = cancelledCalls,
            TimeoutCalls = timeoutCalls,
            NativeRetries = nativeRetries,
            TotalRouteElapsedMilliseconds = routeElapsed,
            TotalQueueMilliseconds = queueElapsed,
            KnownUsageCalls = knownUsage,
            ReportedInputCalls = reportedInput,
            ReportedCachedInputCalls = reportedCached,
            ReportedOutputCalls = reportedOutput,
            InputTokens = inputTokens,
            CachedInputTokens = cachedInputTokens,
            UncachedInputTokens = uncachedInputTokens,
            OutputTokens = outputTokens,
            TotalTokens = totalTokens,
            CacheHitRate = cacheEligibleInput > 0 ? (double)cacheEligibleCached / cacheEligibleInput : null,
            AverageRouteElapsedMilliseconds = routeCalls > 0 ? (double)routeElapsed / routeCalls : null,
            AverageQueueMilliseconds = routeCalls > 0 ? (double)queueElapsed / routeCalls : null,
            DistinctModelCount = models.Count,
            SessionCount = sessionCount,
            FirstEventUtc = firstEventUtc,
            LastEventUtc = lastEventUtc,
            Models = Array.AsReadOnly(models.ToArray()),
            LegacyUnattributedModel = legacyUnattributed,
            Tasks = Array.AsReadOnly(tasks.ToArray()),
            RecentCalls = Array.AsReadOnly(recentCalls.ToArray()),
        };
    }

    private static AiModelUsageSummary ReadModelSummaryRow(WinSqlite3.SqliteStatement stmt, bool isLegacy)
    {
        long calls = stmt.GetInt64(1);
        long mInput = stmt.GetInt64(13);
        long mCached = stmt.GetInt64(14);
        long mUncached = stmt.GetInt64(15);
        long mOutput = stmt.GetInt64(16);
        long mTotal = SaturatingAdd(mInput, mOutput);
        long mCacheIn = stmt.GetInt64(17);
        long mCacheHit = stmt.GetInt64(18);
        long mElapsed = stmt.GetInt64(19);
        long mQueue = stmt.GetInt64(20);
        long lastMs = stmt.GetInt64(25);

        return new AiModelUsageSummary
        {
            Model = stmt.GetString(0),
            IsLegacyUnattributed = isLegacy,
            Calls = calls,
            SucceededCalls = stmt.GetInt64(2),
            FailedCalls = stmt.GetInt64(3),
            CancelledCalls = stmt.GetInt64(4),
            TimeoutCalls = stmt.GetInt64(5),
            MainCalls = stmt.GetInt64(6),
            FallbackCalls = stmt.GetInt64(7),
            CodexReportedCalls = stmt.GetInt64(8),
            NativeRetries = stmt.GetInt64(9),
            ReportedInputCalls = stmt.GetInt64(10),
            ReportedCachedInputCalls = stmt.GetInt64(11),
            ReportedOutputCalls = stmt.GetInt64(12),
            InputTokens = mInput,
            CachedInputTokens = mCached,
            UncachedInputTokens = mUncached,
            OutputTokens = mOutput,
            TotalTokens = mTotal,
            CacheHitRate = mCacheIn > 0 ? (double)mCacheHit / mCacheIn : null,
            TotalElapsedMilliseconds = mElapsed,
            AverageElapsedMilliseconds = calls > 0 ? (double)mElapsed / calls : null,
            AverageQueueMilliseconds = calls > 0 ? (double)mQueue / calls : null,
            TotalInputItems = stmt.GetInt64(21),
            MaxContextWindowTokens = stmt.GetInt32(22),
            DistinctEfforts = stmt.GetString(23),
            DistinctApiModes = stmt.GetString(24),
            LastUsedUtc = DateTimeOffset.FromUnixTimeMilliseconds(lastMs),
        };
    }

    private (string Clause, Action<WinSqlite3.SqliteStatement> Bind) BuildFilter(
        AiUsageTimeRange range,
        string currentSessionId,
        DateTimeOffset nowUtc)
    {
        switch (range)
        {
            case AiUsageTimeRange.Session:
                return ("AND session_id = ?1", stmt => stmt.BindText(1, currentSessionId));

            case AiUsageTimeRange.Today:
            {
                var (start, end) = LocalDayWindow(nowUtc, _localZone);
                return ("AND timestamp_unix_ms >= ?1 AND timestamp_unix_ms < ?2", stmt =>
                {
                    stmt.BindInt64(1, start.ToUnixTimeMilliseconds());
                    stmt.BindInt64(2, end.ToUnixTimeMilliseconds());
                });
            }

            case AiUsageTimeRange.Last7Days:
            {
                long cutoffMs = nowUtc.AddDays(-7).ToUnixTimeMilliseconds();
                return ("AND timestamp_unix_ms >= ?1", stmt => stmt.BindInt64(1, cutoffMs));
            }

            case AiUsageTimeRange.Last30Days:
            {
                long cutoffMs = nowUtc.AddDays(-30).ToUnixTimeMilliseconds();
                return ("AND timestamp_unix_ms >= ?1", stmt => stmt.BindInt64(1, cutoffMs));
            }

            default:
                return (string.Empty, static _ => { });
        }
    }

    internal static (DateTimeOffset Start, DateTimeOffset End) LocalDayWindow(DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        DateTime date = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(nowUtc, zone).Date, DateTimeKind.Unspecified);
        return (StartOfDay(date), StartOfDay(date.AddDays(1)));

        DateTimeOffset StartOfDay(DateTime local)
        {
            // Some zones change offsets at midnight. Use the first real instant of
            // each local date, and resolve an overlap to its earlier occurrence.
            while (zone.IsInvalidTime(local))
            {
                local = local.AddMinutes(1);
            }

            TimeSpan offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }
    }

    private AiUsageOverview EmptyOverview(AiUsageTimeRange range, string sessionId, DateTimeOffset nowUtc, bool persistenceAvailable) => new()
    {
        Range = range,
        CapturedUtc = nowUtc,
        SessionId = sessionId,
        DatabasePath = DatabasePath,
        PersistenceAvailable = persistenceAvailable,
    };

    private static bool TableExists(WinSqlite3.SqliteConnection connection, string tableName)
    {
        using WinSqlite3.SqliteStatement stmt = connection.Prepare("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?1 LIMIT 1;");
        stmt.BindText(1, tableName);
        return stmt.Step();
    }

    private static string? GetMeta(WinSqlite3.SqliteConnection connection, string key)
    {
        using WinSqlite3.SqliteStatement stmt = connection.Prepare("SELECT value FROM usage_meta WHERE key = ?1 LIMIT 1;");
        stmt.BindText(1, key);
        return stmt.Step() ? stmt.GetString(0) : null;
    }

    private static void SetMeta(WinSqlite3.SqliteConnection connection, string key, string value)
    {
        using WinSqlite3.SqliteStatement stmt = connection.Prepare("INSERT OR REPLACE INTO usage_meta(key, value) VALUES (?1, ?2);");
        stmt.BindText(1, key);
        stmt.BindText(2, value);
        stmt.ExecuteNonQuery();
    }

    private static string Known(string? value, string[] allowed, string fallback) =>
        value is not null && value.Length <= 128
            ? Array.Find(allowed, candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) ?? fallback
            : fallback;

    private static long? Nonnegative(long? value) => value is >= 0 ? value : null;

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

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
                // Observational listeners never affect storage writes.
            }
        }
    }
}
