using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class CpuInfo
{
    [JsonPropertyName("cpu_count")]
    public int CpuCount { get; set; }

    [JsonPropertyName("bits")]
    public string Bits { get; set; } = string.Empty;

    [JsonPropertyName("processor")]
    public string Processor { get; set; } = string.Empty;

    [JsonPropertyName("cpu_desc")]
    public string CpuDesc { get; set; } = string.Empty;

    [JsonPropertyName("serial_number")]
    public string SerialNumber { get; set; } = string.Empty;

    [JsonPropertyName("hardware_desc")]
    public string HardwareDesc { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("total_memory")]
    public long TotalMemory { get; set; }

    [JsonPropertyName("memory_units")]
    public string MemoryUnits { get; set; } = string.Empty;
}
