using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Catalog
{
    // Implementation of catalog data access. Does not implement the API interface here
    // to avoid a cross-project dependency from infra -> api. The interface will be moved
    // to a shared contract (or infra) in a follow-up step and this class can then implement it.
    public class EfCatalogRepository : ICatalogRepository
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
            Manufacturer? m = await _db.Manufacturers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return m is null ? null : (m.Id, m.Name);
        }

        public async Task AddManufacturerAsync(Guid id, string name, CancellationToken ct = default)
        {
            _ = _db.Manufacturers.Add(new Manufacturer { Id = id, Name = name });
            await _db.SaveChangesAsync(ct);
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
            PrinterModel? model = await _db.Models.Include(m => m.SupportedFilamentTypes).FirstOrDefaultAsync(m => m.Id == modelId, ct);
            if (model is null)
            {
                return;
            }
            // Remove existing
            _db.PrinterModelFilamentTypes.RemoveRange(model.SupportedFilamentTypes);
            // Add new
            foreach (Guid filamentTypeId in filamentTypeIds)
            {
                _ = _db.PrinterModelFilamentTypes.Add(new PrinterModelFilamentType { PrinterModelId = modelId, FilamentTypeId = filamentTypeId });
            }
        }

        public async Task<IReadOnlyList<PrinterModelDto>> GetModelsCachedAsync(Guid? manufacturerId, CancellationToken ct = default)
        {
            IQueryable<PrinterModel> q = _db.Models.AsNoTracking().Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType).AsQueryable();
            if (manufacturerId.HasValue)
            {
                q = q.Where(m => m.ManufacturerId == manufacturerId.Value);
            }
            List<PrinterModel> models = await q.ToListAsync(ct);
            List<PrinterModelDto> list = models.Select(m => new PrinterModelDto(
                m.Id,
                m.Name,
                m.ManufacturerId,
                m.MotionType.HasValue ? (MotionType?)m.MotionType.Value : null,
                m.MaxX,
                m.MaxY,
                m.MaxZ,
                m.DefaultBackend.HasValue ? (PrinterBackend?)m.DefaultBackend.Value : null,
                m.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray()
            )).ToList();
            return list;
        }

        public async Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct = default)
        {
            PrinterModel? model = await _db.Models.AsNoTracking().Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
            if (model is null)
            {
                return null;
            }
            return new PrinterModelDto(model.Id,
                model.Name,
                model.ManufacturerId,
                model.MotionType.HasValue ? (MotionType?)model.MotionType.Value : null,
                model.MaxX,
                model.MaxY,
                model.MaxZ,
                model.DefaultBackend.HasValue ? (PrinterBackend?)model.DefaultBackend.Value : null,
                model.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray());
        }

        public async Task AddModelAsync(PrinterModel model, CancellationToken ct = default)
        {
            _ = _db.Models.Add(model);
            await _db.SaveChangesAsync(ct);
        }

        public async Task<IEnumerable<Guid>> GetValidFilamentTypeIdsAsync(Guid[] ids, CancellationToken ct = default)
        {
            return await _db.FilamentTypes.AsNoTracking().Where(f => ids.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct);
        }

        public async Task<PrinterModelDto?> GetModelWithFilamentNamesAsync(Guid id, CancellationToken ct = default)
        {
            return await GetModelByIdAsync(id, ct);
        }

        public async Task<Guid?> GetUnknownManufacturerIdAsync(CancellationToken ct = default)
        {
            Manufacturer? unknown = await _db.Manufacturers
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Name == "Unknown", ct);
            return unknown?.Id;
        }

        public async Task<Guid?> GetUnknownModelIdAsync(CancellationToken ct = default)
        {
            Guid? unknownMfgId = await GetUnknownManufacturerIdAsync(ct);
            if (!unknownMfgId.HasValue)
            {
                return null;
            }

            PrinterModel? unknownModel = await _db.Models
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.ManufacturerId == unknownMfgId.Value && m.Name == "Unknown Model", ct);
            return unknownModel?.Id;
        }

        public async Task RemoveModelAsync(Guid id, CancellationToken ct = default)
        {
            PrinterModel? model = await _db.Models.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (model is not null)
            {
                _ = _db.Models.Remove(model);
            }
        }

        /// <summary>
        /// Finds a manufacturer by exact name match for import/lookup purposes (read-only, no creation).
        /// Returns the Manufacturer entity if found, null otherwise.
        /// </summary>
        public async Task<Manufacturer?> FindManufacturerByNameAsync(string name, CancellationToken ct = default)
        {
            return await _db.Manufacturers
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Name == name, ct);
        }

        /// <summary>
        /// Finds a printer model by exact name match within a specific manufacturer for import/lookup purposes (read-only, no creation).
        /// Returns the PrinterModel entity if found, null otherwise.
        /// </summary>
        public async Task<PrinterModel?> FindModelByNameAsync(string name, Guid manufacturerId, CancellationToken ct = default)
        {
            return await _db.Models
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Name == name && m.ManufacturerId == manufacturerId, ct);
        }
    }
}

