using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Basic printer info without live status (for fast loading)
/// <summary>
/// Basic printer information without live status values; optimized for list views / dropdowns.
/// </summary>
public record PrinterBasicDto(
    Guid Id,
    string Name,
    string? Notes,
    string? ManufacturerName = null,
    string? ModelName = null,
    PrinterBackend Backend = PrinterBackend.Moonraker,
    string? ApiKey = null,
    string? OriginalServerUrl = null,
    int BackendPort = 80,  // NOTE: Default 80 is for HTTP. Actual values: 7125 (Moonraker), 80 (PrusaLink/OctoPrint), 8080 (SDCP). See PrinterBackendHelpers.GetDefaultPort()
    int? FrontendPort = null,
    string? BackendUrl = null,
    string? FrontendUrl = null);
