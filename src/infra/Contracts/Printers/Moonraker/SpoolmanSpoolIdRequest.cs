using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class SpoolmanSpoolIdRequest
{
    [JsonPropertyName("spool_id")]
    public int? SpoolId { get; set; }
}
