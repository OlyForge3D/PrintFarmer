using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;

namespace Farm.Web.Api.Infrastructure.UnitOfWork;

public class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _db;

    public UnitOfWork(AppDbContext db)
    {
        _db = db;
    }

    public AppDbContext Context => _db;

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return _db.SaveChangesAsync(ct);
    }
}
