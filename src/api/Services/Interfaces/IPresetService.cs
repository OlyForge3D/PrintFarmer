using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Interface for preset service providing filament preset management functionality.
/// Manages temperature presets for different filament materials (PLA, PETG, ABS, etc.) 
/// with hotend and bed temperature settings for quick printer configuration.
/// </summary>
public interface IPresetService
{
    /// <summary>
    /// Gets the current filament temperature presets for all supported materials.
    /// </summary>
    /// <returns>A preset configuration object containing temperature settings for different filament types</returns>
    FilamentPresetsDto GetPresets();
    
    /// <summary>
    /// Saves new filament temperature presets, replacing the current configuration.
    /// </summary>
    /// <param name="presets">Preset configuration object containing temperature settings for different filament types</param>
    void SavePresets(FilamentPresetsDto presets);
}
