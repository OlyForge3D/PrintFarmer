using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

/// <summary>
/// Internal tracking DTO for active / completed slicing jobs.
/// </summary>
// Slicing job status (shared with worker processes) – keep explicit attribute to decouple from Program options.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SlicingJobStatus
{
    Queued,
    Slicing,
    Completed,
    Error,
    Cancelled
}

public class SlicingJobDto
{
    public string JobId { get; set; } = string.Empty;

    public SlicingJobStatus Status { get; set; } = SlicingJobStatus.Queued;

    public int Progress { get; set; } // 0-100

    public string? Message { get; set; }

    public string SlicerEngine { get; set; } = string.Empty; // prusaslicer, orcaslicer

    public Guid PrinterId { get; set; }

    public string ModelFilePath { get; set; } = string.Empty;

    public string? GcodeFilePath { get; set; }

    public SlicerProfileDto? Profile { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int? EstimatedPrintTime { get; set; }

    public double? EstimatedFilamentUsed { get; set; }

    public int? LayerCount { get; set; }
}
