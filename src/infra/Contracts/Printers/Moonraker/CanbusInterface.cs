using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class CanbusInterface
{
    [JsonPropertyName("tx_queue_len")]
    public int TxQueueLen { get; set; }

    [JsonPropertyName("bitrate")]
    public int Bitrate { get; set; }

    [JsonPropertyName("driver")]
    public string Driver { get; set; } = string.Empty;
}
