using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintProgress
{
    [JsonPropertyName("completion")]
    public double? Completion { get; set; }

    [JsonPropertyName("filepos")]
    public long? Filepos { get; set; }

    [JsonPropertyName("printTime")]
    public double? PrintTime { get; set; }

    [JsonPropertyName("printTimeLeft")]
    public double? PrintTimeLeft { get; set; }

    [JsonPropertyName("printTimeOrigin")]
    public string? PrintTimeOrigin { get; set; }
}
