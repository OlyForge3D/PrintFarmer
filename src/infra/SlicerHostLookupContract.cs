namespace Farm.Infrastructure;

/// <summary>
/// Shared route and authentication contract for slicer-host lookups against the main API.
/// </summary>
public static class SlicerHostLookupContract
{
    /// <summary>Header carrying the shared slicer service credential.</summary>
    public const string ApiKeyHeaderName = "X-Slicer-Api-Key";

    /// <summary>Base route for authenticated, read-only slicer-host lookups.</summary>
    public const string RouteBase = "api/internal/slicer-host";

    /// <summary>Route for all catalog manufacturers.</summary>
    public const string ManufacturersPath = $"{RouteBase}/catalog/manufacturers";

    /// <summary>Builds the route for one catalog printer model.</summary>
    /// <param name="modelId">Catalog printer model identifier.</param>
    /// <returns>The relative lookup route.</returns>
    public static string PrinterModelPath(Guid modelId) =>
        $"{RouteBase}/catalog/printer-models/{modelId:D}";

    /// <summary>Builds the route for one catalog printer model's aliases.</summary>
    /// <param name="modelId">Catalog printer model identifier.</param>
    /// <returns>The relative lookup route.</returns>
    public static string ModelAliasesPath(Guid modelId) =>
        $"{PrinterModelPath(modelId)}/aliases";

    /// <summary>Builds the route for one printer.</summary>
    /// <param name="printerId">Printer identifier.</param>
    /// <returns>The relative lookup route.</returns>
    public static string PrinterPath(Guid printerId) =>
        $"{RouteBase}/printers/{printerId:D}";
}

/// <summary>
/// Minimal printer projection returned to the standalone slicer host.
/// </summary>
/// <param name="Id">Printer identifier.</param>
/// <param name="Name">Printer display name.</param>
/// <param name="ModelId">Catalog printer model identifier.</param>
/// <param name="ModelName">Catalog printer model name.</param>
public sealed record SlicerHostPrinterLookupDto(
    Guid Id,
    string Name,
    Guid ModelId,
    string? ModelName);
