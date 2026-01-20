using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class CanbusResponse
{
    [JsonPropertyName("can_uuids")]
    public CanUuid[] CanUuids { get; set; } = Array.Empty<CanUuid>();
}
