namespace Kit.AiHub.Security;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Removes credentials and personal profile paths before model dispatch.
/// </summary>
public sealed partial class DataSanitizer
{
    private const string RedactedSecret = "[REDACTED_SECRET]";
    private const int MaximumTextLength = 1024 * 1024;
    private const int MaximumJsonBytes = 4 * 1024 * 1024;
    private const int MaximumJsonDepth = 64;
    private static readonly HashSet<string> SecretFields = new(StringComparer.Ordinal)
    {
        "apikey", "xapikey", "password", "passwd", "pwd", "token", "tokens",
        "accesstoken", "refreshtoken", "idtoken", "sessiontoken", "authtoken", "apitoken",
        "bearertoken", "csrftoken", "xsrftoken", "authorization", "proxyauthorization",
        "cookie", "cookies", "setcookie", "privatekey", "secret", "secrets", "clientsecret",
        "accesskey", "accesskeyid", "secretkey", "secretaccesskey", "subscriptionkey",
        "credential", "credentials", "connectionstring", "connectionstrings",
    };
    private readonly string _userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Returns an independent JSON value while preserving its structure and scalar types.
    /// Sensitive fields are redacted after JSON escape sequences have been decoded.
    /// </summary>
    public JsonElement SanitizeJson(JsonElement value)
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteSanitized(value, writer, redact: false, depth: 0);
            }

            using var document = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            throw new InvalidDataException("The AI Hub input could not be sanitized.");
        }
    }

    /// <summary>
    /// Sanitizes decoded strings, including credential assignments, private keys, and URLs.
    /// </summary>
    public string SanitizeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length > MaximumTextLength)
        {
            throw new InvalidDataException("The AI Hub text exceeds its sanitization limit.");
        }

        try
        {
            string result = PrivateKeyPattern().Replace(text, RedactedSecret);
            result = UrlCredentialsPattern().Replace(result, match => match.Groups["scheme"].Value + RedactedSecret + "@");
            result = UrlQuerySecretPattern().Replace(result, match => match.Groups["label"].Value + RedactedSecret);
            result = CredentialAssignmentPattern().Replace(result, match => match.Groups["label"].Value + RedactedSecret);
            result = AuthorizationHeaderPattern().Replace(result, match => match.Groups["label"].Value + " " + RedactedSecret);
            result = BearerPattern().Replace(result, RedactedSecret);
            result = KnownTokenPattern().Replace(result, RedactedSecret);
            result = UserPathPattern().Replace(result, "%USERPROFILE%");

            if (!string.IsNullOrEmpty(_userProfilePath))
            {
                result = ReplaceProfilePath(result, _userProfilePath);
                result = ReplaceProfilePath(result, _userProfilePath.Replace('\\', '/'));
                result = ReplaceProfilePath(result, _userProfilePath.Replace(@"\", @"\\"));
            }

            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            throw new InvalidDataException("The AI Hub text could not be sanitized within its time limit.");
        }
    }

    private void WriteSanitized(JsonElement value, Utf8JsonWriter writer, bool redact, int depth)
    {
        if (depth > MaximumJsonDepth)
        {
            throw new InvalidDataException("The AI Hub JSON exceeds its nesting limit.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Name.Length > MaximumTextLength)
                    {
                        throw new InvalidDataException("An AI Hub JSON property exceeds its size limit.");
                    }

                    writer.WritePropertyName(property.Name);
                    WriteSanitized(property.Value, writer, redact || IsSecretField(property.Name), depth + 1);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteSanitized(item, writer, redact, depth + 1);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(redact ? RedactedSecret : SanitizeText(value.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Number:
                if (redact)
                {
                    writer.WriteNumberValue(0);
                }
                else
                {
                    value.WriteTo(writer);
                }

                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(!redact && value.GetBoolean());
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("The AI Hub JSON value is invalid.");
        }

        if (writer.BytesCommitted + writer.BytesPending > MaximumJsonBytes)
        {
            throw new InvalidDataException("The AI Hub JSON exceeds its sanitization limit.");
        }
    }

    private static bool IsSecretField(string name)
    {
        var normalized = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
        }

        string field = normalized.ToString();
        return SecretFields.Contains(field) ||
            field.EndsWith("apikey", StringComparison.Ordinal) ||
            field.EndsWith("privatekey", StringComparison.Ordinal) ||
            field.EndsWith("password", StringComparison.Ordinal) ||
            field.EndsWith("clientsecret", StringComparison.Ordinal);
    }

    private static string ReplaceProfilePath(string text, string profilePath)
    {
        int start = 0;
        int index = text.IndexOf(profilePath, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        while (index >= 0)
        {
            int end = index + profilePath.Length;
            bool boundary = end == text.Length || (!char.IsLetterOrDigit(text[end]) && text[end] is not ('_' or '-' or '.'));
            result.Append(text, start, index - start);
            result.Append(boundary ? "%USERPROFILE%" : text[index..end]);
            start = end;
            index = text.IndexOf(profilePath, start, StringComparison.OrdinalIgnoreCase);
        }

        result.Append(text, start, text.Length - start);
        return result.ToString();
    }

    [GeneratedRegex(@"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----[\s\S]*?(?:-----END (?:[A-Z0-9]+ )*PRIVATE KEY-----|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^/\s?#]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex UrlCredentialsPattern();

    [GeneratedRegex(@"(?<label>[?&](?:api[-_]?key|(?:access|refresh|id|auth)[-_]?token|token|key|sig|signature|code|secret|password)=)[^&#\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex UrlQuerySecretPattern();

    [GeneratedRegex(@"(?<![a-z0-9])(?<label>[""']?(?:api[-_ ]?key|x[-_ ]?api[-_ ]?key|password|passwd|pwd|(?:access|refresh|session|auth|bearer|id|csrf|xsrf)[-_ ]?token|token|(?:client[-_ ]?)?secret|private[-_ ]?key|secret[-_ ]?access[-_ ]?key|access[-_ ]?key(?:[-_ ]?id)?|authorization|proxy[-_ ]?authorization|cookies?|set[-_ ]?cookie)[""']?[ \t]*[:=][ \t]*)(?:\[REDACTED_SECRET\]|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[^\s,;""'&}\]]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex CredentialAssignmentPattern();

    [GeneratedRegex(@"\b(?<label>(?:authorization|proxy[-_ ]?authorization|cookie|set[-_ ]?cookie)[ \t]*[:=])[ \t]*[^\r\n]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex AuthorizationHeaderPattern();

    [GeneratedRegex(@"\b(?:bearer|basic)[ \t]+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\b(?:sk-[a-z0-9_-]{8,}|(?:gh[pousr]_|github_pat_)[a-z0-9_]{8,}|xox[baprs]-[a-z0-9-]{8,}|eyJ[a-z0-9_-]+\.[a-z0-9_-]+\.[a-z0-9_-]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex KnownTokenPattern();

    [GeneratedRegex(@"(?<![a-z0-9])(?:[a-z]:[\\/]+(?:Users|Documents and Settings)[\\/]+|[\\/]+Users[\\/]+|/(?:home|Users)/)[^\\/\r\n""'<>|:*?]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex UserPathPattern();

    /// <summary>
    /// Masks a file or item name while preserving its extension.
    /// </summary>
    public string DesensitizeName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        string extension = Path.GetExtension(rawName);
        string nameWithoutExt = Path.GetFileNameWithoutExtension(rawName);
        if (nameWithoutExt.Length <= 4)
        {
            return $"***{extension}";
        }

        return $"{nameWithoutExt[..2]}***{nameWithoutExt[^2..]}{extension}";
    }

    /// <summary>
    /// Builds stable opaque references and bidirectional lookup for a collection of items.
    /// </summary>
    public (Dictionary<string, string> ForwardMap, Dictionary<string, string> ReverseMap) CreateOpaqueMapping(
        IEnumerable<string> rawIds,
        string prefix = "item")
    {
        var forward = new Dictionary<string, string>(StringComparer.Ordinal);
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        int index = 1;
        foreach (var rawId in rawIds)
        {
            if (string.IsNullOrWhiteSpace(rawId) || forward.ContainsKey(rawId))
            {
                continue;
            }

            string opaqueId = $"{prefix}-{index:D6}";
            forward[rawId] = opaqueId;
            reverse[opaqueId] = rawId;
            index++;
        }

        return (forward, reverse);
    }
}
