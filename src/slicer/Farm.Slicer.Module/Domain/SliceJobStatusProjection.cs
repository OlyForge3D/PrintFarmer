namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Fields shared by persisted slice jobs and the narrow status-list projection.
/// </summary>
public interface ISliceJobStatusSource
{
    Guid Id { get; }

    string Status { get; }

    Guid? PrinterId { get; }

    int ProgressPercent { get; }

    DateTime QueuedAt { get; }

    DateTime? StartedAt { get; }

    DateTime? CompletedAt { get; }

    string? ErrorMessage { get; }

    string? LayoutDegradationReason { get; }

    string? FailureReason { get; }

    int? EstimatedPrintTimeSeconds { get; }

    decimal? FilamentUsedGrams { get; }

    string ModelFileName { get; }

    string? SlicerEngineName { get; }
}

/// <summary>
/// Read-only slice-job fields required to build the public status response.
/// </summary>
public sealed class SliceJobStatusProjection : ISliceJobStatusSource
{
    public Guid Id { get; init; }

    public string Status { get; init; } = string.Empty;

    public Guid? PrinterId { get; init; }

    public int ProgressPercent { get; init; }

    public DateTime QueuedAt { get; init; }

    public DateTime? StartedAt { get; init; }

    public DateTime? CompletedAt { get; init; }

    public string? ErrorMessage { get; init; }

    public string? LayoutDegradationReason { get; init; }

    public string? FailureReason { get; init; }

    public int? EstimatedPrintTimeSeconds { get; init; }

    public decimal? FilamentUsedGrams { get; init; }

    public string ModelFileName { get; init; } = string.Empty;

    public string? SlicerEngineName { get; init; }

    /// <summary>
    /// Creates a status projection from a materialized job, for in-memory repository substitutes.
    /// </summary>
    public static SliceJobStatusProjection From(SliceJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new SliceJobStatusProjection
        {
            Id = job.Id,
            Status = job.Status,
            PrinterId = job.PrinterId,
            ProgressPercent = job.ProgressPercent,
            QueuedAt = job.QueuedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            ErrorMessage = job.ErrorMessage,
            LayoutDegradationReason = job.LayoutDegradationReason,
            FailureReason = job.FailureReason,
            EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
            FilamentUsedGrams = job.FilamentUsedGrams,
            ModelFileName = job.ModelFileName,
            SlicerEngineName = job.SlicerEngineName,
        };
    }
}
