using System;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Api
{
    public class EfApiKeyRepository
    {
        private readonly AppDbContext _db;

        public EfApiKeyRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<ApiKey?> GetByKeyHashAsync(string keyHash)
        {
            return await _db.Set<ApiKey>().FirstOrDefaultAsync(a => a.KeyHash == keyHash && a.IsActive);
        }

        public async Task AddAsync(ApiKey key)
        {
            await _db.Set<ApiKey>().AddAsync(key);
            await _db.SaveChangesAsync();
        }
    }
}
