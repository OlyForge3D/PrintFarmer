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
