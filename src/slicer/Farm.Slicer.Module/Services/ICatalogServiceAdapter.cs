using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Lightweight record representing a catalog printer model, used by slicer controllers
/// without depending on the full catalog domain.
/// </summary>
/// <param name="Id">The model identifier.</param>
/// <param name="Name">The model display name.</param>
/// <param name="ManufacturerName">The manufacturer name.</param>
public record CatalogModelInfo(Guid Id, string Name, string? ManufacturerName);

/// <summary>
/// Adapter interface for catalog/printer model queries used by slicer services.
/// The host application provides the implementation bridging to the catalog domain.
/// </summary>
public interface ICatalogServiceAdapter
{
    /// <summary>
    /// Gets the list of manufacturer names registered in the catalog.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of manufacturer names.</returns>
    Task<IReadOnlyList<string>> GetManufacturerNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the printer model name by its ID.
    /// </summary>
    /// <param name="printerModelId">The printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The model name, or <c>null</c> if not found.</returns>
    Task<string?> GetPrinterModelNameAsync(Guid printerModelId, CancellationToken ct = default);

    /// <summary>
    /// Gets a printer model by its ID, returning lightweight model info.
    /// </summary>
    /// <param name="modelId">The printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The model info, or <c>null</c> if not found.</returns>
    Task<CatalogModelInfo?> GetModelByIdAsync(Guid modelId, CancellationToken ct = default);

    /// <summary>
    /// Gets slicer model aliases for a given printer model.
    /// </summary>
    /// <param name="modelId">The printer model identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of slicer model aliases.</returns>
    Task<IReadOnlyList<SlicerModelAliasDto>> GetModelAliasesAsync(Guid modelId, CancellationToken ct = default);
}
