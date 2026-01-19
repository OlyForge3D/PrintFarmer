using System.Collections.Generic;
using Farm.Infrastructure;
using Farm.Web.Api.Controllers.Requests;

namespace Farm.Web.Api.Services.Catalog;

/// <summary>
/// Web-specific catalog service interface.
/// Works with request DTOs from controllers.
/// Implementation wraps Infrastructure.Services.Catalog.ICatalogService.
/// </summary>
public interface ICatalogService
{
    Task<(IReadOnlyList<ManufacturerDto> List, string? Etag)> GetManufacturersAsync(CancellationToken ct);

    Task<ManufacturerDto> CreateManufacturerAsync(string name, string? url, string? description, CancellationToken ct);

    Task<ManufacturerDto?> GetManufacturerByIdAsync(Guid id, CancellationToken ct);

    Task<(IReadOnlyList<PrinterModelDto> List, string? Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);

    Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct);

    Task<PrinterModelDto> CreateModelAsync(CreateModelRequest req, CancellationToken ct);

    Task<PrinterModelDto?> UpdateModelAsync(Guid id, UpdateModelRequest req, CancellationToken ct);

    Task DeleteModelAsync(Guid id, CancellationToken ct);

    Task<IEnumerable<SlicerModelAliasDto>> GetModelAliasesAsync(Guid modelId, CancellationToken ct);

    Task<IEnumerable<SlicerModelAliasDto>> UpdateModelAliasesAsync(Guid modelId, List<string> orcaSlicerNames, List<string> prusaSlicerNames, CancellationToken ct);

    // Component model methods - Get
    Task<IReadOnlyList<HotendModelDto>> GetHotendModelsAsync(CancellationToken ct);
    Task<IReadOnlyList<ExtruderModelDto>> GetExtruderModelsAsync(CancellationToken ct);
    Task<IReadOnlyList<ToolheadModelDto>> GetToolheadModelsAsync(CancellationToken ct);
    Task<IReadOnlyList<NozzleModelDto>> GetNozzleModelsAsync(CancellationToken ct);

    // Component model methods - CRUD
    Task<HotendModelDto> CreateHotendModelAsync(CreateHotendModelDto dto, CancellationToken ct);
    Task<HotendModelDto?> UpdateHotendModelAsync(Guid id, UpdateHotendModelDto dto, CancellationToken ct);
    Task DeleteHotendModelAsync(Guid id, CancellationToken ct);

    Task<ExtruderModelDto> CreateExtruderModelAsync(CreateExtruderModelDto dto, CancellationToken ct);
    Task<ExtruderModelDto?> UpdateExtruderModelAsync(Guid id, UpdateExtruderModelDto dto, CancellationToken ct);
    Task DeleteExtruderModelAsync(Guid id, CancellationToken ct);

    Task<ToolheadModelDto> CreateToolheadModelAsync(CreateToolheadModelDto dto, CancellationToken ct);
    Task<ToolheadModelDto?> UpdateToolheadModelAsync(Guid id, UpdateToolheadModelDefDto dto, CancellationToken ct);
    Task DeleteToolheadModelAsync(Guid id, CancellationToken ct);

    Task<NozzleModelDto> CreateNozzleModelAsync(CreateNozzleModelDto dto, CancellationToken ct);
    Task<NozzleModelDto?> UpdateNozzleModelAsync(Guid id, UpdateNozzleModelDto dto, CancellationToken ct);
    Task DeleteNozzleModelAsync(Guid id, CancellationToken ct);

    // Contextual manufacturer methods
    Task<ManufacturersByContextDto> GetManufacturersByContextAsync(CatalogContext context, CancellationToken ct);
}
