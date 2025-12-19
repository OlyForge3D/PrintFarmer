using Farm.Infrastructure.Services.Catalog;
using Farm.Web.Api.Controllers.Requests;

namespace Farm.Web.Api.Services.Catalog;

/// <summary>
/// API adapter that wraps Infrastructure ICatalogService.
/// Converts request DTOs to domain parameters for the core service.
/// This keeps the API layer thin and the core logic reusable.
/// </summary>
public class CatalogServiceAdapter : ICatalogService
{
    private readonly Farm.Infrastructure.Services.Catalog.ICatalogService _coreCatalogService;

    public CatalogServiceAdapter(Farm.Infrastructure.Services.Catalog.ICatalogService coreCatalogService)
    {
        _coreCatalogService = coreCatalogService ?? throw new ArgumentNullException(nameof(coreCatalogService));
    }

    public Task<(IReadOnlyList<ManufacturerDto> list, string? etag)> GetManufacturersAsync(CancellationToken ct)
    {
        return _coreCatalogService.GetManufacturersAsync(ct);
    }

    public Task<ManufacturerDto> CreateManufacturerAsync(string name, CancellationToken ct)
    {
        return _coreCatalogService.CreateManufacturerAsync(name, ct);
    }

    public Task<ManufacturerDto?> GetManufacturerByIdAsync(Guid id, CancellationToken ct)
    {
        return _coreCatalogService.GetManufacturerByIdAsync(id, ct);
    }

    public Task<(IReadOnlyList<PrinterModelDto> list, string? etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct)
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
            req.Type,
            req.MaxX,
            req.MaxY,
            req.MaxZ,
            req.DefaultBackend,
            req.SupportedFilamentTypeIds,
            req.DefaultNozzleDiameter,
            ct);
    }

    public Task<PrinterModelDto?> UpdateModelAsync(Guid id, UpdateModelRequest req, CancellationToken ct)
    {
        return _coreCatalogService.UpdateModelAsync(
            id,
            req.Name,
            req.Type,
            req.MaxX,
            req.MaxY,
            req.MaxZ,
            req.DefaultBackend,
            req.SupportedFilamentTypeIds,
            req.DefaultNozzleDiameter,
            ct);
    }

    public Task DeleteModelAsync(Guid id, CancellationToken ct)
    {
        return _coreCatalogService.DeleteModelAsync(id, ct);
    }
}
