using Farm.Infrastructure.Services.HostUpdates;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Services;

/// <summary>Counts active slicer leases so host-update drain waits for in-flight slicing to finish.</summary>
public sealed class SlicerActiveWorkObservationPort(SlicerDbContext db) : IActiveWorkObservationPort
{
    public Task<int> CountActiveAsync(CancellationToken cancellationToken) =>
        db.SliceJobs.CountAsync(job => job.Status == SliceJobStatus.Processing, cancellationToken);
}
