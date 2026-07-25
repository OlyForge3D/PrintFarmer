using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Internal tracking status for active/completed slicing jobs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SlicingJobStatus
{
    /// <summary>Job is queued and waiting to be picked up by a worker.</summary>
    Queued,

    /// <summary>Job is actively being sliced.</summary>
    Slicing,

    /// <summary>Slicing completed successfully.</summary>
    Completed,

    /// <summary>Slicing failed with an error.</summary>
    Error,

    /// <summary>Job was cancelled before completion.</summary>
    Cancelled,
}

/// <summary>
/// DTO representing a slicing job's current state and parameters.
/// </summary>
public class SlicingJobDto
{
    public string JobId { get; set; } = string.Empty;

    public SlicingJobStatus Status { get; set; } = SlicingJobStatus.Queued;

    public int Progress { get; set; }

    public string? Message { get; set; }

    public string SlicerEngine { get; set; } = string.Empty;

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
