using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class DiskUsage
{
    [JsonPropertyName("used")]
    public long Used { get; set; }

    [JsonPropertyName("free")]
    public long Free { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }
}
