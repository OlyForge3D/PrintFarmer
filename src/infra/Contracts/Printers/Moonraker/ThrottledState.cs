using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class ThrottledState
{
    [JsonPropertyName("bits")]
    public int Bits { get; set; }

    [JsonPropertyName("flags")]
    public string[] Flags { get; set; } = Array.Empty<string>();
}
