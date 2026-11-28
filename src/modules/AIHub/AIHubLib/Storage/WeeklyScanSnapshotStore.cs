using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Storage;

/// <summary>Bounded, local evidence reuse. Reading this store never collects Windows events.</summary>
public sealed class WeeklyScanSnapshotStore
{
    public const int MaxEvents = 12000;
    public const int MaxBytes = 8 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;
    private readonly Func<IReadOnlyList<AuditResult>> _history;
    private readonly Func<DateTimeOffset> _utcNow;

    public WeeklyScanSnapshotStore(string? directory = null, Func<IReadOnlyList<AuditResult>>? history = null, Func<DateTimeOffset>? utcNow = null)
    {
        string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "AIHub", "State");
        _directory = directory ?? Path.Combine(state, "weekly-scans");
        _history = history ?? new AuditHistoryStorage(directory is null ? state : Path.GetDirectoryName(Path.GetFullPath(directory))).LoadHistory;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task SaveAsync(AuditResult audit, IReadOnlyList<SecurityEvent> events, CancellationToken cancellationToken = default)
        => Task.Run(() => SaveCoreAsync(audit, events, cancellationToken), cancellationToken);

    private async Task SaveCoreAsync(AuditResult audit, IReadOnlyList<SecurityEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(events);
        if (!SevenDays(audit, out var start, out var end) || end > _utcNow().UtcDateTime.AddMinutes(1) || end < _utcNow().UtcDateTime.AddDays(-30)) return;
        // Detach mutable audit/UI state before asynchronous persistence.
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(audit, AIHubLibJsonContext.Default.AuditResult), AIHubLibJsonContext.Default.AuditResult)!;
        var candidates = events.Where(item => IsStabilityChannel(item.LogName) && item.TimeCreated is { } time && Utc(time) >= start && Utc(time) < end)
            .OrderByDescending(item => item.TimeCreated).ToArray();
        var retained = candidates.Take(MaxEvents).Select(item => item with { RawXml = null }).ToArray();
        // Hash only the detached evidence, not save time or a random ID. Navigation often
        // republishes the identical scan and must not invalidate a reusable weekly report.
        var snapshot = new WeeklyScanSnapshot(1, string.Empty, DateTime.UnixEpoch, copy, retained)
        { RawEventsTruncated = retained.Length < candidates.Length };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, AIHubLibJsonContext.Default.WeeklyScanSnapshot);
        while (bytes.Length > MaxBytes - 1024 && snapshot.Events.Count > 0)
        {
            snapshot = snapshot with { Events = snapshot.Events.Take(snapshot.Events.Count / 2).ToArray(), RawEventsTruncated = true };
            bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, AIHubLibJsonContext.Default.WeeklyScanSnapshot);
        }
        if (bytes.Length > MaxBytes - 1024) throw new InvalidDataException("The seven-day scan metadata exceeds the snapshot size limit.");
        snapshot = snapshot with { Id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), SavedUtc = _utcNow().UtcDateTime };
        bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, AIHubLibJsonContext.Default.WeeklyScanSnapshot);
        string path = PathFor(new DateTimeOffset(end).ToLocalTime());
        var gate = Gates.GetOrAdd(Path.GetFullPath(_directory), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var previous = Read(path);
            if (previous?.Id == snapshot.Id) return;
            // An old background completion or an incomplete replacement cannot discard a newer complete scan.
            if (previous is not null && SevenDays(previous.Audit, out _, out var previousEnd)
                && (previousEnd > end || (previous.Audit.Timestamp == copy.Timestamp
                    && ((!previous.Audit.EvidenceIncomplete && copy.EvidenceIncomplete)
                        || (previous.Audit.AnalysisOutcome == "ok" && copy.AnalysisOutcome != "ok"))))) return;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            Prune();
        }
        finally { gate.Release(); }
    }

    public Task<WeeklyScanSnapshot?> LoadAsync(DateTimeOffset requestedEnd, CancellationToken cancellationToken = default)
        => Task.Run(() => LoadCoreAsync(requestedEnd, cancellationToken), cancellationToken);

    private async Task<WeeklyScanSnapshot?> LoadCoreAsync(DateTimeOffset requestedEnd, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestedEnd > _utcNow().AddMinutes(1) || requestedEnd < _utcNow().AddDays(-30)) return null;
        var gate = Gates.GetOrAdd(Path.GetFullPath(_directory), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prune();
            var saved = Read(PathFor(requestedEnd));
            if (saved is not null && Compatible(saved.Audit, requestedEnd)) return saved;
            var audit = _history().Where(item => Compatible(item, requestedEnd)).OrderByDescending(item => item.ScanEnd).FirstOrDefault();
            if (audit is null) return null;
            var json = JsonSerializer.Serialize(audit, AIHubLibJsonContext.Default.AuditResult);
            var copy = JsonSerializer.Deserialize(json, AIHubLibJsonContext.Default.AuditResult)!;
            string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            return new(1, id, Utc(audit.Timestamp), copy, []) { RawEventsAvailable = false };
        }
        finally { gate.Release(); }
    }

    public static bool IsStabilityChannel(string channel) => channel is "System" or "Application" or "Setup";

    internal static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    internal static bool SevenDays(AuditResult audit, out DateTime start, out DateTime end)
    {
        start = audit.ScanStart is { } from ? Utc(from) : default;
        end = audit.ScanEnd is { } to ? Utc(to) : default;
        return audit.ScanStart.HasValue && audit.ScanEnd.HasValue && end - start == TimeSpan.FromDays(7);
    }

    private bool Compatible(AuditResult audit, DateTimeOffset requestedEnd) => SevenDays(audit, out _, out var end)
        && end <= requestedEnd.UtcDateTime && end >= _utcNow().UtcDateTime.AddDays(-30)
        && TargetDate(new DateTimeOffset(end)) == TargetDate(requestedEnd);

    internal static DateOnly TargetDate(DateTimeOffset end)
    {
        var local = end.ToLocalTime();
        return DateOnly.FromDateTime((local.TimeOfDay == TimeSpan.Zero ? local.AddTicks(-1) : local).DateTime);
    }

    private string PathFor(DateTimeOffset end) => Path.Combine(_directory, "weekly-scan-" + TargetDate(end).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    private static WeeklyScanSnapshot? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            var value = JsonSerializer.Deserialize(File.ReadAllBytes(path), AIHubLibJsonContext.Default.WeeklyScanSnapshot);
            if (value is not { Version: 1, Audit: not null, Events: not null } || string.IsNullOrWhiteSpace(value.Id)
                || !SevenDays(value.Audit, out var start, out var end) || value.Events.Count > MaxEvents) return null;
            if (value.Events.Any(item => item is null || !IsStabilityChannel(item.LogName) || item.TimeCreated is not { } time || Utc(time) < start || Utc(time) >= end)) return null;
            return value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void Prune()
    {
        if (!Directory.Exists(_directory)) return;
        var cutoff = DateOnly.FromDateTime(_utcNow().ToLocalTime().AddDays(-30).DateTime);
        var files = Directory.EnumerateFiles(_directory, "weekly-scan-*.json")
            .Select(path => (Path: path, Date: DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path).AsSpan("weekly-scan-".Length), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? (DateOnly?)date : null))
            .Where(item => item.Date.HasValue).OrderByDescending(item => item.Date).ToArray();
        for (int index = 0; index < files.Length; index++)
        {
            if (files[index].Date >= cutoff && index < 30) continue;
            try { File.Delete(files[index].Path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
