using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Storage;

public sealed class AuditHistoryStorage
{
    private readonly string _storagePath;
    private readonly object _lock = new();
    private readonly string _mutexName;

    public AuditHistoryStorage(string? customDirectory = null)
    {
        string dir = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kit",
            "AIHub",
            "State");

        Directory.CreateDirectory(dir);
        _storagePath = Path.Combine(dir, "audit_history.json");
        _mutexName = @"Local\Kit.AuditHistory." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_storagePath).ToUpperInvariant())));
    }

    public List<AuditResult> LoadHistory()
    {
        try
        {
            return ReadLockedHistory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new List<AuditResult>();
        }
    }

    public Task<WeeklyAuditReport> LoadWeeklyReportAsync(DateOnly endDate, TimeZoneInfo zone) =>
        Task.Run(() => WeeklyAuditReport.From(ReadLockedHistory(), endDate, zone));

    private List<AuditResult> ReadLockedHistory()
    {
        lock (_lock)
        {
            using var mutex = new Mutex(false, _mutexName);
            AcquireMutex(mutex);
            try
            {
                return ReadHistory();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private List<AuditResult> ReadHistory()
    {
        string json;
        try
        {
            json = File.ReadAllText(_storagePath);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new List<AuditResult>();
        }

        // A damaged/unreadable history must never become an empty history on the next save.
        return JsonSerializer.Deserialize(json, AIHubLibJsonContext.Default.ListAuditResult)
            ?? throw new InvalidDataException("The audit history does not contain a valid list.");
    }

    private static void AcquireMutex(Mutex mutex)
    {
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Timed out waiting for the audit history writer.");
            }
        }
        catch (AbandonedMutexException)
        {
            // Ownership was granted; atomic replacement keeps the last committed file intact.
        }
    }

    /// <summary>
    /// Upper bound on retained snapshots. The age-based retention alone is unbounded
    /// when audits run frequently, and every save rewrites the whole file, so the
    /// count is capped as well to keep both the file and the rewrite bounded.
    /// </summary>
    public const int MaxRetainedAudits = 200;

    public void SaveResult(AuditResult result, int retentionDays = 30)
    {
        lock (_lock)
        {
            using var mutex = new Mutex(false, _mutexName);
            AcquireMutex(mutex);
            string tempPath = _storagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var history = ReadHistory();
                // Local findings and later AI results share the scan's stable timestamp.
                // Updating that snapshot must not manufacture a second scan in statistics.
                history.RemoveAll(r => r.Timestamp == result.Timestamp || r.Timestamp < DateTime.UtcNow.AddDays(-retentionDays));
                history.Insert(0, result);
                history.Sort((left, right) => right.Timestamp.CompareTo(left.Timestamp));

                if (history.Count > MaxRetainedAudits)
                {
                    history.RemoveRange(MaxRetainedAudits, history.Count - MaxRetainedAudits);
                }

                string json = JsonSerializer.Serialize(history, AIHubLibJsonContext.Default.ListAuditResult);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _storagePath, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(tempPath);
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }

    /// <summary>
    /// Aggregated statistics over the retained history. The dashboard consumes these
    /// instead of hardcoded placeholders, so every counter reflects stored audits.
    /// </summary>
    public AuditHistoryStatistics GetStatistics()
    {
        var history = LoadHistory();
        return AuditHistoryStatistics.From(history);
    }

    public Task<AuditHistoryStatistics> GetStatisticsAsync() => Task.Run(GetStatistics);

    public Task<AuditResult?> LoadLatestHistoryAsync()
    {
        return Task.Run(() =>
        {
            var history = LoadHistory();
            return history.Count > 0 ? history[0] : null;
        });
    }

    public Task<List<AuditResult>> LoadHistoryAsync() => Task.Run(LoadHistory);

    public Task SaveHistoryAsync(AuditResult result, int retentionDays = 30) => Task.Run(() => SaveResult(result, retentionDays));
}
