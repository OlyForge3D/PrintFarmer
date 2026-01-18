using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;

namespace Farm.Infrastructure.UnitOfWork;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1724:Type names should not match namespace", Justification = "Type name UnitOfWork matches folder/namespace by design and is part of public API; explicit renaming is deferred.")]
public class AppUnitOfWork(AppDbContext db) : IUnitOfWork
{
    private readonly AppDbContext _db = db;

    public AppDbContext Context => _db;

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return _db.SaveChangesAsync(ct);
    }
}
