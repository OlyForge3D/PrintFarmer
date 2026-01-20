namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Service for loading and seeding database data from YAML configuration files
/// </summary>
public interface IDataSeedService
{
    /// <summary>
    /// Load and seed all data from YAML files
    /// </summary>
    Task SeedAllAsync();

    /// <summary>
    /// Load and seed manufacturers from YAML file
    /// </summary>
    Task SeedManufacturersAsync();

    /// <summary>
    /// Load and seed printer models from YAML file
    /// </summary>
    Task SeedPrinterModelsAsync();

    /// <summary>
    /// Load and seed filament types from YAML file
    /// </summary>
    Task SeedFilamentTypesAsync();

    /// <summary>
    /// Load and seed component models (hotends, extruders, toolheads, nozzles) from YAML files
    /// </summary>
    Task SeedComponentModelsAsync();

    /// <summary>
    /// Reload seed data from YAML files (for admin use)
    /// </summary>
    Task ReloadSeedDataAsync();
}
