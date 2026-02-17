namespace Farm.Slicer.Module.Contracts;

/// <summary>
/// Request to renew a worker's lease on a claimed slicing job.
/// </summary>
public class RenewLeaseRequest
{
    public int LeaseDurationSeconds { get; set; } = 300;
}
