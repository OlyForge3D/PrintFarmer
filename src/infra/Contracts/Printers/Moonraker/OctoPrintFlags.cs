using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintFlags
{
    [JsonPropertyName("operational")]
    public bool Operational { get; set; }

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    [JsonPropertyName("printing")]
    public bool Printing { get; set; }

    [JsonPropertyName("cancelling")]
    public bool Cancelling { get; set; }

    [JsonPropertyName("pausing")]
    public bool Pausing { get; set; }

    [JsonPropertyName("error")]
    public bool Error { get; set; }

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("closedOrError")]
    public bool ClosedOrError { get; set; }
}
