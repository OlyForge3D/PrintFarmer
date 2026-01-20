using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Catalog;

public interface ICatalogRepository
{
    Task<IReadOnlyList<(Guid Id, string Name, string? Url, string? Description)>> GetManufacturersAsync(CancellationToken ct = default);

    Task<(Guid Id, string Name, string? Url, string? Description)?> GetManufacturerByIdAsync(Guid id, CancellationToken ct = default);

    Task AddManufacturerAsync(Guid id, string name, string? url, string? description, CancellationToken ct = default);

    Task<bool> ManufacturerExistsAsync(Guid id, CancellationToken ct = default);

    Task<Guid?> GetUnknownManufacturerIdAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PrinterModelDto>> GetModelsCachedAsync(Guid? manufacturerId, CancellationToken ct = default);

    Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct = default);

    Task AddModelAsync(Domain.PrinterModel model, CancellationToken ct = default);

    Task<IEnumerable<Guid>> GetValidFilamentTypeIdsAsync(Guid[] ids, CancellationToken ct = default);

    Task<PrinterModelDto?> GetModelWithFilamentNamesAsync(Guid id, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);

    Task<Domain.PrinterModel?> GetModelEntityAsync(Guid id, CancellationToken ct = default);

    Task UpdateModelFilamentTypesAsync(Guid modelId, IEnumerable<Guid> filamentTypeIds, CancellationToken ct = default);

    Task UpdateModelToolheadsAsync(Guid modelId, PrinterModelToolheadDto[] toolheads, CancellationToken ct = default);

    Task<Guid?> GetUnknownModelIdAsync(CancellationToken ct = default);

    Task RemoveModelAsync(Guid id, CancellationToken ct = default);

    Task<Manufacturer?> FindManufacturerByNameAsync(string name, CancellationToken ct = default);

    Task<PrinterModel?> FindModelByNameAsync(string name, Guid manufacturerId, CancellationToken ct = default);

    Task<List<Domain.PrinterModelAlias>> GetModelAliasesAsync(Guid modelId, CancellationToken ct = default);

    Task<List<Domain.PrinterModelAlias>> UpdateModelAliasesAsync(Guid modelId, List<string> orcaSlicerNames, List<string> prusaSlicerNames, CancellationToken ct = default);

    // Component model methods - Get
    Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, int? MaxTemp, bool IsHighFlow, NozzleInterfaceType NozzleInterface, string? Description, string? Url)>> GetHotendModelsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, string? GearRatio, bool IsDirectDrive, string? Description, string? Url)>> GetExtruderModelsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, string? Description, string? Url, Guid? DefaultHotendId, Guid? DefaultExtruderId, Guid? DefaultNozzleId)>> GetToolheadModelsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<(Guid Id, string Name, Guid ManufacturerId, string? ManufacturerName, int? MaxTemp, bool IsHardened, NozzleInterfaceType NozzleInterface, string? Description, string? Url)>> GetNozzleModelsAsync(CancellationToken ct = default);

    // Component model methods - Get By Id
    Task<Domain.HotendModelDefinition?> GetHotendModelByIdAsync(Guid id, CancellationToken ct = default);

    Task<Domain.ExtruderModelDefinition?> GetExtruderModelByIdAsync(Guid id, CancellationToken ct = default);

    Task<Domain.ToolheadModelDefinition?> GetToolheadModelByIdAsync(Guid id, CancellationToken ct = default);

    Task<Domain.NozzleModelDefinition?> GetNozzleModelByIdAsync(Guid id, CancellationToken ct = default);

    // Component model methods - Create
    Task AddHotendModelAsync(Domain.HotendModelDefinition model, CancellationToken ct = default);

    Task AddExtruderModelAsync(Domain.ExtruderModelDefinition model, CancellationToken ct = default);

    Task AddToolheadModelAsync(Domain.ToolheadModelDefinition model, CancellationToken ct = default);

    Task AddNozzleModelAsync(Domain.NozzleModelDefinition model, CancellationToken ct = default);

    // Component model methods - Delete
    Task RemoveHotendModelAsync(Guid id, CancellationToken ct = default);

    Task RemoveExtruderModelAsync(Guid id, CancellationToken ct = default);

    Task RemoveToolheadModelAsync(Guid id, CancellationToken ct = default);

    Task RemoveNozzleModelAsync(Guid id, CancellationToken ct = default);

    // Contextual manufacturer queries
    Task<int> CountPrinterModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default);

    Task<int> CountHotendModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default);

    Task<int> CountExtruderModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default);

    Task<int> CountToolheadModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default);

    Task<int> CountNozzleModelsByManufacturerAsync(Guid manufacturerId, CancellationToken ct = default);

    // OEM component lookups - find stock/OEM parts for a manufacturer
    Task<Guid?> GetOemHotendIdAsync(Guid manufacturerId, CancellationToken ct = default);

    Task<Guid?> GetOemExtruderIdAsync(Guid manufacturerId, CancellationToken ct = default);
}
