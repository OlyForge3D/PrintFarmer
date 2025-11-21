using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;

namespace Farm.Infrastructure.UnitOfWork;

public interface IUnitOfWork
{
    AppDbContext Context { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
