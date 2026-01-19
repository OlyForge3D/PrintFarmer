namespace Farm.Infrastructure.Services.Catalog;

/// <summary>
/// Abstraction for catalog caching implementation.
/// Allows different caching strategies across UI platforms (Web API, WPF, CLI, etc).
/// </summary>
public interface IManufacturerCacheProvider
{
    /// <summary>
    /// Get cached manufacturers list with ETag for change detection.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<(IReadOnlyList<ManufacturerDto> List, string? Etag)> GetManufacturersAsync(CancellationToken ct);

    /// <summary>
    /// Get cached printer models, optionally filtered by manufacturer.
    /// </summary>
    /// <param name="manufacturerId">Optional manufacturer ID to filter models by.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<(IReadOnlyList<PrinterModelDto> List, string? Etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);

    /// <summary>
    /// Invalidate manufacturers cache entry.
    /// </summary>
    void InvalidateManufacturers();

    /// <summary>
    /// Invalidate models cache entry(s).
    /// </summary>
    /// <param name="manufacturerId">Optional manufacturer ID to invalidate cache for; if null, invalidates all models.</param>
    void InvalidateModels(Guid? manufacturerId = null);
}
