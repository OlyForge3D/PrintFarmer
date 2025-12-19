namespace Farm.Infrastructure.Services.Catalog.Caching;

/// <summary>
/// Abstraction for catalog caching implementation.
/// Allows Infrastructure layer to cache catalog data without depending on API-specific cache.
/// </summary>
public interface ICatalogCacheProvider
{
    /// <summary>Gets cached manufacturers with optional ETag.</summary>
    Task<(IReadOnlyList<ManufacturerDto> list, string? etag)> GetManufacturersAsync(CancellationToken ct);

    /// <summary>Gets cached models, optionally filtered by manufacturer.</summary>
    Task<(IReadOnlyList<PrinterModelDto> list, string? etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);

    /// <summary>Invalidates the manufacturers cache.</summary>
    void InvalidateManufacturers();

    /// <summary>Invalidates models cache for a specific manufacturer or all models if manufacturerId is null.</summary>
    void InvalidateModels(Guid? manufacturerId = null);
}
