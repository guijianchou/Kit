using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kit.AIHubLib.Models;

namespace Kit.AIHubLib.Services;

/// <summary>
/// Packs exact repeated event messages without losing their occurrence times or source references.
/// The analysis cache and retry checkpoints continue to use individual evidence records.
/// </summary>
public sealed class AuditEvidencePacking
{
    public const int BatchSize = 64;
    public const int SummaryCharacterLimit = 1_024;
    private readonly int[][] _sourceIndices;

    private AuditEvidencePacking(List<EventAnalysisInput> items, int[][] sourceIndices)
    {
        Items = items;
        _sourceIndices = sourceIndices;
    }

    public IReadOnlyList<EventAnalysisInput> Items { get; }

    public static AuditEvidencePacking Create(IReadOnlyList<EventAnalysisInput> inputs)
    {
        var groups = new List<List<int>>();
        var identities = new Dictionary<(int Id, string Log, string Provider, int? Level, string Summary), int>();
        for (int index = 0; index < inputs.Count; index++)
        {
            EventAnalysisInput input = inputs[index];
            var identity = (input.EventId, input.LogName, input.Provider, input.Level, input.Summary);

            // Truncated or missing messages cannot establish that the original records were identical.
            bool canPack = input.Summary.Length is > 0 and < SummaryCharacterLimit && input.OccurrenceTimesUtc is null;
            if (canPack && identities.TryGetValue(identity, out int existing))
            {
                groups[existing].Add(index);
            }
            else
            {
                if (canPack)
                {
                    identities.Add(identity, groups.Count);
                }
                groups.Add([index]);
            }
        }

        var items = new List<EventAnalysisInput>(groups.Count);
        foreach (List<int> group in groups)
        {
            EventAnalysisInput primary = inputs[group[0]];
            items.Add(new EventAnalysisInput
            {
                EventId = primary.EventId,
                LogName = primary.LogName,
                Provider = primary.Provider,
                Level = primary.Level,
                TimeUtc = primary.TimeUtc,
                Summary = primary.Summary,
                OccurrenceTimesUtc = group.Count > 1 ? group.Select(index => inputs[index].TimeUtc).ToArray() : primary.OccurrenceTimesUtc,
            });
        }

        return new(items, groups.Select(group => group.ToArray()).ToArray());
    }

    public HashSet<string> ExpandReferences(IEnumerable<string> references) => references
        .SelectMany(reference => _sourceIndices[Parse(reference)].Select(Format))
        .ToHashSet(StringComparer.Ordinal);

    public AuditIssue RestoreIssue(AuditIssue issue)
    {
        var restored = AuditAnalysisCache.Project(issue, reference => Format(_sourceIndices[Parse(reference)][0]));
        restored.RelatedEventRefs = ExpandReferences(issue.RelatedEventRefs.Append(issue.EventRef)
            .Concat(AuditAnalysisCache.GetNarrativeReferences(issue))).Order(StringComparer.Ordinal).ToList();
        restored.Occurrences = restored.RelatedEventRefs.Count;
        return restored;
    }

    public AuditIssueContainer RestoreBatch(AuditIssueContainer batch) => new()
    {
        Issues = batch.Issues.Select(RestoreIssue).ToList(),
    };

    private int Parse(string reference)
    {
        if (reference is not null && reference.StartsWith("item-", StringComparison.Ordinal)
            && int.TryParse(reference.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index > 0 && index <= _sourceIndices.Length
            && string.Equals(reference, Format(index - 1), StringComparison.Ordinal))
        {
            return index - 1;
        }

        throw new ArgumentException("The reference is not assigned to a packed input.", nameof(reference));
    }

    private static string Format(int index) => "item-" + (index + 1).ToString("D6", CultureInfo.InvariantCulture);
}
