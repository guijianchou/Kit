using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

/// <summary>Retains one bounded evidence window. Only fully completed analysis advances the cache.</summary>
public sealed partial class AuditAnalysisCache
{
    private readonly object _gate = new();
    private readonly string? _path;
    private string _scope = string.Empty;
    private string[] _identities = [];
    private string[] _contexts = [];
    private DateTime?[] _eventTimes = [];
    private List<AuditIssue> _issues = [];
    private bool _loaded;
    public const int MaximumEvents = 400;
    private const int MaximumBytes = 8 * 1024 * 1024;
    private const int SnapshotVersion = 2;
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])item-[0-9]+(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NarrativeReferencePattern();

    public AuditAnalysisCache(string? path = null) => _path = path;

    public AuditAnalysisPlan Prepare(string scope, IReadOnlyList<string> fingerprints, IReadOnlyList<string> contexts, bool force,
        IReadOnlyList<DateTime?>? eventTimes = null, IReadOnlyList<string>? summaries = null)
    {
        if (fingerprints.Count > MaximumEvents || fingerprints.Count != contexts.Count
            || (eventTimes is not null && fingerprints.Count != eventTimes.Count)
            || (summaries is not null && fingerprints.Count != summaries.Count))
            throw new ArgumentException("The analysis evidence window is invalid.");
        var times = eventTimes?.Select(time => time?.ToUniversalTime()).ToArray() ?? new DateTime?[fingerprints.Count];
        var duplicates = new Dictionary<string, int>(StringComparer.Ordinal);
        var identities = fingerprints.Select(value =>
        {
            duplicates.TryGetValue(value, out int count);
            duplicates[value] = count + 1;
            return value + ":" + count.ToString(CultureInfo.InvariantCulture);
        }).ToArray();
        lock (_gate)
        {
            Load();
            var current = identities.Select((identity, index) => (identity, index)).ToDictionary(item => item.identity, item => item.index, StringComparer.Ordinal);
            var previous = _identities.ToHashSet(StringComparer.Ordinal);
            var changedContexts = new HashSet<string>(StringComparer.Ordinal);
            if (force || !string.Equals(scope, _scope, StringComparison.Ordinal))
            {
                changedContexts.UnionWith(contexts);
            }
            else
            {
                var changedTimes = new List<DateTime>();
                var changedSummaries = new List<string>();
                for (int index = 0; index < identities.Length; index++)
                {
                    if (previous.Contains(identities[index])) continue;
                    changedContexts.Add(contexts[index]);
                    if (times[index] is DateTime time) changedTimes.Add(time);
                    if (summaries is not null) changedSummaries.Add(summaries[index]);
                }
                for (int index = 0; index < _identities.Length; index++)
                {
                    if (current.ContainsKey(_identities[index])) continue;
                    changedContexts.Add(_contexts[index]);
                    if (_eventTimes[index] is DateTime time) changedTimes.Add(time);
                }

                // New evidence can establish a relationship that did not produce a finding before.
                // Expand from changed events only: do not walk time neighbours transitively across
                // the whole window. Rechecking the selected providers also replaces their old findings.
                for (int index = 0; index < identities.Length; index++)
                {
                    if (times[index] is DateTime time && changedTimes.Any(changed => (time - changed).Duration() <= CorrelationWindow))
                    {
                        changedContexts.Add(contexts[index]);
                    }
                }

                // A later event may name an already affected application/account outside the time
                // window. Use explicit identifiers, without retaining raw event messages in the cache.
                foreach (var issue in _issues)
                {
                    if (changedSummaries.Any(summary => MentionsAffected(summary, issue.Affected)))
                    {
                        foreach (int index in References(issue).Select(reference => Parse(reference, _identities.Length)).Where(index => index >= 0))
                        {
                            changedContexts.Add(_contexts[index]);
                        }
                    }
                }
            }

            // A finding may connect providers. Recheck its whole evidence chain when one part changes.
            bool expanded;
            do
            {
                expanded = false;
                foreach (var issue in _issues)
                {
                    var indices = References(issue).Select(reference => Parse(reference, _identities.Length)).Where(index => index >= 0).ToArray();
                    if (indices.Any(index => changedContexts.Contains(_contexts[index])))
                        foreach (int index in indices) expanded |= changedContexts.Add(_contexts[index]);
                }
            } while (expanded);

            var reused = new List<AuditIssue>();
            if (!force && string.Equals(scope, _scope, StringComparison.Ordinal))
            {
                foreach (var issue in _issues)
                {
                    var refs = References(issue).ToArray();
                    if (refs.Any(reference => Parse(reference, _identities.Length) is var index
                        && (index < 0 || changedContexts.Contains(_contexts[index]) || !current.ContainsKey(_identities[index])))) continue;
                    reused.Add(Project(issue, reference => Reference(current[_identities[Parse(reference, _identities.Length)]])));
                }
            }
            return new(scope, identities, contexts.ToArray(), Enumerable.Range(0, identities.Length).Where(index => changedContexts.Contains(contexts[index])).ToArray(), reused, times);
        }
    }

    public void Commit(AuditAnalysisPlan plan, IReadOnlyList<AuditIssue> completedIssues)
    {
        lock (_gate)
        {
            if (completedIssues.Any(issue => issue is null || issue.RelatedEventRefs is null
                || References(issue).Any(reference => Parse(reference, plan.Identities.Length) < 0)))
                throw new ArgumentException("The analysis references evidence outside its window.", nameof(completedIssues));
            var projected = completedIssues.Select(issue => Project(issue, reference => reference)).ToList();
            _scope = plan.Scope;
            _identities = plan.Identities;
            _contexts = plan.Contexts;
            _eventTimes = plan.EventTimes ?? new DateTime?[plan.Identities.Length];
            _issues = projected;
            Save();
        }
    }

    private void Load()
    {
        if (_loaded || _path is null) return;
        _loaded = true;
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) return;
            var snapshot = JsonSerializer.Deserialize(stream, AIHubLibJsonContext.Default.AuditAnalysisSnapshot);
            if (snapshot is null || snapshot.Version != SnapshotVersion || snapshot.Identities is null || snapshot.Contexts is null || snapshot.Issues is null
                || snapshot.EventTimes is null || snapshot.EventTimes.Length != snapshot.Identities.Length
                || snapshot.Identities.Length > MaximumEvents || snapshot.Identities.Length != snapshot.Contexts.Length
                || snapshot.Identities.Distinct(StringComparer.Ordinal).Count() != snapshot.Identities.Length
                || snapshot.Identities.Any(string.IsNullOrWhiteSpace) || snapshot.Contexts.Any(value => value is null)
                || snapshot.Issues.Any(issue => issue is null || issue.RelatedEventRefs is null || !ValidIssue(issue)
                    || References(issue).Any(reference => reference is null || Parse(reference, snapshot.Identities.Length) < 0))) return;
            _scope = snapshot.Scope;
            _identities = snapshot.Identities;
            _contexts = snapshot.Contexts;
            _eventTimes = snapshot.EventTimes;
            _issues = snapshot.Issues;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // The cache is optional. Missing or invalid data causes a full analysis.
        }
    }

    private void Save()
    {
        if (_path is null) return;
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new AuditAnalysisSnapshot(SnapshotVersion, _scope, _identities, _contexts, _issues, _eventTimes), AIHubLibJsonContext.Default.AuditAnalysisSnapshot);
            if (bytes.Length > MaximumBytes) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ManagedCommon.Logger.LogWarning("AI audit result cache could not be saved; results remain available in this session.");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    public static AuditIssue Project(AuditIssue issue, Func<string, string> map) => new AuditIssueEnhanced(issue)
    {
        EventRef = map(issue.EventRef),
        RelatedEventRefs = References(issue).Select(map).Distinct(StringComparer.Ordinal).ToList(),
        Occurrences = References(issue).Distinct(StringComparer.Ordinal).Count(),
        Title = MapNarrative(issue.Title, map),
        TitleZh = MapNarrative(issue.TitleZh, map),
        Description = MapNarrative(issue.Description, map),
        DescriptionZh = MapNarrative(issue.DescriptionZh, map),
        RootCause = MapNarrative(issue.RootCause, map),
        RootCauseZh = MapNarrative(issue.RootCauseZh, map),
        Recommendation = MapNarrative(issue.Recommendation, map),
        RecommendationZh = MapNarrative(issue.RecommendationZh, map),
    };

    // Narrative-only citations are evidence too: validate them, track their invalidation, and make
    // them available in the structured evidence list. Replace in one pass to avoid 1 -> 2 -> 3 cascades.
    public static IEnumerable<string> GetNarrativeReferences(AuditIssue issue) => NarrativeFields(issue)
        .Where(value => !string.IsNullOrEmpty(value)).SelectMany(value => NarrativeReferencePattern().Matches(value).Select(match => match.Value))
        .Distinct(StringComparer.Ordinal);

    private static string MapNarrative(string value, Func<string, string> map) => string.IsNullOrEmpty(value)
        ? value : NarrativeReferencePattern().Replace(value, match => map(match.Value));

    private static IEnumerable<string> NarrativeFields(AuditIssue issue) =>
        [issue.Title, issue.TitleZh, issue.Description, issue.DescriptionZh, issue.RootCause, issue.RootCauseZh, issue.Recommendation, issue.RecommendationZh];

    private static IEnumerable<string> References(AuditIssue issue) => issue.RelatedEventRefs.Append(issue.EventRef)
        .Concat(GetNarrativeReferences(issue)).Distinct(StringComparer.Ordinal);

    private static bool MentionsAffected(string summary, string affected)
    {
        if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(affected) || affected.Length < 3) return false;
        for (int start = 0; start <= summary.Length - affected.Length;)
        {
            int index = summary.IndexOf(affected, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            int end = index + affected.Length;
            if ((index == 0 || !IsIdentifierCharacter(summary[index - 1]))
                && (end == summary.Length || !IsIdentifierCharacter(summary[end])
                    || (summary[end] == '.' && (end + 1 == summary.Length || !IsIdentifierCharacter(summary[end + 1]))))) return true;
            start = index + 1;
        }
        return false;
    }

    private static bool IsIdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '-' or '.';

    private static bool ValidIssue(AuditIssue issue) => !string.IsNullOrWhiteSpace(issue.Key) && issue.Key.Length <= 48
        && issue.Severity is "High" or "Medium" or "Low" && issue.Confidence is "High" or "Medium" or "Low"
        && issue.Occurrences > 0 && issue.EventId is not null && issue.EventTimestamp is not null && issue.Source is not null
        && issue.LogName is not null && issue.Category is not null && issue.Affected is not null
        && NarrativeFields(issue).All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 8192);
    internal static string Reference(int index) => "item-" + (index + 1).ToString("D6", CultureInfo.InvariantCulture);
    internal static int Parse(string reference, int count) => reference is not null && reference.Length == 11 && reference.StartsWith("item-", StringComparison.Ordinal)
        && int.TryParse(reference.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0 && number <= count ? number - 1 : -1;
}

public sealed record AuditAnalysisSnapshot(int Version, string Scope, string[] Identities, string[] Contexts, List<AuditIssue> Issues, DateTime?[]? EventTimes = null);

public sealed record AuditAnalysisPlan(string Scope, string[] Identities, string[] Contexts, int[] RequestedIndices, List<AuditIssue> ReusedIssues, DateTime?[]? EventTimes = null)
{
    public AuditIssue RestoreReference(AuditIssue issue) => AuditAnalysisCache.Project(issue, reference =>
    {
        int index = AuditAnalysisCache.Parse(reference, RequestedIndices.Length);
        if (index < 0) throw new InvalidOperationException("The analysis references evidence outside its request.");
        return AuditAnalysisCache.Reference(RequestedIndices[index]);
    });
}
