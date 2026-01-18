using System.Text.Json;
using System.Text.Json.Serialization;

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
        catch
        {
            return PrinterBackend.Moonraker; // graceful fallback
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
        // Serialize as integer for frontend compatibility
        writer.WriteNumberValue((int)value);
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
        catch
        {
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
                JsonTokenType.Number => reader.TryGetInt32(out int i) ? i != 0 : false,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool ParseString(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? false
            : value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.Ordinal) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBooleanValue(value);
    }
}
