using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class TemperatureData
{
    [JsonPropertyName("temperatures")]
    public Dictionary<string, double[][]> Temperatures { get; set; } = new Dictionary<string, double[][]>();

    [JsonPropertyName("targets")]
    public Dictionary<string, double[][]> Targets { get; set; } = new Dictionary<string, double[][]>();
}

#pragma warning restore CA2227
