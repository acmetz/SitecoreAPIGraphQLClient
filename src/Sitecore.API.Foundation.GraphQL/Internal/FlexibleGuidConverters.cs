using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sitecore.API.Foundation.GraphQL.Internal;

/// <summary>
/// System.Text.Json converter that flexibly parses Guid values from standard JSON tokens.
/// Prefers early returns to keep control flow simple and readable.
/// </summary>
internal sealed class FlexibleGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Fast-path: strings are the most common representation for GUIDs
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (!string.IsNullOrEmpty(s) && Guid.TryParse(s, out var g))
            {
                return g;
            }

            throw new JsonException("The JSON value is not a valid GUID string.");
        }

        // Null cannot be mapped to non-nullable Guid
        if (reader.TokenType == JsonTokenType.Null)
        {
            throw new JsonException("Cannot convert null to Guid.");
        }

        // Fallback: let the reader handle canonical binary/number formats
        return reader.GetGuid();
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

/// <summary>
/// System.Text.Json converter that flexibly parses nullable Guid values.
/// Uses guard clauses and early returns for clarity.
/// </summary>
internal sealed class FlexibleNullableGuidConverter : JsonConverter<Guid?>
{
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Null maps directly to null Guid
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        // String: allow empty => null, otherwise try Guid.TryParse
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrEmpty(s))
            {
                return null;
            }

            if (Guid.TryParse(s, out var g))
            {
                return g;
            }

            throw new JsonException("The JSON value is not a valid GUID string.");
        }

        // Fallback to native reader for supported non-string formats
        return reader.GetGuid();
    }

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteStringValue(value.Value);
            return;
        }

        writer.WriteNullValue();
    }
}
