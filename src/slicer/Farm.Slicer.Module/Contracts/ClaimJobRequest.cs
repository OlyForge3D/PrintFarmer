namespace Farm.Slicer.Module.Contracts;

/// <summary>
/// Request sent by a worker to claim the next available slice job (pull model).
/// </summary>
public class ClaimJobRequest
{
    public Guid WorkerId { get; set; }

    /// <summary>
    /// Optional capabilities that the worker supports (e.g., "orcaslicer", "prusaslicer").
    /// </summary>
    public string[]? Capabilities { get; set; }

    /// <summary>
    /// Lease duration in seconds (default 300 = 5 minutes).
    /// </summary>
    public int LeaseDurationSeconds { get; set; } = 300;
}
