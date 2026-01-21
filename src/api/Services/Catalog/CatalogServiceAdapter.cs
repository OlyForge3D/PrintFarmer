using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Catalog;
using Farm.Web.Api.Controllers.Requests;

namespace Farm.Web.Api.Services.Catalog;

/// <summary>
/// API adapter that wraps Infrastructure ICatalogService.
/// Converts request DTOs to domain parameters for the core service.
/// This keeps the API layer thin and the core logic reusable.
/// </summary>
public class CatalogServiceAdapter(Farm.Infrastructure.Services.Catalog.ICatalogService coreCatalogService) : ICatalogService
{
    private readonly Farm.Infrastructure.Services.Catalog.ICatalogService _coreCatalogService = coreCatalogService ?? throw new ArgumentNullException(nameof(coreCatalogService));

    public Task<(IReadOnlyList<ManufacturerDto> List, string? Etag)> GetManufacturersAsync(CancellationToken ct)
    {
        return _coreCatalogService.GetManufacturersAsync(ct);
    }

    public Task<ManufacturerDto> CreateManufacturerAsync(string name, string? url, string? description, CancellationToken ct)
    {
        return _coreCatalogService.CreateManufacturerAsync(name, url, description, ct);
    }

    public Task<ManufacturerDto?> GetManufacturerByIdAsync(Guid id, CancellationToken ct)
    {
        return _coreCatalogService.GetManufacturerByIdAsync(id, ct);
    }

    public Task<(IReadOnlyList<PrinterModelDto> List, string? Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct)
    {
        return _coreCatalogService.GetModelsAsync(manufacturerId, ct);
    }

    public Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct)
    {
        return _coreCatalogService.GetModelByIdAsync(id, ct);
    }

    public Task<PrinterModelDto> CreateModelAsync(CreateModelRequest req, CancellationToken ct)
    {
        return _coreCatalogService.CreateModelAsync(
            req.ManufacturerId,
            req.Name,
            req.MotionType,
            req.MaxX,
            req.MaxY,
            req.MaxZ,
            req.DefaultBackend,
            req.SupportedFilamentTypeIds,
            req.HasHeatedBed,
            req.HasEnclosure,
            req.MultiMaterial,
            req.SupportsAutoLeveling,
            req.MaxBedTemp,
            req.MaxPrintSpeed,
            ct);
    }

    public Task<PrinterModelDto?> UpdateModelAsync(Guid id, UpdateModelRequest req, CancellationToken ct)
    {
        return _coreCatalogService.UpdateModelAsync(
            id,
            req.Name,
            req.MotionType,
            req.MaxX,
            req.MaxY,
            req.MaxZ,
            req.DefaultBackend,
            req.SupportedFilamentTypeIds,
            req.HasHeatedBed,
            req.HasEnclosure,
            req.MultiMaterial,
            req.SupportsAutoLeveling,
            req.MaxBedTemp,
            req.MaxPrintSpeed,
            req.Toolheads,
            ct);
    }

    public Task DeleteModelAsync(Guid id, CancellationToken ct)
    {
        return _coreCatalogService.DeleteModelAsync(id, ct);
    }

    public async Task<IEnumerable<SlicerModelAliasDto>> GetModelAliasesAsync(Guid modelId, CancellationToken ct)
    {
        // Verify model exists
        PrinterModelDto? model = await _coreCatalogService.GetModelByIdAsync(modelId, ct);
        if (model == null)
        {
            throw new KeyNotFoundException($"Printer model with ID {modelId} not found");
        }

        // Get aliases from the database
        IEnumerable<SlicerModelAliasDto> aliases = await _coreCatalogService.GetModelAliasesAsync(modelId, ct);
        return aliases;
    }

    public async Task<IEnumerable<SlicerModelAliasDto>> UpdateModelAliasesAsync(Guid modelId, List<string> orcaSlicerNames, List<string> prusaSlicerNames, CancellationToken ct)
    {
        // Verify model exists
        PrinterModelDto? model = await _coreCatalogService.GetModelByIdAsync(modelId, ct);
        if (model == null)
        {
            throw new KeyNotFoundException($"Printer model with ID {modelId} not found");
        }

        // Update aliases
        IEnumerable<SlicerModelAliasDto> aliases = await _coreCatalogService.UpdateModelAliasesAsync(modelId, orcaSlicerNames, prusaSlicerNames, ct);
        return aliases;
    }

    // Component model methods - delegate to core service
    public Task<IReadOnlyList<HotendModelDto>> GetHotendModelsAsync(CancellationToken ct)
        => _coreCatalogService.GetHotendModelsAsync(ct);

    public Task<IReadOnlyList<ExtruderModelDto>> GetExtruderModelsAsync(CancellationToken ct)
        => _coreCatalogService.GetExtruderModelsAsync(ct);

    public Task<IReadOnlyList<ToolheadModelDto>> GetToolheadModelsAsync(CancellationToken ct)
        => _coreCatalogService.GetToolheadModelsAsync(ct);

    public Task<IReadOnlyList<NozzleModelDto>> GetNozzleModelsAsync(CancellationToken ct)
        => _coreCatalogService.GetNozzleModelsAsync(ct);

    // Component model CRUD - Hotend
    public Task<HotendModelDto> CreateHotendModelAsync(CreateHotendModelDto dto, CancellationToken ct)
        => _coreCatalogService.CreateHotendModelAsync(dto, ct);

    public Task<HotendModelDto?> UpdateHotendModelAsync(Guid id, UpdateHotendModelDto dto, CancellationToken ct)
        => _coreCatalogService.UpdateHotendModelAsync(id, dto, ct);

    public Task DeleteHotendModelAsync(Guid id, CancellationToken ct)
        => _coreCatalogService.DeleteHotendModelAsync(id, ct);

    // Component model CRUD - Extruder
    public Task<ExtruderModelDto> CreateExtruderModelAsync(CreateExtruderModelDto dto, CancellationToken ct)
        => _coreCatalogService.CreateExtruderModelAsync(dto, ct);

    public Task<ExtruderModelDto?> UpdateExtruderModelAsync(Guid id, UpdateExtruderModelDto dto, CancellationToken ct)
        => _coreCatalogService.UpdateExtruderModelAsync(id, dto, ct);

    public Task DeleteExtruderModelAsync(Guid id, CancellationToken ct)
        => _coreCatalogService.DeleteExtruderModelAsync(id, ct);

    // Component model CRUD - Toolhead
    public Task<ToolheadModelDto> CreateToolheadModelAsync(CreateToolheadModelDto dto, CancellationToken ct)
        => _coreCatalogService.CreateToolheadModelAsync(dto, ct);

    public Task<ToolheadModelDto?> UpdateToolheadModelAsync(Guid id, UpdateToolheadModelDefDto dto, CancellationToken ct)
        => _coreCatalogService.UpdateToolheadModelAsync(id, dto, ct);

    public Task DeleteToolheadModelAsync(Guid id, CancellationToken ct)
        => _coreCatalogService.DeleteToolheadModelAsync(id, ct);

    // Component model CRUD - Nozzle
    public Task<NozzleModelDto> CreateNozzleModelAsync(CreateNozzleModelDto dto, CancellationToken ct)
        => _coreCatalogService.CreateNozzleModelAsync(dto, ct);

    public Task<NozzleModelDto?> UpdateNozzleModelAsync(Guid id, UpdateNozzleModelDto dto, CancellationToken ct)
        => _coreCatalogService.UpdateNozzleModelAsync(id, dto, ct);

    public Task DeleteNozzleModelAsync(Guid id, CancellationToken ct)
        => _coreCatalogService.DeleteNozzleModelAsync(id, ct);

    // Contextual manufacturer query
    public Task<ManufacturersByContextDto> GetManufacturersByContextAsync(CatalogContext context, CancellationToken ct)
        => _coreCatalogService.GetManufacturersByContextAsync(context, ct);
}
