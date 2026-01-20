using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Catalog;

// Implementation of catalog data access. Does not implement the API interface here
// to avoid a cross-project dependency from infra -> api. The interface will be moved
// to a shared contract (or infra) in a follow-up step and this class can then implement it.
public class EfCatalogRepository(AppDbContext db) : ICatalogRepository
{
    private readonly AppDbContext _db = db;

    public async Task<IReadOnlyList<(Guid Id, string Name, string? Url, string? Description)>> GetManufacturersAsync(CancellationToken ct = default)
    {
        var rows = await _db.Manufacturers.AsNoTracking().Select(m => new { m.Id, m.Name, m.Url, m.Description }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Name, r.Url, r.Description)).ToList();
    }

    public async Task<(Guid Id, string Name, string? Url, string? Description)?> GetManufacturerByIdAsync(Guid id, CancellationToken ct = default)
    {
        Manufacturer? m = await _db.Manufacturers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return m is null ? null : (m.Id, m.Name, m.Url, m.Description);
    }

    public async Task AddManufacturerAsync(Guid id, string name, string? url, string? description, CancellationToken ct = default)
    {
        _ = _db.Manufacturers.Add(new Manufacturer { Id = id, Name = name, Url = url, Description = description });
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
        return await _db.PrinterModels.Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType).AsSplitQuery().FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task UpdateModelFilamentTypesAsync(Guid modelId, IEnumerable<Guid> filamentTypeIds, CancellationToken ct = default)
    {
        PrinterModel? model = await _db.PrinterModels.Include(m => m.SupportedFilamentTypes).FirstOrDefaultAsync(m => m.Id == modelId, ct);
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

    public async Task UpdateModelToolheadsAsync(Guid modelId, PrinterModelToolheadDto[] toolheads, CancellationToken ct = default)
    {
        PrinterModel? model = await _db.PrinterModels.Include(m => m.Toolheads).FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model is null)
        {
            return;
        }

        // Remove existing toolheads
        _db.PrinterModelToolheads.RemoveRange(model.Toolheads);

        // Add new toolheads
        foreach (PrinterModelToolheadDto th in toolheads)
        {
            // Use the provided ID or generate a new one if it's empty
            Guid toolheadId = th.Id == Guid.Empty ? Guid.NewGuid() : th.Id;
            _ = _db.PrinterModelToolheads.Add(new PrinterModelToolhead
            {
                Id = toolheadId,
                PrinterModelId = modelId,
                Name = th.Name,
                Index = th.Index,
                NozzleDiameter = th.NozzleDiameter,
                NozzleType = th.NozzleType.HasValue ? (int)th.NozzleType.Value : null,
                MaxHotendTemp = th.MaxHotendTemp,
                MaxFlowRate = th.MaxFlowRate,
                ToolheadType = th.ToolheadType.HasValue ? (int)th.ToolheadType.Value : null,

                // Component model references (Guids)
                HotendModelId = th.HotendModelId,
                ExtruderModelId = th.ExtruderModelId,
                ToolheadModelDefId = th.ToolheadModelDefId,
                NozzleModelId = th.NozzleModelId,
                SupportedMaterials = th.SupportedMaterials ?? [],
                IsPrimary = th.IsPrimary
            });
        }
    }

    public async Task<IReadOnlyList<PrinterModelDto>> GetModelsCachedAsync(Guid? manufacturerId, CancellationToken ct = default)
    {
        IQueryable<PrinterModel> q = _db.PrinterModels.AsNoTracking().Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType).AsSplitQuery();
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
            m.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray(),
            m.HasHeatedBed,
            m.HasEnclosure,
            m.MultiMaterial,
            m.NumberOfExtruders,
            m.SupportsAutoLeveling,
            m.MaxBedTemp,
            m.MaxPrintSpeed)).ToList();
        return list;
    }

    public async Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct = default)
    {
        PrinterModel? model = await _db.PrinterModels.AsNoTracking()
            .Include(m => m.SupportedFilamentTypes).ThenInclude(sf => sf.FilamentType)
            .Include(m => m.Toolheads).ThenInclude(t => t.HotendModel)
            .Include(m => m.Toolheads).ThenInclude(t => t.ExtruderModel)
            .Include(m => m.Toolheads).ThenInclude(t => t.ToolheadModelDef)
            .Include(m => m.Toolheads).ThenInclude(t => t.NozzleModel)
            .AsSplitQuery()
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        return model is null
            ? null
            : new PrinterModelDto(
                model.Id,
                model.Name,
                model.ManufacturerId,
                model.MotionType.HasValue ? (MotionType?)model.MotionType.Value : null,
                model.MaxX,
                model.MaxY,
                model.MaxZ,
                model.DefaultBackend.HasValue ? (PrinterBackend?)model.DefaultBackend.Value : null,
                model.SupportedFilamentTypes.Select(sf => sf.FilamentType!.Name).ToArray(),
                model.HasHeatedBed,
                model.HasEnclosure,
                model.MultiMaterial,
                model.NumberOfExtruders,
                model.SupportsAutoLeveling,
                model.MaxBedTemp,
                model.MaxPrintSpeed,
                model.Toolheads.Select(t => new PrinterModelToolheadDto(
                    t.Id,
                    t.Name,
                    t.Index,
                    t.NozzleDiameter,
                    t.NozzleType.HasValue ? (NozzleType)t.NozzleType.Value : null,
                    t.MaxHotendTemp,
                    t.MaxFlowRate,
                    t.ToolheadType.HasValue ? (ToolheadType)t.ToolheadType.Value : null,

                    // Component model references (IDs and names)
                    t.HotendModelId,
                    t.HotendModel?.Name,
                    t.ExtruderModelId,
                    t.ExtruderModel?.Name,
                    t.ToolheadModelDefId,
                    t.ToolheadModelDef?.Name,
                    t.NozzleModelId,
                    t.NozzleModel?.Name,
                    t.SupportedMaterials,
                    t.IsPrimary)).OrderBy(t => t.Index).ToArray());
    }

    public async Task AddModelAsync(PrinterModel model, CancellationToken ct = default)
    {
        _ = _db.PrinterModels.Add(model);
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

        PrinterModel? unknownModel = await _db.PrinterModels
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ManufacturerId == unknownMfgId.Value && m.Name == "Unknown Model", ct);
        return unknownModel?.Id;
    }

    public async Task RemoveModelAsync(Guid id, CancellationToken ct = default)
    {
        PrinterModel? model = await _db.PrinterModels.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is not null)
        {
            _ = _db.PrinterModels.Remove(model);
        }
    }

    /// <summary>
    /// Finds a manufacturer by exact name match for import/lookup purposes (read-only, no creation).
    /// Returns the Manufacturer entity if found, null otherwise.
    /// </summary>
    /// <param name="name">The exact name of the manufacturer to find.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
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
    /// <param name="name">The exact name of the printer model to find.</param>
    /// <param name="manufacturerId">The ID of the manufacturer to search within.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task<PrinterModel?> FindModelByNameAsync(string name, Guid manufacturerId, CancellationToken ct = default)
    {
        return await _db.PrinterModels
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Name == name && m.ManufacturerId == manufacturerId, ct);
    }

    public async Task<List<Domain.PrinterModelAlias>> GetModelAliasesAsync(Guid modelId, CancellationToken ct = default)
    {
        return await _db.PrinterModelAliases
            .Where(a => a.PrinterModelId == modelId)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<List<Domain.PrinterModelAlias>> UpdateModelAliasesAsync(Guid modelId, List<string> orcaSlicerNames, List<string> prusaSlicerNames, CancellationToken ct = default)
    {
        // EF Core 10: Use ExecuteDeleteAsync for efficient bulk delete without loading entities
        await _db.PrinterModelAliases
            .Where(a => a.PrinterModelId == modelId)
            .ExecuteDeleteAsync(ct);

        // Add new OrcaSlicer aliases
        foreach (string name in orcaSlicerNames ?? new List<string>())
        {
            _db.PrinterModelAliases.Add(new Domain.PrinterModelAlias
            {
                Id = Guid.NewGuid(),
                PrinterModelId = modelId,
                SlicerModelName = name,
                SlicerType = "OrcaSlicer",
                CreatedAt = DateTime.UtcNow
            });
        }

        // Add new PrusaSlicer aliases
        foreach (string name in prusaSlicerNames ?? new List<string>())
        {
            _db.PrinterModelAliases.Add(new Domain.PrinterModelAlias
            {
                Id = Guid.NewGuid(),
                PrinterModelId = modelId,
                SlicerModelName = name,
                SlicerType = "PrusaSlicer",
                CreatedAt = DateTime.UtcNow
            });
        }

        // Return the updated list
        return await _db.PrinterModelAliases
            .Where(a => a.PrinterModelId == modelId)
            .ToListAsync(ct);
    }

    // ============ Component Model Methods ============
    public async Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, int? MaxTemp, bool IsHighFlow, NozzleInterfaceType NozzleInterface, string? Description, string? Url)>> GetHotendModelsAsync(CancellationToken ct = default)
    {
        List<HotendModelDefinition> hotends = await _db.HotendModelDefinitions
            .Include(h => h.Manufacturer)
            .AsNoTracking()
            .OrderBy(h => h.Manufacturer!.Name)
            .ThenBy(h => h.Name)
            .ToListAsync(ct);

        return hotends.Select(h => (
            h.Id,
            h.Name,
            h.ManufacturerId,
            h.Manufacturer?.Name,
            h.MaxTemp,
            h.IsHighFlow,
            h.NozzleInterface,
            h.Description,
            h.Url)).ToList();
    }

    public async Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, string? GearRatio, bool IsDirectDrive, string? Description, string? Url)>> GetExtruderModelsAsync(CancellationToken ct = default)
    {
        List<ExtruderModelDefinition> extruders = await _db.ExtruderModelDefinitions
            .Include(e => e.Manufacturer)
            .AsNoTracking()
            .OrderBy(e => e.Manufacturer!.Name)
            .ThenBy(e => e.Name)
            .ToListAsync(ct);

        return extruders.Select(e => (
            e.Id,
            e.Name,
            e.ManufacturerId,
            e.Manufacturer?.Name,
            e.GearRatio,
            e.IsDirectDrive,
            e.Description,
            e.Url)).ToList();
    }

    public async Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, string? Description, string? Url, Guid? DefaultHotendId, Guid? DefaultExtruderId, Guid? DefaultNozzleId)>> GetToolheadModelsAsync(CancellationToken ct = default)
    {
        List<ToolheadModelDefinition> toolheads = await _db.ToolheadModelDefinitions
            .Include(t => t.Manufacturer)
            .AsNoTracking()
            .OrderBy(t => t.Manufacturer!.Name)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);

        return toolheads.Select(t => (
            t.Id,
            t.Name,
            t.ManufacturerId,
            t.Manufacturer?.Name,
            t.Description,
            t.Url,
            t.DefaultHotendId,
            t.DefaultExtruderId,
            t.DefaultNozzleId)).ToList();
    }

    public async Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, int? MaxTemp, bool IsHardened, NozzleInterfaceType NozzleInterface, string? Description, string? Url)>> GetNozzleModelsAsync(CancellationToken ct = default)
    {
        List<NozzleModelDefinition> nozzles = await _db.NozzleModelDefinitions
            .Include(n => n.Manufacturer)
            .AsNoTracking()
            .OrderBy(n => n.Manufacturer!.Name)
            .ThenBy(n => n.Name)
            .ToListAsync(ct);

        return nozzles.Select(n => (
            n.Id,
            n.Name,
            n.ManufacturerId,
            n.Manufacturer?.Name,
            n.MaxTemp,
            n.IsHardened,
            n.NozzleInterface,
            n.Description,
            n.Url)).ToList();
    }

    // ============ Component Model CRUD Methods ============

    // Get By Id
    public Task<HotendModelDefinition?> GetHotendModelByIdAsync(Guid id, CancellationToken ct = default)
        => _db.HotendModelDefinitions.Include(h => h.Manufacturer).FirstOrDefaultAsync(h => h.Id == id, ct);

    public Task<ExtruderModelDefinition?> GetExtruderModelByIdAsync(Guid id, CancellationToken ct = default)
        => _db.ExtruderModelDefinitions.Include(e => e.Manufacturer).FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<ToolheadModelDefinition?> GetToolheadModelByIdAsync(Guid id, CancellationToken ct = default)
        => _db.ToolheadModelDefinitions.Include(t => t.Manufacturer).FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<NozzleModelDefinition?> GetNozzleModelByIdAsync(Guid id, CancellationToken ct = default)
        => _db.NozzleModelDefinitions.Include(n => n.Manufacturer).FirstOrDefaultAsync(n => n.Id == id, ct);

    // Add
    public async Task AddHotendModelAsync(HotendModelDefinition model, CancellationToken ct = default)
    {
        _ = _db.HotendModelDefinitions.Add(model);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddExtruderModelAsync(ExtruderModelDefinition model, CancellationToken ct = default)
    {
        _ = _db.ExtruderModelDefinitions.Add(model);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddToolheadModelAsync(ToolheadModelDefinition model, CancellationToken ct = default)
    {
        _ = _db.ToolheadModelDefinitions.Add(model);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddNozzleModelAsync(NozzleModelDefinition model, CancellationToken ct = default)
    {
        _ = _db.NozzleModelDefinitions.Add(model);
        await _db.SaveChangesAsync(ct);
    }

    // Remove
    public async Task RemoveHotendModelAsync(Guid id, CancellationToken ct = default)
    {
        HotendModelDefinition? model = await _db.HotendModelDefinitions.FindAsync(new object[] { id }, ct);
        if (model is not null)
        {
            _ = _db.HotendModelDefinitions.Remove(model);
        }
    }

    public async Task RemoveExtruderModelAsync(Guid id, CancellationToken ct = default)
    {
        ExtruderModelDefinition? model = await _db.ExtruderModelDefinitions.FindAsync(new object[] { id }, ct);
        if (model is not null)
        {
            _ = _db.ExtruderModelDefinitions.Remove(model);
        }
    }

    public async Task RemoveToolheadModelAsync(Guid id, CancellationToken ct = default)
    {
        ToolheadModelDefinition? model = await _db.ToolheadModelDefinitions.FindAsync(new object[] { id }, ct);
        if (model is not null)
        {
            _ = _db.ToolheadModelDefinitions.Remove(model);
        }
    }

    public async Task RemoveNozzleModelAsync(Guid id, CancellationToken ct = default)
    {
        NozzleModelDefinition? model = await _db.NozzleModelDefinitions.FindAsync(new object[] { id }, ct);
        if (model is not null)
        {
            _ = _db.NozzleModelDefinitions.Remove(model);
        }
    }

    // Contextual manufacturer counts
    public Task<int> CountPrinterModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default)
        => _db.PrinterModels.CountAsync(m => m.ManufacturerId == manufacturerId, ct);

    public Task<int> CountHotendModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default)
        => _db.HotendModelDefinitions.CountAsync(h => h.ManufacturerId == manufacturerId, ct);

    public Task<int> CountExtruderModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default)
        => _db.ExtruderModelDefinitions.CountAsync(e => e.ManufacturerId == manufacturerId, ct);

    public Task<int> CountToolheadModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default)
        => _db.ToolheadModelDefinitions.CountAsync(t => t.ManufacturerId == manufacturerId, ct);

    public Task<int> CountNozzleModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default)
        => _db.NozzleModelDefinitions.CountAsync(n => n.ManufacturerId == manufacturerId, ct);
}
