using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Json;

/// <summary>
/// Converts string "true"/"false" or numeric "1"/"0" to bool.
/// Used in OrcaSlicer profile JSON where boolean fields may be serialized as strings.
/// </summary>
public class StringToBoolJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String => reader.GetString() is "true" or "1",
            JsonTokenType.Number => reader.GetInt32() != 0,
            _ => false
        };
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteBooleanValue(value);
    }
}
