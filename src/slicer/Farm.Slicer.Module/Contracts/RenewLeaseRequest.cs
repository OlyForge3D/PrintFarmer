using System.ComponentModel.DataAnnotations;
using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Contracts;

/// <summary>
/// Request to renew a worker's lease on a claimed slicing job.
/// </summary>
public class RenewLeaseRequest
{
    [Range(SliceJob.MinimumLeaseDurationSeconds, SliceJob.MaximumLeaseDurationSeconds)]
    public int LeaseDurationSeconds { get; set; } = 300;
}
