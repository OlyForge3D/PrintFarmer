using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class MqttSubscribeRequest
{
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("qos")]
    public int Qos { get; set; }

    [JsonPropertyName("timeout")]
    public double? Timeout { get; set; }
}
