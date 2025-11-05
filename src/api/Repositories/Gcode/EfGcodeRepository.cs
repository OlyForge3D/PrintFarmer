using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Repositories.Gcode
{
    public class EfGcodeRepository : IGcodeRepository
    {
        private readonly AppDbContext _db;

        public EfGcodeRepository(AppDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public Task<List<GcodeFile>> QueryLibraryAsync(string? search, string? material, double? nozzleDiameter, Guid? targetPrinterId, CancellationToken ct)
        {
            IQueryable<GcodeFile> query = _db.GcodeFiles
                .Include(g => g.SourcePrinter)
                .Include(g => g.TargetPrinter)
                .Include(g => g.TargetModel)
                .AsQueryable();

            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(g => g.OriginalFileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                         g.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                         (g.Description != null && g.Description.Contains(search, StringComparison.OrdinalIgnoreCase)));
            }

            if (!string.IsNullOrEmpty(material))
            {
                query = query.Where(g => g.RequiredMaterial == material);
            }

            if (nozzleDiameter.HasValue)
            {
                double nd = nozzleDiameter.Value;
                query = query.Where(g => g.RequiredNozzleDiameter != null && Math.Abs(g.RequiredNozzleDiameter.Value - nd) < 0.001);
            }

            if (targetPrinterId.HasValue)
            {
                query = query.Where(g => g.TargetPrinterId == targetPrinterId.Value);
            }

            return query.OrderByDescending(g => g.UploadedAt).ToListAsync(ct);
        }

        public Task<GcodeFile?> GetByIdWithIncludesAsync(Guid id, CancellationToken ct)
        {
            return _db.GcodeFiles
                .Include(g => g.SourcePrinter)
                .Include(g => g.TargetPrinter)
                .Include(g => g.TargetModel)
                .FirstOrDefaultAsync(g => g.Id == id, ct);
        }

        public Task<GcodeFile?> FindByHashAsync(string hash, CancellationToken ct)
        {
            return _db.GcodeFiles.FirstOrDefaultAsync(g => g.FileHash == hash, ct);
        }

        public Task<GcodeFile?> GetByFullPathAsync(string fullPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return Task.FromResult<GcodeFile?>(null);
            }
            return _db.GcodeFiles.FirstOrDefaultAsync(g => g.FilePath == fullPath, ct);
        }

        public async Task<Guid?> GetLatestHarvestOperationIdForPrinterAsync(Guid printerId, CancellationToken ct)
        {
            GcodeHarvestOperation? op = await _db.GcodeHarvestOperations
                .Where(o => o.PrinterId == printerId)
                .OrderByDescending(o => o.StartedAt)
                .FirstOrDefaultAsync(ct);
            return op?.Id;
        }

        public Task AddAsync(GcodeFile file, CancellationToken ct)
        {
            _db.GcodeFiles.Add(file);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(GcodeFile file, CancellationToken ct)
        {
            _db.GcodeFiles.Remove(file);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
    }
}
