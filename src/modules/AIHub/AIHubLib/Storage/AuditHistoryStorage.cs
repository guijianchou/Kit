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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
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
                return JsonSerializer.Deserialize<List<AuditResult>>(json) ?? new List<AuditResult>();
            }
            catch
            {
                return new List<AuditResult>();
            }
        }
    }

    public void SaveResult(AuditResult result, int retentionDays = 30)
    {
        lock (_lock)
        {
            var history = LoadHistory();
            history.RemoveAll(r => r.Timestamp < DateTime.UtcNow.AddDays(-retentionDays));
            history.Insert(0, result);

            string json = JsonSerializer.Serialize(history, JsonOptions);
            string tempPath = _storagePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
    }

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
