using System;
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
        if (string.IsNullOrWhiteSpace(value)) return PrinterBackend.Moonraker;
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
        // Preserve existing string enum behavior (exact enum name)
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Permissive converter for PrintJobStatusDto; mirrors PrinterBackendJsonConverter behavior.
/// </summary>
public sealed class PrintJobStatusDtoJsonConverter : JsonConverter<PrintJobStatusDto>
{
    public override PrintJobStatusDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => ParseString(reader.GetString()),
                JsonTokenType.Number => reader.TryGetInt32(out var i) && Enum.IsDefined(typeof(PrintJobStatusDto), i)
                    ? (PrintJobStatusDto)i : PrintJobStatusDto.Queued,
                _ => PrintJobStatusDto.Queued
            };
        }
        catch
        {
            return PrintJobStatusDto.Queued;
        }
    }

    private static PrintJobStatusDto ParseString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PrintJobStatusDto.Queued;
        }
        if (int.TryParse(value, out var num) && Enum.IsDefined(typeof(PrintJobStatusDto), num))
        {
            return (PrintJobStatusDto)num;
        }
        if (Enum.TryParse<PrintJobStatusDto>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }
        return PrintJobStatusDto.Queued;
    }

    public override void Write(Utf8JsonWriter writer, PrintJobStatusDto value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
