using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class QueuedJob
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("time_added")]
    public double TimeAdded { get; set; }

    [JsonPropertyName("time_in_queue")]
    public double TimeInQueue { get; set; }
}
