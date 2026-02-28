namespace Farm.Infrastructure;

/// <summary>
/// Update payload for a filament type.
/// </summary>
/// <param name="Name">Display name of the filament type.</param>
/// <param name="DefaultTemperatures">Default hotend and bed temperatures.</param>
/// <param name="IsAbrasive">True if the filament contains abrasive materials requiring hardened nozzles.</param>
/// <param name="NeedsEnclosure">True if the filament requires an enclosure for optimal printing.</param>
/// <param name="DefaultPricePerKg">Default price per kilogram in USD, or null to auto-detect.</param>
/// <param name="DefaultDensity">Default material density in g/cm³, or null to auto-detect.</param>
public record UpdateFilamentTypeRequest(string Name, TempTargets DefaultTemperatures, bool IsAbrasive = false, bool NeedsEnclosure = false, double? DefaultPricePerKg = null, double? DefaultDensity = null);

// Filament temperature presets (admin-configurable) - now dynamic
/// <summary>
/// Dynamic filament temperature presets keyed by filament name.
/// </summary>
public record FilamentPresetsDto(Dictionary<string, TempTargets> Presets);
