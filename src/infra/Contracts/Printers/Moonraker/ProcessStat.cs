using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class ProcessStat
{
    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("cpu_usage")]
    public double CpuUsage { get; set; }

    [JsonPropertyName("memory")]
    public int Memory { get; set; }

    [JsonPropertyName("mem_units")]
    public string MemUnits { get; set; } = string.Empty;
}
