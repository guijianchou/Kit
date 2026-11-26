using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public Task<AuditPeriodReport> LoadPeriodReportAsync(DateOnly endDate, TimeZoneInfo zone, bool monthly) => Task.Run(() =>
    {
        lock (_lock)
        {
            using var mutex = new Mutex(false, _mutexName);
            AcquireMutex(mutex);
            try
            {
                var history = ReadHistory();
                try
                {
                    var archive = AuditReportArchiveStorage.Update(ReportsDirectory, history);
                    var report = AuditReportArchiveStorage.Report(archive, endDate, zone, monthly);
                    SavePeriodReport(report, monthly);
                    return report;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    return AuditPeriodReport.From(history, endDate, zone, monthly) with { ArchiveUnavailable = true };
                }
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    });

    public string ReportsDirectory => Path.Combine(Path.GetDirectoryName(_storagePath)!, "reports");

    private AuditPeriodReport SavePeriodReport(AuditPeriodReport report, bool monthly)
    {
        Directory.CreateDirectory(ReportsDirectory);
        string prefix = monthly ? "monthly-" : "weekly-";
        string path = Path.Combine(ReportsDirectory, prefix + report.EndDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".md");
        if (report.HistoryIncomplete && File.Exists(path)) return report;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, "# " + (monthly ? "审计月报 / Monthly audit report" : "审计周报 / Weekly audit report")
                + Environment.NewLine + Environment.NewLine + report.Format(chinese: true) + Environment.NewLine + report.Format(chinese: false));
            File.Move(temporary, path, overwrite: true);
            // Keep at most 93 dated exports per kind. Only files with our exact generated names qualify.
            foreach (var old in Directory.EnumerateFiles(ReportsDirectory, prefix + "*.md")
                .Where(file => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file).AsSpan(prefix.Length), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                .OrderByDescending(file => file, StringComparer.Ordinal).Skip(93))
            {
                File.Delete(old);
            }
            return report;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

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
                history.RemoveAll(r => r.Timestamp == result.Timestamp);
                history.Insert(0, result);
                history.Sort((left, right) => right.Timestamp.CompareTo(left.Timestamp));
                // Archive before raw-history eviction, including a late AI revision of an old scan.
                var archiveInputs = history.ToArray();
                history.RemoveAll(r => r.Timestamp != result.Timestamp && r.Timestamp < DateTime.UtcNow.AddDays(-retentionDays));

                if (history.Count > MaxRetainedAudits)
                {
                    // Preserve one daily observation before filling remaining slots with recent scans.
                    // Frequent scans must not erase the whole monthly baseline in a few days.
                    var retained = history.GroupBy(audit => audit.Timestamp.ToLocalTime().Date)
                        .Select(group => group.First()).Take(MaxRetainedAudits).ToHashSet();
                    foreach (var audit in history)
                    {
                        if (retained.Count >= MaxRetainedAudits) break;
                        retained.Add(audit);
                    }
                    history.RemoveAll(audit => !retained.Contains(audit));
                }

                string json = JsonSerializer.Serialize(history, AIHubLibJsonContext.Default.ListAuditResult);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _storagePath, overwrite: true);
                try
                {
                    var archive = AuditReportArchiveStorage.Update(ReportsDirectory, archiveInputs);
                    var date = DateOnly.FromDateTime(result.Timestamp.ToLocalTime());
                    SavePeriodReport(AuditReportArchiveStorage.Report(archive, date, TimeZoneInfo.Local, monthly: false), monthly: false);
                    SavePeriodReport(AuditReportArchiveStorage.Report(archive, date, TimeZoneInfo.Local, monthly: true), monthly: true);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    // Audit persistence has succeeded. Opening the report retries its independent export.
                    ManagedCommon.Logger.LogWarning("Audit saved; the period report export could not be refreshed.");
                }
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
