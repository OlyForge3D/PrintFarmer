using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class ProcessStats
{
    [JsonPropertyName("moonraker_stats")]
    public ProcessStat[] MoonrakerStats { get; set; } = Array.Empty<ProcessStat>();

    [JsonPropertyName("throttled_state")]
    public ThrottledState? ThrottledState { get; set; }

    [JsonPropertyName("cpu_temp")]
    public double? CpuTemp { get; set; }

    [JsonPropertyName("network")]
    public Dictionary<string, NetworkStats> Network { get; set; } = new Dictionary<string, NetworkStats>();

    [JsonPropertyName("system_cpu_usage")]
    public Dictionary<string, double> SystemCpuUsage { get; set; } = new Dictionary<string, double>();

    [JsonPropertyName("system_uptime")]
    public double SystemUptime { get; set; }

    [JsonPropertyName("websocket_connections")]
    public int WebsocketConnections { get; set; }
}

#pragma warning restore CA2227
