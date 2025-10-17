using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Web.Api.Repositories.Catalog;

public interface ICatalogRepository
{
    Task<IReadOnlyList<(Guid Id, string Name)>> GetManufacturersAsync(CancellationToken ct = default);
    Task<(Guid Id, string Name)?> GetManufacturerByIdAsync(Guid id, CancellationToken ct = default);
    Task AddManufacturerAsync(Guid id, string name, CancellationToken ct = default);
    Task<bool> ManufacturerExistsAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<PrinterModelDto>> GetModelsCachedAsync(Guid? manufacturerId, CancellationToken ct = default);
    Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct = default);
    Task AddModelAsync(Farm.Infrastructure.Domain.PrinterModel model, CancellationToken ct = default);
    Task<IEnumerable<Guid>> GetValidFilamentTypeIdsAsync(Guid[] ids, CancellationToken ct = default);
    Task<PrinterModelDto?> GetModelWithFilamentNamesAsync(Guid id, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    Task<Farm.Infrastructure.Domain.PrinterModel?> GetModelEntityAsync(Guid id, CancellationToken ct = default);
    Task UpdateModelFilamentTypesAsync(Guid modelId, IEnumerable<Guid> filamentTypeIds, CancellationToken ct = default);
}
