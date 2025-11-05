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
    public class ProfilesRepository : IProfilesRepository
    {
        private readonly AppDbContext _db;
        public ProfilesRepository(AppDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        public async Task AddAsync(SlicerProfile profile, CancellationToken ct)
        {
            await _db.SlicerProfiles.AddAsync(profile, ct);
        }

        public async Task<SlicerProfile?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            return await _db.SlicerProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        }

        public async Task<IReadOnlyList<SlicerProfile>> ListAsync(CancellationToken ct)
        {
            return await _db.SlicerProfiles.OrderBy(p => p.Name).ToListAsync(ct);
        }

        public Task RemoveAsync(SlicerProfile profile, CancellationToken ct)
        {
            _db.SlicerProfiles.Remove(profile);
            return Task.CompletedTask;
        }

        public async Task SaveChangesAsync(CancellationToken ct)
        {
            await _db.SaveChangesAsync(ct);
        }
    }
}
