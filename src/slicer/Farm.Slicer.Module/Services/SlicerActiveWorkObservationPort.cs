using Farm.Infrastructure.Services.HostUpdates;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Counts active slicer leases so host-update drain waits for in-flight slicing to finish. A
/// slicer schema that was provably never migrated has no leases (issue #3126); any other read
/// failure propagates and fails the drain closed.
/// </summary>
public sealed class SlicerActiveWorkObservationPort(SlicerDbContext db) : IActiveWorkObservationPort
{
    private static readonly Type[] ObservedEntities = [typeof(SliceJob)];

    public async Task<int> CountActiveAsync(CancellationToken cancellationToken) =>
        await HostUpdateSchemaAbsenceProof.IsNeverMigratedAsync(db, ObservedEntities, cancellationToken).ConfigureAwait(false)
            ? 0
            : await db.SliceJobs.CountAsync(job => job.Status == SliceJobStatus.Processing, cancellationToken).ConfigureAwait(false);
}
