namespace Kit.AiHub.Security;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Host invariants apply independently of editable prompt text and plugin validators.
/// </summary>
public sealed class OutputContractAuditor
{
    private static readonly FrozenSet<string> ForbiddenProperties = new[]
    {
        "command", "commandLine", "shell", "script", "executable", "arguments", "toolCalls",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> KnownActions = new[] { "skip", "move", "delete", "restart" }.ToFrozenSet(StringComparer.Ordinal);

    public static string ExtractJsonPayload(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 1_048_576)
        {
            throw new JsonException("The model response is empty or exceeds the size limit.");
        }

        string trimmed = content.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[7..^3].Trim();
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal) && trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[3..^3].Trim();
        }

        // Parse the entire response, rather than accepting JSON hidden inside arbitrary text.
        using var document = JsonDocument.Parse(trimmed, new JsonDocumentOptions { MaxDepth = 32 });
        return trimmed;
    }

    public void Validate(JsonElement root, IReadOnlySet<string> validIds, IReadOnlySet<string> allowedActions)
    {
        ArgumentNullException.ThrowIfNull(validIds);
        ArgumentNullException.ThrowIfNull(allowedActions);
        if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            throw new InvalidOperationException("The output contract requires a JSON object or array.");
        }

        Visit(root, validIds, allowedActions);
    }

    public void ValidateReferencedIds(JsonElement root, HashSet<string> validIds, string idPropertyName = "itemId")
    {
        if (idPropertyName != "itemId")
        {
            throw new ArgumentException("Task references must use itemId.", nameof(idPropertyName));
        }

        Validate(root, validIds, new HashSet<string>(StringComparer.Ordinal) { "skip" });
    }

    private static void Visit(JsonElement value, IReadOnlySet<string> validIds, IReadOnlySet<string> allowedActions)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                Visit(item, validIds, allowedActions);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.Add(property.Name) || ForbiddenProperties.Contains(property.Name))
            {
                throw new InvalidOperationException("The output contains a duplicate or prohibited field.");
            }

            if (property.Name.Equals("itemId", StringComparison.OrdinalIgnoreCase)
                && (property.Value.ValueKind != JsonValueKind.String || !validIds.Contains(property.Value.GetString()!)))
            {
                throw new InvalidOperationException("The output references an unknown input item.");
            }

            if (property.Name.Equals("actionType", StringComparison.OrdinalIgnoreCase)
                && (property.Value.ValueKind != JsonValueKind.String || !KnownActions.Contains(property.Value.GetString()!) || !allowedActions.Contains(property.Value.GetString()!)))
            {
                throw new InvalidOperationException("The output suggests an action outside the task allowlist.");
            }

            if (property.Name.Equals("isConfirmedByUser", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind != JsonValueKind.False)
            {
                throw new InvalidOperationException("The model cannot grant user confirmation.");
            }

            if ((property.Name.Equals("targetRelativePath", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("destinationRelativePath", StringComparison.OrdinalIgnoreCase))
                && (property.Value.ValueKind != JsonValueKind.String || !IsSafeRelativePath(property.Value.GetString()!)))
            {
                throw new InvalidOperationException("The output contains an unsafe relative path.");
            }

            Visit(property.Value, validIds, allowedActions);
        }
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (value.Length > 260 || value[0] is '/' or '\\' || value.Any(c => c < ' ' || c is ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            return false;
        }

        return value.Split('/', '\\').All(segment => segment.Length > 0 && segment is not "." and not ".."
            && !segment.EndsWith('.') && !segment.EndsWith(' ') && !IsDeviceName(segment));
    }

    private static bool IsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9');
    }
}
