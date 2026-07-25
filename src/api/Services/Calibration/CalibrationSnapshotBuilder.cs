using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Web.Api.Services.Calibration;

internal static class CalibrationSnapshotBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ComputeSha256(object effectiveConfiguration)
    {
        ArgumentNullException.ThrowIfNull(effectiveConfiguration);

        JsonElement element = JsonSerializer.SerializeToElement(
            effectiveConfiguration,
            SerializerOptions);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteCanonical(writer, element);
        }

        byte[] hash = SHA256.HashData(stream.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element
                    .EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long signedInteger))
                {
                    writer.WriteNumberValue(signedInteger);
                }
                else if (element.TryGetUInt64(out ulong unsignedInteger))
                {
                    writer.WriteNumberValue(unsignedInteger);
                }
                else if (element.TryGetDouble(out double floatingPoint))
                {
                    writer.WriteNumberValue(floatingPoint);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Unsupported JSON number '{element.GetRawText()}'.");
                }

                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }
}
