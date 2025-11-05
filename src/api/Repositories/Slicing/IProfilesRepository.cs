using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Repositories.Slicing
{
    public interface IProfilesRepository
    {
        Task AddAsync(SlicerProfile profile, CancellationToken ct);
        Task<SlicerProfile?> GetByIdAsync(Guid id, CancellationToken ct);
        Task<IReadOnlyList<SlicerProfile>> ListAsync(CancellationToken ct);
        Task RemoveAsync(SlicerProfile profile, CancellationToken ct);
        Task SaveChangesAsync(CancellationToken ct);
    }
}
