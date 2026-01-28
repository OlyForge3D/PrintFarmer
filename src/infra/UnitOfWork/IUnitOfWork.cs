using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;

namespace Farm.Infrastructure.UnitOfWork;

/// <summary>
/// Simplified unit of work interface for coordinating database transactions.
/// Provides access to the underlying DbContext and atomic save operations.
/// </summary>
public interface IUnitOfWork
{
    AppDbContext Context { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
