using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Slicing;

public interface IProfilesRepository
{
    Task<List<SlicerProfile>> GetAllAsync(CancellationToken ct);
    Task<SlicerProfile?> FindByIdAsync(Guid id, CancellationToken ct);
    Task AddAsync(SlicerProfile profile, CancellationToken ct);
    Task RemoveAsync(SlicerProfile profile, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
