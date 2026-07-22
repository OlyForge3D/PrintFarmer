using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.Catalog;

/// <summary>
/// Core catalog service providing manufacturer and printer model management.
/// Pure business logic - no web-specific dependencies.
/// Cache management delegated to implementer via ICatalogCacheProvider.
/// </summary>
public interface ICatalogService
{
    /// <summary>Gets all manufacturers with optional ETag for caching.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<(IReadOnlyList<ManufacturerDto> List, string? Etag)> GetManufacturersAsync(CancellationToken ct);

    /// <summary>Creates a new manufacturer with normalized name and optional metadata.</summary>
    /// <param name="name">The manufacturer name.</param>
    /// <param name="url">Optional URL for the manufacturer website.</param>
    /// <param name="description">Optional description of the manufacturer.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ManufacturerDto> CreateManufacturerAsync(string name, string? url, string? description, CancellationToken ct);

    /// <summary>Gets a manufacturer by ID.</summary>
    /// <param name="id">The manufacturer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ManufacturerDto?> GetManufacturerByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Gets all printer models, optionally filtered by manufacturer.</summary>
    /// <param name="manufacturerId">Optional manufacturer ID to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<(IReadOnlyList<PrinterModelDto> List, string? Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);

    /// <summary>Gets a printer model by ID.</summary>
    /// <param name="id">The printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Creates a new printer model.</summary>
    /// <param name="manufacturerId">The manufacturer identifier.</param>
    /// <param name="name">The printer model name.</param>
    /// <param name="type">Optional motion type (e.g., CoreXY, Cartesian).</param>
    /// <param name="maxX">Optional maximum X dimension in mm.</param>
    /// <param name="maxY">Optional maximum Y dimension in mm.</param>
    /// <param name="maxZ">Optional maximum Z dimension in mm.</param>
    /// <param name="defaultBackend">Optional default printer backend type.</param>
    /// <param name="supportedFilamentTypeIds">Optional array of supported filament type IDs.</param>
    /// <param name="hasHeatedBed">Optional flag indicating heated bed support.</param>
    /// <param name="hasEnclosure">Optional flag indicating enclosure presence.</param>
    /// <param name="multiMaterial">Optional flag indicating multi-material capability.</param>
    /// <param name="supportsAutoLeveling">Optional flag indicating auto-leveling support.</param>
    /// <param name="maxBedTemp">Optional maximum bed temperature in Celsius.</param>
    /// <param name="maxPrintSpeed">Optional maximum print speed in mm/s.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrinterModelDto> CreateModelAsync(
        Guid manufacturerId,
        string name,
        MotionType? type,
        double? maxX,
        double? maxY,
        double? maxZ,
        PrinterBackend? defaultBackend,
        Guid[]? supportedFilamentTypeIds,
        bool? hasHeatedBed,
        bool? hasEnclosure,
        bool? multiMaterial,
        bool? supportsAutoLeveling,
        int? maxBedTemp,
        int? maxPrintSpeed,
        CancellationToken ct);

    /// <summary>Updates an existing printer model.</summary>
    /// <param name="id">The printer model identifier.</param>
    /// <param name="name">Optional new name for the printer model.</param>
    /// <param name="type">Optional motion type (e.g., CoreXY, Cartesian).</param>
    /// <param name="maxX">Optional maximum X dimension in mm.</param>
    /// <param name="maxY">Optional maximum Y dimension in mm.</param>
    /// <param name="maxZ">Optional maximum Z dimension in mm.</param>
    /// <param name="defaultBackend">Optional default printer backend type.</param>
    /// <param name="supportedFilamentTypeIds">Optional array of supported filament type IDs.</param>
    /// <param name="hasHeatedBed">Optional flag indicating heated bed support.</param>
    /// <param name="hasEnclosure">Optional flag indicating enclosure presence.</param>
    /// <param name="multiMaterial">Optional flag indicating multi-material capability.</param>
    /// <param name="supportsAutoLeveling">Optional flag indicating auto-leveling support.</param>
    /// <param name="maxBedTemp">Optional maximum bed temperature in Celsius.</param>
    /// <param name="maxPrintSpeed">Optional maximum print speed in mm/s.</param>
    /// <param name="defaultWattage">Optional default power consumption in watts.</param>
    /// <param name="defaultHourlyRate">Optional default machine hourly rate.</param>
    /// <param name="toolheads">Optional array of toolhead configurations.</param>
    /// <param name="defaultAutoDispatchState">Optional default auto-dispatch state for new printers.</param>
    /// <param name="defaultStartBehavior">Optional default start behavior for new printers.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrinterModelDto?> UpdateModelAsync(
        Guid id,
        string? name,
        MotionType? type,
        double? maxX,
        double? maxY,
        double? maxZ,
        PrinterBackend? defaultBackend,
        Guid[]? supportedFilamentTypeIds,
        bool? hasHeatedBed,
        bool? hasEnclosure,
        bool? multiMaterial,
        bool? supportsAutoLeveling,
        int? maxBedTemp,
        int? maxPrintSpeed,
        decimal? defaultWattage,
        decimal? defaultHourlyRate,
        PrinterModelToolheadDto[]? toolheads,
        AutoDispatchState? defaultAutoDispatchState,
        StartBehavior? defaultStartBehavior,
        CancellationToken ct);

    /// <summary>Deletes a printer model.</summary>
    /// <param name="id">The printer model identifier to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteModelAsync(Guid id, CancellationToken ct);

    /// <summary>Gets all slicer model name aliases (OrcaSlicer, PrusaSlicer) for a printer model.</summary>
    /// <param name="modelId">The printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<SlicerModelAliasDto>> GetModelAliasesAsync(Guid modelId, CancellationToken ct);

    /// <summary>Updates slicer model name aliases for a printer model.</summary>
    /// <param name="modelId">The printer model identifier.</param>
    /// <param name="orcaSlicerNames">List of OrcaSlicer model name aliases.</param>
    /// <param name="prusaSlicerNames">List of PrusaSlicer model name aliases.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<SlicerModelAliasDto>> UpdateModelAliasesAsync(Guid modelId, List<string> orcaSlicerNames, List<string> prusaSlicerNames, CancellationToken ct);

    /// <summary>Finds a manufacturer by name. Returns null if not found.</summary>
    /// <param name="name">The manufacturer name to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ManufacturerDto?> FindManufacturerByNameAsync(string name, CancellationToken ct);

    /// <summary>Finds a printer model by name and manufacturer ID. Returns null if not found.</summary>
    /// <param name="name">The printer model name to search for.</param>
    /// <param name="manufacturerId">The manufacturer identifier to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrinterModelDto?> FindModelByNameAsync(string name, Guid manufacturerId, CancellationToken ct);

    /// <summary>Gets the default (Unknown) manufacturer and model IDs.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<(Guid ManufacturerId, Guid ModelId)> GetDefaultCatalogIdsAsync(CancellationToken ct);

    // ============ Component Model Methods ============

    /// <summary>Gets all hotend model definitions.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<HotendModelDto>> GetHotendModelsAsync(CancellationToken ct);

    /// <summary>Creates a new hotend model definition.</summary>
    Task<HotendModelDto> CreateHotendModelAsync(CreateHotendModelDto dto, CancellationToken ct);

    /// <summary>Updates an existing hotend model definition.</summary>
    Task<HotendModelDto?> UpdateHotendModelAsync(Guid id, UpdateHotendModelDto dto, CancellationToken ct);

    /// <summary>Deletes a hotend model definition.</summary>
    Task DeleteHotendModelAsync(Guid id, CancellationToken ct);

    /// <summary>Gets all extruder model definitions.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ExtruderModelDto>> GetExtruderModelsAsync(CancellationToken ct);

    /// <summary>Creates a new extruder model definition.</summary>
    Task<ExtruderModelDto> CreateExtruderModelAsync(CreateExtruderModelDto dto, CancellationToken ct);

    /// <summary>Updates an existing extruder model definition.</summary>
    Task<ExtruderModelDto?> UpdateExtruderModelAsync(Guid id, UpdateExtruderModelDto dto, CancellationToken ct);

    /// <summary>Deletes an extruder model definition.</summary>
    Task DeleteExtruderModelAsync(Guid id, CancellationToken ct);

    /// <summary>Gets all toolhead model definitions.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ToolheadModelDto>> GetToolheadModelsAsync(CancellationToken ct);

    /// <summary>Creates a new toolhead model definition.</summary>
    Task<ToolheadModelDto> CreateToolheadModelAsync(CreateToolheadModelDto dto, CancellationToken ct);

    /// <summary>Updates an existing toolhead model definition.</summary>
    Task<ToolheadModelDto?> UpdateToolheadModelAsync(Guid id, UpdateToolheadModelDefDto dto, CancellationToken ct);

    /// <summary>Deletes a toolhead model definition.</summary>
    Task DeleteToolheadModelAsync(Guid id, CancellationToken ct);

    /// <summary>Gets all nozzle model definitions.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<NozzleModelDto>> GetNozzleModelsAsync(CancellationToken ct);

    /// <summary>Creates a new nozzle model definition.</summary>
    Task<NozzleModelDto> CreateNozzleModelAsync(CreateNozzleModelDto dto, CancellationToken ct);

    /// <summary>Updates an existing nozzle model definition.</summary>
    Task<NozzleModelDto?> UpdateNozzleModelAsync(Guid id, UpdateNozzleModelDto dto, CancellationToken ct);

    /// <summary>Deletes a nozzle model definition.</summary>
    Task DeleteNozzleModelAsync(Guid id, CancellationToken ct);

    // ============ Contextual Manufacturer Methods ============

    /// <summary>Gets manufacturers grouped by whether they have items in the specified catalog context.</summary>
    /// <param name="context">The catalog context (Printers, Hotends, Extruders, Toolheads, Nozzles).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ManufacturersByContextDto> GetManufacturersByContextAsync(CatalogContext context, CancellationToken ct);
}
