// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Microsoft.PowerToys.Monitor;

public static class MonitorStatusStore
{
    private const string UnexpectedExitMessage = "Unexpected exit";
    public static readonly TimeSpan StaleRunningScanTimeout = MonitorScanTimeouts.NoProgress;

    public static long BeginRun(string databasePath, string scanId, MonitorScanTrigger trigger, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scanId);

        using SqliteConnection connection = OpenConnection(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_runs(scan_id, trigger, status, started_at)
            VALUES ($scanId, $trigger, $status, $startedAt);
            SELECT last_insert_rowid();
            """;
        AddParameter(command, "$scanId", scanId);
        AddParameter(command, "$trigger", ToStorageValue(trigger));
        AddParameter(command, "$status", ToStorageValue(MonitorScanStatus.Running));
        AddParameter(command, "$startedAt", FormatTimestamp(startedAt));

        object? result = command.ExecuteScalar();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static void CompleteRun(
        string databasePath,
        long runId,
        MonitorScanStatus status,
        DateTimeOffset completedAt,
        int? recordCount,
        int warningCount,
        string? message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        using SqliteConnection connection = OpenConnection(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scan_runs
            SET status = $status,
                completed_at = $completedAt,
                record_count = $recordCount,
                warning_count = $warningCount,
                message = $message
            WHERE id = $runId;
            """;
        AddParameter(command, "$status", ToStorageValue(status));
        AddParameter(command, "$completedAt", FormatTimestamp(completedAt));
        AddParameter(command, "$recordCount", recordCount);
        AddParameter(command, "$warningCount", Math.Max(0, warningCount));
        AddParameter(command, "$message", string.IsNullOrWhiteSpace(message) ? null : message);
        AddParameter(command, "$runId", runId);
        command.ExecuteNonQuery();
    }

    public static int CompleteLatestRunningRun(
        string databasePath,
        string scanId,
        MonitorScanTrigger trigger,
        MonitorScanStatus status,
        DateTimeOffset completedAt,
        int? recordCount,
        int warningCount,
        string? message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scanId);

        using SqliteConnection connection = OpenConnection(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scan_runs
            SET status = $status,
                completed_at = $completedAt,
                record_count = $recordCount,
                warning_count = $warningCount,
                message = $message
            WHERE id = (
                SELECT id
                FROM scan_runs
                WHERE scan_id = $scanId
                  AND trigger = $trigger
                  AND status = $runningStatus
                ORDER BY started_at DESC, id DESC
                LIMIT 1
            );
            """;
        AddParameter(command, "$status", ToStorageValue(status));
        AddParameter(command, "$completedAt", FormatTimestamp(completedAt));
        AddParameter(command, "$recordCount", recordCount);
        AddParameter(command, "$warningCount", Math.Max(0, warningCount));
        AddParameter(command, "$message", string.IsNullOrWhiteSpace(message) ? null : message);
        AddParameter(command, "$scanId", scanId);
        AddParameter(command, "$trigger", ToStorageValue(trigger));
        AddParameter(command, "$runningStatus", ToStorageValue(MonitorScanStatus.Running));

        return command.ExecuteNonQuery();
    }

    public static int MarkStaleRunningScansAsFailed(string databasePath, DateTimeOffset cutoff, DateTimeOffset now, string? activeScanId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        using SqliteConnection connection = OpenConnection(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scan_runs
            SET status = $failedStatus,
                completed_at = $completedAt,
                message = $message
            WHERE status = $runningStatus
              AND started_at < $cutoff
              AND ($activeScanId IS NULL OR scan_id <> $activeScanId);
            """;
        AddParameter(command, "$failedStatus", ToStorageValue(MonitorScanStatus.Failed));
        AddParameter(command, "$completedAt", FormatTimestamp(now));
        AddParameter(command, "$message", UnexpectedExitMessage);
        AddParameter(command, "$runningStatus", ToStorageValue(MonitorScanStatus.Running));
        AddParameter(command, "$cutoff", FormatTimestamp(cutoff));
        AddParameter(command, "$activeScanId", string.IsNullOrWhiteSpace(activeScanId) ? null : activeScanId);

        return command.ExecuteNonQuery();
    }

    public static int RefreshStaleRunningScans(string databasePath, DateTimeOffset now, string? activeScanId = null)
    {
        return MarkStaleRunningScansAsFailed(databasePath, now - StaleRunningScanTimeout, now, activeScanId);
    }

    public static MonitorStatusSummary GetSummary(string databasePath, MonitorStatusRange range, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        using SqliteConnection connection = OpenConnection(databasePath);
        return GetSummary(connection, range, now);
    }

    public static bool TryGetSummary(string databasePath, MonitorStatusRange range, DateTimeOffset now, out MonitorStatusSummary summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        if (!File.Exists(databasePath))
        {
            summary = CreateSummary(Array.Empty<RunRow>(), range, now);
            return false;
        }

        using SqliteConnection connection = OpenConnection(databasePath);
        summary = GetSummary(connection, range, now);
        return true;
    }

    public static bool TryGetLatestRun(string databasePath, out MonitorStatusRun? latestRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        if (!File.Exists(databasePath))
        {
            latestRun = null;
            return false;
        }

        using SqliteConnection connection = OpenConnection(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT scan_id, trigger, status, started_at
            FROM scan_runs
            ORDER BY started_at DESC, id DESC
            LIMIT 1;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            latestRun = null;
            return false;
        }

        latestRun = new MonitorStatusRun(
            reader.GetString(0),
            ParseTrigger(reader.GetString(1)),
            ParseStatus(reader.GetString(2)),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
        return true;
    }

    private static MonitorStatusSummary GetSummary(SqliteConnection connection, MonitorStatusRange range, DateTimeOffset now)
    {
        IReadOnlyList<RunRow> rows = ReadRows(connection, range, now);
        return CreateSummary(rows, range, now);
    }

    private static MonitorStatusSummary CreateSummary(IReadOnlyList<RunRow> rows, MonitorStatusRange range, DateTimeOffset now)
    {
        DateTime today = now.UtcDateTime.Date;
        List<RunRow> countedRows = rows
            .Where(row => row.Status != MonitorScanStatus.Canceled)
            .ToList();
        DateTime startDate = GetStartDate(countedRows, range, today);
        List<RunRow> rangedRows = countedRows
            .Where(row => row.StartedAt.UtcDateTime.Date >= startDate && row.StartedAt.UtcDateTime.Date <= today)
            .ToList();
        Dictionary<DateTime, List<RunRow>> rowsByDay = rangedRows
            .GroupBy(row => row.StartedAt.UtcDateTime.Date)
            .ToDictionary(group => group.Key, group => group.ToList());

        List<MonitorStatusDay> days = new();
        for (DateTime day = startDate; day <= today; day = day.AddDays(1))
        {
            rowsByDay.TryGetValue(day, out List<RunRow>? dayRows);
            dayRows ??= new List<RunRow>();
            days.Add(new MonitorStatusDay(
                day,
                GetWorstStatus(dayRows),
                dayRows.Count,
                CountStatus(dayRows, MonitorScanStatus.Success),
                CountStatus(dayRows, MonitorScanStatus.Warning),
                CountStatus(dayRows, MonitorScanStatus.Failed)));
        }

        RunRow? latestRow = rangedRows.OrderByDescending(row => row.StartedAt).ThenByDescending(row => row.Id).FirstOrDefault();
        return new MonitorStatusSummary(
            latestRow?.Status,
            rangedRows.Count,
            CountStatus(rangedRows, MonitorScanStatus.Success),
            CountStatus(rangedRows, MonitorScanStatus.Warning),
            CountStatus(rangedRows, MonitorScanStatus.Failed),
            days,
            latestRow?.Message);
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Pooling = false,
        };

        SqliteConnection connection = new(builder.ToString());
        connection.Open();
        ApplyConnectionPragmas(connection);
        EnsureDatabase(connection);
        return connection;
    }

    private static void ApplyConnectionPragmas(SqliteConnection connection)
    {
        // The worker writes scan runs while the Settings UI reads summaries from a separate connection.
        // WAL lets a reader and a writer proceed concurrently, and busy_timeout makes the rare lock contention
        // wait briefly instead of throwing SQLITE_BUSY (which would flash the dashboard to "unavailable").
        // WAL is a persistent database attribute; busy_timeout is per-connection, so both are set on every open.
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout = 5000;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureDatabase(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS scan_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scan_id TEXT NOT NULL,
                trigger TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NULL,
                record_count INTEGER NULL,
                warning_count INTEGER NOT NULL DEFAULT 0,
                message TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_scan_runs_started_at ON scan_runs(started_at);
            CREATE INDEX IF NOT EXISTS idx_scan_runs_status_started_at ON scan_runs(status, started_at);
            """;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<RunRow> ReadRows(SqliteConnection connection, MonitorStatusRange range, DateTimeOffset now)
    {
        using SqliteCommand command = connection.CreateCommand();
        DateTime today = now.UtcDateTime.Date;
        DateTimeOffset endDate = new(today.AddDays(1), TimeSpan.Zero);
        DateTimeOffset? startDate = range switch
        {
            MonitorStatusRange.SevenDays => new DateTimeOffset(today.AddDays(-6), TimeSpan.Zero),
            MonitorStatusRange.ThirtyDays => new DateTimeOffset(today.AddDays(-29), TimeSpan.Zero),
            _ => null,
        };

        if (startDate.HasValue)
        {
            command.CommandText = """
                SELECT id, status, started_at, warning_count, message
                FROM scan_runs
                WHERE status <> $canceledStatus
                  AND started_at >= $startDate
                  AND started_at < $endDate
                ORDER BY started_at ASC, id ASC;
                """;
            AddParameter(command, "$startDate", FormatTimestamp(startDate.Value));
        }
        else
        {
            command.CommandText = """
                SELECT id, status, started_at, warning_count, message
                FROM scan_runs
                WHERE status <> $canceledStatus
                  AND started_at < $endDate
                ORDER BY started_at ASC, id ASC;
                """;
        }

        AddParameter(command, "$canceledStatus", ToStorageValue(MonitorScanStatus.Canceled));
        AddParameter(command, "$endDate", FormatTimestamp(endDate));

        List<RunRow> rows = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RunRow(
                reader.GetInt64(0),
                ParseStatus(reader.GetString(1)),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    private static DateTime GetStartDate(IReadOnlyList<RunRow> rows, MonitorStatusRange range, DateTime today)
    {
        return range switch
        {
            MonitorStatusRange.SevenDays => today.AddDays(-6),
            MonitorStatusRange.ThirtyDays => today.AddDays(-29),
            MonitorStatusRange.All when rows.Count > 0 => rows.Min(row => row.StartedAt.UtcDateTime.Date),
            _ => today.AddDays(-6),
        };
    }

    private static MonitorScanStatus? GetWorstStatus(IReadOnlyList<RunRow> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        if (rows.Any(row => row.Status == MonitorScanStatus.Failed))
        {
            return MonitorScanStatus.Failed;
        }

        if (rows.Any(row => row.Status == MonitorScanStatus.Warning))
        {
            return MonitorScanStatus.Warning;
        }

        if (rows.Any(row => row.Status == MonitorScanStatus.Running))
        {
            return MonitorScanStatus.Running;
        }

        return MonitorScanStatus.Success;
    }

    private static int CountStatus(IEnumerable<RunRow> rows, MonitorScanStatus status)
    {
        return rows.Count(row => row.Status == status);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static string ToStorageValue(MonitorScanStatus status)
    {
        return status.ToString().ToLowerInvariant();
    }

    private static string ToStorageValue(MonitorScanTrigger trigger)
    {
        return trigger.ToString().ToLowerInvariant();
    }

    private static MonitorScanStatus ParseStatus(string value)
    {
        return value switch
        {
            "running" => MonitorScanStatus.Running,
            "success" => MonitorScanStatus.Success,
            "warning" => MonitorScanStatus.Warning,
            "failed" => MonitorScanStatus.Failed,
            "canceled" => MonitorScanStatus.Canceled,
            _ => MonitorScanStatus.Failed,
        };
    }

    private static MonitorScanTrigger ParseTrigger(string value)
    {
        return value switch
        {
            "manual" => MonitorScanTrigger.Manual,
            "background" => MonitorScanTrigger.Background,
            _ => MonitorScanTrigger.Background,
        };
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private sealed record RunRow(
        long Id,
        MonitorScanStatus Status,
        DateTimeOffset StartedAt,
        int WarningCount,
        string? Message);
}
