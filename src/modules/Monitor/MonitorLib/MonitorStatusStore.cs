// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Microsoft.PowerToys.Monitor;

public static class MonitorStatusStore
{
    private const string UnexpectedExitMessage = "Unexpected exit";
    public static readonly TimeSpan StaleRunningScanTimeout = TimeSpan.FromMinutes(30);

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

    public static int MarkStaleRunningScansAsFailed(string databasePath, DateTimeOffset cutoff, DateTimeOffset now)
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
              AND started_at < $cutoff;
            """;
        AddParameter(command, "$failedStatus", ToStorageValue(MonitorScanStatus.Failed));
        AddParameter(command, "$completedAt", FormatTimestamp(now));
        AddParameter(command, "$message", UnexpectedExitMessage);
        AddParameter(command, "$runningStatus", ToStorageValue(MonitorScanStatus.Running));
        AddParameter(command, "$cutoff", FormatTimestamp(cutoff));

        return command.ExecuteNonQuery();
    }

    public static int RefreshStaleRunningScans(string databasePath, DateTimeOffset now)
    {
        return MarkStaleRunningScansAsFailed(databasePath, now - StaleRunningScanTimeout, now);
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

    private static MonitorStatusSummary GetSummary(SqliteConnection connection, MonitorStatusRange range, DateTimeOffset now)
    {
        IReadOnlyList<RunRow> rows = ReadRows(connection);
        return CreateSummary(rows, range, now);
    }

    private static MonitorStatusSummary CreateSummary(IReadOnlyList<RunRow> rows, MonitorStatusRange range, DateTimeOffset now)
    {
        DateTime today = now.UtcDateTime.Date;
        DateTime startDate = GetStartDate(rows, range, today);
        List<RunRow> rangedRows = rows
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
        EnsureDatabase(connection);
        return connection;
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

    private static IReadOnlyList<RunRow> ReadRows(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, status, started_at, warning_count, message
            FROM scan_runs
            ORDER BY started_at ASC, id ASC;
            """;

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
            _ => MonitorScanStatus.Failed,
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
