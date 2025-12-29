using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Catalog;

/// <summary>
/// Core catalog service providing manufacturer and printer model management.
/// Pure business logic - no web-specific dependencies.
/// Cache management delegated to implementer via ICatalogCacheProvider.
/// </summary>
public interface ICatalogService
{
    /// <summary>Gets all manufacturers with optional ETag for caching.</summary>
    Task<(IReadOnlyList<ManufacturerDto> list, string? etag)> GetManufacturersAsync(CancellationToken ct);

    /// <summary>Creates a new manufacturer with normalized name.</summary>
    Task<ManufacturerDto> CreateManufacturerAsync(string name, CancellationToken ct);

    /// <summary>Gets a manufacturer by ID.</summary>
    Task<ManufacturerDto?> GetManufacturerByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Gets all printer models, optionally filtered by manufacturer.</summary>
    Task<(IReadOnlyList<PrinterModelDto> list, string? etag)> GetModelsAsync(Guid? manufacturerId, CancellationToken ct);

    /// <summary>Gets a printer model by ID.</summary>
    Task<PrinterModelDto?> GetModelByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Creates a new printer model.</summary>
    Task<PrinterModelDto> CreateModelAsync(
        Guid manufacturerId,
        string name,
        MotionType? type,
        double? maxX,
        double? maxY,
        double? maxZ,
        PrinterBackend? defaultBackend,
        Guid[]? supportedFilamentTypeIds,
        double? defaultNozzleDiameter,
        CancellationToken ct);

    /// <summary>Updates an existing printer model.</summary>
    Task<PrinterModelDto?> UpdateModelAsync(
        Guid id,
        string? name,
        MotionType? type,
        double? maxX,
        double? maxY,
        double? maxZ,
        PrinterBackend? defaultBackend,
        Guid[]? supportedFilamentTypeIds,
        double? defaultNozzleDiameter,
        CancellationToken ct);

    /// <summary>Deletes a printer model.</summary>
    Task DeleteModelAsync(Guid id, CancellationToken ct);

    /// <summary>Finds a manufacturer by name. Returns null if not found.</summary>
    Task<ManufacturerDto?> FindManufacturerByNameAsync(string name, CancellationToken ct);

    /// <summary>Finds a printer model by name and manufacturer ID. Returns null if not found.</summary>
    Task<PrinterModelDto?> FindModelByNameAsync(string name, Guid manufacturerId, CancellationToken ct);

    /// <summary>Gets the default (Unknown) manufacturer and model IDs.</summary>
    Task<(Guid ManufacturerId, Guid ModelId)> GetDefaultCatalogIdsAsync(CancellationToken ct);
}
