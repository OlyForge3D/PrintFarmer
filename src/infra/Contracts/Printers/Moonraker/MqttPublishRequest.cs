using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// MQTT Models
public class MqttPublishRequest
{
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }

    [JsonPropertyName("qos")]
    public int Qos { get; set; }

    [JsonPropertyName("retain")]
    public bool Retain { get; set; }

    [JsonPropertyName("timeout")]
    public double Timeout { get; set; } = 5.0;
}
