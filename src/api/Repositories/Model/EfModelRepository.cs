using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Repositories.Model
{
    public class EfModelRepository : IModelRepository
    {
        private readonly AppDbContext _db;

        public EfModelRepository(AppDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task AddAsync(Model3D model, CancellationToken ct)
        {
            _ = await _db.Models3D.AddAsync(model, ct);
        }

        public async Task<Model3D?> GetByHashAsync(string fileHash, CancellationToken ct)
        {
            return await _db.Models3D.FirstOrDefaultAsync(m => m.FileHash == fileHash, ct);
        }

        public async Task<Model3D?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            return await _db.Models3D.FirstOrDefaultAsync(m => m.Id == id && m.IsValid, ct);
        }

        public async Task<IReadOnlyList<Model3D>> ListValidAsync(CancellationToken ct)
        {
            return await _db.Models3D.Where(m => m.IsValid).OrderByDescending(m => m.UploadedAt).ToListAsync(ct);
        }

        public async Task RemoveAsync(Model3D model, CancellationToken ct)
        {
            _ = _db.Models3D.Remove(model);
            await Task.CompletedTask;
        }

        public async Task SaveChangesAsync(CancellationToken ct)
        {
            await _db.SaveChangesAsync(ct);
        }
    }
}
