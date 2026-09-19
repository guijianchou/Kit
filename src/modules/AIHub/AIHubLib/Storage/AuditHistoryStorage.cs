using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Storage;

public sealed class AuditHistoryStorage
{
    private readonly string _storagePath;
    private readonly object _lock = new();

    public AuditHistoryStorage(string? customDirectory = null)
    {
        string dir = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kit",
            "AIHub",
            "State");

        Directory.CreateDirectory(dir);
        _storagePath = Path.Combine(dir, "audit_history.json");
    }

    public List<AuditResult> LoadHistory()
    {
        lock (_lock)
        {
            if (!File.Exists(_storagePath))
            {
                return new List<AuditResult>();
            }

            try
            {
                string json = File.ReadAllText(_storagePath);
                return JsonSerializer.Deserialize(json, AIHubLibJsonContext.Default.ListAuditResult) ?? new List<AuditResult>();
            }
            catch
            {
                return new List<AuditResult>();
            }
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
            var history = LoadHistory();
            history.RemoveAll(r => r.Timestamp < DateTime.UtcNow.AddDays(-retentionDays));
            history.Insert(0, result);

            if (history.Count > MaxRetainedAudits)
            {
                history.RemoveRange(MaxRetainedAudits, history.Count - MaxRetainedAudits);
            }

            string json = JsonSerializer.Serialize(history, AIHubLibJsonContext.Default.ListAuditResult);
            string tempPath = _storagePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storagePath, overwrite: true);
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
