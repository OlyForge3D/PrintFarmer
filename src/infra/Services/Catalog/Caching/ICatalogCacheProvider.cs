namespace Farm.Infrastructure.Services.Catalog.Caching;

/// <summary>
/// Abstraction for catalog caching implementation.
/// Allows Infrastructure layer to cache catalog data without depending on API-specific cache.
/// </summary>
public interface ICatalogCacheProvider
{
    /// <summary>Gets cached manufacturers with optional ETag.</summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<(IReadOnlyList<ManufacturerDto> List, string? Etag)> GetManufacturersAsync(CancellationToken ct);

    /// <summary>Gets cached models, optionally filtered by manufacturer.</summary>
    /// <param name="manufacturerId">Optional manufacturer ID to filter models by.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<(IReadOnlyList<PrinterModelDto> List, string? Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);

    /// <summary>Invalidates the manufacturers cache.</summary>
    void InvalidateManufacturers();

    /// <summary>Invalidates models cache for a specific manufacturer or all models if manufacturerId is null.</summary>
    /// <param name="manufacturerId">Optional manufacturer ID to invalidate cache for; if null, invalidates all models.</param>
    void InvalidateModels(Guid? manufacturerId = null);
}
