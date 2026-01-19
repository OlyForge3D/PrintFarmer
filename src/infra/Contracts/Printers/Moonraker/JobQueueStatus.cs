using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Job Queue Models
public class JobQueueStatus
{
    [JsonPropertyName("queued_jobs")]
    public QueuedJob[] QueuedJobs { get; set; } = Array.Empty<QueuedJob>();

    [JsonPropertyName("queue_state")]
    public string QueueState { get; set; } = string.Empty;
}
