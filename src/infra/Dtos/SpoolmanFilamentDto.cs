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

    public double Density { get; init; } = 1.24;

    public double Diameter { get; init; } = 1.75;

    public double? Weight { get; init; }

    public double? SpoolWeight { get; init; }

    public int? SettingsExtruderTemp { get; init; }

    public int? SettingsBedTemp { get; init; }

    public string? ColorHex { get; init; }

    public string? ExternalId { get; init; }

    public string? Comment { get; init; }
}
