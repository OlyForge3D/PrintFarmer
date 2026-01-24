using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Catalog;

/// <summary>
/// Repository for managing printer catalog data including manufacturers, printer models,
/// component models (hotends, extruders, toolheads, nozzles), and slicer aliases.
/// </summary>
public interface ICatalogRepository
{
    /// <summary>Gets all manufacturers as lightweight tuples.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<(Guid Id, string Name, string? Url, string? Description)>> GetManufacturersAsync(CancellationToken ct = default);

    /// <summary>Gets a manufacturer by ID.</summary>
    /// <param name="id">The manufacturer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<(Guid Id, string Name, string? Url, string? Description)?> GetManufacturerByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a new manufacturer.</summary>
    /// <param name="id">The manufacturer identifier.</param>
    /// <param name="name">The manufacturer name.</param>
    /// <param name="url">Optional manufacturer website URL.</param>
    /// <param name="description">Optional manufacturer description.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddManufacturerAsync(Guid id, string name, string? url, string? description, CancellationToken ct = default);

    /// <summary>Checks if a manufacturer exists by ID.</summary>
    /// <param name="id">The manufacturer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ManufacturerExistsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets the ID of the "Unknown" manufacturer used as a default.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<Guid?> GetUnknownManufacturerIdAsync(CancellationToken ct = default);

    /// <summary>Gets printer models with caching, optionally filtered by manufacturer.</summary>
    /// <param name="manufacturerId">Optional manufacturer ID to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<PrinterModelDto>> GetModelsCachedAsync(Guid? manufacturerId, CancellationToken ct = default);

    /// <summary>Gets a printer model by ID.</summary>
    /// <param name="id">The printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a new printer model.</summary>
    /// <param name="model">The printer model entity to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddModelAsync(Domain.PrinterModel model, CancellationToken ct = default);

    /// <summary>Filters and returns only valid filament type IDs from the provided list.</summary>
    /// <param name="ids">Array of filament type IDs to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<Guid>> GetValidFilamentTypeIdsAsync(Guid[] ids, CancellationToken ct = default);

    /// <summary>Gets a printer model with filament type names populated.</summary>
    /// <param name="id">The printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrinterModelDto?> GetModelWithFilamentNamesAsync(Guid id, CancellationToken ct = default);

    /// <summary>Saves pending changes to the database.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Gets the raw printer model entity for modification.</summary>
    /// <param name="id">The printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Domain.PrinterModel?> GetModelEntityAsync(Guid id, CancellationToken ct = default);

    /// <summary>Updates the supported filament types for a printer model.</summary>
    /// <param name="modelId">The printer model identifier.</param>
    /// <param name="filamentTypeIds">The filament type IDs to associate.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateModelFilamentTypesAsync(Guid modelId, IEnumerable<Guid> filamentTypeIds, CancellationToken ct = default);

    /// <summary>Updates the toolhead configurations for a printer model.</summary>
    /// <param name="modelId">The printer model identifier.</param>
    /// <param name="toolheads">Array of toolhead configurations.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateModelToolheadsAsync(Guid modelId, PrinterModelToolheadDto[] toolheads, CancellationToken ct = default);

    /// <summary>Gets the ID of the "Unknown" model used as a default.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<Guid?> GetUnknownModelIdAsync(CancellationToken ct = default);

    /// <summary>Removes a printer model by ID.</summary>
    /// <param name="id">The printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveModelAsync(Guid id, CancellationToken ct = default);

    /// <summary>Finds a manufacturer by name.</summary>
    /// <param name="name">The manufacturer name to find.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Manufacturer?> FindManufacturerByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Finds a printer model by name within a manufacturer.</summary>
    /// <param name="name">The model name to find.</param>
    /// <param name="manufacturerId">The manufacturer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrinterModel?> FindModelByNameAsync(string name, Guid manufacturerId, CancellationToken ct = default);

    /// <summary>Gets slicer model name aliases for a printer model.</summary>
    /// <param name="modelId">The printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<Domain.PrinterModelAlias>> GetModelAliasesAsync(Guid modelId, CancellationToken ct = default);

    /// <summary>Updates slicer model name aliases for a printer model.</summary>
    /// <param name="modelId">The printer model identifier.</param>
    /// <param name="orcaSlicerNames">List of OrcaSlicer model name aliases.</param>
    /// <param name="prusaSlicerNames">List of PrusaSlicer model name aliases.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<Domain.PrinterModelAlias>> UpdateModelAliasesAsync(Guid modelId, List<string> orcaSlicerNames, List<string> prusaSlicerNames, CancellationToken ct = default);

    // Component model methods - Get

    /// <summary>Gets all hotend model definitions.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, int? MaxTemp, bool IsHighFlow, NozzleInterfaceType NozzleInterface, string? Description, string? Url)>> GetHotendModelsAsync(CancellationToken ct = default);

    /// <summary>Gets all extruder model definitions.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, string? GearRatio, bool IsDirectDrive, string? Description, string? Url)>> GetExtruderModelsAsync(CancellationToken ct = default);

    /// <summary>Gets all toolhead model definitions.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, string? Description, string? Url, Guid? DefaultHotendId, Guid? DefaultExtruderId, Guid? DefaultNozzleId)>> GetToolheadModelsAsync(CancellationToken ct = default);

    /// <summary>Gets all nozzle model definitions.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, double Diameter, int? MaxTemp, NozzleType NozzleType, bool IsHardened, NozzleInterfaceType NozzleInterface, string? Description, string? Url)>> GetNozzleModelsAsync(CancellationToken ct = default);

    // Component model methods - Get By Id

    /// <summary>Gets a hotend model definition by ID.</summary>
    /// <param name="id">The hotend model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Domain.HotendModelDefinition?> GetHotendModelByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets an extruder model definition by ID.</summary>
    /// <param name="id">The extruder model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Domain.ExtruderModelDefinition?> GetExtruderModelByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets a toolhead model definition by ID.</summary>
    /// <param name="id">The toolhead model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Domain.ToolheadModelDefinition?> GetToolheadModelByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets a nozzle model definition by ID.</summary>
    /// <param name="id">The nozzle model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Domain.NozzleModelDefinition?> GetNozzleModelByIdAsync(Guid id, CancellationToken ct = default);

    // Component model methods - Create

    /// <summary>Adds a new hotend model definition.</summary>
    /// <param name="model">The hotend model to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddHotendModelAsync(Domain.HotendModelDefinition model, CancellationToken ct = default);

    /// <summary>Adds a new extruder model definition.</summary>
    /// <param name="model">The extruder model to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddExtruderModelAsync(Domain.ExtruderModelDefinition model, CancellationToken ct = default);

    /// <summary>Adds a new toolhead model definition.</summary>
    /// <param name="model">The toolhead model to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddToolheadModelAsync(Domain.ToolheadModelDefinition model, CancellationToken ct = default);

    /// <summary>Adds a new nozzle model definition.</summary>
    /// <param name="model">The nozzle model to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddNozzleModelAsync(Domain.NozzleModelDefinition model, CancellationToken ct = default);

    // Component model methods - Delete

    /// <summary>Removes a hotend model definition by ID.</summary>
    /// <param name="id">The hotend model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveHotendModelAsync(Guid id, CancellationToken ct = default);

    /// <summary>Removes an extruder model definition by ID.</summary>
    /// <param name="id">The extruder model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveExtruderModelAsync(Guid id, CancellationToken ct = default);

    /// <summary>Removes a toolhead model definition by ID.</summary>
    /// <param name="id">The toolhead model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveToolheadModelAsync(Guid id, CancellationToken ct = default);

    /// <summary>Removes a nozzle model definition by ID.</summary>
    /// <param name="id">The nozzle model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveNozzleModelAsync(Guid id, CancellationToken ct = default);

    // Contextual manufacturer queries

    /// <summary>Counts printer models for a manufacturer.</summary>
    /// <param name="manufacturerId">The manufacturer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountPrinterModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default);

    /// <summary>Counts hotend models for a manufacturer.</summary>
    /// <param name="manufacturerId">The manufacturer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountHotendModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default);

    /// <summary>Counts extruder models for a manufacturer.</summary>
    /// <param name="manufacturerId">The manufacturer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountExtruderModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default);

    /// <summary>Counts toolhead models for a manufacturer.</summary>
    /// <param name="manufacturerId">The manufacturer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountToolheadModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default);

    /// <summary>Counts nozzle models for a manufacturer.</summary>
    /// <param name="manufacturerId">The manufacturer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountNozzleModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default);
}
