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
    Task<(IReadOnlyList<ManufacturerDto> list, string? etag)> GetManufacturersAsync(CancellationToken ct);

    /// <summary>
    /// Get cached printer models, optionally filtered by manufacturer.
    /// </summary>
    Task<(IReadOnlyList<PrinterModelDto> list, string? etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);

    /// <summary>
    /// Invalidate manufacturers cache entry.
    /// </summary>
    void InvalidateManufacturers();

    /// <summary>
    /// Invalidate models cache entry(s).
    /// </summary>
    void InvalidateModels(Guid? manufacturerId = null);
}
