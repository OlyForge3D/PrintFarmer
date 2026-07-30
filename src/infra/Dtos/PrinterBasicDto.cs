using System.Text.Json.Serialization;
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? ApiKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? OriginalServerUrl = null,
    int BackendPort = 80,  // NOTE: Default 80 is for HTTP. Actual values: 7125 (Moonraker), 80 (PrusaLink/OctoPrint/SDCP). See PrinterBackendHelpers.GetDefaultPort()
    int? FrontendPort = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? BackendUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string? FrontendUrl = null);
