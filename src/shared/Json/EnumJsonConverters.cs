using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Web.Shared.Json;

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
                JsonTokenType.Number => reader.TryGetInt32(out var i) && Enum.IsDefined(typeof(PrinterBackend), i)
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
        if (int.TryParse(value, out var num) && Enum.IsDefined(typeof(PrinterBackend), num))
        {
            return (PrinterBackend)num;
        }
        // case-insensitive name match
        if (Enum.TryParse<PrinterBackend>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }
        return PrinterBackend.Moonraker;
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
                JsonTokenType.Number => reader.TryGetInt32(out var i) && Enum.IsDefined(typeof(PrintJobStatus), i)
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
        if (string.IsNullOrWhiteSpace(value))
        {
            return PrintJobStatus.Queued;
        }

        if (int.TryParse(value, out var num) && Enum.IsDefined(typeof(PrintJobStatus), num))
        {
            return (PrintJobStatus)num;
        }
        if (Enum.TryParse<PrintJobStatus>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }
        return PrintJobStatus.Queued;
    }

    public override void Write(Utf8JsonWriter writer, PrintJobStatus value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
