using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Json;

/// <summary>
/// Permissive converter for PrinterBackend enum. Accepts:
///  - Exact enum names (case-insensitive)
///  - Lowercase names ("moonraker")
///  - Integer values (0,1,2)
///  - String-wrapped integers ("0")
/// Falls back to Moonraker if unrecognized to avoid breaking older test data.
/// </summary>
public sealed class PrinterBackendJsonConverter : JsonConverter<PrinterBackend>
{
    public override PrinterBackend Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => ParseString(reader.GetString()),
                JsonTokenType.Number => reader.TryGetInt32(out int i) && Enum.IsDefined(typeof(PrinterBackend), i)
                    ? (PrinterBackend)i : PrinterBackend.Moonraker,
                _ => PrinterBackend.Moonraker
            };
        }
        catch (JsonException)
        {
            return PrinterBackend.Moonraker; // graceful fallback
        }
        catch (InvalidOperationException)
        {
            // e.g. malformed UTF-16 surrogate content in the string token
            return PrinterBackend.Moonraker;
        }
    }

    private static PrinterBackend ParseString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PrinterBackend.Moonraker;
        }

        // numeric-as-string
        if (int.TryParse(value, out int num) && Enum.IsDefined(typeof(PrinterBackend), num))
        {
            return (PrinterBackend)num;
        }

        // case-insensitive name match
        return Enum.TryParse(value, ignoreCase: true, out PrinterBackend parsed) ? parsed : PrinterBackend.Moonraker;
    }

    public override void Write(Utf8JsonWriter writer, PrinterBackend value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Serialize as string name for frontend compatibility (matches TypeScript PrinterBackendString)
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Permissive converter for PrintJobStatus; mirrors PrinterBackendJsonConverter behavior.
/// </summary>
public sealed class PrintJobStatusJsonConverter : JsonConverter<PrintJobStatus>
{
    public override PrintJobStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => ParseString(reader.GetString()),
                JsonTokenType.Number => reader.TryGetInt32(out int i) && Enum.IsDefined(typeof(PrintJobStatus), i)
                    ? (PrintJobStatus)i : PrintJobStatus.Queued,
                _ => PrintJobStatus.Queued
            };
        }
        catch (JsonException)
        {
            return PrintJobStatus.Queued;
        }
        catch (InvalidOperationException)
        {
            // e.g. malformed UTF-16 surrogate content in the string token
            return PrintJobStatus.Queued;
        }
    }

    private static PrintJobStatus ParseString(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? PrintJobStatus.Queued
            : int.TryParse(value, out int num) && Enum.IsDefined(typeof(PrintJobStatus), num)
            ? (PrintJobStatus)num
            : Enum.TryParse(value, ignoreCase: true, out PrintJobStatus parsed) ? parsed : PrintJobStatus.Queued;
    }

    public override void Write(Utf8JsonWriter writer, PrintJobStatus value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Backward-compatible converter for the <c>NozzleModelExportDto.NozzleInterface</c> export
/// field (epic #1823 / issue #1826). The field itself is a plain <c>string?</c> holding the
/// <see cref="NozzleInterfaceType"/> member name, matching the name-based treatment already
/// given to <c>NozzleType</c>/<c>HardnessOverride</c> exports. Pre-existing export backups
/// wrote this field as a raw JSON number (the enum ordinal); this converter accepts either
/// shape on read so old backups keep restoring, and always writes the string form.
/// </summary>
public sealed class NozzleInterfaceExportJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                // Legacy backup: raw ordinal. Translate to the member name when it maps to a
                // known value; otherwise keep the numeric text so downstream validation can
                // reject it explicitly rather than silently coercing to a default.
                if (reader.TryGetInt32(out int ordinal) && Enum.IsDefined(typeof(NozzleInterfaceType), ordinal))
                {
                    return ((NozzleInterfaceType)ordinal).ToString();
                }

                // Doesn't fit Int32, or isn't a defined ordinal (e.g. overflow, "3.5", or an
                // out-of-range integer): preserve the raw numeric text so downstream validation
                // (TryParseExportedEnum's numeric-string reject path) still sees a non-empty,
                // non-name value and rejects the row explicitly, rather than this converter
                // returning null and the row silently defaulting. ValueSpan is only valid when
                // the reader isn't backed by a multi-segment buffer (e.g. large HTTP request
                // bodies read off a PipeReader); fall back to ValueSequence otherwise.
                return reader.HasValueSequence
                    ? System.Text.Encoding.UTF8.GetString(System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence))
                    : System.Text.Encoding.UTF8.GetString(reader.ValueSpan);
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

/// <summary>
/// Permissive converter for bool that accepts string representations ("true", "false", "True", "False", "1", "0").
/// Used for OrcaSlicer profile JSON where "instantiation": "true" is a string value.
/// </summary>
public sealed class StringToBoolJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.String => ParseString(reader.GetString()),
                JsonTokenType.Number => reader.TryGetInt32(out int i) && i != 0,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // e.g. malformed UTF-16 surrogate content in the string token
            return false;
        }
    }

    private static bool ParseString(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.Ordinal) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBooleanValue(value);
    }
}
