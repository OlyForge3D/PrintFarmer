using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Spoolman Models
public class SpoolmanStatus
{
    [JsonPropertyName("spoolman_connected")]
    public bool SpoolmanConnected { get; set; }

    [JsonPropertyName("pending_reports")]
    public PendingReport[] PendingReports { get; set; } = Array.Empty<PendingReport>();

    [JsonPropertyName("spool_id")]
    public int? SpoolId { get; set; }
}
