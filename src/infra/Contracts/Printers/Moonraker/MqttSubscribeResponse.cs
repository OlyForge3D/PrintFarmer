using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class MqttSubscribeResponse
{
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public string Payload { get; set; } = string.Empty;
}
