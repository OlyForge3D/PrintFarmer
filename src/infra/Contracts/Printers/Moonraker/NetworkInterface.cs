using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class NetworkInterface
{
    [JsonPropertyName("mac_address")]
    public string MacAddress { get; set; } = string.Empty;

    [JsonPropertyName("ip_addresses")]
    public IpAddress[] IpAddresses { get; set; } = Array.Empty<IpAddress>();
}
