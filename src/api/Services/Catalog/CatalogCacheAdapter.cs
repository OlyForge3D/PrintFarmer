using Farm.Infrastructure.Services.Catalog.Caching;
using Farm.Web.Api.Infrastructure.Caching;

namespace Farm.Web.Api.Services.Catalog;

/// <summary>
/// API adapter that implements ICatalogCacheProvider abstraction.
/// Wraps API-specific ICatalogCache to work with Infrastructure layer.
/// </summary>
public class CatalogCacheAdapter(ICatalogCache cache) : ICatalogCacheProvider
{
    private readonly ICatalogCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    public async Task<(IReadOnlyList<ManufacturerDto> List, string? Etag)> GetManufacturersAsync(CancellationToken ct)
    {
        (IReadOnlyList<ManufacturerDto> List, string Etag) result = await _cache.GetManufacturersAsync(ct);
        return (result.List, result.Etag);
    }

    public async Task<(IReadOnlyList<PrinterModelDto> List, string? Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct)
    {
        (IReadOnlyList<PrinterModelDto> List, string Etag) result = await _cache.GetModelsAsync(manufacturerId, ct);
        return (result.List, result.Etag);
    }

    public void InvalidateManufacturers()
    {
        _cache.InvalidateManufacturers();
    }

    public void InvalidateModels(Guid? manufacturerId = null)
    {
        _cache.InvalidateModels();
    }
}
