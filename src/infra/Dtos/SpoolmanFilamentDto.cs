namespace Farm.Infrastructure;

/// <summary>
/// Represents a filament type (product definition) retrieved from Spoolman.
/// Unlike SpoolmanSpoolDto which represents a physical spool instance,
/// this captures the filament product itself (e.g., "PolyTerra PLA Charcoal Black").
/// </summary>
public record SpoolmanFilamentDto(
    int Id,
    string? Name,
    string? Material,
    string? ColorHex,
    string? Vendor,
    double? Density,
    double? Diameter,
    double? Weight,
    double? SpoolWeight,
    double? Price,
    int? SettingsExtruderTemp,
    int? SettingsBedTemp,
    string? ArticleNumber,
    string? Comment,
    string? MultiColorHexes,
    string? ExternalId);

/// <summary>
/// Represents a vendor retrieved from Spoolman.
/// </summary>
public record SpoolmanVendorDto(
    int Id,
    string Name,
    string? ExternalId);

/// <summary>
/// Request to create or update a filament in Spoolman via its REST API.
/// </summary>
public record SpoolmanCreateFilamentRequest
{
    public string? Name { get; init; }

    public int? VendorId { get; init; }

    public string? Material { get; init; }

    public double? Density { get; init; }

    public double? Diameter { get; init; }

    public double? Weight { get; init; }

    public double? SpoolWeight { get; init; }

    public int? SettingsExtruderTemp { get; init; }

    public int? SettingsBedTemp { get; init; }

    public string? ColorHex { get; init; }

    public string? ExternalId { get; init; }

    public string? Comment { get; init; }

    public double? Price { get; init; }

    public string? ArticleNumber { get; init; }

    public string? MultiColorHexes { get; init; }
}

/// <summary>
/// Request to associate a retail barcode with a Spoolman filament by storing it in articleNumber.
/// </summary>
public record SpoolmanBarcodeMappingRequest
{
    public string? Barcode { get; init; }

    public int? FilamentId { get; init; }
}

/// <summary>
/// Request to bulk-update a set of filaments in Spoolman.
/// Only non-null fields are applied to each filament.
/// </summary>
public record SpoolmanBulkUpdateFilamentsRequest
{
    /// <summary>IDs of filaments to update (required).</summary>
    public int[] FilamentIds { get; init; } = [];

    /// <summary>Vendor ID to set on all selected filaments (null = no change).</summary>
    public int? VendorId { get; init; }

    /// <summary>Material to set (null = no change).</summary>
    public string? Material { get; init; }

    /// <summary>Price to set (null = no change).</summary>
    public double? Price { get; init; }

    /// <summary>Extruder temp to set (null = no change).</summary>
    public int? SettingsExtruderTemp { get; init; }

    /// <summary>Bed temp to set (null = no change).</summary>
    public int? SettingsBedTemp { get; init; }

    /// <summary>Comment to set (null = no change).</summary>
    public string? Comment { get; init; }
}

/// <summary>
/// Result of a bulk filament update operation.
/// </summary>
public record SpoolmanBulkUpdateResult(
    int UpdatedCount,
    int ErrorCount,
    string[] Errors);

/// <summary>
/// Request to bulk-delete a set of filaments from Spoolman.
/// </summary>
public record SpoolmanBulkDeleteRequest
{
    /// <summary>IDs of filaments to delete (required).</summary>
    public int[] FilamentIds { get; init; } = [];
}
