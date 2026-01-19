using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class PendingReport
{
    [JsonPropertyName("spool_id")]
    public int SpoolId { get; set; }

    [JsonPropertyName("filament_used")]
    public double FilamentUsed { get; set; }
}
