namespace Farm.Web.Api.DTOs.Slicing;

/// <summary>
/// Request to claim the next available slice job (worker pull model)
/// </summary>
public record ClaimJobRequest
{
    /// <summary>
    /// Worker ID attempting to claim a job
    /// </summary>
    public Guid WorkerId { get; init; }

    /// <summary>
    /// Optional capabilities that the worker supports (e.g., "orcaslicer", "prusaslicer")
    /// If null, worker can accept any job.
    /// </summary>
    public string[]? Capabilities { get; init; }

    /// <summary>
    /// Lease duration in seconds (default 300 = 5 minutes)
    /// Job will be automatically unclaimed if worker doesn't complete within this time
    /// </summary>
    public int LeaseDurationSeconds { get; init; } = 300;
}
