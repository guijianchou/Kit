using System.Text;

namespace LocalServerHub.Core.Commands;

/// <summary>
/// Converts between an argv token list and the one editable string a text box can
/// hold.
/// </summary>
/// <remarks>
/// Arguments are stored and executed as discrete tokens (plan.md §3.1) and must
/// never be rebuilt by splitting a string somewhere else. An editor still needs a
/// string, so the conversion lives here and only here, and it round-trips: a token
/// containing a space comes back as one token, not two. <see cref="Quote"/> is also
/// what <see cref="CommandLineBuilder"/> uses to render a command for display, so
/// the editor and the readout agree on what quoting looks like.
/// </remarks>
public static class ArgumentText
{
    /// <summary>Wraps a token in quotes only when it would otherwise re-split.</summary>
    public static string Quote(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (token.Length > 0 && token.All(c => c != ' ' && c != '"' && c != '\t'))
        {
            return token;
        }

        StringBuilder quoted = new("\"");
        int backslashes = 0;
        foreach (char c in token)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            quoted.Append(c);
            backslashes = 0;
        }

        // Backslashes before a closing quote must be doubled under Windows argv rules.
        quoted.Append('\\', backslashes * 2);
        return quoted.Append('"').ToString();
    }

    /// <summary>Renders tokens as a single editable line.</summary>
    public static string Format(IEnumerable<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return string.Join(' ', tokens.Select(Quote));
    }

    /// <summary>
    /// Splits an edited line back into tokens. Unterminated quotes are tolerated -
    /// the user is mid-typing - and yield the rest of the line as one token.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<string> tokens = [];
        StringBuilder current = new();
        bool inQuotes = false;
        bool hasContent = false;

        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];

            if (c == '\\')
            {
                int start = index;
                while (index < text.Length && text[index] == '\\') index++;
                int count = index - start;
                if (index < text.Length && text[index] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 0) inQuotes = !inQuotes;
                    else current.Append('"');
                }
                else
                {
                    current.Append('\\', count);
                    index--;
                }
                hasContent = true;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                // An empty "" is a real, intentional empty argument.
                hasContent = true;
                continue;
            }

            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasContent)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasContent = false;
                }

                continue;
            }

            current.Append(c);
            hasContent = true;
        }

        if (hasContent)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
