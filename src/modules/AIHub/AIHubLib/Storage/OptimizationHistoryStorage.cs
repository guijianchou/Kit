using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;

namespace Kit.AIHubLib.Storage;

public sealed class OptimizationHistoryStorage
{
    public const int MaximumRecords = 20;
    public const int HistoryRetentionDays = 30;
    public const int MaximumDetailedItems = 500;
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<Guid, byte> ActiveRuns = new();
    private readonly string _path;
    private readonly Func<DateTime> _utcNow;

    public OptimizationHistoryStorage(string? customDirectory = null)
        : this(customDirectory, () => DateTime.UtcNow)
    {
    }

    internal OptimizationHistoryStorage(string? customDirectory, Func<DateTime> utcNow)
    {
        string directory = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "AIHub", "State");
        _path = Path.Combine(Path.GetFullPath(directory), "optimization_history.json");
        _utcNow = utcNow;
    }

    public Task<IReadOnlyList<OptimizationExecutionRecord>> LoadAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<OptimizationExecutionRecord>>(() =>
    {
        lock (Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = LoadCore();
            if (RemoveExpired(records) > 0) WriteRecords(records, cancellationToken);
            foreach (var record in records.Where(record => record.State == "running" && !ActiveRuns.ContainsKey(record.RunId)))
            {
                // Never replay an unfinished record. A crash can happen after a filesystem
                // action and before its checkpoint, so remaining entries need a new scan.
                record.State = "interrupted";
                record.FailureReason = "The previous execution did not finish. Scan again to verify current files; no actions were retried.";
            }
            return records;
        }
    }, cancellationToken);

    public Task SaveAsync(OptimizationExecutionRecord record, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = LoadCore();
            records.RemoveAll(item => item.RunId == record.RunId);
            // Clone through the source-generated contract: the retained document must
            // never hold a caller's mutable in-memory result or its candidate references.
            var stored = JsonSerializer.Deserialize(ExportRecordJson(record), AIHubLibJsonContext.Default.OptimizationExecutionRecord)!;
            stored.Items = stored.Items.Take(MaximumDetailedItems).ToList();
            records.Insert(0, stored);
            RemoveExpired(records);
            records = records.Take(MaximumRecords).ToList();
            WriteRecords(records, cancellationToken);
        }
    }, cancellationToken);

    public static string ExportRecordJson(OptimizationExecutionRecord record) =>
        JsonSerializer.Serialize(record, AIHubLibJsonContext.Default.OptimizationExecutionRecord);

    internal static void MarkActive(Guid runId) => ActiveRuns.TryAdd(runId, 0);
    internal static void MarkFinished(Guid runId) => ActiveRuns.TryRemove(runId, out _);

    private int RemoveExpired(List<OptimizationExecutionRecord> records)
    {
        DateTime cutoff = _utcNow().AddDays(-HistoryRetentionDays);
        return records.RemoveAll(record => Utc(record.StartedUtc) < cutoff);
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private void WriteRecords(List<OptimizationExecutionRecord> records, CancellationToken cancellationToken)
    {
        byte[] bytes;
        while ((bytes = JsonSerializer.SerializeToUtf8Bytes(records, AIHubLibJsonContext.Default.ListOptimizationExecutionRecord)).Length > MaximumFileBytes && records.Count > 1)
        {
            records.RemoveAt(records.Count - 1);
        }
        if (bytes.Length > MaximumFileBytes) throw new IOException("The optimization record exceeds the local history size limit.");
        EnsureSafePath();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        EnsureSafePath();
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePath();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary) && OptimizationFileSafety.HasNoReparsePoints(temporary)) File.Delete(temporary);
        }
    }

    private List<OptimizationExecutionRecord> LoadCore()
    {
        EnsureSafePath();
        if (!File.Exists(_path)) return new();
        if (new FileInfo(_path).Length > MaximumFileBytes) throw new IOException("Optimization history is larger than the supported limit.");
        var records = JsonSerializer.Deserialize(File.ReadAllBytes(_path), AIHubLibJsonContext.Default.ListOptimizationExecutionRecord)
            ?? throw new JsonException("Optimization history must contain a record list.");
        if (records.Any(record => record is null || record.Items is null || record.Items.Any(item => item is null) ||
            record.TotalCount < 0 || record.ProcessedCount < 0 || record.ProcessedCount > record.TotalCount ||
            record.SucceededCount < 0 || record.FailedCount < 0 || record.SkippedCount < 0 ||
            (long)record.SucceededCount + record.FailedCount + record.SkippedCount != record.ProcessedCount))
            throw new JsonException("Invalid optimization history record.");
        foreach (var record in records) record.Items = record.Items.Take(MaximumDetailedItems).ToList();
        return records.Take(MaximumRecords).ToList();
    }

    private void EnsureSafePath()
    {
        if (!OptimizationFileSafety.HasNoReparsePoints(_path) || Directory.Exists(_path))
            throw new IOException("Optimization history must be an ordinary local file without reparse points.");
    }
}
