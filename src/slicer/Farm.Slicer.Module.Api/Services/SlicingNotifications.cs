using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Notification sent via SignalR when a slicing job completes (success or failure result).
/// </summary>
public class SlicingCompletionNotification
{
    public Guid JobId { get; set; }

    public Guid UserId { get; set; }

    public SlicingJobStatus Status { get; set; }

    public bool Success { get; set; }

    public Uri? ResultFileUrl { get; set; }

    public double ProcessingTimeSeconds { get; set; }

    public double EstimatedPrintTimeSeconds { get; set; }

    public double EstimatedFilamentUsageGrams { get; set; }

    public int LayerCount { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CompletedAt { get; set; }

    public Dictionary<string, object> Metadata { get; set; } = [];
}

/// <summary>
/// Notification sent via SignalR when a slicing job fails.
/// </summary>
public class SlicingFailureNotification
{
    public Guid JobId { get; set; }

    public Guid UserId { get; set; }

    public SlicingJobStatus Status { get; set; }

    public string ErrorMessage { get; set; } = string.Empty;

    public DateTime FailedAt { get; set; }

    public int RetryCount { get; set; }

    public bool CanRetry { get; set; }

    public Dictionary<string, object> Metadata { get; set; } = [];
}
