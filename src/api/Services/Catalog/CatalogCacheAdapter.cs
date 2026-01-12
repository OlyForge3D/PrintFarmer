using Farm.Infrastructure.Services.Catalog.Caching;
using Farm.Web.Api.Infrastructure.Caching;

namespace Farm.Web.Api.Services.Catalog;

/// <summary>
/// API adapter that implements ICatalogCacheProvider abstraction.
/// Wraps API-specific ICatalogCache to work with Infrastructure layer.
/// </summary>
public class CatalogCacheAdapter : ICatalogCacheProvider
{
    private readonly ICatalogCache _cache;

    public CatalogCacheAdapter(ICatalogCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<(IReadOnlyList<ManufacturerDto> list, string? etag)> GetManufacturersAsync(CancellationToken ct)
    {
        var result = await _cache.GetManufacturersAsync(ct);
        return (result.list, result.etag);
    }

    public async Task<(IReadOnlyList<PrinterModelDto> list, string? etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct)
    {
        var result = await _cache.GetModelsAsync(manufacturerId, ct);
        return (result.list, result.etag);
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
