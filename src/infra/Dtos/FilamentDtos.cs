namespace Farm.Infrastructure;

/// <summary>
/// Update payload for a filament type.
/// </summary>
public record UpdateFilamentTypeRequest(string Name, TempTargets DefaultTemperatures);

// Filament temperature presets (admin-configurable) - now dynamic
/// <summary>
/// Dynamic filament temperature presets keyed by filament name.
/// </summary>
public record FilamentPresetsDto(Dictionary<string, TempTargets> Presets);
