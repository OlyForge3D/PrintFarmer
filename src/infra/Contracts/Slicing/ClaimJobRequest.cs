using System;

namespace Farm.Infrastructure.Contracts.Slicing;

/// <summary>
/// Request sent by a worker to claim the next available slice job (pull model).
/// Moved to shared contracts so workers can construct it without referencing the API assembly.
/// </summary>
public class ClaimJobRequest
{
    /// <summary>
    /// Worker ID attempting to claim a job
    /// </summary>
    public Guid WorkerId { get; set; }

    /// <summary>
    /// Optional capabilities that the worker supports (e.g., "orcaslicer", "prusaslicer")
    /// If null or empty, worker can accept any job.
    /// </summary>
    public string[]? Capabilities { get; set; }

    /// <summary>
    /// Lease duration in seconds (default 300 = 5 minutes).
    /// Job will be automatically unclaimed if worker doesn't complete within this time.
    /// </summary>
    public int LeaseDurationSeconds { get; set; } = 300;
}
