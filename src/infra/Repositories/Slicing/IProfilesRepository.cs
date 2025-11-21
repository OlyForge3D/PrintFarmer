using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Slicing;

public interface IProfilesRepository
{
    Task<List<ProcessProfile>> GetAllAsync(CancellationToken ct);
    Task<ProcessProfile?> FindByIdAsync(Guid id, CancellationToken ct);
    Task AddAsync(ProcessProfile profile, CancellationToken ct);
    Task RemoveAsync(ProcessProfile profile, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
