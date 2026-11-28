using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Storage;

// All callers hold AuditHistoryStorage's process/thread lock.
internal static class AuditReportArchiveStorage
{
    // 93 retained report cutoffs plus both calendar months needed by the earliest cutoff.
    internal const int MaxDays = 155;
    internal const int MaxScansPerDay = 512;
    internal const int MaxBytes = 32 * 1024 * 1024;

    internal static AuditReportArchive Update(string directory, IReadOnlyList<AuditResult> history)
    {
        string path = Path.Combine(directory, "daily-summary.json");
        var archive = new AuditReportArchive();
        var legacyCounts = new Dictionary<DateOnly, int>();
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > MaxBytes)
                throw new InvalidDataException("The daily audit archive exceeds its size limit.");
            string json = File.ReadAllText(path);
            if (json.AsSpan().TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                var legacy = JsonSerializer.Deserialize(json, AIHubLibJsonContext.Default.ListAuditDailySnapshot)
                    ?? throw new InvalidDataException("The daily audit archive is invalid.");
                if (legacy.Count > 93 || legacy.Any(day => day is null || !ValidResult(day.Snapshot) || day.Audits < 1)
                    || legacy.Select(day => day.Date).Distinct().Count() != legacy.Count)
                    throw new InvalidDataException("The daily audit archive is invalid.");
                // Old arrays only kept the last scan. Recover surviving raw scans, but never
                // claim to have reconstructed evidence that was already discarded.
                foreach (var day in legacy)
                {
                    var migrated = new AuditArchiveDay { Date = day.Date, EvidenceIncomplete = true };
                    Merge(migrated, [day.Snapshot]);
                    archive.Days.Add(migrated);
                    legacyCounts[day.Date] = day.Audits;
                }
                archive.PrunedThrough = legacy.Count == 0 ? LatestExportDate(directory) : Before(legacy.Min(day => day.Date));
            }
            else
            {
                archive = JsonSerializer.Deserialize(json, AIHubLibJsonContext.Default.AuditReportArchive)
                    ?? throw new InvalidDataException("The daily audit archive is invalid.");
                Validate(archive);
                archive.Version = 3;
            }
        }
        else if (LatestExportDate(directory) is { } lastExport)
        {
            // Raw retention can also discard scans within a surviving date. Treat all
            // previously exported dates as unverified, not just dates before raw history.
            // A finite boundary lets genuinely new periods become complete again.
            archive.PrunedThrough = lastExport;
        }

        var days = archive.Days.ToDictionary(day => day.Date);
        foreach (var group in history.GroupBy(scan => DateOnly.FromDateTime(scan.Timestamp.ToLocalTime())))
        {
            if (!days.TryGetValue(group.Key, out var day))
            {
                day = new AuditArchiveDay { Date = group.Key };
                days.Add(group.Key, day);
            }
            Merge(day, group);
        }
        // Preserve old counts even for dates no longer present in raw history. Apply
        // this floor after merging surviving scans so migration does not count them twice.
        foreach (var entry in legacyCounts) days[entry.Key].Audits = Math.Max(days[entry.Key].Audits, entry.Value);
        archive.Days = days.Values.OrderByDescending(day => day.Date).ToList();
        while (archive.Days.Count > MaxDays) RemoveOldestDay(archive);

        byte[] bytes = Serialize(archive);
        while (bytes.Length > MaxBytes)
        {
            // Bound total disk/memory use without permanently wedging future saves.
            if (archive.Days.Count > 1)
            {
                RemoveOldestDay(archive);
            }
            else
            {
                var day = archive.Days.Single();
                day.EvidenceIncomplete = true;
                if (day.Scans.Count > 1)
                {
                    RemoveOldestScan(day);
                }
                else
                {
                    var scan = day.Scans.Single();
                    if (scan.Findings.Length > 0)
                    {
                        scan.Findings = scan.Findings.Take(scan.Findings.Length / 2).ToArray();
                    }
                    else
                    {
                        // Oversized/corrupt scalar metadata must not turn compaction into an infinite loop.
                        scan.UsedModel = string.Empty;
                        scan.AiReportSummary = null;
                        scan.AiReportMeta = null;
                        scan.AiAnalysisScope = null;
                        scan.AiEvidenceFingerprint = null;
                        scan.CollectionProfile = null;
                        scan.SkippedChannels = null;
                        scan.LimitedChannels = null;
                        scan.AnalysisOutcome = "partial";
                        scan.IncompleteLogs = true;
                    }
                }
                Compact(day);
            }
            bytes = Serialize(archive);
        }

        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return archive;
    }

    internal static AuditPeriodReport Report(AuditReportArchive archive, DateOnly endDate, TimeZoneInfo zone, bool monthly)
    {
        var report = AuditPeriodReport.From(archive.Days.SelectMany(day => day.RestoreScans()).ToArray(), endDate, zone, monthly,
            archive.Days.ToDictionary(day => day.Date, day => day.Audits));
        bool Relevant(DateOnly date) => (date >= report.StartDate && date <= report.EndDate)
            || (date >= report.PreviousStartDate && date <= report.PreviousEndDate);
        return report with
        {
            HistoryIncomplete = (archive.PrunedThrough is { } cutoff && report.PreviousStartDate <= cutoff)
                || archive.Days.Any(day => Relevant(day.Date) && day.EvidenceIncomplete),
        };
    }

    private static void Merge(AuditArchiveDay day, IEnumerable<AuditResult> results)
    {
        var scans = day.Scans.ToDictionary(scan => scan.Timestamp);
        var bodies = day.Findings.Select((finding, index) => (Key: Fingerprint(finding), Index: index))
            .GroupBy(item => item.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (!ValidResult(result)) throw new InvalidDataException("An audit snapshot is invalid.");
            if (day.OmittedThrough is { } omitted && result.Timestamp <= omitted) continue;
            if (!scans.ContainsKey(result.Timestamp)) day.Audits++;
            var indices = new List<int>();
            foreach (var finding in result.Findings)
            {
                string key = Fingerprint(finding);
                if (!bodies.TryGetValue(key, out int index))
                {
                    index = day.Findings.Count;
                    day.Findings.Add(finding);
                    bodies.Add(key, index);
                }
                indices.Add(index);
            }
            scans[result.Timestamp] = new AuditArchiveScan
            {
                Timestamp = result.Timestamp, HealthScore = result.HealthScore,
                AnalysisOutcome = result.AnalysisOutcome?.Length > 32 ? "partial" : result.AnalysisOutcome,
                UsedModel = result.UsedModel?.Length > 256 ? result.UsedModel[..256] : result.UsedModel ?? string.Empty,
                IncompleteLogs = result.EvidenceIncomplete || result.SkippedChannels?.Count > 0 || result.LimitedChannels?.Count > 0,
                EvidenceIncomplete = result.EvidenceIncomplete,
                ScanStart = result.ScanStart, ScanEnd = result.ScanEnd, EventsScanned = result.EventsScanned,
                CollectionProfile = result.CollectionProfile,
                SkippedChannels = result.SkippedChannels?.ToList(), LimitedChannels = result.LimitedChannels?.ToList(),
                AiReportSummary = result.AiReportSummary, AiReportMeta = result.AiReportMeta,
                AiAnalysisScope = result.AiAnalysisScope, AiEvidenceFingerprint = result.AiEvidenceFingerprint,
                AiAnalyzedEvents = result.AiAnalyzedEvents, AiReusedEvents = result.AiReusedEvents,
                Findings = indices.ToArray(),
            };
        }
        day.Scans = scans.Values.OrderByDescending(scan => scan.Timestamp).ToList();
        while (day.Scans.Count > MaxScansPerDay) RemoveOldestScan(day);
        Compact(day);
    }

    private static string Fingerprint(AuditIssueEnhanced finding) => JsonSerializer.Serialize((AuditIssue)finding, AIHubLibJsonContext.Default.AuditIssue);

    private static void Compact(AuditArchiveDay day)
    {
        var used = day.Scans.SelectMany(scan => scan.Findings).Distinct().ToArray();
        var map = used.Select((old, index) => (old, index)).ToDictionary(item => item.old, item => item.index);
        day.Findings = used.Select(index => day.Findings[index]).ToList();
        foreach (var scan in day.Scans) scan.Findings = scan.Findings.Select(index => map[index]).ToArray();
    }

    private static void RemoveOldestScan(AuditArchiveDay day)
    {
        var scan = day.Scans.MinBy(scan => scan.Timestamp)!;
        day.OmittedThrough = scan.Timestamp;
        day.EvidenceIncomplete = true;
        day.Scans.Remove(scan);
    }

    private static void RemoveOldestDay(AuditReportArchive archive)
    {
        var oldest = archive.Days.MinBy(day => day.Date)!;
        if (archive.PrunedThrough is null || oldest.Date > archive.PrunedThrough.Value) archive.PrunedThrough = oldest.Date;
        archive.Days.Remove(oldest);
    }

    private static DateOnly Before(DateOnly date) => date == DateOnly.MinValue ? date : date.AddDays(-1);
    private static DateOnly? LatestExportDate(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        DateOnly? latest = null;
        foreach (string path in Directory.EnumerateFiles(directory, "*.md"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int prefix = name.StartsWith("weekly-", StringComparison.Ordinal) ? 7 : name.StartsWith("monthly-", StringComparison.Ordinal) ? 8 : 0;
            if (prefix > 0 && DateOnly.TryParseExact(name.AsSpan(prefix), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date) && (latest is null || date > latest.Value)) latest = date;
        }
        return latest;
    }
    private static byte[] Serialize(AuditReportArchive archive) => JsonSerializer.SerializeToUtf8Bytes(archive, AIHubLibJsonContext.Default.AuditReportArchive);
    private static bool ValidResult(AuditResult? result) => result?.Findings is not null && result.Findings.All(issue => issue is not null);

    private static void Validate(AuditReportArchive archive)
    {
        if (archive.Version is not (2 or 3) || archive.Days is null || archive.Days.Count > MaxDays
            || archive.Days.Any(day => day is null || day.Audits < 1 || day.Findings is null || day.Findings.Any(finding => finding is null)
                || day.Scans is null || day.Scans.Count == 0 || day.Scans.Any(scan => scan is null || scan.Findings is null
                    || scan.Findings.Any(index => index < 0 || index >= day.Findings.Count))
                || day.Scans.Select(scan => scan.Timestamp).Distinct().Count() != day.Scans.Count)
            || archive.Days.Select(day => day.Date).Distinct().Count() != archive.Days.Count)
            throw new InvalidDataException("The daily audit archive is invalid.");
    }
}
