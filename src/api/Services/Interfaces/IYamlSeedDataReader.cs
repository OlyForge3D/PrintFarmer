using Farm.Web.Api.Models.SeedData;

namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Service for reading and parsing YAML seed data files
/// </summary>
public interface IYamlSeedDataReader
{
    /// <summary>
    /// Read manufacturers from YAML file
    /// </summary>
    Task<List<ManufacturerSeedDto>> ReadManufacturersAsync();

    /// <summary>
    /// Read printer models from YAML file
    /// </summary>
    Task<List<PrinterModelSeedDto>> ReadPrinterModelsAsync();

    /// <summary>
    /// Read filament types from YAML file
    /// </summary>
    Task<List<FilamentTypeSeedDto>> ReadFilamentTypesAsync();

    /// <summary>
    /// Read hotend models from YAML file
    /// </summary>
    Task<List<HotendModelSeedDto>> ReadHotendsAsync();

    /// <summary>
    /// Read extruder models from YAML file
    /// </summary>
    Task<List<ExtruderModelSeedDto>> ReadExtrudersAsync();

    /// <summary>
    /// Read toolhead models from YAML file
    /// </summary>
    Task<List<ToolheadModelSeedDto>> ReadToolheadsAsync();

    /// <summary>
    /// Read nozzle models from YAML file
    /// </summary>
    Task<List<NozzleModelSeedDto>> ReadNozzlesAsync();
}
