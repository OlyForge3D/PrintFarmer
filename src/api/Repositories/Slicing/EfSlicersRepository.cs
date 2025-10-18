using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Repositories.Slicing
{
    public class EfSlicersRepository : ISlicersRepository
    {
        private readonly AppDbContext _db;
        public EfSlicersRepository(AppDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        public async Task AddAsync(SlicerService svc, CancellationToken ct)
        {
            await _db.SlicerServices.AddAsync(svc, ct);
        }

        public async Task<IReadOnlyList<SlicerService>> ListAsync(CancellationToken ct)
        {
            return await _db.SlicerServices.OrderBy(s => s.Name).ToListAsync(ct);
        }

        public async Task<SlicerService?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            return await _db.SlicerServices.FindAsync(new object[] { id }, ct);
        }

        public Task RemoveAsync(SlicerService svc, CancellationToken ct)
        {
            _db.SlicerServices.Remove(svc);
            return Task.CompletedTask;
        }

        public async Task SaveChangesAsync(CancellationToken ct)
        {
            await _db.SaveChangesAsync(ct);
        }
    }
}
