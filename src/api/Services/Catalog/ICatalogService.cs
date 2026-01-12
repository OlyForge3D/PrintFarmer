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
    Task<(IReadOnlyList<ManufacturerDto> list, string? etag)> GetManufacturersAsync(CancellationToken ct);
    Task<ManufacturerDto> CreateManufacturerAsync(string name, CancellationToken ct);
    Task<ManufacturerDto?> GetManufacturerByIdAsync(Guid id, CancellationToken ct);

    Task<(IReadOnlyList<PrinterModelDto> list, string? etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);
    Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct);
    Task<PrinterModelDto> CreateModelAsync(CreateModelRequest req, CancellationToken ct);
    Task<PrinterModelDto?> UpdateModelAsync(Guid id, UpdateModelRequest req, CancellationToken ct);
    Task DeleteModelAsync(Guid id, CancellationToken ct);
}

