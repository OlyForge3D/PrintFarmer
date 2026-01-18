using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Slicing;

public interface ISlicersRepository
{
    Task<IReadOnlyList<SlicerService>> ListAsync(CancellationToken ct);
    Task AddAsync(SlicerService svc, CancellationToken ct);
    Task<SlicerService?> GetByIdAsync(Guid id, CancellationToken ct);
    Task RemoveAsync(SlicerService svc, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
