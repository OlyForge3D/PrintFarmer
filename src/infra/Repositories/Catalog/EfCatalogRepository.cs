using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Catalog
{
    // Implementation of catalog data access. Does not implement the API interface here
    // to avoid a cross-project dependency from infra -> api. The interface will be moved
    // to a shared contract (or infra) in a follow-up step and this class can then implement it.
    public class EfCatalogRepository : Farm.Infrastructure.Repositories.Catalog.ICatalogRepository
    {
        private readonly AppDbContext _db;

        public EfCatalogRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<IReadOnlyList<(Guid Id, string Name)>> GetManufacturersAsync(CancellationToken ct = default)
        {
            var rows = await _db.Manufacturers.AsNoTracking().Select(m => new { m.Id, m.Name }).ToListAsync(ct);
            return rows.Select(r => (r.Id, r.Name)).ToList();
        }

        public async Task<(Guid Id, string Name)?> GetManufacturerByIdAsync(Guid id, CancellationToken ct = default)
        {
            var m = await _db.Manufacturers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return m is null ? null : (m.Id, m.Name);
        }

        public Task AddManufacturerAsync(Guid id, string name, CancellationToken ct = default)
        {
            _db.Manufacturers.Add(new Manufacturer { Id = id, Name = name });
            return Task.CompletedTask;
        }

        public Task<bool> ManufacturerExistsAsync(Guid id, CancellationToken ct = default)
        {
            return _db.Manufacturers.AsNoTracking().AnyAsync(m => m.Id == id, ct);
        }

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            return _db.SaveChangesAsync(ct);
        }

        public async Task<PrinterModel?> GetModelEntityAsync(Guid id, CancellationToken ct = default)
        {
            return await _db.Models.Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType).FirstOrDefaultAsync(m => m.Id == id, ct);
        }

        public async Task UpdateModelFilamentTypesAsync(Guid modelId, IEnumerable<Guid> filamentTypeIds, CancellationToken ct = default)
        {
            var model = await _db.Models.Include(m => m.SupportedFilamentTypes).FirstOrDefaultAsync(m => m.Id == modelId, ct);
            if (model is null)
            {
                return;
            }
            // Remove existing
            _db.PrinterModelFilamentTypes.RemoveRange(model.SupportedFilamentTypes);
            // Add new
            foreach (Guid filamentTypeId in filamentTypeIds)
            {
                _db.PrinterModelFilamentTypes.Add(new PrinterModelFilamentType { PrinterModelId = modelId, FilamentTypeId = filamentTypeId });
            }
        }

        public async Task<IReadOnlyList<PrinterModelDto>> GetModelsCachedAsync(Guid? manufacturerId, CancellationToken ct = default)
        {
            var q = _db.Models.AsNoTracking().Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType).AsQueryable();
            if (manufacturerId.HasValue)
            {
                q = q.Where(m => m.ManufacturerId == manufacturerId.Value);
            }
            var models = await q.ToListAsync(ct);
            var list = models.Select(m => new PrinterModelDto(
                m.Id,
                m.Name,
                m.ManufacturerId,
                m.MotionType.HasValue ? (Farm.Web.Shared.MotionType?)m.MotionType.Value : null,
                m.MaxX,
                m.MaxY,
                m.MaxZ,
                m.DefaultBackend.HasValue ? (Farm.Web.Shared.PrinterBackend?)m.DefaultBackend.Value : null,
                m.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray()
            )).ToList();
            return list;
        }

        public async Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct = default)
        {
            var model = await _db.Models.AsNoTracking().Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
            if (model is null)
            {
                return null;
            }
            return new PrinterModelDto(model.Id,
                model.Name,
                model.ManufacturerId,
                model.MotionType.HasValue ? (Farm.Web.Shared.MotionType?)model.MotionType.Value : null,
                model.MaxX,
                model.MaxY,
                model.MaxZ,
                model.DefaultBackend.HasValue ? (Farm.Web.Shared.PrinterBackend?)model.DefaultBackend.Value : null,
                model.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray());
        }

        public Task AddModelAsync(PrinterModel model, CancellationToken ct = default)
        {
            _db.Models.Add(model);
            return Task.CompletedTask;
        }

        public async Task<IEnumerable<Guid>> GetValidFilamentTypeIdsAsync(Guid[] ids, CancellationToken ct = default)
        {
            return await _db.FilamentTypes.AsNoTracking().Where(f => ids.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct);
        }

        public async Task<PrinterModelDto?> GetModelWithFilamentNamesAsync(Guid id, CancellationToken ct = default)
        {
            return await GetModelByIdAsync(id, ct);
        }
    }
}
