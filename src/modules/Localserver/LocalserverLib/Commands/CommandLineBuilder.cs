using System.Globalization;
using System.Text;
using System.Text.Json;
using LocalServerHub.Core.Models;

namespace LocalServerHub.Core.Commands;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>One problem found while assembling a command line.</summary>
public sealed record CommandDiagnostic(DiagnosticSeverity Severity, string Flag, string Message);

/// <summary>
/// The outcome of turning a service definition plus a set of parameter values
/// into something runnable.
/// </summary>
public sealed record CommandPlan
{
    /// <summary>Program to execute, resolved against the service's cwd.</summary>
    public required string FileName { get; init; }

    /// <summary>Arguments as discrete tokens. Never re-parsed from a string.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Indexes of argument tokens that must be masked when displayed.</summary>
    public IReadOnlySet<int> SecretArgumentIndexes { get; init; } = new HashSet<int>();

    /// <summary>Secret values used only to redact child-process output.</summary>
    public IReadOnlyList<string> SecretValues { get; init; } = [];

    /// <summary>Effective port after parameter binding, or null when the service has none.</summary>
    public int? Port { get; init; }

    public IReadOnlyList<CommandDiagnostic> Diagnostics { get; init; } = [];

    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Turns a service's parameter knowledge base into an argv list (plan.md §3.1).
/// </summary>
/// <remarks>
/// Two rules matter here and are enforced rather than documented:
/// arguments are always produced as a token list and never as a single string
/// that something else re-splits; and the string shown to the user for copying
/// is produced by a separate function, so "what I see" and "what runs" can never
/// drift apart (plan.md §4.5).
/// </remarks>
public static class CommandLineBuilder
{
    /// <summary>
    /// Resolves effective parameter values, lowest precedence first: the
    /// parameter's own default, then <see cref="ServiceDefinition.DefaultPort"/>
    /// for whichever parameter binds to the port, then the preset (when named),
    /// then the caller's overrides.
    /// </summary>
    /// <remarks>
    /// DefaultPort outranks the parameter default on purpose. It is the field the
    /// settings window edits, so if it lost to the knowledge-base default the PORT
    /// column would claim a port the service never listens on.
    /// </remarks>
    public static Dictionary<string, object?> ResolveValues(
        ServiceDefinition definition,
        string? presetName = null,
        IReadOnlyDictionary<string, object?>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (ParameterDefinition parameter in definition.Parameters)
        {
            values[parameter.Flag] = parameter.Default;
        }

        if (definition.DefaultPort is { } defaultPort)
        {
            foreach (ParameterDefinition parameter in definition.Parameters
                .Where(p => string.Equals(p.BindsTo, "port", StringComparison.OrdinalIgnoreCase)))
            {
                values[parameter.Flag] = defaultPort;
            }
        }

        if (!string.IsNullOrEmpty(presetName))
        {
            ServicePreset? preset = definition.Presets
                .FirstOrDefault(p => string.Equals(p.Name, presetName, StringComparison.Ordinal));
            if (preset is not null)
            {
                foreach ((string flag, object? value) in preset.Args)
                {
                    values[flag] = value;
                }
            }
        }

        if (overrides is not null)
        {
            foreach ((string flag, object? value) in overrides)
            {
                values[flag] = value;
            }
        }

        return values;
    }

    public static CommandPlan Build(
        ServiceDefinition definition,
        IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(values);

        List<CommandDiagnostic> diagnostics = [];
        List<string> arguments = [.. definition.BaseArgs];
        HashSet<int> secretArgumentIndexes = [];
        List<string> secretValues = [];
        int? port = definition.DefaultPort;

        foreach (ParameterDefinition parameter in definition.Parameters)
        {
            values.TryGetValue(parameter.Flag, out object? raw);
            if (!IsPresent(parameter, raw))
            {
                continue;
            }

            ValidateRelations(parameter, values, diagnostics);

            if (parameter.Type == ParameterType.Boolean)
            {
                arguments.Add(parameter.Flag);
                continue;
            }

            string? text = FormatValue(parameter, raw, diagnostics);
            if (text is null)
            {
                continue;
            }

            if (string.Equals(parameter.BindsTo, "port", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort))
            {
                port = parsedPort;
            }

            if (parameter.Style == ParameterValueStyle.Equals)
            {
                arguments.Add($"{parameter.Flag}={text}");
                if (parameter.Type == ParameterType.Secret)
                {
                    secretArgumentIndexes.Add(arguments.Count - 1);
                    secretValues.Add(text);
                }
            }
            else
            {
                arguments.Add(parameter.Flag);
                arguments.Add(text);
                if (parameter.Type == ParameterType.Secret)
                {
                    secretArgumentIndexes.Add(arguments.Count - 1);
                    secretValues.Add(text);
                }
            }
        }

        return new CommandPlan
        {
            FileName = definition.Executable,
            Arguments = arguments,
            SecretArgumentIndexes = secretArgumentIndexes,
            SecretValues = secretValues,
            Port = port,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Formats a plan for display and copy-paste only. This is deliberately NOT
    /// what gets executed.
    /// </summary>
    public static string ToDisplayCommand(CommandPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ToDisplayCommand(plan.FileName, plan.Arguments, plan.SecretArgumentIndexes);
    }

    /// <summary>
    /// Formats an arbitrary program plus argv for display. Exists so a caller that
    /// knows more than the plan does - the launcher, which may have introduced an
    /// interpreter - can render the command that will really run without
    /// hand-joining tokens and losing quoting or secret masking.
    /// </summary>
    /// <param name="secretArgumentIndexes">Indexes into <paramref name="arguments"/> to mask.</param>
    public static string ToDisplayCommand(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlySet<int>? secretArgumentIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        StringBuilder builder = new();
        builder.Append(QuoteForDisplay(fileName ?? string.Empty));
        for (int index = 0; index < arguments.Count; index++)
        {
            bool isSecret = secretArgumentIndexes?.Contains(index) == true;
            string argument = isSecret ? "***" : arguments[index];
            builder.Append(' ').Append(QuoteForDisplay(argument));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reduces a value to a CLR primitive.
    /// </summary>
    /// <remarks>
    /// Values typed <c>object?</c> normally arrive already unwrapped, because the
    /// catalog store installs a converter for them. This is the backstop for any
    /// other route in - a caller with its own serializer options, or a definition
    /// built by hand. A JsonElement reaching the checks below would fail silently
    /// rather than loudly, which is exactly the failure that made this necessary.
    /// </remarks>
    private static object? Unwrap(object? raw) => raw switch
    {
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            // Cast so the arms do not unify to double; see JsonPrimitiveConverter.
            JsonValueKind.Number => element.TryGetInt64(out long integral)
                ? (object)integral
                : element.GetDouble(),
            _ => element.ToString(),
        },
        _ => raw,
    };

    /// <summary>A boolean is "present" only when true; other types when non-empty.</summary>
    private static bool IsPresent(ParameterDefinition parameter, object? raw)
    {
        object? value = Unwrap(raw);
        if (value is null)
        {
            return false;
        }

        if (parameter.Type == ParameterType.Boolean)
        {
            return value switch
            {
                bool flag => flag,
                string text => bool.TryParse(text, out bool parsed) && parsed,
                _ => false,
            };
        }

        return value is not string text2 || text2.Length > 0;
    }

    private static void ValidateRelations(
        ParameterDefinition parameter,
        IReadOnlyDictionary<string, object?> values,
        List<CommandDiagnostic> diagnostics)
    {
        foreach (string other in parameter.ConflictsWith)
        {
            if (values.TryGetValue(other, out object? otherValue)
                && IsTruthy(otherValue))
            {
                diagnostics.Add(new CommandDiagnostic(
                    DiagnosticSeverity.Error,
                    parameter.Flag,
                    $"{parameter.Flag} cannot be combined with {other}."));
            }
        }

        foreach (string required in parameter.Requires)
        {
            if (!values.TryGetValue(required, out object? requiredValue) || !IsTruthy(requiredValue))
            {
                diagnostics.Add(new CommandDiagnostic(
                    DiagnosticSeverity.Error,
                    parameter.Flag,
                    $"{parameter.Flag} requires {required} to be set."));
            }
        }
    }

    private static bool IsTruthy(object? value) => Unwrap(value) switch
    {
        null => false,
        bool flag => flag,
        string text => text.Length > 0 && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static string? FormatValue(
        ParameterDefinition parameter,
        object? raw,
        List<CommandDiagnostic> diagnostics)
    {
        object? value = Unwrap(raw);
        string text = value switch
        {
            null => string.Empty,
            string stringValue => stringValue,
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        if (parameter.Type == ParameterType.Number)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                diagnostics.Add(new CommandDiagnostic(
                    DiagnosticSeverity.Error,
                    parameter.Flag,
                    $"{parameter.Flag} expects a number but got '{text}'."));
                return null;
            }

            if (parameter.Min is { } min && number < min)
            {
                diagnostics.Add(new CommandDiagnostic(
                    DiagnosticSeverity.Error,
                    parameter.Flag,
                    $"{parameter.Flag} must be at least {min.ToString(CultureInfo.InvariantCulture)}."));
                return null;
            }

            if (parameter.Max is { } max && number > max)
            {
                diagnostics.Add(new CommandDiagnostic(
                    DiagnosticSeverity.Error,
                    parameter.Flag,
                    $"{parameter.Flag} must be at most {max.ToString(CultureInfo.InvariantCulture)}."));
                return null;
            }
        }

        if (parameter.Type == ParameterType.Enum
            && parameter.Options.Count > 0
            && !parameter.Options.Contains(text, StringComparer.Ordinal))
        {
            diagnostics.Add(new CommandDiagnostic(
                DiagnosticSeverity.Error,
                parameter.Flag,
                $"{parameter.Flag} must be one of: {string.Join(", ", parameter.Options)}."));
            return null;
        }

        return text;
    }

    /// <summary>
    /// Shared with the settings editor so a command shown here and a token list
    /// edited there quote identically.
    /// </summary>
    private static string QuoteForDisplay(string token) => ArgumentText.Quote(token);
}
