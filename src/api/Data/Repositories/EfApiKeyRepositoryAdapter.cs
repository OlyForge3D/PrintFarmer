using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Data.Repositories
{
    // Adapter that implements the API-layer IApiKeyRepository using AppDbContext directly
    public class EfApiKeyRepositoryAdapter : IApiKeyRepository
    {
        private readonly AppDbContext _db;

        public EfApiKeyRepositoryAdapter(AppDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task AddAsync(ApiKey key)
        {
            await _db.Set<ApiKey>().AddAsync(key);
            await _db.SaveChangesAsync();
        }

        public async Task<ApiKey?> GetByKeyHashAsync(string keyHash)
        {
            return await _db.Set<ApiKey>().FirstOrDefaultAsync(a => a.KeyHash == keyHash && a.IsActive);
        }

        public async Task<IEnumerable<ApiKey>> GetByUserIdAsync(Guid userId)
        {
            return await _db.Set<ApiKey>().Where(k => k.UserId == userId).ToArrayAsync();
        }
    }
}
