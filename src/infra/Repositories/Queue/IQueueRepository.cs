using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Queue;

public interface IQueueRepository
{
    Task<List<PrintJob>> GetAllAsync(CancellationToken ct);
    Task<PrintJob?> FindByIdAsync(Guid id, CancellationToken ct);
    Task AddAsync(PrintJob item, CancellationToken ct);
    Task RemoveAsync(PrintJob item, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
