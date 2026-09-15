using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalServerHub.Core.Configuration;

/// <summary>
/// Reads loosely-typed JSON values into plain CLR primitives instead of
/// <see cref="JsonElement"/>.
/// </summary>
/// <remarks>
/// <para>
/// A parameter's default and a preset's argument values are declared as
/// <c>object?</c>, because their type is whatever the flag needs. System.Text.Json's
/// default behaviour for <c>object</c> is to hand back a <see cref="JsonElement"/>,
/// which then has to be understood by every consumer downstream.
/// </para>
/// <para>
/// That went wrong in a way worth recording: a boolean flag declared
/// <c>"default": true</c> never appeared on the command line, because the code
/// deciding whether a switch is present matched <c>bool</c> and <c>string</c> and a
/// JsonElement is neither. Numbers and strings survived only by accident, via
/// ToString(). Unwrapping here means nothing after this point has to know
/// JsonElement exists.
/// </para>
/// </remarks>
public sealed class JsonPrimitiveConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            // Integers stay integral so a port renders as "8080" and not "8080.0",
            // and so a large value never reaches a command line as "1E+21". The cast
            // is load-bearing: without it both arms unify to double and the integral
            // branch is widened away.
            JsonTokenType.Number => reader.TryGetInt64(out long integral)
                ? (object)integral
                : reader.GetDouble(),
            // Objects and arrays have no meaning as a flag value, but discarding them
            // would silently drop hand-written config. They are kept as-is.
            _ => JsonElement.ParseValue(ref reader),
        };

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case long integral:
                writer.WriteNumberValue(integral);
                break;
            case int integral:
                writer.WriteNumberValue(integral);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                // Serialising by runtime type would re-enter this converter and
                // recurse; the concrete type is passed explicitly instead.
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                break;
        }
    }
}
