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
    public const int RetentionDays = 30;
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

    public Task<AuditStabilityReportDocument> LoadPeriodReportDocumentAsync(DateOnly endDate, TimeZoneInfo zone, bool monthly) => Task.Run(() =>
    {
        lock (_lock)
        {
            using var mutex = new Mutex(false, _mutexName);
            AcquireMutex(mutex);
            try
            {
                var history = ReadHistory();
                string path = ReportPath(endDate, monthly);
                if (endDate < OldestReportDate()) throw new InvalidDataException("The selected report is outside the 30-day retention period.");
                AuditPeriodReport report;
                try
                {
                    var archive = AuditReportArchiveStorage.Update(ReportsDirectory, history);
                    report = AuditReportArchiveStorage.Report(archive, endDate, zone, monthly);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    report = AuditPeriodReport.From(history, endDate, zone, monthly) with { ArchiveUnavailable = true };
                }

                if ((report.ArchiveUnavailable || report.CurrentRetainedHistoryIncomplete) && File.Exists(path))
                {
                    // Retained evidence cannot replace an existing, fuller document.
                    // Reading the previous version also works on the first window open.
                    return AuditStabilityReportDocument.Read(path) with { RefreshFailed = true };
                }

                SavePeriodReport(report, monthly);
                var document = AuditStabilityReportDocument.Read(path);
                if (document.Monthly != monthly || document.Report.EndDate != endDate)
                    throw new InvalidDataException("The saved report does not match the selected period.");
                return document;
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
        PruneReportExports();
        if (report.EndDate < OldestReportDate()) return report;
        Directory.CreateDirectory(ReportsDirectory);
        string path = ReportPath(report.EndDate, monthly);
        if (report.CurrentRetainedHistoryIncomplete && File.Exists(path)) return report;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var document = AuditStabilityReportDocument.Create(report, monthly);
            File.WriteAllText(temporary, document.Markdown);
            _ = AuditStabilityReportDocument.Read(temporary);
            File.Move(temporary, path, overwrite: true);
            return report;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private string ReportPath(DateOnly endDate, bool monthly) => Path.Combine(ReportsDirectory,
        (monthly ? "monthly-" : "weekly-") + endDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".md");

    private static DateOnly OldestReportDate() => DateOnly.FromDateTime(DateTime.Now).AddDays(1 - RetentionDays);

    private void PruneReportExports()
    {
        if (!Directory.Exists(ReportsDirectory)) return;
        foreach (string prefix in new[] { "weekly-", "monthly-" })
        {
            foreach (string file in Directory.EnumerateFiles(ReportsDirectory, prefix + "*.md"))
            {
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file).AsSpan(prefix.Length), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
                    && date < OldestReportDate()) File.Delete(file);
            }
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
        PruneReportExports();
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
        var history = JsonSerializer.Deserialize(json, AIHubLibJsonContext.Default.ListAuditResult)
            ?? throw new InvalidDataException("The audit history does not contain a valid list.");
        if (history.RemoveAll(result => Utc(result.Timestamp) < DateTime.UtcNow.AddDays(-RetentionDays)) > 0)
        {
            WriteHistory(history);
        }
        return history;
    }

    private void WriteHistory(List<AuditResult> history)
    {
        string temporary = _storagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(history, AIHubLibJsonContext.Default.ListAuditResult));
            File.Move(temporary, _storagePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static DateTime Utc(DateTime timestamp) => timestamp.Kind == DateTimeKind.Local
        ? timestamp.ToUniversalTime() : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);

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
                if (Utc(result.Timestamp) >= DateTime.UtcNow.AddDays(-RetentionDays)) history.Insert(0, result);
                history.Sort((left, right) => right.Timestamp.CompareTo(left.Timestamp));
                // Archive before raw-history eviction, including a late AI revision of an old scan.
                var archiveInputs = history.ToArray();
                history.RemoveAll(r => Utc(r.Timestamp) < DateTime.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, RetentionDays)));

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
