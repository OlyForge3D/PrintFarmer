using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintJobResponse
{
    [JsonPropertyName("job")]
    public OctoPrintJob Job { get; set; } = new();

    [JsonPropertyName("progress")]
    public OctoPrintProgress Progress { get; set; } = new();

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;
}
