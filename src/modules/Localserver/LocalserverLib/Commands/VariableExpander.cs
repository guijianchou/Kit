using System.Runtime.Versioning;
using System.Text;
using LocalServerHub.Core.Configuration;
using LocalServerHub.Core.Models;

namespace LocalServerHub.Core.Commands;

/// <summary>
/// Expands <c>${...}</c> placeholders in environment values, health URLs and
/// open-in-browser links.
/// </summary>
/// <remarks>
/// Supported names are <c>service.id</c>, <c>service.name</c>, <c>service.cwd</c>,
/// <c>service.port</c>, <c>env.NAME</c> and <c>secret.NAME</c>. Anything else is
/// left untouched and reported, because silently swallowing a typo like
/// <c>${service.prot}</c> is how a service ends up probing the wrong URL forever.
/// </remarks>
public sealed class VariableExpander
{
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly SecretStore? _secretStore;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly HashSet<string> _expandingEnvironment = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _resolvedSecrets = [];

    public VariableExpander(IReadOnlyDictionary<string, string> values, SecretStore? secretStore = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
        _secretStore = secretStore;
        _environment = environment is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
    }

    public static VariableExpander ForService(ServiceDefinition definition, int? port, SecretStore? secrets = null) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["service.id"] = definition.Id,
            ["service.name"] = definition.Name,
            ["service.cwd"] = definition.Cwd,
            ["service.port"] = port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        }, secrets, definition.Env);

    public IReadOnlyList<string> ResolvedSecrets => _resolvedSecrets;

    [SupportedOSPlatform("windows")]
    public string ExpandRequired(string? template)
    {
        string result = Expand(template);
        if (_unresolved.Count > 0)
        {
            throw new InvalidOperationException("A configuration variable is missing, cyclic, or malformed. Check service, env and secret references.");
        }
        return result;
    }

    /// <summary>Names that were referenced but not defined, in encounter order.</summary>
    public IReadOnlyList<string> Unresolved => _unresolved;

    private readonly List<string> _unresolved = [];

    [SupportedOSPlatform("windows")]
    public string Expand(string? template)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("${", StringComparison.Ordinal))
        {
            return template ?? string.Empty;
        }

        StringBuilder result = new(template.Length);
        int index = 0;
        while (index < template.Length)
        {
            int start = template.IndexOf("${", index, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(template, index, template.Length - index);
                break;
            }

            int end = template.IndexOf('}', start + 2);
            if (end < 0)
            {
                _unresolved.Add("unterminated placeholder");
                result.Append(template, index, template.Length - index);
                break;
            }

            result.Append(template, index, start - index);
            string name = template[(start + 2)..end];

            // Try secret store first if the name starts with "secret."
            if (name.StartsWith("secret.", StringComparison.OrdinalIgnoreCase))
            {
                string secretName = name["secret.".Length..];
                string? secretValue = string.IsNullOrWhiteSpace(secretName) ? null : _secretStore?.Get(secretName);
                if (secretValue != null)
                {
                    if (secretValue.Length > 0 && !_resolvedSecrets.Contains(secretValue, StringComparer.Ordinal))
                        _resolvedSecrets.Add(secretValue);
                    result.Append(secretValue);
                    index = end + 1;
                    continue;
                }
                // Fall through to unresolved if secret not found.
            }

            if (name.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
            {
                string environmentName = name["env.".Length..];
                string? environmentValue = null;
                if (_environment.TryGetValue(environmentName, out string? configured))
                {
                    if (_expandingEnvironment.Add(environmentName))
                    {
                        try { environmentValue = Expand(configured); }
                        finally { _expandingEnvironment.Remove(environmentName); }
                    }
                    else
                    {
                        _unresolved.Add(name);
                    }
                }
                else if (!string.IsNullOrEmpty(environmentName))
                {
                    environmentValue = Environment.GetEnvironmentVariable(environmentName);
                }
                if (environmentValue is not null)
                {
                    result.Append(environmentValue);
                    index = end + 1;
                    continue;
                }
            }

            if (_values.TryGetValue(name, out string? value))
            {
                result.Append(value);
            }
            else
            {
                if (!_unresolved.Contains(name, StringComparer.Ordinal))
                {
                    _unresolved.Add(name);
                }

                // Keep the placeholder so the broken value is visible rather than
                // turning into an empty string that looks plausible.
                result.Append(template, start, end - start + 1);
            }

            index = end + 1;
        }

        return result.ToString();
    }
}
