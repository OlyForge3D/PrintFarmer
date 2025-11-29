using System.Collections.Generic;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Catalog;

public interface ICatalogService
{
    Task<(IReadOnlyList<ManufacturerDto> list, string? etag)> GetManufacturersAsync(CancellationToken ct);
    Task<ManufacturerDto> CreateManufacturerAsync(string name, CancellationToken ct);
    Task<ManufacturerDto?> GetManufacturerByIdAsync(Guid id, CancellationToken ct);

    Task<(IReadOnlyList<PrinterModelDto> list, string? etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);
    Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct);
    Task<PrinterModelDto> CreateModelAsync(Controllers.Requests.CreateModelRequest req, CancellationToken ct);
    Task<PrinterModelDto?> UpdateModelAsync(Guid id, Controllers.Requests.UpdateModelRequest req, CancellationToken ct);
    Task DeleteModelAsync(Guid id, CancellationToken ct);
}
