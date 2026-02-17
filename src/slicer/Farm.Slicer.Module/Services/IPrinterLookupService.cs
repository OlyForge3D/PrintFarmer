namespace Farm.Slicer.Module.Services;

/// <summary>
/// Lightweight record representing a printer from the main API,
/// used by slicer services without depending on the full printer domain.
/// </summary>
/// <param name="Id">The printer identifier.</param>
/// <param name="Name">The user-assigned printer name.</param>
/// <param name="ModelId">The catalog printer model identifier, if catalogued.</param>
/// <param name="ModelName">The resolved model name, if catalogued.</param>
public record PrinterInfo(Guid Id, string Name, Guid? ModelId, string? ModelName);

/// <summary>
/// Adapter interface for printer lookups used by slicer services in split mode.
/// In monolithic mode the main API resolves printer details from the database directly.
/// In standalone (split) mode an HTTP-based implementation calls back to the main API.
/// </summary>
public interface IPrinterLookupService
{
    /// <summary>
    /// Resolves a printer by its identifier.
    /// </summary>
    /// <param name="printerId">The printer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The printer info, or <c>null</c> if not found or the main API is unreachable.</returns>
    Task<PrinterInfo?> GetPrinterByIdAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the display name for a printer.
    /// Returns <c>"Unknown"</c> when the printer cannot be resolved.
    /// </summary>
    /// <param name="printerId">The printer identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The printer name, or <c>"Unknown"</c> on failure.</returns>
    Task<string> GetPrinterNameAsync(Guid printerId, CancellationToken ct = default);
}
