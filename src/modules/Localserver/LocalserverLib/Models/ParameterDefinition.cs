namespace LocalServerHub.Core.Models;

/// <summary>
/// How a parameter's value is turned into command-line tokens.
/// </summary>
public enum ParameterType
{
    /// <summary>Presence flag: emitted when true, omitted when false.</summary>
    Boolean,
    String,
    Number,
    /// <summary>One of <see cref="ParameterDefinition.Options"/>.</summary>
    Enum,
    /// <summary>A filesystem path. Same emission as String, different editor.</summary>
    Path,
    /// <summary>Resolved from the encrypted store; never logged or shown in plain text.</summary>
    Secret,
}

/// <summary>
/// Whether the flag and its value are separate tokens or joined by '='.
/// </summary>
public enum ParameterValueStyle
{
    /// <summary><c>--port 3080</c></summary>
    Space,
    /// <summary><c>--port=3080</c></summary>
    Equals,
}

/// <summary>
/// One documented switch of a service. This is the knowledge base entry that
/// drives both the generated UI control and the generated command line
/// (plan.md §3.1).
/// </summary>
public sealed record ParameterDefinition
{
    /// <summary>The literal flag as the target program spells it, e.g. "-noopen" or "--port".</summary>
    public required string Flag { get; init; }

    /// <summary>Human label shown in the parameter form.</summary>
    public required string Name { get; init; }

    public ParameterType Type { get; init; } = ParameterType.Boolean;

    /// <summary>Default value, in the JSON-native type for <see cref="Type"/>.</summary>
    public object? Default { get; init; }

    public ParameterValueStyle Style { get; init; } = ParameterValueStyle.Space;

    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>Allowed values when <see cref="Type"/> is <see cref="ParameterType.Enum"/>.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>
    /// Flags that cannot be used together with this one. Enabling this flag clears
    /// them in the UI instead of letting the user build a command that fails.
    /// </summary>
    public IReadOnlyList<string> ConflictsWith { get; init; } = [];

    /// <summary>Flags that must also be present for this one to be meaningful.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>
    /// Binds this parameter to a well-known service field so the rest of the app
    /// can follow it. "port" keeps the health check URL and the open-in-browser
    /// link in sync with whatever the user typed.
    /// </summary>
    public string? BindsTo { get; init; }

    public string? Description { get; init; }

    /// <summary>False for switches the user should not touch from the UI.</summary>
    public bool IsEditable { get; init; } = true;
}

/// <summary>
/// A named bundle of parameter values, e.g. "debug logging on port 8080".
/// </summary>
public sealed record ServicePreset
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Flag to value. Flags absent here fall back to their defaults.</summary>
    public IReadOnlyDictionary<string, object?> Args { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}
