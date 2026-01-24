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
    /// <summary>Gets all manufacturers with ETag.</summary>
    Task<(IReadOnlyList<ManufacturerDto> List, string? Etag)> GetManufacturersAsync(CancellationToken ct);

    /// <summary>Creates a new manufacturer.</summary>
    Task<ManufacturerDto> CreateManufacturerAsync(string name, string? url, string? description, CancellationToken ct);

    /// <summary>Gets a manufacturer by ID.</summary>
    Task<ManufacturerDto?> GetManufacturerByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Gets printer models with ETag, optionally filtered by manufacturer.</summary>
    Task<(IReadOnlyList<PrinterModelDto> List, string? Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);

    /// <summary>Gets a printer model by ID.</summary>
    Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Creates a new printer model.</summary>
    Task<PrinterModelDto> CreateModelAsync(CreateModelRequest req, CancellationToken ct);

    /// <summary>Updates an existing printer model.</summary>
    Task<PrinterModelDto?> UpdateModelAsync(Guid id, UpdateModelRequest req, CancellationToken ct);

    /// <summary>Deletes a printer model.</summary>
    Task DeleteModelAsync(Guid id, CancellationToken ct);

    /// <summary>Gets slicer aliases for a printer model.</summary>
    Task<IEnumerable<SlicerModelAliasDto>> GetModelAliasesAsync(Guid modelId, CancellationToken ct);

    /// <summary>Updates slicer aliases for a printer model.</summary>
    Task<IEnumerable<SlicerModelAliasDto>> UpdateModelAliasesAsync(Guid modelId, List<string> orcaSlicerNames, List<string> prusaSlicerNames, CancellationToken ct);

    // Component model methods - Get

    /// <summary>Gets all hotend models.</summary>
    Task<IReadOnlyList<HotendModelDto>> GetHotendModelsAsync(CancellationToken ct);

    /// <summary>Gets all extruder models.</summary>
    Task<IReadOnlyList<ExtruderModelDto>> GetExtruderModelsAsync(CancellationToken ct);

    /// <summary>Gets all toolhead models.</summary>
    Task<IReadOnlyList<ToolheadModelDto>> GetToolheadModelsAsync(CancellationToken ct);

    /// <summary>Gets all nozzle models.</summary>
    Task<IReadOnlyList<NozzleModelDto>> GetNozzleModelsAsync(CancellationToken ct);

    // Component model methods - CRUD

    /// <summary>Creates a new hotend model.</summary>
    Task<HotendModelDto> CreateHotendModelAsync(CreateHotendModelDto dto, CancellationToken ct);

    /// <summary>Updates a hotend model.</summary>
    Task<HotendModelDto?> UpdateHotendModelAsync(Guid id, UpdateHotendModelDto dto, CancellationToken ct);

    /// <summary>Deletes a hotend model.</summary>
    Task DeleteHotendModelAsync(Guid id, CancellationToken ct);

    /// <summary>Creates a new extruder model.</summary>
    Task<ExtruderModelDto> CreateExtruderModelAsync(CreateExtruderModelDto dto, CancellationToken ct);

    /// <summary>Updates an extruder model.</summary>
    Task<ExtruderModelDto?> UpdateExtruderModelAsync(Guid id, UpdateExtruderModelDto dto, CancellationToken ct);

    /// <summary>Deletes an extruder model.</summary>
    Task DeleteExtruderModelAsync(Guid id, CancellationToken ct);

    /// <summary>Creates a new toolhead model.</summary>
    Task<ToolheadModelDto> CreateToolheadModelAsync(CreateToolheadModelDto dto, CancellationToken ct);

    /// <summary>Updates a toolhead model.</summary>
    Task<ToolheadModelDto?> UpdateToolheadModelAsync(Guid id, UpdateToolheadModelDefDto dto, CancellationToken ct);

    /// <summary>Deletes a toolhead model.</summary>
    Task DeleteToolheadModelAsync(Guid id, CancellationToken ct);

    /// <summary>Creates a new nozzle model.</summary>
    Task<NozzleModelDto> CreateNozzleModelAsync(CreateNozzleModelDto dto, CancellationToken ct);

    /// <summary>Updates a nozzle model.</summary>
    Task<NozzleModelDto?> UpdateNozzleModelAsync(Guid id, UpdateNozzleModelDto dto, CancellationToken ct);

    /// <summary>Deletes a nozzle model.</summary>
    Task DeleteNozzleModelAsync(Guid id, CancellationToken ct);

    // Contextual manufacturer methods

    /// <summary>Gets manufacturers filtered by context (printers vs. components).</summary>
    Task<ManufacturersByContextDto> GetManufacturersByContextAsync(CatalogContext context, CancellationToken ct);
}
