using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class SystemInfoData
{
    [JsonPropertyName("cpu_info")]
    public CpuInfo CpuInfo { get; set; } = new();

    [JsonPropertyName("sd_info")]
    public SdInfo SdInfo { get; set; } = new();

    [JsonPropertyName("distribution")]
    public DistributionInfo Distribution { get; set; } = new();

    [JsonPropertyName("available_services")]
    public string[] AvailableServices { get; set; } = Array.Empty<string>();

    [JsonPropertyName("instance_ids")]
    public Dictionary<string, string> InstanceIds { get; set; } = new Dictionary<string, string>();

    [JsonPropertyName("service_state")]
    public Dictionary<string, ServiceState> ServiceStates { get; set; } = new Dictionary<string, ServiceState>();

    [JsonPropertyName("virtualization")]
    public VirtualizationInfo Virtualization { get; set; } = new();

    [JsonPropertyName("python")]
    public PythonInfo Python { get; set; } = new();

    [JsonPropertyName("network")]
    public Dictionary<string, NetworkInterface> Network { get; set; } = new();

    [JsonPropertyName("canbus")]
    public Dictionary<string, CanbusInterface> Canbus { get; set; } = new();
}

#pragma warning restore CA2227
